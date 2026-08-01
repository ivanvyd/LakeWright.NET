using System.Text.Json;
using Lakewright.Core.Tenancy;
using Lakewright.Multitenancy.Model;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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
public sealed class OperationStore(LakewrightDbContext db, AuditLog audit, TimeProvider time)
{
    /// <summary>The <c>Idempotency-Key</c> length a caller may send.</summary>
    public const int MaxClientRequestIdLength = 200;

    /// <summary>
    /// Starts an operation, or returns the one this caller already started under the same
    /// <paramref name="clientRequestId"/>.
    /// </summary>
    /// <remarks>
    /// Replay matters more here than in a typical API: a duplicate operation is a duplicate
    /// Databricks run, which the tenant is billed for and which no amount of retry-safety in the
    /// worker prevents, because the second POST is a genuinely different operation as far as the
    /// worker can tell.
    ///
    /// The unique index is what enforces this, not the lookup below. Two retries arriving together
    /// both find nothing, both insert, and one loses on the constraint — which is the path that
    /// returns the winner.
    ///
    /// A returned operation whose <see cref="Operation.Kind"/> differs from
    /// <paramref name="kind"/> is a key the caller reused for different content: an insert always
    /// carries the requested kind, so only a replay can disagree.
    /// </remarks>
    public async Task<Operation> CreateAsync(
        TenantContext tenant,
        string principalId,
        string kind,
        string? clientRequestId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        if (clientRequestId is not null
            && await FindByClientRequestIdAsync(tenant, principalId, clientRequestId, cancellationToken)
                is { } existing)
        {
            return existing;
        }

        var operation = new Operation
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = tenant.TenantId,
            PrincipalId = principalId,
            Kind = kind,
            State = OperationState.Pending,
            // 64 characters is the Jobs API cap. A GUID in "N" form is 32.
            IdempotencyKey = Guid.CreateVersion7().ToString("N"),
            ClientRequestId = clientRequestId,
            CreatedAt = time.GetUtcNow()
        };

        db.Operations.Add(operation);
        audit.Record(
            tenant.TenantId, principalId, AuditActions.OperationStarted,
            resourceType: nameof(Operation), resourceId: operation.Id.ToString());

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (clientRequestId is not null && IsUniqueViolation(e))
        {
            // Detaching the operation alone would leave its audit row queued, and the next
            // SaveChanges on this context would record a start that never happened.
            db.ChangeTracker.Clear();

            var winner = await FindByClientRequestIdAsync(
                tenant, principalId, clientRequestId, cancellationToken);

            // A unique violation with no matching row means some other constraint failed, and
            // reporting that as a replay would hide it.
            if (winner is null) { throw; }

            return winner;
        }

