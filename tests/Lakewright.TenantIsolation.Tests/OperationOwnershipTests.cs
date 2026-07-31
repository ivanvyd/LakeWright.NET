using Lakewright.Core.Tenancy;
using Lakewright.Multitenancy;
using Lakewright.Multitenancy.Model;
using Microsoft.EntityFrameworkCore;

namespace Lakewright.TenantIsolation.Tests;

/// <summary>
/// A Databricks statement id says nothing about who may read its results, so ownership has to come
/// from somewhere else. It comes from here.
/// </summary>
[Trait("Category", "TenantIsolation")]
[Collection(nameof(PostgresTests))]
public class OperationOwnershipTests(PostgresFixture postgres)
{
    private static readonly TenantId AcmeId = TenantId.Parse("0198f000-0000-7000-8000-0000000000d1");
    private static readonly TenantId GlobexId = TenantId.Parse("0198f000-0000-7000-8000-0000000000d2");

    private static TenantContext Ctx(TenantId id) =>
        TenantContextFactory.ForTenant(id, "analytics");

    private static async Task<LakewrightDbContext> SeedAsync(PostgresFixture postgres)
    {
        var db = await postgres.NewDatabaseAsync();
        var now = DateTimeOffset.UtcNow;

        foreach (var (id, name) in new[] { (AcmeId, "acme"), (GlobexId, "globex") })
        {
            db.Organizations.Add(new Organization
            {
                Id = id,
                Name = name,
                Slug = name,
                CreatedAt = now,
                Schema = UnityCatalogIdentifier.SchemaForTenant(id),
                State = OrganizationState.Active
            });
        }

        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task An_operation_is_invisible_to_another_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SeedAsync(postgres);
        var store = new OperationStore(db);

        var acmeOperation = await store.CreateAsync(Ctx(AcmeId), "auth0|alice", "analysis", ct);

        // Globex knows the id. That is the realistic case: ids leak through logs, support
        // tickets and browser history. Knowing one must not be enough.
        var seenByGlobex = await store.FindAsync(Ctx(GlobexId), acmeOperation.Id, ct);

        seenByGlobex.ShouldBeNull();
        (await store.FindAsync(Ctx(AcmeId), acmeOperation.Id, ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_missing_operation_is_indistinguishable_from_one_you_may_not_see()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SeedAsync(postgres);
        var store = new OperationStore(db);

        var acmeOperation = await store.CreateAsync(Ctx(AcmeId), "auth0|alice", "analysis", ct);

        var notYours = await store.FindAsync(Ctx(GlobexId), acmeOperation.Id, ct);
        var doesNotExist = await store.FindAsync(Ctx(GlobexId), Guid.CreateVersion7(), ct);

        notYours.ShouldBeNull();
        doesNotExist.ShouldBeNull();
    }

    [Fact]
    public async Task Recording_an_external_id_for_another_tenants_operation_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SeedAsync(postgres);
        var store = new OperationStore(db);

        var acmeOperation = await store.CreateAsync(Ctx(AcmeId), "auth0|alice", "analysis", ct);

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await store.RecordExternalIdAsync(
                Ctx(GlobexId), acmeOperation.Id, "statement-123", ct));

        // And the row is untouched.
        var reloaded = await store.FindAsync(Ctx(AcmeId), acmeOperation.Id, ct);
        reloaded!.ExternalId.ShouldBeNull();
        reloaded.State.ShouldBe(OperationState.Pending);
    }

    [Fact]
    public async Task Idempotency_keys_are_unique_so_a_retry_cannot_start_a_second_run()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SeedAsync(postgres);
        var store = new OperationStore(db);

        var first = await store.CreateAsync(Ctx(AcmeId), "auth0|alice", "analysis", ct);

        db.Operations.Add(new Operation
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = AcmeId,
            PrincipalId = "auth0|alice",
            Kind = "analysis",
            State = OperationState.Pending,
            IdempotencyKey = first.IdempotencyKey,
            CreatedAt = DateTimeOffset.UtcNow
        });

        await Should.ThrowAsync<DbUpdateException>(async () => await db.SaveChangesAsync(ct));
    }
}
