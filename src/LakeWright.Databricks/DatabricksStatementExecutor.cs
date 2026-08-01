using Microsoft.Azure.Databricks.Client;
using Microsoft.Azure.Databricks.Client.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LakeWright.Databricks;

/// <summary>
/// Runs statements through <c>Microsoft.Azure.Databricks.Client</c>, translating its two failure
/// modes into <see cref="StatementOutcome"/>.
/// </summary>
public sealed partial class DatabricksStatementExecutor : IStatementExecutor
{
    // These log identifiers and codes, never free text. The client's exception message is the raw
    // HTTP response body, and a Databricks error message can quote the value that caused it — a
    // rejected parameter, a malformed literal — so both are tenant data. The threat model says
    // logging never includes parameter values; an earlier version of these templates broke that.
    // The detail still reaches the caller on StatementOutcome.Failure, where it is the caller's
    // decision what to do with it.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Databricks rejected a statement request for tenant {TenantId} (HTTP {StatusCode})")]
    private partial void LogRequestRejected(Core.Tenancy.TenantId? tenantId, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Statement {StatementId} for tenant {TenantId} ended {State}: {ErrorCode}")]
    private partial void LogStatementFailed(string? statementId, Core.Tenancy.TenantId? tenantId, StatementExecutionState? state, string errorCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Statement {StatementId} returned unrecognised state {State}; treating as pending")]
    private partial void LogUnrecognisedState(string? statementId, StatementExecutionState? state);

    private readonly DatabricksClient _client;
    private readonly DatabricksOptions _options;
    private readonly ILogger<DatabricksStatementExecutor> _logger;

    public DatabricksStatementExecutor(
        DatabricksClient client,
        IOptions<DatabricksOptions> options,
        ILogger<DatabricksStatementExecutor> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<StatementOutcome> ExecuteAsync(
        TenantScopedStatement statement,
        CancellationToken cancellationToken)
    {
        // A struct always has an implicit parameterless constructor, so `default` bypasses both
        // Create factories and arrives here with a null Tenant. Without this the failure is a
        // NullReferenceException three lines down, which reads as a bug in the wrong place.
        ArgumentNullException.ThrowIfNull(statement.Tenant);

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
            Disposition = _options.Disposition,
            Format = _options.Disposition == SqlStatementDisposition.INLINE
                ? StatementFormat.JSON_ARRAY
                : StatementFormat.ARROW_STREAM,

            // INLINE hard-fails at 25 MiB and cancels the statement rather than truncating, so a
            // row limit is what keeps an interactive query from dying instead of degrading.
            RowLimit = _options.Disposition == SqlStatementDisposition.INLINE
                ? _options.InlineRowLimit
                : null,
            WaitTimeout = _options.WaitTimeout,
            OnWaitTimeout = SqlStatementOnWaitTimeout.CONTINUE
        };

        StatementExecution response;
        try
        {
            response = await _client.SQL.StatementExecution.Execute(request, cancellationToken);
        }
        catch (ClientApiException ex)
        {
            // The request itself was rejected: unknown warehouse, bad auth, malformed body.
            LogRequestRejected(statement.Tenant.TenantId, (int)ex.StatusCode);
            return new StatementOutcome.Failure("REQUEST_REJECTED", ex.Message, null, IsTransient: false);
        }

        return Translate(response, statement.Tenant.TenantId);
    }

    public async Task<StatementOutcome> GetAsync(
        Core.Tenancy.TenantContext tenant,
        string statementId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        try
        {
            var response = await _client.SQL.StatementExecution.Get(statementId, cancellationToken);
            return Translate(response, tenant.TenantId);
        }
        catch (ClientApiException ex)
        {
            return new StatementOutcome.Failure("REQUEST_REJECTED", ex.Message, statementId, IsTransient: false);
        }
    }

    public Task CancelAsync(
        Core.Tenancy.TenantContext tenant,
        string statementId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return _client.SQL.StatementExecution.Cancel(statementId, cancellationToken);
    }

    private StatementOutcome Translate(StatementExecution response, Core.Tenancy.TenantId? tenantId)
    {
        var state = response.Status?.State;

        switch (state)
        {
            case StatementExecutionState.SUCCEEDED:
                return Succeeded(response);

            case StatementExecutionState.PENDING:
            case StatementExecutionState.RUNNING:
                return new StatementOutcome.Pending(response.StatementId);

            case StatementExecutionState.FAILED:
            case StatementExecutionState.CLOSED:
            case StatementExecutionState.CANCELED:
                var error = response.Status?.Error;
                var code = error?.ErrorCode.ToString() ?? state.ToString() ?? "UNKNOWN";
                LogStatementFailed(response.StatementId, tenantId, state, code);
                return new StatementOutcome.Failure(
                    code,
                    error?.Message ?? $"Statement ended in state {state}.",
                    response.StatementId,
                    IsTransient: IsTransient(error?.ErrorCode));

            default:
                // Databricks reserves the right to add states. Treating an unrecognised one as a
                // hard failure would turn a platform addition into an outage, so it is reported
                // as still running and the caller polls. See ADR 0005.
                LogUnrecognisedState(response.StatementId, state);
                return new StatementOutcome.Pending(response.StatementId);
        }
    }

    private static StatementOutcome Succeeded(StatementExecution response)
    {
        var columns = response.Manifest?.Schema?.Columns?.Select(c => c.Name).ToArray() ?? [];
        var totalRows = response.Manifest?.TotalRowCount ?? 0;

        // EXTERNAL_LINKS leaves DataArray null and puts the rows behind presigned URLs. Reporting
        // that as a Success with an empty row list would be indistinguishable from a query that
        // genuinely matched nothing.
        var links = response.Result?.ExternalLinks?.ToArray() ?? [];
        if (links.Length > 0)
        {
            return new StatementOutcome.LargeResult(
                columns,
                [.. links.Select(l => new Uri(l.ExternalLink))],
                totalRows,
                response.StatementId);
        }

        var rows = response.Result?.DataArray?
            .Select(IReadOnlyList<string?> (r) => r.ToArray())
            .ToArray() ?? [];

        return new StatementOutcome.Success(columns, rows, totalRows, response.StatementId);
    }

    private static bool IsTransient(StatementExecutionErrorCode? code) => code switch
    {
        StatementExecutionErrorCode.TEMPORARILY_UNAVAILABLE => true,
        StatementExecutionErrorCode.WORKSPACE_TEMPORARILY_UNAVAILABLE => true,
        StatementExecutionErrorCode.SERVICE_UNDER_MAINTENANCE => true,
        StatementExecutionErrorCode.RESOURCE_EXHAUSTED => true,
        StatementExecutionErrorCode.INTERNAL_ERROR => true,
        StatementExecutionErrorCode.IO_ERROR => true,
        StatementExecutionErrorCode.ABORTED => true,
        _ => false
    };
}
