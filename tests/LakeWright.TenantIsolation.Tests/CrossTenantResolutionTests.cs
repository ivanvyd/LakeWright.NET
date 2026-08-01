using LakeWright.Core.Tenancy;
using LakeWright.Multitenancy;
using LakeWright.Multitenancy.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// Behavioural half of the isolation suite: the resolver against a real Postgres.
/// </summary>
[Trait("Category", "TenantIsolation")]
[Collection(nameof(PostgresTests))]
public class CrossTenantResolutionTests(PostgresFixture postgres)
{
    private static readonly TenantId AcmeId = TenantId.Parse("0198f000-0000-7000-8000-0000000000a1");
    private static readonly TenantId GlobexId = TenantId.Parse("0198f000-0000-7000-8000-0000000000b2");

    private const string AlicePrincipal = "auth0|alice";
    private const string BobPrincipal = "auth0|bob";

    private static EfTenantContextResolver ResolverFor(LakeWrightDbContext db) =>
        new(db, Options.Create(new MultitenancyOptions { Catalog = "analytics" }));

    private static async Task<LakeWrightDbContext> SeedTwoTenantsAsync(PostgresFixture postgres)
    {
        var db = await postgres.NewDatabaseAsync();
        var now = DateTimeOffset.UtcNow;

        db.Organizations.AddRange(
            new Organization
            {
                Id = AcmeId,
                Name = "Acme",
                Slug = "acme",
                CreatedAt = now,
                Schema = UnityCatalogIdentifier.SchemaForTenant(AcmeId),
                State = OrganizationState.Active
            },
            new Organization
            {
                Id = GlobexId,
                Name = "Globex",
                Slug = "globex",
                CreatedAt = now,
                Schema = UnityCatalogIdentifier.SchemaForTenant(GlobexId),
                State = OrganizationState.Active
            });

        db.Memberships.AddRange(
            new Membership { Id = Guid.CreateVersion7(), OrganizationId = AcmeId, PrincipalId = AlicePrincipal, Role = MembershipRole.Admin, CreatedAt = now },
            new Membership { Id = Guid.CreateVersion7(), OrganizationId = GlobexId, PrincipalId = BobPrincipal, Role = MembershipRole.Admin, CreatedAt = now });

        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task A_member_resolves_to_their_own_schema()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SeedTwoTenantsAsync(postgres);

        // Act
        var ctx = await ResolverFor(db).ResolveAsync(AcmeId, AlicePrincipal, ct);

        // Assert
        ctx.ShouldNotBeNull();
        ctx.TenantId.ShouldBe(AcmeId);
        ctx.Schema.ShouldBe(UnityCatalogIdentifier.SchemaForTenant(AcmeId));
    }

    [Fact]
    public async Task Naming_another_tenant_resolves_to_nothing()
    {
        // Arrange — the request is well-formed and the organization exists. Alice is not in it.
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SeedTwoTenantsAsync(postgres);

        // Act
        var ctx = await ResolverFor(db).ResolveAsync(GlobexId, AlicePrincipal, ct);

        // Assert
        ctx.ShouldBeNull();
    }

    [Fact]
    public async Task An_unknown_tenant_is_indistinguishable_from_one_you_cannot_reach()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SeedTwoTenantsAsync(postgres);
        var resolver = ResolverFor(db);

        // Act
        var notAMember = await resolver.ResolveAsync(GlobexId, AlicePrincipal, ct);
        var doesNotExist = await resolver.ResolveAsync(TenantId.New(), AlicePrincipal, ct);

        // Assert — both null, so the shape of the refusal reveals nothing. Callers map this to 404.
        notAMember.ShouldBeNull();
        doesNotExist.ShouldBeNull();
    }

    [Fact]
    public async Task A_suspended_organization_refuses_its_own_members()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SeedTwoTenantsAsync(postgres);
        var acme = await db.Organizations.FindAsync([AcmeId], ct);
        acme!.State = OrganizationState.Suspended;
        await db.SaveChangesAsync(ct);

        // Act
        var ctx = await ResolverFor(db).ResolveAsync(AcmeId, AlicePrincipal, ct);

        // Assert
        ctx.ShouldBeNull();
    }

    [Fact]
    public async Task Two_organizations_cannot_share_a_schema()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SeedTwoTenantsAsync(postgres);
        db.Organizations.Add(new Organization
        {
            Id = TenantId.New(),
            Name = "Impostor",
            Slug = "impostor",
            CreatedAt = DateTimeOffset.UtcNow,
            Schema = UnityCatalogIdentifier.SchemaForTenant(AcmeId),
            State = OrganizationState.Active
        });

        // Act
        var refused = await Should.ThrowAsync<DbUpdateException>(
            async () => await db.SaveChangesAsync(ct));

        // Assert — enforced by a unique index, because provisioning code is the thing most likely
        // to be wrong.
        refused.ShouldNotBeNull();
    }

    [Fact]
    public async Task Audit_events_cannot_be_edited_or_deleted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SeedTwoTenantsAsync(postgres);
        var evt = new AuditEvent
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = AcmeId,
            PrincipalId = AlicePrincipal,
            Action = "organization.provisioned",
            ResourceType = "organization",
            ResourceId = AcmeId.ToString(),
            OccurredAt = DateTimeOffset.UtcNow
        };
        db.AuditEvents.Add(evt);
        await db.SaveChangesAsync(ct);
        db.AuditEvents.Remove(evt);

        // Act
        var refused = await Should.ThrowAsync<InvalidOperationException>(
            async () => await db.SaveChangesAsync(ct));

        // Assert
        refused.Message.ShouldContain("append-only");
    }

    [Fact]
    public async Task The_append_only_guard_covers_every_save_overload()
    {
        // Arrange — overriding only SaveChangesAsync(CancellationToken) left the synchronous path
        // and the two-argument async overload open. A security review demonstrated both bypassing
        // the guard.
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SeedTwoTenantsAsync(postgres);
        var evt = new AuditEvent
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = AcmeId,
            PrincipalId = AlicePrincipal,
            Action = "organization.provisioned",
            ResourceType = "organization",
            OccurredAt = DateTimeOffset.UtcNow
        };
        db.AuditEvents.Add(evt);
        await db.SaveChangesAsync(ct);
        db.Entry(evt).State = EntityState.Deleted;

        // Act
        var synchronous = Should.Throw<InvalidOperationException>(() => db.SaveChanges());
        var twoArgumentAsync = await Should.ThrowAsync<InvalidOperationException>(
            async () => await db.SaveChangesAsync(acceptAllChangesOnSuccess: true, ct));

        // Assert — ExecuteUpdate and ExecuteDelete bypass the change tracker entirely and are
        // covered at the database instead; see AuditLockdownTests.
        synchronous.Message.ShouldContain("append-only");
        twoArgumentAsync.Message.ShouldContain("append-only");
    }
}
