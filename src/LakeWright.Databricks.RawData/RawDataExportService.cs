using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using LakeWright.Core.Tenancy;

namespace LakeWright.Databricks.RawData;

/// <summary>Bounded inline CSV followed by tenant-owned, external-links streaming for larger data sets.</summary>
public sealed class RawDataExportService(
    IStatementExecutor statements,
    ITenantScopedExport exports,
    IRawDataExportOwnership ownership,
    RawDataOptions? options = null) : IRawDataExportService
{
    private readonly RawDataOptions _options = options ?? new RawDataOptions();

    public async Task<RawDataExportStart> StartAsync(
        TenantContext tenant,
        string ownerKey,
        string operationId,
        RawDataSource source,
        RawDataRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        _options.Validate();
        source.Validate();

        var inlineStatement = new RawDataStatementBuilder(source, request, _options).BuildExport(tenant, _options, inline: true);
        var outcome = await statements.ExecuteAsync(inlineStatement, cancellationToken).ConfigureAwait(false);
        if (outcome is StatementOutcome.Success success && success.TotalRowCount <= _options.ExportInlineRowCap)
        {
            return new RawDataExportStart(operationId, RawDataExportMode.Inline, ToCsv(success.ColumnNames, success.Rows, source));
        }
        if (outcome is StatementOutcome.Failure failure)
        {
            throw new RawDataWarehouseException(failure.ErrorCode);
        }
        if (outcome is StatementOutcome.Pending)
        {
            throw new InvalidOperationException("The inline export did not complete within its polling budget.");
        }

        var operation = new RawDataExportOperation(operationId, tenant.TenantId, ownerKey, source, request);
        await ownership.RecordAsync(operation, cancellationToken).ConfigureAwait(false);
        return new RawDataExportStart(operationId, RawDataExportMode.ExternalLinks, null);
    }

    public async IAsyncEnumerable<string> StreamCsvAsync(
        TenantContext tenant,
        string ownerKey,
        string operationId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        var operation = await ownership.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (operation is null || operation.TenantId != tenant.TenantId || !string.Equals(operation.OwnerKey, ownerKey, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The raw-data export operation is not owned by this caller.");
        }

        operation.Source.Validate();
        var statement = new RawDataStatementBuilder(operation.Source, operation.Request, _options).BuildExport(tenant, _options, inline: false);
        await foreach (var row in exports.StreamAsync(statement, cancellationToken).ConfigureAwait(false))
        {
            if (row.Column is { } header)
            {
                yield return Csv(header.Columns);
                continue;
            }

            yield return Csv(Normalize(row.Values, operation.Source));
        }
    }

    private static IReadOnlyList<string> ToCsv(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string?>> rows, RawDataSource source) =>
        [Csv(columns), .. rows.Select(row => Csv(Normalize(row, source)))];

    private static IReadOnlyList<string?> Normalize(IReadOnlyList<string?> values, RawDataSource source) => !source.NeutralizeCsvFormulas
        ? values
        : values.Select((value, index) => value is { Length: > 0 }
            && index < source.Fields.Count
            && source.Fields[index].Kind == RawDataKind.Text
            && value[0] is '=' or '+' or '-' or '@'
                ? "'" + value
                : value).ToArray();

    private static string Csv(IEnumerable<string?> values) => string.Join(",", values.Select(value =>
        value is null ? string.Empty : $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));
}

internal sealed class MemoryRawDataExportOwnership : IRawDataExportOwnership
{
    private readonly ConcurrentDictionary<string, RawDataExportOperation> _operations = new(StringComparer.Ordinal);

    public ValueTask RecordAsync(RawDataExportOperation operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!_operations.TryAdd(operation.OperationId, operation)
            && (!_operations.TryGetValue(operation.OperationId, out var existing)
                || existing.TenantId != operation.TenantId
                || !string.Equals(existing.OwnerKey, operation.OwnerKey, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("The raw-data export operation id is already owned by another caller.");
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<RawDataExportOperation?> GetAsync(string operationId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_operations.TryGetValue(operationId, out var operation) ? operation : null);
}
