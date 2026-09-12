using LakeWright.Core.Tenancy;
using Microsoft.Azure.Databricks.Client;
using Microsoft.Azure.Databricks.Client.Models;
using Microsoft.Extensions.Logging;

namespace LakeWright.Databricks;

internal interface IDatabricksStatementSession
{
    Task<StatementOutcome> ExecuteAsync(
        SqlStatement request,
        TenantId tenantId,
        CancellationToken cancellationToken);

    Task<StatementOutcome> GetAsync(
        TenantId tenantId,
        string statementId,
        CancellationToken cancellationToken);

    Task CancelAsync(string statementId, CancellationToken cancellationToken);

    Task<StatementExecutionResultChunk> GetChunkAsync(string statementId, int chunkIndex, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This statement session does not support result continuation.");
}

/// <summary>
/// The shared Statement Execution API lifecycle used by tenant queries and privileged system
/// billing reads.
/// </summary>
internal sealed partial class DatabricksStatementSession(
    DatabricksClient client,
    ILogger logger) : IDatabricksStatementSession
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Databricks rejected a statement request for tenant {TenantId} (HTTP {StatusCode})")]
    private static partial void LogRequestRejected(ILogger logger, TenantId tenantId, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Statement {StatementId} for tenant {TenantId} ended {State}: {ErrorCode}")]
    private static partial void LogStatementFailed(ILogger logger, string? statementId, TenantId tenantId, StatementExecutionState? state, string errorCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Statement {StatementId} returned unrecognised state {State}; treating as pending")]
    private static partial void LogUnrecognisedState(ILogger logger, string? statementId, StatementExecutionState? state);

    public async Task<StatementOutcome> ExecuteAsync(
        SqlStatement request,
        TenantId tenantId,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.SQL.StatementExecution.Execute(request, cancellationToken);
            return Translate(response, tenantId);
        }
        catch (ClientApiException ex)
        {
            LogRequestRejected(logger, tenantId, (int)ex.StatusCode);
            return new StatementOutcome.Failure("REQUEST_REJECTED", ex.Message, null, IsTransient: false)
            {
                StatusCode = ex.StatusCode
            };
        }
    }

    public async Task<StatementOutcome> GetAsync(
        TenantId tenantId,
        string statementId,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.SQL.StatementExecution.Get(statementId, cancellationToken);
            return Translate(response, tenantId);
        }
        catch (ClientApiException ex)
        {
            return new StatementOutcome.Failure(
                "REQUEST_REJECTED", ex.Message, statementId, IsTransient: false)
            {
                StatusCode = ex.StatusCode
            };
        }
    }

    public Task CancelAsync(string statementId, CancellationToken cancellationToken) =>
        client.SQL.StatementExecution.Cancel(statementId, cancellationToken);

    public Task<StatementExecutionResultChunk> GetChunkAsync(string statementId, int chunkIndex, CancellationToken cancellationToken) =>
        client.SQL.StatementExecution.GetResultChunk(statementId, chunkIndex, cancellationToken);

    private StatementOutcome Translate(StatementExecution response, TenantId tenantId)
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
                LogStatementFailed(logger, response.StatementId, tenantId, state, code);
                return new StatementOutcome.Failure(
                    code,
                    error?.Message ?? $"Statement ended in state {state}.",
                    response.StatementId,
                    IsTransient(error?.ErrorCode));

            default:
                LogUnrecognisedState(logger, response.StatementId, state);
                return new StatementOutcome.Pending(response.StatementId);
        }
    }

    private static StatementOutcome Succeeded(StatementExecution response)
    {
        if (response.Manifest?.Truncated == true)
        {
            return new StatementOutcome.Failure("RESULT_TRUNCATED", "The warehouse truncated the result.", response.StatementId, false)
            {
                IsTruncated = true,
            };
        }
        var columns = response.Manifest?.Schema?.Columns?.Select(c => c.Name).ToArray() ?? [];
        var totalRows = response.Manifest?.TotalRowCount ?? 0;
        if (totalRows < 0 || response.Manifest?.TotalChunkCount < 0
            || ((totalRows > 0 || response.Manifest?.TotalChunkCount > 0) && response.Result is null))
        {
            return new StatementOutcome.Failure("RESULT_INCOMPLETE", "The warehouse omitted the result data.", response.StatementId, false);
        }
        var links = response.Result?.ExternalLinks?.ToArray() ?? [];
        var totalChunks = response.Manifest?.TotalChunkCount is > 0 ? response.Manifest.TotalChunkCount : (int?)null;
        if (links.Length > 0 || response.Manifest?.Format is StatementFormat.ARROW_STREAM or StatementFormat.CSV)
        {
            var chunks = response.Result is { } result ? StatementResultReader.ExternalChunks(result) : [];
            return new StatementOutcome.LargeResult(
                columns,
                [.. chunks.Select(chunk => chunk.Link)],
                totalRows,
                response.StatementId)
            {
                Chunks = chunks,
                TotalChunkCount = totalChunks,
            };
        }

        // The completion reader validates and collects the chunk rows before either public
        // executor or billing consumer sees them; avoid retaining a second copy here.
        return new StatementOutcome.Success(columns, [], totalRows, response.StatementId)
        {
            FirstChunk = response.Result,
            TotalChunkCount = totalChunks,
        };
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
