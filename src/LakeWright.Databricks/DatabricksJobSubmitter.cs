using LakeWright.Core.Jobs;
using Microsoft.Azure.Databricks.Client;
using Microsoft.Azure.Databricks.Client.Models;
using Microsoft.Extensions.Logging;

namespace LakeWright.Databricks;

/// <summary>
/// Submits Lakeflow job runs through <c>Microsoft.Azure.Databricks.Client</c>.
/// </summary>
public sealed partial class DatabricksJobSubmitter(
    DatabricksClient client,
    ILogger<DatabricksJobSubmitter> logger) : IJobSubmitter
{
    private readonly ILogger<DatabricksJobSubmitter> _logger = logger;

    // Identifiers and a status code only. The client's exception message is the raw HTTP
    // response body, which can quote tenant-supplied values. See the note in
    // DatabricksStatementExecutor.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Databricks rejected a job submission for tenant {TenantId}, job {JobId} (HTTP {StatusCode})")]
    private partial void LogSubmitRejected(Core.Tenancy.TenantId tenantId, long jobId, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cancelling run {RunId} was rejected with {StatusCode}")]
    private partial void LogCancelRejected(long runId, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId} reported unrecognised lifecycle state {State}; treating as running")]
    private partial void LogUnrecognisedState(long runId, RunStatusState state);

    public async Task<RunOutcome> SubmitAsync(TenantScopedJobRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run.Tenant);

        try
        {
            var runId = await client.Jobs.RunNow(
                run.JobId,
                RunParameters.CreateJobParams(new Dictionary<string, string>(run.Parameters, StringComparer.Ordinal)),
                run.IdempotencyKey,
                queueSettings: null,
                cancellationToken);

            return new RunOutcome.Submitted(runId);
        }
        catch (ClientApiException ex)
        {
            LogSubmitRejected(run.Tenant.TenantId, run.JobId, (int)ex.StatusCode);

            // No run id: either nothing started, or something started and we cannot learn its id.
            // Reconciliation resolves the difference by re-submitting the same idempotency key.
            return new RunOutcome.Failed(null, ex.Message, IsTransient: false);
        }
    }

    public async Task CancelRunAsync(long runId, CancellationToken cancellationToken)
    {
        try
        {
            await client.Jobs.RunsCancel(runId, cancellationToken);
        }
        catch (ClientApiException e)
        {
            // The caller has already given up on this run; a cancel that fails because the run is
            // already terminal, or gone, changes nothing it would do differently. Logged rather
            // than thrown so the abandonment still completes.
            LogCancelRejected(runId, (int)e.StatusCode);
        }
    }

    public async Task<RunOutcome> GetRunAsync(long runId, CancellationToken cancellationToken)
    {
        try
        {
            // RunsGet returns a tuple of the run and its repair history; the history is only
            // populated when includeHistory is set, which it is not.
            var (run, _) = await client.Jobs.RunsGet(runId, includeHistory: false, includeResolvedValues: false, cancellationToken);
            return Translate(runId, run.Status);
        }
        catch (ClientApiException ex)
        {
            return new RunOutcome.Failed(runId, ex.Message, IsTransient: false);
        }
    }

    private RunOutcome Translate(long runId, RunStatus? status)
    {
        if (status is null) { return new RunOutcome.Running(runId); }

        switch (status.State)
        {
            case RunStatusState.BLOCKED:
            case RunStatusState.PENDING:
            case RunStatusState.QUEUED:
            case RunStatusState.RUNNING:
            case RunStatusState.TERMINATING:
                return new RunOutcome.Running(runId);

            case RunStatusState.TERMINATED:
                return FromTermination(runId, status.TerminationDetails);

            default:
                // Databricks documents run states as extensible. Treating one they added last week
                // as a failure turns their release into our outage, so an unrecognised state is
                // reported as still running and the caller keeps polling. See ADR 0005.
                LogUnrecognisedState(runId, status.State);
                return new RunOutcome.Running(runId);
        }
    }

    private static RunOutcome FromTermination(long runId, TerminationDetails? details)
    {
        // A terminated run with no detail is not something to guess about. Reporting it as still
        // running means the caller polls again and gets a real answer, rather than us inventing one.
        if (details is null) { return new RunOutcome.Running(runId); }

        return details.Code switch
        {
            RunTerminationCode.SUCCESS => new RunOutcome.Succeeded(runId),

            // Some tasks failed and the run still completed. Reporting success hides it; reporting
            // failure at least surfaces it with the platform's own wording rather than a verdict
            // of ours.
            RunTerminationCode.SUCCESS_WITH_FAILURES => Failure(
                runId, details, "The run completed with task failures."),

            RunTerminationCode.USER_CANCELED or RunTerminationCode.CANCELED =>
                new RunOutcome.Cancelled(runId),

            RunTerminationCode.SKIPPED => Failure(runId, details, "The run was skipped."),

            _ => Failure(runId, details, details.Code.ToString())
        };
    }

    private static RunOutcome.Failed Failure(long runId, TerminationDetails details, string fallback) =>
        new(runId,
            string.IsNullOrWhiteSpace(details.Message) ? fallback : details.Message,
            // Type is the platform's own transient-versus-permanent signal, which is a better
            // source than a hand-maintained list of the twenty-odd termination codes.
            IsTransient: details.Type is RunTerminationType.INTERNAL_ERROR or RunTerminationType.CLOUD_FAILURE);
}
