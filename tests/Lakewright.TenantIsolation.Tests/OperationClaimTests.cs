using Lakewright.Core.Tenancy;
using Lakewright.Multitenancy;
using Lakewright.Multitenancy.Model;
using Microsoft.EntityFrameworkCore;

namespace Lakewright.TenantIsolation.Tests;

/// <summary>
/// The claim loop. Its one job is that two workers never get the same row.
/// </summary>
[Trait("Category", "TenantIsolation")]
[Collection(nameof(PostgresTests))]
public class OperationClaimTests(PostgresFixture postgres)
{
    private static readonly TenantId AcmeId = TenantId.Parse("0198f000-0000-7000-8000-0000000000e1");

    private static TenantContext Ctx() => TenantContextFactory.ForTenant(AcmeId, "analytics");

    private static async Task<LakewrightDbContext> SeedAsync(PostgresFixture postgres, int operations)
    {
        var db = await postgres.NewDatabaseAsync();
        db.Organizations.Add(new Organization
        {
            Id = AcmeId,
            Name = "Acme",
            Slug = "acme",
            CreatedAt = DateTimeOffset.UtcNow,
            Schema = UnityCatalogIdentifier.SchemaForTenant(AcmeId),
            State = OrganizationState.Active
        });
        await db.SaveChangesAsync();

        var store = new OperationStore(db);
        for (var i = 0; i < operations; i++)
        {
            await store.CreateAsync(Ctx(), "auth0|alice", "analysis", CancellationToken.None);
        }

        return db;
    }

    [Fact]
    public async Task Claiming_returns_null_when_there_is_nothing_pending()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SeedAsync(postgres, operations: 0);

        (await new OperationStore(db).ClaimNextAsync(ct)).ShouldBeNull();
    }

    [Fact]
    public async Task A_claimed_operation_is_not_handed_out_twice()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SeedAsync(postgres, operations: 1);
        var store = new OperationStore(db);

        var first = await store.ClaimNextAsync(ct);
        var second = await store.ClaimNextAsync(ct);

        first.ShouldNotBeNull();
        second.ShouldBeNull("the only operation was already claimed");
    }

    [Fact]
    public async Task Concurrent_workers_never_claim_the_same_operation()
    {
        // Ten connections race for twenty rows; every row must go to exactly one of them.
        //
        // This catches the realistic bug: a select-then-update claim, verified by replacing the
        // single statement with one and watching this test go red. It does NOT prove `SKIP LOCKED`
        // is required — that was measured too, and the test stays green without it, because the
        // single-statement update is atomic either way. `SKIP LOCKED` prevents the convoy, and
        // nothing here measures blocking.
        var ct = TestContext.Current.CancellationToken;
        const int Operations = 20;
        const int Workers = 10;

        await using var seed = await SeedAsync(postgres, Operations);
        var connectionString = seed.Database.GetConnectionString()!;

        var claimed = new System.Collections.Concurrent.ConcurrentBag<Guid>();

        await Task.WhenAll(Enumerable.Range(0, Workers).Select(async _ =>
        {
            // A separate context per worker: sharing one would serialise on the connection and
            // the test would pass without exercising SKIP LOCKED at all.
            await using var db = PostgresFixture.ContextFor(connectionString);
            var store = new OperationStore(db);

            while (await store.ClaimNextAsync(ct) is { } operation)
            {
                claimed.Add(operation.Id);
            }
        }));

        claimed.Count.ShouldBe(Operations, "every operation should be claimed exactly once");
        claimed.Distinct().Count().ShouldBe(Operations, "no operation may be claimed twice");
    }

    [Fact]
    public async Task An_operation_orphaned_between_submit_and_record_is_found_by_reconciliation()
    {
        // The crash-critical window from ADR 0005: a worker dies after submitting to Databricks
        // and before writing the run id. The row is claimed, still pending, and has no external
        // id. Nothing else in the system can tell that run exists.
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SeedAsync(postgres, operations: 1);
        var store = new OperationStore(db);

        var claimed = await store.ClaimNextAsync(ct);
        claimed.ShouldNotBeNull();
        claimed.ExternalId.ShouldBeNull();

        // Nothing to reconcile yet: the worker may still be mid-submit.
        var tooSoon = await store.FindOrphanedForReconciliationAsync(
            DateTimeOffset.UtcNow.AddMinutes(-5), ct);
        tooSoon.ShouldBeEmpty();

        // Once the row is older than the grace period it is an orphan.
        var orphans = await store.FindOrphanedForReconciliationAsync(
            DateTimeOffset.UtcNow.AddMinutes(5), ct);
        orphans.Select(o => o.Id).ShouldContain(claimed.Id);
    }

    [Fact]
    public async Task Recording_the_external_id_takes_the_operation_out_of_reconciliation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SeedAsync(postgres, operations: 1);
        var store = new OperationStore(db);

        var claimed = await store.ClaimNextAsync(ct);
        await store.RecordExternalIdAsync(Ctx(), claimed!.Id, "01f18d14-a905-1cef", ct);

        var orphans = await store.FindOrphanedForReconciliationAsync(
            DateTimeOffset.UtcNow.AddMinutes(5), ct);

        orphans.ShouldBeEmpty();
        (await store.FindAsync(Ctx(), claimed.Id, ct))!.State.ShouldBe(OperationState.Running);
    }
}
