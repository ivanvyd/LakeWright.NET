using System.Diagnostics;
using LakeWright.Core.Features;
using Microsoft.Azure.Databricks.Client.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LakeWright.Databricks;

/// <summary>
/// Runs statements through <c>Microsoft.Azure.Databricks.Client</c>, translating its two failure
/// modes into <see cref="StatementOutcome"/>.
/// </summary>
public sealed class DatabricksStatementExecutor : IStatementExecutor
{
    private readonly IDatabricksStatementSession _session;
    private readonly DatabricksOptions _options;
    private readonly TimeProvider _time;
    private readonly StatementTerminalPoller _poller;
    private readonly ILakeWrightFeatureGate _features;

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
        TimeProvider? time = null,
        ILakeWrightFeatureGate? features = null)
    {
        _session = session;
        _options = options;
        _time = time ?? TimeProvider.System;
        _poller = new StatementTerminalPoller(_session, _time);
        _features = features ?? new AlwaysOnFeatureGate();
    }

    public async Task<StatementOutcome> ExecuteAsync(
        TenantScopedStatement statement,
        CancellationToken cancellationToken)
    {
        _features.EnsureEnabled(LakeWrightFeatures.Statements);
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
        var request = new SqlStatement
        {
            WarehouseId = _options.WarehouseId,

            // Catalog and schema come from the tenant context, never from the caller.
            Catalog = statement.Tenant.Catalog,
            Schema = statement.Tenant.Schema,

            Statement = statement.Sql,
            Parameters = [.. statement.Parameters.Select(p => new SqlStatementParameter
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
                ? await _poller.PollAsync(statement.Tenant, outcome, startedAt, execution, cancellationToken).ConfigureAwait(false)
                : outcome;
            RecordOutcome(outcome, execution.Kind, startedAt);
            return outcome;
        }
        catch (StatementBudgetExceededException)
        {
            LakeWrightDatabricksTelemetry.RecordBudgetExceeded(
                execution.Kind,
                _time.GetUtcNow() - startedAt);
            throw;
        }
    }

    public async Task<StatementOutcome> GetAsync(
        Core.Tenancy.TenantContext tenant,
        string statementId,
        CancellationToken cancellationToken)
    {
        _features.EnsureEnabled(LakeWrightFeatures.Statements);
        ArgumentNullException.ThrowIfNull(tenant);

        return await _session.GetAsync(tenant.TenantId, statementId, cancellationToken);
    }

    public Task CancelAsync(
        Core.Tenancy.TenantContext tenant,
        string statementId,
        CancellationToken cancellationToken)
    {
        _features.EnsureEnabled(LakeWrightFeatures.Statements);
        ArgumentNullException.ThrowIfNull(tenant);
        return _session.CancelAsync(statementId, cancellationToken);
    }

    private void RecordOutcome(StatementOutcome outcome, string kind, DateTimeOffset startedAt) =>
        LakeWrightDatabricksTelemetry.RecordStatement(outcome, kind, _time.GetUtcNow() - startedAt);
}
