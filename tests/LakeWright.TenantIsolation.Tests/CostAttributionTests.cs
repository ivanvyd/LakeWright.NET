using LakeWright.Core.Cost;
using LakeWright.Core.Tenancy;
using LakeWright.Multitenancy;
using LakeWright.Multitenancy.Cost;
using LakeWright.Multitenancy.Model;
using Microsoft.Extensions.Options;
using static LakeWright.TenantIsolation.Tests.TestApi;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// The cost attribution surface against a real Postgres.
/// </summary>
/// <remarks>
/// Three things the suite is here to assert. First, the math: the elapsed-time proxy weights
/// wall-clock seconds by the configured DBU/hour rate. Second, the boundary: a tenant never sees
/// another tenant's compute. Third, the contract: the implementation returns nothing for an
/// empty window rather than a default zero, because a customer-facing usage page that always
/// reads "0 DBU" is the dashboard no one trusts.
/// </remarks>
[Trait("Category", "TenantIsolation")]
[Collection(nameof(PostgresTests))]
public class CostAttributionTests(PostgresFixture postgres)
{
    private static TenantContext Acme() => TenantContextFactory.ForTenant(AcmeId, "analytics");
    private static TenantContext Globex() => TenantContextFactory.ForTenant(GlobexId, "analytics");

    [Fact]
    public async Task ResolveAsync_aggregates_only_the_called_tenants_operations()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        var (acmeClaimed, acmeCompleted) = await SeedAcmeAndGlobexOperationsAsync(db, ct);

        var attribution = new OperationCostAttribution(
            db,
            Options.Create(new CostAttributionOptions
            {
                WarehouseSku = "2X-Small Serverless",
                DbusPerHour = 0.30
            }));

        var summary = await attribution.ResolveAsync(
            Acme(),
            acmeClaimed.AddMinutes(-1),
            acmeCompleted.AddMinutes(1),
            ct);

