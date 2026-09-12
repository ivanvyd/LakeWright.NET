using System.Net;
using System.Runtime.CompilerServices;
using LakeWright.Core.Features;
using Microsoft.Azure.Databricks.Client;
using Microsoft.Azure.Databricks.Client.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LakeWright.Databricks;

/// <summary>Streams complete tenant-scoped JSON results through expiring external chunk links.</summary>
public sealed class DatabricksTenantScopedExport : ITenantScopedExport
{
    private readonly IDatabricksStatementSession _session;
    private readonly DatabricksOptions _options;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly StatementTerminalPoller _poller;
    private readonly ILakeWrightFeatureGate _features;

    public DatabricksTenantScopedExport(
        DatabricksClient client,
        IOptions<DatabricksOptions> options,
        HttpClient http,
        ILogger<DatabricksTenantScopedExport> logger)
        : this(new DatabricksStatementSession(client, logger), options.Value, http, logger)
    {
    }

    internal DatabricksTenantScopedExport(
        IDatabricksStatementSession session,
        DatabricksOptions options,
        HttpClient http,
        ILogger<DatabricksTenantScopedExport> logger,
        TimeProvider? time = null,
        ILakeWrightFeatureGate? features = null)
    {
        _session = session;
        _options = options;
        _http = http;
        _time = time ?? TimeProvider.System;
        _poller = new StatementTerminalPoller(session, _time);
        _features = features ?? new AlwaysOnFeatureGate();
    }

    public async IAsyncEnumerable<ExportRow> StreamAsync(
        TenantScopedStatement statement,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _features.EnsureEnabled(LakeWrightFeatures.Statements);
        ArgumentNullException.ThrowIfNull(statement.Tenant);
        var execution = statement.Options ?? _options.Statement ?? new StatementOptions { WaitTimeout = _options.WaitTimeout };
        execution.Validate();
        using var deadline = new StatementDeadline(_time, execution.TotalBudget, cancellationToken);
        await using var rows = StreamCoreAsync(statement, execution, deadline, deadline.Token).GetAsyncEnumerator(deadline.Token);
        while (await deadline.RunAsync(_ => rows.MoveNextAsync().AsTask()).ConfigureAwait(false))
        {
            yield return rows.Current;
        }
    }

    private async IAsyncEnumerable<ExportRow> StreamCoreAsync(
        TenantScopedStatement statement,
        StatementOptions execution,
        StatementDeadline deadline,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var startedAt = _time.GetUtcNow();
        using var activity = LakeWrightDatabricksTelemetry.Source.StartActivity("lakewright.statement.export");
        activity?.SetTag("statement.kind", execution.Kind);
        var request = new SqlStatement
        {
            WarehouseId = _options.WarehouseId,
            Catalog = statement.Tenant.Catalog,
            Schema = statement.Tenant.Schema,
            Statement = statement.Sql,
            Parameters = [.. statement.Parameters.Select(parameter => new SqlStatementParameter
            {
                Name = parameter.Name, Value = parameter.Value, Type = parameter.Type,
            })],
            Disposition = SqlStatementDisposition.EXTERNAL_LINKS,
            Format = StatementFormat.JSON_ARRAY,
            WaitTimeout = execution.WaitTimeout,
            OnWaitTimeout = execution.OnWaitTimeout,
        };
        var outcome = await _session.ExecuteAsync(request, statement.Tenant.TenantId, cancellationToken).ConfigureAwait(false);
        deadline.Observe(outcome);
        deadline.Check();
        if (execution.OnWaitTimeout == SqlStatementOnWaitTimeout.CONTINUE)
        {
            outcome = await _poller.PollAsync(statement.Tenant, outcome, startedAt, execution, cancellationToken).ConfigureAwait(false);
            deadline.Observe(outcome);
            deadline.Check();
        }
        LakeWrightDatabricksTelemetry.RecordStatement(outcome, execution.Kind, _time.GetUtcNow() - startedAt);
        if (outcome is StatementOutcome.Failure failure)
        {
            throw new HttpRequestException($"Databricks export failed ({failure.ErrorCode}).", null, failure.StatusCode);
        }
        // An empty JSON result can omit external_links altogether.
        if (outcome is StatementOutcome.Success { TotalRowCount: 0, Rows.Count: 0 } empty)
        {
            yield return new ExportRow(new ExportColumn(empty.ColumnNames), []);
            yield break;
        }
        if (outcome is not StatementOutcome.LargeResult result)
        {
            throw new InvalidOperationException("The export did not return a completed external result.");
        }
        var hasMetadata = result.Chunks.Count > 0;
        var page = hasMetadata ? result.Chunks : result.Links.Select((link, index) =>
            new ExternalResultChunk(index, -1, -1, link, null, null)).ToArray();
        var pageIndex = 0;
        var expectedIndex = 0;
        long totalRows = 0;
        yield return new ExportRow(new ExportColumn(result.ColumnNames), []);
        while (pageIndex < page.Count)
        {
            deadline.Check();
            var chunk = page[pageIndex];
            if (chunk.Index != expectedIndex || expectedIndex >= StatementResultReader.MaxChunks
                || (hasMetadata && (chunk.RowOffset != totalRows || chunk.RowCount < 0)))
            {
                throw Incomplete();
            }
            using var response = await OpenChunkAsync(result.StatementId, chunk, cancellationToken).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            long chunkRows = 0;
            await foreach (var row in ExternalJsonRows.ReadAsync(stream, result.ColumnNames.Count, execution.Kind, cancellationToken).ConfigureAwait(false))
            {
                deadline.Check();
                chunkRows++;
                totalRows++;
                if (totalRows > result.TotalRowCount || (hasMetadata && chunkRows > chunk.RowCount)) { throw Incomplete(); }
                yield return new ExportRow(null, row);
            }
            if (hasMetadata && chunkRows != chunk.RowCount) { throw Incomplete(); }
            expectedIndex++;
            pageIndex++;
            if (chunk.NextChunkIndex is { } next && next != expectedIndex) { throw Incomplete(); }
            if (pageIndex < page.Count) { continue; }
            if (chunk.NextChunkIndex is null) { break; }
            page = StatementResultReader.ExternalChunks(await GetChunkAsync(
                result.StatementId, expectedIndex, cancellationToken).ConfigureAwait(false));
            hasMetadata = true;
            pageIndex = 0;
            if (page.Count == 0) { throw Incomplete(); }
        }
        if (totalRows != result.TotalRowCount
            || (result.TotalChunkCount is { } chunks && expectedIndex != chunks)) { throw Incomplete(); }
    }

