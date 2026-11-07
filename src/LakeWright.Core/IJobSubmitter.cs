using LakeWright.Core.Tenancy;

namespace LakeWright.Core.Jobs;

/// <summary>
/// Submits and tracks Lakeflow job runs for a tenant.
/// </summary>
/// <remarks>
/// <see cref="SubmitAsync"/> takes a <see cref="TenantScopedJobRun"/> and nothing else, so a caller
/// cannot submit a job without a resolved tenant.
///
/// <see cref="GetRunAsync"/> takes a run id, which this layer cannot tie to a tenant, exactly as
/// with <c>IStatementExecutor.GetAsync</c>. Ownership is enforced above by the operation
/// record, which stores the tenant and the run id together. An endpoint that polls a run id taken
/// from a request is a cross-tenant read.
/// </remarks>
public interface IJobSubmitter
{
    /// <summary>
    /// Starts a run, or returns the existing one if this idempotency key has been used before.
    /// </summary>
    /// <remarks>
    /// **This is also the reconciliation mechanism**, which is why there is no separate
    /// "find the run I already started" method. A worker that died between submitting and recording
    /// the run id left no local trace of the run, and the Jobs API does not expose the idempotency
    /// token on a run, so it cannot be searched for. Re-submitting with the same key does the job:
    /// Databricks returns the original run id rather than starting a second run.
    ///
    /// One caveat worth knowing before relying on it: the deduplication window is undocumented, and
    /// re-submitting a key whose run has since been deleted raises an error rather than starting
    /// fresh. Reconciliation therefore treats a failed re-submission as a dead operation instead of
    /// retrying forever.
    /// </remarks>
    Task<RunOutcome> SubmitAsync(TenantScopedJobRun run, CancellationToken cancellationToken);

    /// <summary>Reads the remote run state.</summary>
    /// <remarks>A failed status request throws. It does not establish that the run has stopped.</remarks>
    Task<RunOutcome> GetRunAsync(long runId, CancellationToken cancellationToken);

    /// <summary>
    /// Asks Databricks to stop a run.
    /// </summary>
    /// <remarks>
    /// A successful call requests cancellation. Cancellation is asynchronous, so callers must
    /// confirm a terminal state through <see cref="GetRunAsync"/> before releasing run ownership.
    /// A rejected request throws and leaves the run state unknown.
    /// </remarks>
    Task CancelRunAsync(long runId, CancellationToken cancellationToken);
}
