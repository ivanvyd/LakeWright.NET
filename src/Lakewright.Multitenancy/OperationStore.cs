using Lakewright.Core.Tenancy;
using Lakewright.Multitenancy.Model;
using Microsoft.EntityFrameworkCore;

namespace Lakewright.Multitenancy;

/// <summary>
/// The only way to reach an operation, and therefore the only way to reach a Databricks statement
/// identifier.
/// </summary>
/// <remarks>
/// Every method takes a <see cref="TenantContext"/> and filters on it. There is no lookup by
/// external identifier alone, because that is the query whose absence stops a caller polling
/// another tenant's results with an identifier from a log line.
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
    /// <c>FOR UPDATE SKIP LOCKED</c> is what lets several workers share a queue without
    /// coordination. A worker's claim never waits on a row another worker holds, so workers do not
    /// form a blocking convoy behind one slow item, and a worker that dies mid-claim releases its
    /// lock on rollback and the row returns to the pool.
    ///
    /// The update and the select are one statement so that claiming is atomic. Selecting first and
    /// updating second is the version of this that looks correct and hands the same row to two
    /// workers under load.
    ///
    /// Not tenant-scoped: the worker serves every tenant. The tenant comes off the claimed row.
    /// </remarks>
    public async Task<Operation?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        var claimed = await db.Operations
            .FromSql($"""
                UPDATE operations
                SET "ClaimedAt" = now()
                WHERE "Id" = (
                    SELECT "Id" FROM operations
                    WHERE "State" = 0 AND "ClaimedAt" IS NULL
                    ORDER BY "CreatedAt"
                    FOR UPDATE SKIP LOCKED
                    LIMIT 1
                )
                RETURNING *
                """)
            .ToListAsync(cancellationToken);

        return claimed.FirstOrDefault();
    }

    /// <summary>Marks a claimed operation as finished.</summary>
    public async Task CompleteAsync(
        Guid operationId,
        OperationState state,
        string? error,
        CancellationToken cancellationToken)
    {
        var operation = await db.Operations.SingleAsync(o => o.Id == operationId, cancellationToken);
        operation.State = state;
        operation.Error = error;
        operation.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Operations that were submitted but whose external identifier was never written, older than
    /// <paramref name="olderThan"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately not tenant-scoped: reconciliation is a system job, not a tenant action, and it
    /// runs outside any request. It is the one query here without a tenant filter, which is why it
    /// is named for what it is.
    /// </remarks>
    public Task<List<Operation>> FindOrphanedForReconciliationAsync(
        DateTimeOffset olderThan,
        CancellationToken cancellationToken) =>
        db.Operations
            .Where(o => o.ExternalId == null
                     && o.State == OperationState.Pending
                     && o.ClaimedAt != null
                     && o.ClaimedAt < olderThan)
            .ToListAsync(cancellationToken);
}
