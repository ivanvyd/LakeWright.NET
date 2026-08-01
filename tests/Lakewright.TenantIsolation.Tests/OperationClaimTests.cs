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

    /// <summary>Moves the orphan cutoff into the future, so a row ages without the test waiting.</summary>
    private static readonly TimeSpan AlreadyOrphaned = TimeSpan.FromMinutes(-5);
    private static readonly TimeSpan NotYetOrphaned = TimeSpan.FromMinutes(5);

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

        var store = new OperationStore(db, new AuditLog(db, TimeProvider.System), TimeProvider.System);
        for (var i = 0; i < operations; i++)
        {
            await store.CreateAsync(Ctx(), "auth0|alice", "analysis", clientRequestId: null, CancellationToken.None);
        }

        return db;
    }

    [Fact]
    public async Task Claiming_returns_null_when_there_is_nothing_pending()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SeedAsync(postgres, operations: 0);
        var store = new OperationStore(db, new AuditLog(db, TimeProvider.System), TimeProvider.System);

        // Act
        var claimed = await store.ClaimNextAsync(ct);

        // Assert
        claimed.ShouldBeNull();
    }

    [Fact]
    public async Task A_claimed_operation_is_not_handed_out_twice()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SeedAsync(postgres, operations: 1);
        var store = new OperationStore(db, new AuditLog(db, TimeProvider.System), TimeProvider.System);

        // Act
        var first = await store.ClaimNextAsync(ct);
        var second = await store.ClaimNextAsync(ct);

        // Assert
        first.ShouldNotBeNull();
        second.ShouldBeNull("the only operation was already claimed");
    }

    [Fact]
    public async Task Concurrent_workers_never_claim_the_same_operation()
    {
        // Arrange — ten connections race for twenty rows. A separate context per worker, because
        // sharing one would serialise on the connection and the test would exercise nothing.
        //
        // This catches the realistic bug: a select-then-update claim, verified by replacing the
        // single statement with one and watching this go red. It does NOT prove `SKIP LOCKED` is
        // required — that was measured too, and the test stays green without it, because the
        // single-statement update is atomic either way. `SKIP LOCKED` prevents the convoy, and
        // nothing here measures blocking.
        var ct = TestContext.Current.CancellationToken;
        const int Operations = 20;
        const int Workers = 10;
        await using var seed = await SeedAsync(postgres, Operations);
        var connectionString = seed.Database.GetConnectionString()!;
        var claimed = new System.Collections.Concurrent.ConcurrentBag<Guid>();

        // Act
        await Task.WhenAll(Enumerable.Range(0, Workers).Select(async _ =>
        {
            await using var db = PostgresFixture.ContextFor(connectionString);
            var store = new OperationStore(db, new AuditLog(db, TimeProvider.System), TimeProvider.System);

            while (await store.ClaimNextAsync(ct) is { } operation)
            {
                claimed.Add(operation.Id);
            }
        }));

        // Assert
        claimed.Count.ShouldBe(Operations, "every operation should be claimed exactly once");
        claimed.Distinct().Count().ShouldBe(Operations, "no operation may be claimed twice");
    }

    [Fact]
    public async Task Work_queued_by_a_tenant_that_is_since_suspended_is_never_claimed()
    {
        // Arrange — the resolver refuses a suspended organization at request time. Without the same
        // rule on the claim, work queued while the tenant was active keeps spending Databricks
        // compute after their access was cut off.
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SeedAsync(postgres, operations: 1);
        var store = new OperationStore(db, new AuditLog(db, TimeProvider.System), TimeProvider.System);
        var acme = await db.Organizations.FindAsync([AcmeId], ct);
        acme!.State = OrganizationState.Suspended;
        await db.SaveChangesAsync(ct);

        // Act
        var whileSuspended = await store.ClaimNextAsync(ct);
        acme.State = OrganizationState.Active;
        await db.SaveChangesAsync(ct);
        var afterReinstating = await store.ClaimNextAsync(ct);

        // Assert — reinstating makes the queued work runnable again rather than losing it.
        whileSuspended.ShouldBeNull();
        afterReinstating.ShouldNotBeNull();
    }

    [Fact]
    public async Task An_orphan_belonging_to_a_suspended_tenant_is_never_reconciled()
    {
        // Arrange — a suspension and a crashed worker often share a cause, so this is the case
        // where re-submitting is most likely and least wanted. The first version of the
        // reconciliation claim was missing the organization join that ClaimNextAsync has.
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SeedAsync(postgres, operations: 1);
        var store = new OperationStore(db, new AuditLog(db, TimeProvider.System), TimeProvider.System);
        await store.ClaimNextAsync(ct);
        var acme = await db.Organizations.FindAsync([AcmeId], ct);
        acme!.State = OrganizationState.Suspended;
        await db.SaveChangesAsync(ct);

        // Act
        var whileSuspended = await store.ClaimOrphanForReconciliationAsync(AlreadyOrphaned, ct);
        acme.State = OrganizationState.Active;
        await db.SaveChangesAsync(ct);
        var afterReinstating = await store.ClaimOrphanForReconciliationAsync(AlreadyOrphaned, ct);

        // Assert
        whileSuspended.ShouldBeNull();
        afterReinstating.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_worker_uses_the_stored_schema_rather_than_deriving_it()
    {
        // Arrange — Organization.Schema is stored precisely so a later change to the naming
        // convention cannot repoint existing tenants. The worker derived it anyway in the first
        // version, which would have sent every job to a schema that may belong to someone else.
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SeedAsync(postgres, operations: 0);
        var acme = await db.Organizations.FindAsync([AcmeId], ct);
        acme!.Slug = "acme_moved";
        await db.Database.ExecuteSqlAsync(
            $"UPDATE organizations SET \"Schema\" = 'legacy_schema_name' WHERE \"Id\" = {AcmeId.Value}",
            ct);
        db.ChangeTracker.Clear();
        var store = new OperationStore(db, new AuditLog(db, TimeProvider.System), TimeProvider.System);

        // Act
        var resolved = await store.ResolveClaimedTenantAsync(AcmeId, "analytics", ct);

        // Assert
        resolved.ShouldNotBeNull();
        resolved.Schema.ShouldBe("legacy_schema_name");
        resolved.Schema.ShouldNotBe(UnityCatalogIdentifier.SchemaForTenant(AcmeId));
    }

    [Fact]
    public async Task Completing_an_operation_twice_does_not_overwrite_the_first_outcome()
    {
        // Arrange — reconciliation can claim a row a slow-but-alive worker is still processing, so
        // both can reach CompleteAsync. The first to arrive should win.
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SeedAsync(postgres, operations: 1);
        var store = new OperationStore(db, new AuditLog(db, TimeProvider.System), TimeProvider.System);
        var claimed = await store.ClaimNextAsync(ct);

        // Act
        await store.CompleteAsync(AcmeId, claimed!.Id, OperationState.Succeeded, null, ct);
        await store.CompleteAsync(AcmeId, claimed.Id, OperationState.Failed, "late writer", ct);

        // Assert
        db.ChangeTracker.Clear();
        var final = await store.FindAsync(Ctx(), claimed.Id, ct);
        final!.State.ShouldBe(OperationState.Succeeded);
        final.Error.ShouldBeNull();
    }

    [Fact]
    public async Task Completing_another_tenants_operation_is_refused()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SeedAsync(postgres, operations: 1);
        var store = new OperationStore(db, new AuditLog(db, TimeProvider.System), TimeProvider.System);
        var claimed = await store.ClaimNextAsync(ct);

        // Act
        var refused = await Should.ThrowAsync<InvalidOperationException>(
            async () => await store.CompleteAsync(
                TenantId.New(), claimed!.Id, OperationState.Succeeded, null, ct));

        // Assert
        refused.Message.ShouldContain("does not belong to tenant");
        (await store.FindAsync(Ctx(), claimed!.Id, ct))!.State.ShouldBe(OperationState.Pending);
    }

    [Fact]
    public async Task An_operation_orphaned_between_submit_and_record_is_found_by_reconciliation()
    {
        // Arrange — the crash-critical window from ADR 0005: a worker dies after submitting to
        // Databricks and before writing the run id. The row is claimed, still pending, and has no
        // external id. Nothing else in the system can tell that run exists.
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SeedAsync(postgres, operations: 1);
        var store = new OperationStore(db, new AuditLog(db, TimeProvider.System), TimeProvider.System);
        var claimed = await store.ClaimNextAsync(ct);

        // Act
        var tooSoon = await store.ClaimOrphanForReconciliationAsync(NotYetOrphaned, ct);
        var onceAged = await store.ClaimOrphanForReconciliationAsync(AlreadyOrphaned, ct);

        // Assert — nothing to reconcile while the worker may still be mid-submit; taking the row
        // from a live worker is the race the grace period exists to avoid.
        claimed!.ExternalId.ShouldBeNull();
        tooSoon.ShouldBeNull();
        onceAged.ShouldNotBeNull();
        onceAged.Id.ShouldBe(claimed.Id);
    }

    [Fact]
    public async Task Claiming_an_orphan_stops_a_second_reconciler_taking_it()
    {
        // Arrange — re-stamping ClaimedAt is the claim. Without it two reconcilers would both
        // re-submit, and the second would race the first's RecordExternalIdAsync.
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SeedAsync(postgres, operations: 1);
        var store = new OperationStore(db, new AuditLog(db, TimeProvider.System), TimeProvider.System);
        await store.ClaimNextAsync(ct);

        // Act
        var first = await store.ClaimOrphanForReconciliationAsync(AlreadyOrphaned, ct);
        var second = await store.ClaimOrphanForReconciliationAsync(NotYetOrphaned, ct);

        // Assert
        first.ShouldNotBeNull();
        second.ShouldBeNull();
    }

    [Fact]
    public async Task An_abandoned_run_is_reconcilable_and_carries_its_run_id()
    {
        // Arrange — a submitted, recorded operation whose worker then stopped polling it. This
        // test asserted the opposite until 2026-08-01: that recording an external id took the row
        // out of reconciliation for good. That was the bug, not the contract. Nothing else watches
        // a Running row, so a rolling deploy mid-poll left it Running with no end.
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SeedAsync(postgres, operations: 1);
        var store = new OperationStore(db, new AuditLog(db, TimeProvider.System), TimeProvider.System);
        var claimed = await store.ClaimNextAsync(ct);
        await store.RecordExternalIdAsync(Ctx(), claimed!.Id, "994455", ct);

        // Act
        var reconcilable = await store.ClaimOrphanForReconciliationAsync(AlreadyOrphaned, ct);

        // Assert — reclaimed with the run id intact, which is what lets the worker resume the poll
        // instead of submitting the run a second time.
        reconcilable.ShouldNotBeNull();
        reconcilable.Id.ShouldBe(claimed.Id);
        reconcilable.ExternalId.ShouldBe("994455");
        reconcilable.State.ShouldBe(OperationState.Running);
    }

    [Fact]
    public async Task A_completed_operation_is_never_reconciled()
    {
        // Arrange — the stop condition for the widened claim above. Without it, every finished
        // operation would be reclaimed forever.
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SeedAsync(postgres, operations: 1);
        var store = new OperationStore(db, new AuditLog(db, TimeProvider.System), TimeProvider.System);
        var claimed = await store.ClaimNextAsync(ct);
        await store.RecordExternalIdAsync(Ctx(), claimed!.Id, "994455", ct);

        // Act
        await store.CompleteAsync(AcmeId, claimed.Id, OperationState.Succeeded, null, ct);

        // Assert
        (await store.ClaimOrphanForReconciliationAsync(AlreadyOrphaned, ct)).ShouldBeNull();
    }
}
