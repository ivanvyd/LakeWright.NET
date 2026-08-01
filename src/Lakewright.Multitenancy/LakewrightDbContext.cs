using Lakewright.Core.Tenancy;
using Lakewright.Multitenancy.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Lakewright.Multitenancy;

public sealed class LakewrightDbContext(DbContextOptions<LakewrightDbContext> options)
    : DbContext(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();

    public DbSet<Membership> Memberships => Set<Membership>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<Operation> Operations => Set<Operation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var tenantId = new ValueConverter<TenantId, Guid>(v => v.Value, v => new TenantId(v));

        modelBuilder.Entity<Organization>(e =>
        {
            e.ToTable("organizations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasConversion(tenantId);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(80).IsRequired();
            e.Property(x => x.Schema).HasMaxLength(63).IsRequired();
            e.HasIndex(x => x.Slug).IsUnique();

            // Two organizations sharing a schema would be a silent cross-tenant read, so the
            // database refuses it rather than trusting the provisioning code to be correct.
            e.HasIndex(x => x.Schema).IsUnique();
        });

        modelBuilder.Entity<Membership>(e =>
        {
            e.ToTable("memberships");
            e.HasKey(x => x.Id);
            e.Property(x => x.OrganizationId).HasConversion(tenantId);
            e.Property(x => x.PrincipalId).HasMaxLength(200).IsRequired();
            e.HasOne(x => x.Organization).WithMany(x => x.Memberships)
                .HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);

            // Membership is looked up on every tenant-scoped request, so it is the one index
            // whose absence would show up as latency across the whole product.
            e.HasIndex(x => new { x.PrincipalId, x.OrganizationId }).IsUnique();
        });

        modelBuilder.Entity<Operation>(e =>
        {
            e.ToTable("operations");
            e.HasKey(x => x.Id);
            e.Property(x => x.OrganizationId).HasConversion(tenantId);
            e.Property(x => x.PrincipalId).HasMaxLength(200).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(100).IsRequired();
            e.Property(x => x.IdempotencyKey).HasMaxLength(64).IsRequired();
            e.Property(x => x.ExternalId).HasMaxLength(200);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.HasOne(x => x.Organization).WithMany()
                .HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);

            // Lookups are always (tenant, operation). An index on the operation alone would
            // support a query that must not exist.
            e.HasIndex(x => new { x.OrganizationId, x.Id });

            // A second run for the same key is the duplicate-submission bug this exists to stop.
            e.HasIndex(x => x.IdempotencyKey).IsUnique();

            // Reconciliation scans for rows stuck without an external id.
            e.HasIndex(x => new { x.State, x.ClaimedAt });

            // No row version here on purpose. Reconciliation and a slow-but-alive worker can both
            // decide to act on the same row, and the fix for that is for reconciliation to claim
            // atomically the way ClaimNextAsync does, not for every write to carry a token. An
            // xmin token was tried and removed: the claim is a raw UPDATE, so the version the
            // change tracker holds afterwards is read mid-statement and every subsequent write
            // failed as a false conflict. Deferred to the reconciliation actor, which is where the
            // contention actually is.
        });

        modelBuilder.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.OrganizationId).HasConversion(
                v => v!.Value.Value,
                v => new TenantId(v));
            e.Property(x => x.PrincipalId).HasMaxLength(200).IsRequired();
            e.Property(x => x.Action).HasMaxLength(100).IsRequired();
            e.Property(x => x.ResourceType).HasMaxLength(100).IsRequired();
            e.Property(x => x.ResourceId).HasMaxLength(200);
            e.Property(x => x.Detail).HasColumnType("jsonb");
            e.HasIndex(x => new { x.OrganizationId, x.OccurredAt });
        });
    }

    /// <summary>
    /// Refuses to persist a modification or deletion of an <see cref="AuditEvent"/>.
    /// </summary>
    /// <remarks>
    /// The entity is init-only, so ordinary code cannot do this. The guard catches the routes that
    /// bypass the type: a future property that loses its init-only modifier, or an entity attached
    /// in the <c>Deleted</c> state.
    ///
    /// It does <em>not</em> catch <c>ExecuteUpdate</c> or <c>ExecuteDelete</c>, which run straight
    /// against the database and never call any of these methods, nor raw SQL. An earlier version of
    /// this comment claimed otherwise, and a security review disproved it. That gap is closed at the database instead, by
    /// <see cref="DatabaseHardening"/>, which grants the application role select and insert on
    /// <c>audit_events</c> and nothing else.
    ///
    /// Every save overload is covered, because they are independent virtual methods: overriding
    /// only the async one left <c>SaveChanges()</c> and the two-argument async overload open.
    /// </remarks>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardAuditEvents();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        GuardAuditEvents();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void GuardAuditEvents()
    {
        var tampered = ChangeTracker.Entries<AuditEvent>()
            .FirstOrDefault(e => e.State is EntityState.Modified or EntityState.Deleted);

        if (tampered is not null)
        {
            throw new InvalidOperationException(
                $"audit_events is append-only; attempted to {tampered.State} " +
                $"event {tampered.Entity.Id}.");
        }
    }
}
