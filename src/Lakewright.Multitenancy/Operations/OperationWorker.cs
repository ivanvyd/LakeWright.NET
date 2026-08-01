using System.Globalization;
using Lakewright.Core.Tenancy;
using Lakewright.Databricks;
using Lakewright.Multitenancy.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lakewright.Multitenancy.Operations;

/// <summary>
/// Drives operations from Pending to a terminal state: claim, submit, record, poll, complete.
/// </summary>
/// <remarks>
/// The order of the first three is the whole point, per ADR 0005. Claiming before submitting means
/// two workers cannot submit the same operation. Recording the run id immediately after submitting
/// narrows, but does not close, the window in which a crash orphans a run — closing it is
/// reconciliation's job, and this class deliberately does not pretend otherwise.
/// </remarks>
public sealed partial class OperationWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<OperationWorkerOptions> options,
    IOptions<MultitenancyOptions> tenancy,
    ILogger<OperationWorker> logger,
    TimeProvider timeProvider) : BackgroundService
{
    private readonly OperationWorkerOptions _options = options.Value;
    private readonly MultitenancyOptions _tenancy = tenancy.Value;
    private readonly ILogger<OperationWorker> _logger = logger;

    [LoggerMessage(Level = LogLevel.Information, Message = "Operation {OperationId} for tenant {TenantId} submitted as run {RunId}")]
    private partial void LogSubmitted(Guid operationId, TenantId tenantId, long runId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Operation {OperationId} failed: {Reason}")]
    private partial void LogFailed(Guid operationId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconciled orphaned operation {OperationId} to run {RunId}")]
    private partial void LogReconciled(Guid operationId, long runId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Operation worker iteration failed")]
    private partial void LogIterationFailed(Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool didWork;
            try
            {
                didWork = await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failed iteration must not kill the worker: the queue would silently stop
                // draining and nothing would say so.
                LogIterationFailed(ex);
                didWork = false;
            }

            if (!didWork)
            {
                await Task.Delay(_options.IdleDelay, timeProvider, stoppingToken);
            }
        }
    }

    /// <summary>
    /// One unit of work. Returns true if anything was done, so the caller knows whether to idle.
    /// </summary>
    /// <remarks>Internal so tests can drive a single iteration instead of racing a loop.</remarks>
    internal async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        // A scope per iteration: this is a singleton and DbContext is scoped, so a captured
        // context would be shared across every operation for the lifetime of the process.
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<OperationStore>();
        var submitter = scope.ServiceProvider.GetRequiredService<IJobSubmitter>();

        if (await store.ClaimNextAsync(cancellationToken) is { } claimed)
        {
            await SubmitAndPollAsync(store, submitter, claimed, isReconciliation: false, cancellationToken);
            return true;
        }

        if (await store.ClaimOrphanForReconciliationAsync(_options.ReconciliationGracePeriod, cancellationToken) is { } orphan)
        {
            await SubmitAndPollAsync(store, submitter, orphan, isReconciliation: true, cancellationToken);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Submits an operation and polls it to a terminal state.
    /// </summary>
    /// <remarks>
    /// One method for both a first submission and a reconciliation, because the steps are
    /// identical. They were two near-copies, and the risk that fixes a review found: a change to
    /// the tenant-inactive branch or the failure handling applied to one and forgotten in the
    /// other, with nothing to catch the divergence.
    ///
    /// Reconciliation differs only in wording. Re-submitting an orphan with its original
    /// idempotency key returns the run that key already started rather than starting a second one,
    /// so the mechanics are the same call. If it fails, the operation is marked failed rather than
    /// retried forever: the deduplication window is undocumented, and a key whose run was deleted
    /// errors permanently.
    /// </remarks>
    private async Task SubmitAndPollAsync(
        OperationStore store,
        IJobSubmitter submitter,
        Operation operation,
        bool isReconciliation,
        CancellationToken cancellationToken)
    {
        // Reads the organization's stored schema rather than deriving it. See
        // OperationStore.ResolveClaimedTenantAsync for why deriving it was wrong.
        var tenant = await store.ResolveClaimedTenantAsync(
            operation.OrganizationId, _tenancy.Catalog, cancellationToken);

        if (tenant is null)
        {
            // The tenant went inactive between the claim and here. Not an error worth retrying.
            await store.CompleteAsync(
                operation.OrganizationId, operation.Id, OperationState.Cancelled,
                "The organization is no longer active.", cancellationToken);
            return;
        }

        var run = TenantScopedJobRun.Create(tenant, _options.JobId, operation.IdempotencyKey);
        var outcome = await submitter.SubmitAsync(run, cancellationToken);

        if (outcome is not RunOutcome.Submitted submitted)
        {
            var detail = outcome is RunOutcome.Failed failed ? failed.Reason : outcome.GetType().Name;
            var reason = isReconciliation ? $"Could not reconcile: {detail}" : detail;

            LogFailed(operation.Id, reason);
            await store.CompleteAsync(
                operation.OrganizationId, operation.Id, OperationState.Failed, reason, cancellationToken);
            return;
        }

        // The crash-critical write. Everything between submitting above and this returning is the
        // window reconciliation exists to close.
        await store.RecordExternalIdAsync(
            tenant,
            operation.Id,
            submitted.RunId.ToString(CultureInfo.InvariantCulture),
            cancellationToken);

        if (isReconciliation)
        {
            LogReconciled(operation.Id, submitted.RunId);
        }
        else
        {
            LogSubmitted(operation.Id, operation.OrganizationId, submitted.RunId);
        }

        await PollAsync(store, submitter, operation, submitted.RunId, cancellationToken);
    }

    private async Task PollAsync(
        OperationStore store,
        IJobSubmitter submitter,
        Operation operation,
        long runId,
        CancellationToken cancellationToken)
    {
        var interval = _options.InitialPollInterval;
        var deadline = timeProvider.GetUtcNow() + _options.RunTimeout;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (timeProvider.GetUtcNow() >= deadline)
            {
                await store.CompleteAsync(
                    operation.OrganizationId, operation.Id, OperationState.Failed,
                    "The run exceeded the configured timeout.", cancellationToken);
                return;
            }

            await Task.Delay(WithJitter(interval), timeProvider, cancellationToken);
            interval = Min(interval * 2, _options.MaxPollInterval);

            switch (await submitter.GetRunAsync(runId, cancellationToken))
            {
                case RunOutcome.Succeeded:
                    await store.CompleteAsync(operation.OrganizationId, operation.Id, OperationState.Succeeded, null, cancellationToken);
                    return;

                case RunOutcome.Cancelled:
                    await store.CompleteAsync(operation.OrganizationId, operation.Id, OperationState.Cancelled, null, cancellationToken);
                    return;

                case RunOutcome.Failed failed:
                    LogFailed(operation.Id, failed.Reason);
                    await store.CompleteAsync(operation.OrganizationId, operation.Id, OperationState.Failed, failed.Reason, cancellationToken);
                    return;

                default:
                    continue;
            }
        }
    }

    /// <summary>
    /// Spreads retries so that workers that started together do not poll in lockstep, which is how
    /// a fleet turns a rate limit into an outage.
    /// </summary>
    private static TimeSpan WithJitter(TimeSpan interval) =>
        interval + TimeSpan.FromMilliseconds(Random.Shared.Next(0, (int)interval.TotalMilliseconds / 2));

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
