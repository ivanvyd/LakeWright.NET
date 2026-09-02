using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LakeWright.Core.Tenancy;
using Microsoft.Azure.Databricks.Client;
using Microsoft.Azure.Databricks.Client.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LakeWright.Databricks;

/// <summary>
/// The default <see cref="ITenantScopedExport"/>, implemented as a streaming walk over a
/// statement's presigned external-links result.
/// </summary>
/// <remarks>
/// <para>
/// The export asks the warehouse for an <c>EXTERNAL_LINKS</c> disposition regardless of the
/// configured default, so its memory profile is the same whether the host's interactive
/// queries are tuned for <c>INLINE</c> or for external links. The warehouse puts one chunk
/// per file in its own storage and hands back presigned URLs; the export walks them in
/// order and yields each chunk's rows.
/// </para>
/// <para>
/// The presigned URLs do not accept an <c>Authorization</c> header (the warehouse signs the
/// request as Azure blob SAS, and Azure rejects requests that carry both a SAS and an
/// Authorization header with HTTP 400). The fetch uses a plain <see cref="HttpClient"/>.
/// See the executor's <see cref="StatementOutcome.LargeResult"/> doc comment for the
/// constraint.
/// </para>
/// </remarks>
public sealed partial class DatabricksTenantScopedExport : ITenantScopedExport
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Tenant-scoped export failed: tenant {TenantId}, statement {StatementId}, code {ErrorCode}")]
    private partial void LogStatementFailed(TenantId? tenantId, string? statementId, string errorCode);

    private readonly IDatabricksStatementSession _session;
    private readonly DatabricksOptions _options;
    private readonly HttpClient _http;
    private readonly ILogger<DatabricksTenantScopedExport> _logger;

    public DatabricksTenantScopedExport(
        DatabricksClient client,
        IOptions<DatabricksOptions> options,
        HttpClient http,
        ILogger<DatabricksTenantScopedExport> logger)
    {
        _session = new DatabricksStatementSession(client, logger);
        _options = options.Value;
        _http = http;
        _logger = logger;
    }

    internal DatabricksTenantScopedExport(
        IDatabricksStatementSession session,
        DatabricksOptions options,
        HttpClient http,
        ILogger<DatabricksTenantScopedExport> logger)
    {
        _session = session;
        _options = options;
        _http = http;
        _logger = logger;
    }

    public async IAsyncEnumerable<ExportRow> StreamAsync(
        TenantScopedStatement statement,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statement.Tenant);

        var request = new SqlStatement
        {
            WarehouseId = _options.WarehouseId,
            Catalog = statement.Tenant.Catalog,
            Schema = statement.Tenant.Schema,
            Statement = statement.Sql,
            Parameters = [.. statement.ParametersForExecution().Select(p => new SqlStatementParameter
            {
                Name = p.Name,
                Value = p.Value,
                Type = p.Type
            })],
            // EXTERNAL_LINKS is the only disposition whose rows can be walked without
            // first materialising them. INLINE caps at 25 MiB and either returns or
            // cancels, neither of which a streaming export can use. JSON_ARRAY (rather
            // than ARROW_STREAM) is chosen so the chunk-fetch side does not need an
            // Apache Arrow dependency; the warehouse's JSON_ARRAY shape is the same
            // { "data_array": [[...]] } envelope as its INLINE response.
            Disposition = SqlStatementDisposition.EXTERNAL_LINKS,
            Format = StatementFormat.JSON_ARRAY,
            WaitTimeout = _options.WaitTimeout,
            OnWaitTimeout = SqlStatementOnWaitTimeout.CONTINUE
        };

        var outcome = await _session.ExecuteAsync(
            request,
            statement.Tenant.TenantId,
            cancellationToken).ConfigureAwait(false);

        if (outcome is StatementOutcome.Failure failure)
        {
            LogStatementFailed(statement.Tenant.TenantId, failure.StatementId, failure.ErrorCode);
            throw new HttpRequestException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Databricks rejected or failed the export (code {failure.ErrorCode})."),
                inner: null,
                statusCode: failure.StatusCode);
        }

        if (outcome is StatementOutcome.Pending)
        {
            throw new InvalidOperationException(
                "Databricks returned a still-running statement; the export is not a polling surface. " +
                "Use IStatementExecutor.ExecuteAsync and poll the returned statement id, then call " +
                "ITenantScopedExport.StreamAsync with a shorter statement or longer WaitTimeout.");
        }

        if (outcome is not StatementOutcome.LargeResult result)
        {
            throw new InvalidOperationException(
                "Databricks export did not return an external-links result.");
        }

        var columnNames = result.ColumnNames.ToArray();

        if (columnNames.Length == 0)
        {
            yield break;
        }

        // The header is the first item in the stream. A caller that writes CSV can use
        // the header to write its column-name row, then write the values.
        yield return new ExportRow(new ExportColumn(columnNames), Array.Empty<string?>());

        foreach (var link in result.Links)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await foreach (var row in FetchChunkAsync(link, columnNames, cancellationToken).ConfigureAwait(false))
            {
                yield return row;
            }
        }
    }

    private async IAsyncEnumerable<ExportRow> FetchChunkAsync(
        Uri chunkUrl,
        string[] columnNames,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Presigned SAS URLs do not accept an Authorization header; the chunk is a
        // public blob scoped by signature. See remarks on the class.
        using var request = new HttpRequestMessage(HttpMethod.Get, chunkUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Databricks chunk fetch answered {(int)response.StatusCode} {response.ReasonPhrase}: {body}"),
                inner: null,
                statusCode: response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("data_array", out var dataArray)
            || dataArray.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var entry in dataArray.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var values = new string?[columnNames.Length];
            var i = 0;
            foreach (var cell in entry.EnumerateArray())
            {
                if (i >= values.Length)
                {
                    break;
                }
                values[i++] = cell.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.String => cell.GetString(),
                    JsonValueKind.Number => cell.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => cell.GetRawText(),
                };
            }
            yield return new ExportRow(null, values);
        }
    }
}
