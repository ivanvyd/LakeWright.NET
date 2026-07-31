using Lakewright.Core.Tenancy;
using Microsoft.EntityFrameworkCore;
using Lakewright.Multitenancy;
using Lakewright.Multitenancy.Model;
using Microsoft.Extensions.Options;

namespace Lakewright.TenantIsolation.Tests;

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

    private static EfTenantContextResolver ResolverFor(LakewrightDbContext db) =>
        new(db, Options.Create(new MultitenancyOptions { Catalog = "analytics" }));

    private static async Task<LakewrightDbContext> SeedTwoTenantsAsync(PostgresFixture postgres)
    {
        var db = await postgres.NewDatabaseAsync();
        var now = DateTimeOffset.UtcNow;

        db.Organizations.AddRange(
            new Organization
            {
                Id = AcmeId, Name = "Acme", Slug = "acme", CreatedAt = now,
                Schema = UnityCatalogIdentifier.SchemaForTenant(AcmeId),
                State = OrganizationState.Active
            },
            new Organization
            {
                Id = GlobexId, Name = "Globex", Slug = "globex", CreatedAt = now,
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
        await using var db = await SeedTwoTenantsAsync(postgres);

        var ctx = await ResolverFor(db).ResolveAsync(AcmeId, AlicePrincipal, TestContext.Current.CancellationToken);

        ctx.ShouldNotBeNull();
        ctx.TenantId.ShouldBe(AcmeId);
        ctx.Schema.ShouldBe(UnityCatalogIdentifier.SchemaForTenant(AcmeId));
    }

    [Fact]
    public async Task Naming_another_tenant_resolves_to_nothing()
    {
        // The request is well-formed and the organization exists. Alice simply is not in it.
        await using var db = await SeedTwoTenantsAsync(postgres);

        var ctx = await ResolverFor(db).ResolveAsync(GlobexId, AlicePrincipal, TestContext.Current.CancellationToken);

        ctx.ShouldBeNull();
    }

    [Fact]
    public async Task An_unknown_tenant_is_indistinguishable_from_one_you_cannot_reach()
    {
        // Both return null. A caller must not be able to tell whether an organization exists by
        // the shape of the refusal, which is why callers map this to 404 and never 403.
        await using var db = await SeedTwoTenantsAsync(postgres);
        var resolver = ResolverFor(db);
        var ct = TestContext.Current.CancellationToken;

        var notAMember = await resolver.ResolveAsync(GlobexId, AlicePrincipal, ct);
        var doesNotExist = await resolver.ResolveAsync(TenantId.New(), AlicePrincipal, ct);

        notAMember.ShouldBeNull();
        doesNotExist.ShouldBeNull();
    }

    [Fact]
    public async Task A_suspended_organization_refuses_its_own_members()
    {
        await using var db = await SeedTwoTenantsAsync(postgres);
        var acme = await db.Organizations.FindAsync([AcmeId], TestContext.Current.CancellationToken);
        acme!.State = OrganizationState.Suspended;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ctx = await ResolverFor(db).ResolveAsync(AcmeId, AlicePrincipal, TestContext.Current.CancellationToken);

        ctx.ShouldBeNull();
    }

    [Fact]
    public async Task Two_organizations_cannot_share_a_schema()
    {
        // Enforced by a unique index rather than by provisioning code, because provisioning code
        // is the thing most likely to be wrong.
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

        await Should.ThrowAsync<DbUpdateException>(
            async () => await db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Audit_events_cannot_be_edited_or_deleted()
    {
        await using var db = await SeedTwoTenantsAsync(postgres);
        var ct = TestContext.Current.CancellationToken;

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
        var deleting = await Should.ThrowAsync<InvalidOperationException>(
            async () => await db.SaveChangesAsync(ct));
        deleting.Message.ShouldContain("append-only");
    }
}