    private async Task<HttpResponseMessage> OpenChunkAsync(string statementId, ExternalResultChunk chunk, CancellationToken cancellationToken)
    {
        var renewed = false;
        if (chunk.ExpiresAt is { } expiration && expiration <= _time.GetUtcNow().AddSeconds(5))
        {
            chunk = await RenewChunkAsync(statementId, chunk, cancellationToken).ConfigureAwait(false);
            renewed = true;
        }
        var response = await SendChunkAsync(chunk.Link, cancellationToken).ConfigureAwait(false);
        if (!renewed && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            response.Dispose();
            chunk = await RenewChunkAsync(statementId, chunk, cancellationToken).ConfigureAwait(false);
            response = await SendChunkAsync(chunk.Link, cancellationToken).ConfigureAwait(false);
        }
        if (response.IsSuccessStatusCode) { return response; }
        var status = response.StatusCode;
        response.Dispose();
        throw new HttpRequestException("The external result chunk could not be read.", null, status);
    }

    private async Task<ExternalResultChunk> RenewChunkAsync(string statementId, ExternalResultChunk previous, CancellationToken cancellationToken)
    {
        var page = StatementResultReader.ExternalChunks(await GetChunkAsync(
            statementId, previous.Index, cancellationToken).ConfigureAwait(false));
        var renewed = page.SingleOrDefault(chunk => chunk.Index == previous.Index);
        if (renewed is null || renewed.RowCount != previous.RowCount || renewed.RowOffset != previous.RowOffset
            || renewed.NextChunkIndex != previous.NextChunkIndex
            || renewed.ExpiresAt <= _time.GetUtcNow()) { throw Incomplete(); }
        return renewed;
    }

    private async Task<StatementExecutionResultChunk> GetChunkAsync(string statementId, int index, CancellationToken cancellationToken)
    {
        try
        {
            return await _session.GetChunkAsync(statementId, index, cancellationToken).ConfigureAwait(false);
        }
        catch (ClientApiException exception)
        {
            throw new HttpRequestException("The warehouse could not return the external result chunk.", null, exception.StatusCode);
        }
    }

    private async Task<HttpResponseMessage> SendChunkAsync(Uri link, CancellationToken cancellationToken)
    {
        if (_http.DefaultRequestHeaders.Authorization is not null)
        {
            throw new InvalidOperationException("External result downloads require an HttpClient without default Authorization.");
        }
        if (!link.IsAbsoluteUri || (link.Scheme != Uri.UriSchemeHttps && !(link.IsLoopback && link.Scheme == Uri.UriSchemeHttp)))
        {
            throw new InvalidDataException("External result links must use HTTPS.");
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, link);
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            // Transport exceptions can quote the signed URL; do not forward their text or inner exception.
            throw new HttpRequestException("The external result chunk could not be reached.", null, exception.StatusCode);
        }
    }

    private static InvalidDataException Incomplete() => new("External result metadata or row counts are incomplete or inconsistent.");
}
