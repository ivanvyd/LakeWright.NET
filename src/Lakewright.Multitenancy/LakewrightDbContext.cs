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
    /// The entity is init-only, so ordinary code cannot do this. This catches the routes that
    /// bypass the type: raw SQL through the change tracker, a future property that loses its
    /// init-only modifier, or an <c>ExecuteUpdate</c> someone adds later. An append-only claim in
    /// a compliance document should be enforced somewhere that fails, not only documented.
    /// </remarks>
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var tampered = ChangeTracker.Entries<AuditEvent>()
            .FirstOrDefault(e => e.State is EntityState.Modified or EntityState.Deleted);

        if (tampered is not null)
        {
            throw new InvalidOperationException(
                $"audit_events is append-only; attempted to {tampered.State} " +
                $"event {tampered.Entity.Id}.");
        }

        return base.SaveChangesAsync(cancellationToken);
    }
}