        return operation;
    }

    private Task<Operation?> FindByClientRequestIdAsync(
        TenantContext tenant,
        string principalId,
        string clientRequestId,
        CancellationToken cancellationToken) =>
        db.Operations.SingleOrDefaultAsync(
            o => o.OrganizationId == tenant.TenantId
                && o.PrincipalId == principalId
                && o.ClientRequestId == clientRequestId,
            cancellationToken);

    private static bool IsUniqueViolation(DbUpdateException e) =>
        e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

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

    /// <summary>
    /// Builds the tenant context for an operation the worker has already claimed, reading the
    /// organization's stored schema.
    /// </summary>
    /// <remarks>
    /// System-scoped, like the two claim methods: the worker acts for every tenant and has no
    /// request principal to check membership against. Possession of a claimed row is the
    /// authorisation, and the row's tenant came from a resolved context at creation time.
    ///
    /// It reads <see cref="Organization.Schema"/> rather than deriving it from the tenant id. The
    /// worker did derive it in the first version, which quietly defeated the reason that column
    /// exists: a tenant whose stored schema ever diverges from the naming convention would have had
    /// every background job pointed at the wrong schema, and schema names are globally unique, so
    /// the wrong schema may well belong to another tenant.
    /// </remarks>
    public async Task<TenantContext?> ResolveClaimedTenantAsync(
        TenantId tenantId,
        string catalog,
        CancellationToken cancellationToken)
    {
        var schema = await db.Organizations
            .Where(o => o.Id == tenantId && o.State == OrganizationState.Active)
            .Select(o => o.Schema)
            .SingleOrDefaultAsync(cancellationToken);

        return schema is null ? null : TenantContextFactory.ForTenant(tenantId, catalog, schema);
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
        // Guarded on the current state so a second completer is a no-op rather than a blind
        // overwrite. Reconciliation can claim a row a slow-but-alive worker is still processing, in
        // which case both reach here; the first one to arrive wins and the second changes nothing.
        // ExecuteUpdate and SaveChanges are separate statements, so an explicit transaction is what
        // makes the audit row and the completion land together. Without it, a crash between them
        // leaves an audit trail that disagrees with the operation it describes.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var updated = await db.Operations
            .Where(o => o.Id == operationId
                     && o.OrganizationId == tenantId
                     && o.CompletedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(o => o.State, state)
                      .SetProperty(o => o.Error, error)
                      .SetProperty(o => o.CompletedAt, time.GetUtcNow()),
                cancellationToken);

        if (updated == 0)
        {
            if (!await db.Operations
                    .AnyAsync(o => o.Id == operationId && o.OrganizationId == tenantId, cancellationToken))
            {
                throw new InvalidOperationException(
                    $"Operation {operationId} does not belong to tenant {tenantId}.");
            }

            // Someone else completed it first. A second audit row would show the operation
            // finishing twice.
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        audit.Record(
            tenantId, SystemPrincipal.Worker, AuditActions.OperationCompleted,
            resourceType: nameof(Operation), resourceId: operationId.ToString(),
            detail: JsonSerializer.Serialize(new { state = state.ToString() }));

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Claims one operation that a worker stopped watching, whose grace period has elapsed.
    /// Returns null when there is nothing to reconcile.
    /// </summary>
    /// <remarks>
    /// Two shapes of abandoned row, and the caller tells them apart by whether
    /// <see cref="Operation.ExternalId"/> is set.
    ///
    /// Without one, the worker died between submitting to Databricks and recording the run id. The
    /// run may exist; nothing local knows its id, so it is re-submitted under the original
    /// idempotency key.
    ///
    /// With one, the run is known and the worker simply stopped polling it — which an ordinary
    /// rolling deploy causes, because <c>PollAsync</c> exits on the shutdown token and nothing
    /// resumes it. That case was missed at first: reconciliation required
    /// <c>ExternalId IS NULL</c>, so a redeploy while any operation was mid-poll left it
    /// <see cref="OperationState.Running"/> forever, reported to the tenant as still in progress
    /// with no error and no end.
    ///
    /// This claims atomically rather than reading a list and writing later. A read-then-write lets
    /// reconciliation and a slow-but-alive worker both act on one row, and the later write silently
    /// undoes the earlier. Re-stamping <c>ClaimedAt</c> is the claim: it takes the row out of the
    /// orphan window for another grace period, so a second reconciler passes over it.
    ///
    /// Honours organization lifecycle for the same reason <see cref="ClaimNextAsync"/> does. This
    /// join was missing in the first version, so reconciliation would happily re-submit a job for a
    /// tenant suspended while its worker was down — the one case where re-submission is most likely,
    /// because a suspension and a crashed worker often share a cause.
    ///
    /// Deliberately not tenant-scoped: reconciliation is a system job, not a tenant action, and it
    /// runs outside any request.
    /// </remarks>
    public async Task<Operation?> ClaimOrphanForReconciliationAsync(
        TimeSpan gracePeriod,
        CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow() - gracePeriod;

        var claimed = await db.Operations
            .FromSql($"""
                UPDATE operations o
                SET "ClaimedAt" = now()
                WHERE o."Id" = (
                    SELECT c."Id" FROM operations c
                    JOIN organizations org ON org."Id" = c."OrganizationId"
                    WHERE c."State" IN ({(int)OperationState.Pending}, {(int)OperationState.Running})
                      AND c."CompletedAt" IS NULL
                      AND c."ClaimedAt" IS NOT NULL
                      AND c."ClaimedAt" < {cutoff}
                      AND org."State" = {(int)OrganizationState.Active}
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

