using Lakewright.Core.Tenancy;
using Lakewright.Multitenancy.Model;
using Microsoft.EntityFrameworkCore;

namespace Lakewright.Multitenancy;

/// <summary>
/// The only way to reach an operation, and therefore the only way to reach a Databricks statement
/// identifier.
/// </summary>
/// <remarks>
/// Every tenant-facing method takes a tenant and filters on it. There is no lookup by external
/// identifier alone, because that is the query whose absence stops a caller polling another
/// tenant's results with an identifier from a log line.
///
/// Two methods are deliberately system-scoped and say so at their own definitions:
/// <see cref="ClaimNextAsync"/> and <see cref="ClaimOrphanForReconciliationAsync"/>. They run in
/// the worker, outside any request, and serve every tenant. Nothing else may be added to that list
/// without the same explicit note, because a blanket guarantee in this comment that two of the
/// methods do not honour is worse than no comment.
/// </remarks>
public sealed class OperationStore(LakewrightDbContext db)
{
    public async Task<Operation> CreateAsync(
        TenantContext tenant,
        string principalId,
        string kind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        var operation = new Operation
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = tenant.TenantId,
            PrincipalId = principalId,
            Kind = kind,
            State = OperationState.Pending,
            // 64 characters is the Jobs API cap. A GUID in "N" form is 32.
            IdempotencyKey = Guid.CreateVersion7().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.Operations.Add(operation);
        await db.SaveChangesAsync(cancellationToken);
        return operation;
    }

    /// <summary>
    /// Finds an operation the tenant owns, or null.
    /// </summary>
    /// <remarks>
    /// Null covers both "no such operation" and "belongs to someone else", so a caller cannot use
    /// the response to discover that an identifier is real. Surface it as 404.
    /// </remarks>
    public Task<Operation?> FindAsync(
        TenantContext tenant,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        return db.Operations
            .SingleOrDefaultAsync(
                o => o.Id == operationId && o.OrganizationId == tenant.TenantId,
                cancellationToken);
    }

    /// <summary>
    /// Records the identifier Databricks issued.
    /// </summary>
    /// <remarks>
    /// The write that must happen immediately after submission. The gap between submitting and
    /// this returning is the window in which a crash orphans a run, and it is the case the
    /// integration tests cover because no happy path can reach it.
    /// </remarks>
    public async Task RecordExternalIdAsync(
        TenantContext tenant,
        Guid operationId,
        string externalId,
        CancellationToken cancellationToken)
    {
        var operation = await FindAsync(tenant, operationId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Operation {operationId} does not belong to tenant {tenant.TenantId}.");

        operation.ExternalId = externalId;
        operation.State = OperationState.Running;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Claims one pending operation for this worker, or returns null if there is nothing to do.
    /// </summary>
    /// <remarks>
    /// Two separate properties, and it is worth not conflating them, because measuring showed the
    /// obvious description of this was wrong.
    ///
    /// <b>Exactly-once comes from the single statement.</b> The update and its subquery are one
    /// statement, so claiming is atomic. Selecting first and updating second is the version that
    /// looks correct and hands the same row to two workers under load; the concurrency test fails
    /// against that and passes against this.
    ///
    /// <b><c>SKIP LOCKED</c> buys throughput, not correctness.</b> Removing it leaves the
    /// concurrency test green: a competing worker blocks on the row lock, re-evaluates, and takes
    /// a different row rather than duplicating one. What it prevents is the convoy — ten workers
    /// queued behind one slow claim instead of moving on to other rows. Measured, not assumed:
    /// this comment previously credited it with the exactly-once guarantee, and the test passed
    /// with it deleted.
    ///
    /// A worker that dies mid-claim releases its lock on rollback and the row returns to the pool.
    ///
    /// Not tenant-scoped: the worker serves every tenant, and the tenant comes off the claimed row.
    /// It does still honour organization lifecycle. An operation queued while a tenant was active
    /// and claimed after it was suspended would keep spending Databricks compute for a tenant whose
    /// access was cut off, so the claim joins organizations and takes only active ones. The
    /// request-time resolver has the same rule; this is the asynchronous half of it.
    /// </remarks>
    public async Task<Operation?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        // Both states are interpolated, which FromSql turns into parameters rather than text.
        // A literal 0 here would silently start claiming a different state if the enum is ever
        // reordered, with nothing to catch it: the raw SQL does not see the enum at all.
        var claimed = await db.Operations
            .FromSql($"""
                UPDATE operations o
                SET "ClaimedAt" = now()
                WHERE o."Id" = (
                    SELECT c."Id" FROM operations c
                    JOIN organizations org ON org."Id" = c."OrganizationId"
                    WHERE c."State" = {(int)OperationState.Pending}
                      AND c."ClaimedAt" IS NULL
                      AND org."State" = {(int)OrganizationState.Active}
                    ORDER BY c."CreatedAt"
                    FOR UPDATE OF c SKIP LOCKED
                    LIMIT 1
                )
                RETURNING o.*
                """)
            // The caller wants the claimed row as data. Leaving it untracked keeps the raw UPDATE
            // from seeding the change tracker with a row state that later writes would compare
            // against; those writes re-read the row.
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return claimed.FirstOrDefault();
    }

    /// <summary>Marks a claimed operation as finished.</summary>
    /// <remarks>
    /// Takes the owning tenant and filters on it, even though today's only caller is the worker
    /// acting on a row it just claimed. The cost is one predicate; the alternative is a method
    /// that writes to any operation by id, which is a cross-tenant write the first time someone
    /// puts an admin endpoint in front of it.
    /// </remarks>
    public async Task CompleteAsync(
        TenantId tenantId,
        Guid operationId,
        OperationState state,
        string? error,
        CancellationToken cancellationToken)
    {
        var operation = await db.Operations
            .SingleOrDefaultAsync(
                o => o.Id == operationId && o.OrganizationId == tenantId,
                cancellationToken)
            ?? throw new InvalidOperationException(
                $"Operation {operationId} does not belong to tenant {tenantId}.");

        operation.State = state;
        operation.Error = error;
        operation.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Claims one operation that was claimed but never given an external identifier, and whose
    /// grace period has elapsed. Returns null when there is nothing to reconcile.
    /// </summary>
    /// <remarks>
    /// These are the rows left by a worker that died between submitting to Databricks and recording
    /// the run id. The run may exist; nothing local knows its id.
    ///
    /// This claims atomically rather than reading a list and writing later. A read-then-write lets
    /// reconciliation and a slow-but-alive worker both act on one row, and the later write silently
    /// undoes the earlier. Re-stamping <c>ClaimedAt</c> is the claim: it takes the row out of the
    /// orphan window for another grace period, so a second reconciler passes over it.
    ///
    /// Deliberately not tenant-scoped: reconciliation is a system job, not a tenant action, and it
    /// runs outside any request.
    /// </remarks>
    public async Task<Operation?> ClaimOrphanForReconciliationAsync(
        TimeSpan gracePeriod,
        CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow - gracePeriod;

        var claimed = await db.Operations
            .FromSql($"""
                UPDATE operations o
                SET "ClaimedAt" = now()
                WHERE o."Id" = (
                    SELECT c."Id" FROM operations c
                    WHERE c."State" = {(int)OperationState.Pending}
                      AND c."ExternalId" IS NULL
                      AND c."ClaimedAt" IS NOT NULL
                      AND c."ClaimedAt" < {cutoff}
                    ORDER BY c."ClaimedAt"
                    FOR UPDATE OF c SKIP LOCKED
                    LIMIT 1
                )
                RETURNING o.*
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return claimed.FirstOrDefault();
    }
}
