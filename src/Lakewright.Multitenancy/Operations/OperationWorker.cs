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
            await ProcessAsync(store, submitter, claimed, cancellationToken);
            return true;
        }

        if (await store.ClaimOrphanForReconciliationAsync(_options.ReconciliationGracePeriod, cancellationToken) is { } orphan)
        {
            await ReconcileAsync(store, submitter, orphan, cancellationToken);
            return true;
        }

        return false;
    }

    private async Task ProcessAsync(
        OperationStore store,
        IJobSubmitter submitter,
        Operation operation,
        CancellationToken cancellationToken)
    {
        var tenant = TenantContextFactory.ForTenant(operation.OrganizationId, _tenancy.Catalog);
        var run = TenantScopedJobRun.Create(tenant, _options.JobId, operation.IdempotencyKey);

        var outcome = await submitter.SubmitAsync(run, cancellationToken);

        if (outcome is not RunOutcome.Submitted submitted)
        {
            var reason = outcome is RunOutcome.Failed f ? f.Reason : outcome.GetType().Name;
            LogFailed(operation.Id, reason);
            await store.CompleteAsync(operation.OrganizationId, operation.Id, OperationState.Failed, reason, cancellationToken);
            return;
        }

        // The crash-critical write. Everything between submitting above and this returning is the
        // window reconciliation exists to close.
        await store.RecordExternalIdAsync(tenant, operation.Id, submitted.RunId.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
        LogSubmitted(operation.Id, operation.OrganizationId, submitted.RunId);

        await PollAsync(store, submitter, operation, submitted.RunId, cancellationToken);
    }

    /// <summary>
    /// Re-submits an orphan with its original idempotency key.
    /// </summary>
    /// <remarks>
    /// Databricks returns the run the key already started rather than starting a second one, so
    /// this both discovers the lost run id and is safe if no run was ever created. If it fails, the
    /// operation is marked failed rather than retried forever: the deduplication window is
    /// undocumented, and a key whose run was deleted errors permanently.
    /// </remarks>
    private async Task ReconcileAsync(
        OperationStore store,
        IJobSubmitter submitter,
        Operation orphan,
        CancellationToken cancellationToken)
    {
        var tenant = TenantContextFactory.ForTenant(orphan.OrganizationId, _tenancy.Catalog);
        var run = TenantScopedJobRun.Create(tenant, _options.JobId, orphan.IdempotencyKey);

        var outcome = await submitter.SubmitAsync(run, cancellationToken);

        if (outcome is not RunOutcome.Submitted submitted)
        {
            var reason = outcome is RunOutcome.Failed f
                ? $"Could not reconcile: {f.Reason}"
                : "Could not reconcile.";
            LogFailed(orphan.Id, reason);
            await store.CompleteAsync(orphan.OrganizationId, orphan.Id, OperationState.Failed, reason, cancellationToken);
            return;
        }

        await store.RecordExternalIdAsync(tenant, orphan.Id, submitted.RunId.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
        LogReconciled(orphan.Id, submitted.RunId);

        await PollAsync(store, submitter, orphan, submitted.RunId, cancellationToken);
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