        // The Acme window contains two operations; the Globex row is not in the result.
        summary.TenantId.ShouldBe(AcmeId);
        summary.Source.ShouldBe(CostSource.Proxy);
        summary.ByKind.ShouldNotBeEmpty();
        summary.ByKind.Sum(b => b.Operations).ShouldBe(2);
        summary.DbusConsumed.ShouldBeGreaterThan(0m);
    }

    [Fact]
    public async Task ResolveAsync_for_a_tenant_with_no_operations_returns_an_empty_summary()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        // Seed only Acme. Globex has no operations, so its summary is empty.
        await SeedAcmeOnlyOperationsAsync(db, ct);

        var attribution = new OperationCostAttribution(
            db,
            Options.Create(new CostAttributionOptions()));

        var summary = await attribution.ResolveAsync(
            Globex(),
            DateTimeOffset.UtcNow.AddDays(-30),
            DateTimeOffset.UtcNow,
            ct);

        summary.TenantId.ShouldBe(GlobexId);
        summary.ByKind.ShouldBeEmpty();
        summary.DbusConsumed.ShouldBe(0m);
    }

    [Fact]
    public async Task ResolveAsync_math_matches_elapsed_seconds_times_dbus_per_second()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();

        var now = DateTimeOffset.Parse("2026-08-15T12:00:00Z", null);
        var claimed = now;
        var completed = now.AddSeconds(600);   // ten minutes of wall-clock compute
        await SeedSingleTenantWithOperationAsync(db, AcmeId, claimed, completed, ct);

        var attribution = new OperationCostAttribution(
            db,
            Options.Create(new CostAttributionOptions
            {
                WarehouseSku = "2X-Small Serverless",
                DbusPerHour = 0.60        // 0.60 DBU/hour = 0.0001666.../s
            }));

        var summary = await attribution.ResolveAsync(
            Acme(),
            claimed.AddMinutes(-1),
            completed.AddMinutes(1),
            ct);

        var expectedDbus = Math.Round((decimal)600.0 * 0.60m / 3600m, 4);
        summary.DbusConsumed.ShouldBe(expectedDbus, tolerance: 0.001m);
        var row = summary.ByKind.Single();
        row.Operations.ShouldBe(1);
        row.ElapsedSeconds.ShouldBe(600, tolerance: 1.0);
    }

    [Fact]
    public async Task ResolveAsync_rejects_an_inverted_window()
    {
        var ct = TestContext.Current.CancellationToken;
        // A real but empty DbContext: the parameter guard runs before any SQL, so a fixture
        // context is the cheapest way to bypass the sealed-type NSubstitute limitation. Do not
        // seed anything; the check must throw on the from/until check, not on a query.
        await using var db = await postgres.NewDatabaseAsync();
        var attribution = new OperationCostAttribution(
            db,
            Options.Create(new CostAttributionOptions()));

        // An inverted window is a caller bug, not something to silently swap around.
        await Should.ThrowAsync<ArgumentException>(async () =>
            await attribution.ResolveAsync(
                Acme(),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddHours(-1),
                ct));
    }

    private static async Task<(DateTimeOffset claimed, DateTimeOffset completed)>
        SeedAcmeAndGlobexOperationsAsync(LakeWrightDbContext db, CancellationToken ct)
    {
        var now = DateTimeOffset.Parse("2026-08-15T12:00:00Z", null);

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

        await db.SaveChangesAsync(ct);

        // Acme: two operations, both completed within the test window.
        db.Operations.AddRange(
            new Operation
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = AcmeId,
                PrincipalId = Alice,
                Kind = "analysis",
                State = OperationState.Succeeded,
                IdempotencyKey = Guid.CreateVersion7().ToString("N"),
                CreatedAt = now,
                ClaimedAt = now,
                CompletedAt = now.AddMinutes(5)
            },
            new Operation
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = AcmeId,
                PrincipalId = Alice,
                Kind = "export",
                State = OperationState.Succeeded,
                IdempotencyKey = Guid.CreateVersion7().ToString("N"),
                CreatedAt = now,
                ClaimedAt = now.AddMinutes(1),
                CompletedAt = now.AddMinutes(3)
            },
            // Globex: one operation, in the same window. Must not appear in Acme's summary.
            new Operation
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = GlobexId,
                PrincipalId = Bob,
                Kind = "analysis",
                State = OperationState.Succeeded,
                IdempotencyKey = Guid.CreateVersion7().ToString("N"),
                CreatedAt = now,
                ClaimedAt = now,
                CompletedAt = now.AddMinutes(2)
            });

        await db.SaveChangesAsync(ct);
        return (now, now.AddMinutes(5));
    }

    private static async Task SeedAcmeOnlyOperationsAsync(LakeWrightDbContext db, CancellationToken ct)
    {
        var now = DateTimeOffset.Parse("2026-08-15T12:00:00Z", null);

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

        await db.SaveChangesAsync(ct);

        // Acme only. Globex has nothing, so the Globex summary must be empty.
        db.Operations.Add(new Operation
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = AcmeId,
            PrincipalId = Alice,
            Kind = "analysis",
            State = OperationState.Succeeded,
            IdempotencyKey = Guid.CreateVersion7().ToString("N"),
            CreatedAt = now,
            ClaimedAt = now,
            CompletedAt = now.AddMinutes(5)
        });

        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedSingleTenantWithOperationAsync(
        LakeWrightDbContext db,
        TenantId tenantId,
        DateTimeOffset claimed,
        DateTimeOffset completed,
        CancellationToken ct)
    {
        db.Organizations.Add(new Organization
        {
            Id = tenantId,
            Name = tenantId == AcmeId ? "Acme" : "Globex",
            Slug = tenantId == AcmeId ? "acme" : "globex",
            CreatedAt = claimed.AddMinutes(-1),
            Schema = UnityCatalogIdentifier.SchemaForTenant(tenantId),
            State = OrganizationState.Active
        });
        await db.SaveChangesAsync(ct);

        db.Operations.Add(new Operation
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = tenantId,
            PrincipalId = Alice,
            Kind = "analysis",
            State = OperationState.Succeeded,
            IdempotencyKey = Guid.CreateVersion7().ToString("N"),
            CreatedAt = claimed.AddMinutes(-1),
            ClaimedAt = claimed,
            CompletedAt = completed
        });
        await db.SaveChangesAsync(ct);
    }
}
