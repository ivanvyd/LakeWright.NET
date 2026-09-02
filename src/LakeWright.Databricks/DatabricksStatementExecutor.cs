using Microsoft.Azure.Databricks.Client.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace LakeWright.Databricks;

/// <summary>
/// Runs statements through <c>Microsoft.Azure.Databricks.Client</c>, translating its two failure
/// modes into <see cref="StatementOutcome"/>.
/// </summary>
public sealed class DatabricksStatementExecutor : IStatementExecutor
{
    private readonly IDatabricksStatementSession _session;
    private readonly DatabricksOptions _options;
    private readonly ITenantScopeStrategyResolver _scopeStrategies;
    private readonly TimeProvider _time;

    public DatabricksStatementExecutor(
        Microsoft.Azure.Databricks.Client.DatabricksClient client,
        IOptions<DatabricksOptions> options,
        ILogger<DatabricksStatementExecutor> logger)
        : this(new DatabricksStatementSession(client, logger), options.Value)
    {
    }

    internal DatabricksStatementExecutor(
        IDatabricksStatementSession session,
        DatabricksOptions options,
        ITenantScopeStrategyResolver? scopeStrategies = null,
        TimeProvider? time = null)
    {
        _session = session;
        _options = options;
        _scopeStrategies = scopeStrategies ?? new DefaultTenantScopeStrategyResolver();
        _time = time ?? TimeProvider.System;
    }

    public async Task<StatementOutcome> ExecuteAsync(
        TenantScopedStatement statement,
        CancellationToken cancellationToken)
    {
        // A struct always has an implicit parameterless constructor, so `default` bypasses both
        // Create factories and arrives here with a null Tenant. Without this the failure is a
        // NullReferenceException three lines down, which reads as a bug in the wrong place.
        ArgumentNullException.ThrowIfNull(statement.Tenant);

        var execution = statement.Options ?? _options.Statement ?? new StatementOptions
        {
            WaitTimeout = _options.WaitTimeout,
            Disposition = _options.Disposition,
            InlineRowLimit = _options.InlineRowLimit,
        };
        execution.Validate();
        var startedAt = _time.GetUtcNow();
        var scoped = statement.Tenant.Location is Core.Tenancy.TenantLocation.SharedSchema
            ? statement.ScopedForExecution(_scopeStrategies.Resolve(statement.Tenant))
            : new ScopedStatementForExecution(statement.Sql, statement.Parameters);
        var request = new SqlStatement
        {
            WarehouseId = _options.WarehouseId,

            // Catalog and schema come from the tenant context, never from the caller.
            Catalog = statement.Tenant.Catalog,
            Schema = statement.Tenant.Schema,

            Statement = scoped.Sql,
            Parameters = [.. scoped.Parameters.Select(p => new SqlStatementParameter
            {
                Name = p.Name,
                Value = p.Value,
                Type = p.Type
            })],

            // INLINE returns rows in the response; EXTERNAL_LINKS returns presigned URLs and
            // leaves DataArray null. Getting this pair wrong is how the first version returned
            // zero rows for every successful query, so disposition and format move together.
            Disposition = execution.Disposition,
            Format = execution.Disposition == SqlStatementDisposition.INLINE
                ? StatementFormat.JSON_ARRAY
                : StatementFormat.ARROW_STREAM,

            // INLINE hard-fails at 25 MiB and cancels the statement rather than truncating, so a
            // row limit is what keeps an interactive query from dying instead of degrading.
            RowLimit = execution.Disposition == SqlStatementDisposition.INLINE
                ? execution.InlineRowLimit
                : null,
            WaitTimeout = execution.WaitTimeout,
            OnWaitTimeout = execution.OnWaitTimeout
        };

        using var activity = LakeWrightDatabricksTelemetry.Source.StartActivity("lakewright.statement.execute");
        activity?.SetTag("statement.kind", execution.Kind);
        try
        {
            var outcome = await _session.ExecuteAsync(request, statement.Tenant.TenantId, cancellationToken);
            outcome = execution.OnWaitTimeout == SqlStatementOnWaitTimeout.CONTINUE
                ? await PollToTerminalAsync(statement.Tenant, outcome, startedAt, execution, cancellationToken).ConfigureAwait(false)
                : outcome;
            RecordOutcome(outcome, execution.Kind, startedAt);
            return outcome;
        }
        catch (StatementBudgetExceededException)
        {
            LakeWrightDatabricksTelemetry.StatementDuration.Record(
                (_time.GetUtcNow() - startedAt).TotalSeconds,
                new TagList { { "statement.kind", execution.Kind } });
            LakeWrightDatabricksTelemetry.StatementOutcomes.Add(1, new TagList
            {
                { "state", "budget_exceeded" },
                { "statement.kind", execution.Kind },
            });
            throw;
        }
    }

    public async Task<StatementOutcome> GetAsync(
        Core.Tenancy.TenantContext tenant,
        string statementId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        return await _session.GetAsync(tenant.TenantId, statementId, cancellationToken);
    }

    public Task CancelAsync(
        Core.Tenancy.TenantContext tenant,
        string statementId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return _session.CancelAsync(statementId, cancellationToken);
    }

    private async Task<StatementOutcome> PollToTerminalAsync(
        Core.Tenancy.TenantContext tenant,
        StatementOutcome outcome,
        DateTimeOffset startedAt,
        StatementOptions execution,
        CancellationToken cancellationToken)
    {
        while (outcome is StatementOutcome.Pending pending)
        {
            var remaining = execution.TotalBudget - (_time.GetUtcNow() - startedAt);
            if (remaining <= TimeSpan.Zero)
            {
                throw new StatementBudgetExceededException(pending.StatementId, execution.TotalBudget);
            }

            var delay = execution.PollInterval < remaining ? execution.PollInterval : remaining;
            await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
            outcome = await _session.GetAsync(tenant.TenantId, pending.StatementId, cancellationToken).ConfigureAwait(false);
        }

        return outcome;
    }

    private void RecordOutcome(StatementOutcome outcome, string kind, DateTimeOffset startedAt)
    {
        LakeWrightDatabricksTelemetry.StatementDuration.Record(
            (_time.GetUtcNow() - startedAt).TotalSeconds,
            new TagList { { "statement.kind", kind } });
        LakeWrightDatabricksTelemetry.StatementOutcomes.Add(1, new TagList
        {
            { "state", outcome switch
                {
                    StatementOutcome.Success => "succeeded",
                    StatementOutcome.LargeResult => "succeeded",
                    StatementOutcome.Failure => "failed",
                    StatementOutcome.Pending => "pending",
                    _ => "unknown",
                }
            },
            { "statement.kind", kind },
        });
    }
}
