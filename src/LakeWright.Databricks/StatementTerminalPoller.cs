using LakeWright.Core.Tenancy;

namespace LakeWright.Databricks;

/// <summary>Waits for a pending Statement Execution API operation without exceeding its local budget.</summary>
internal sealed class StatementTerminalPoller(
    IDatabricksStatementSession session,
    TimeProvider time)
{
    public async Task<StatementOutcome> PollAsync(
        TenantContext tenant,
        StatementOutcome outcome,
        DateTimeOffset startedAt,
        StatementOptions execution,
        CancellationToken cancellationToken)
    {
        while (outcome is StatementOutcome.Pending pending)
        {
            var remaining = execution.TotalBudget - (time.GetUtcNow() - startedAt);
            if (remaining <= TimeSpan.Zero)
            {
                throw new StatementBudgetExceededException(pending.StatementId, execution.TotalBudget);
            }

            var delay = execution.PollInterval < remaining ? execution.PollInterval : remaining;
            await Task.Delay(delay, time, cancellationToken).ConfigureAwait(false);
            outcome = await session.GetAsync(tenant.TenantId, pending.StatementId, cancellationToken).ConfigureAwait(false);
        }

        return outcome;
    }
}
