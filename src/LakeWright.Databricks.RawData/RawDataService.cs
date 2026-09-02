using System.ComponentModel.DataAnnotations;
using System.Globalization;
using LakeWright.Core.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LakeWright.Databricks.RawData;

/// <summary>Default implementation of <see cref="IRawDataService"/>.</summary>
public sealed class RawDataService(IStatementExecutor statements, RawDataOptions? options = null) : IRawDataService
{
    private readonly RawDataOptions _options = options ?? new RawDataOptions();

    public async Task<RawDataPage> QueryAsync(
        TenantContext tenant,
        RawDataSource source,
        RawDataRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        _options.Validate();
        source.Validate();
        if (request.Export)
        {
            throw new NotSupportedException("Raw-data export is not registered by QueryAsync; use the export pipeline.");
        }

        var builder = new RawDataStatementBuilder(source, request, _options);
        var statement = builder.Build(tenant);
        var outcome = await statements.ExecuteAsync(statement, cancellationToken).ConfigureAwait(false);
        return outcome switch
        {
            StatementOutcome.Success success => new RawDataPage(
                source.Fields.Select(field => new RawDataColumn(field.Name, field.DisplayName, field.Kind)).ToArray(),
                success.Rows,
                success.TotalRowCount),
            StatementOutcome.Failure failure => throw new RawDataWarehouseException(failure.ErrorCode),
            StatementOutcome.Pending => throw new InvalidOperationException("The raw-data statement was not configured to complete within its polling budget."),
            StatementOutcome.LargeResult => throw new InvalidOperationException("An interactive raw-data request returned an external result."),
            _ => throw new InvalidOperationException("The statement executor returned an unrecognized raw-data outcome."),
        };
    }
}

/// <summary>A warehouse failure whose detail remains server-side instead of becoming a portal response.</summary>
public sealed class RawDataWarehouseException(string errorCode)
    : InvalidOperationException($"The raw-data query was rejected by the warehouse ({errorCode}).");

internal sealed class RawDataStatementBuilder
{
    private readonly RawDataSource _source;
    private readonly RawDataRequest _request;
    private readonly RawDataOptions _options;
    private readonly Dictionary<string, RawDataField> _fields;
    private readonly List<StatementParameter> _parameters = [];

    public RawDataStatementBuilder(RawDataSource source, RawDataRequest request, RawDataOptions options)
    {
        _source = source;
        _request = request;
        _options = options;
        _fields = source.Fields.ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);
    }

    public TenantScopedStatement Build(TenantContext tenant)
    {
        ValidatePaging();
        var filters = _request.Filters ?? [];
        if (filters.Count > _options.MaximumFilters)
        {
            throw new ValidationException($"At most {_options.MaximumFilters} filters are allowed.");
        }

        var where = new List<string>(filters.Count);
        for (var filterIndex = 0; filterIndex < filters.Count; filterIndex++)
        {
            where.Add(BuildFilter(filters[filterIndex], filterIndex));
        }

        var sort = ResolveSort();
        var projection = string.Join(", ", _source.Fields.Select(Projection));
        var sql = $"SELECT {projection} FROM {_source.BaseView}";
        if (where.Count > 0)
        {
            sql += " WHERE " + string.Join(" AND ", where);
        }
        sql += $" ORDER BY {sort.Field.Column} {(sort.Direction == RawDataSortDirection.Descending ? "DESC" : "ASC")}";

        _parameters.Add(StatementParameter.Int("raw_take", _request.Take));
        _parameters.Add(StatementParameter.Int("raw_skip", _request.Skip));
        sql += " LIMIT :raw_take OFFSET :raw_skip";

        return TenantScopedStatement.Create(
            tenant,
            sql,
            new StatementOptions
            {
                Kind = "raw_data",
                Disposition = Microsoft.Azure.Databricks.Client.Models.SqlStatementDisposition.INLINE,
                InlineRowLimit = _request.Take,
            },
            [.. _parameters]);
    }

    public TenantScopedStatement BuildExport(TenantContext tenant, RawDataOptions options, bool inline)
    {
        ValidatePagingForExport();
        var filters = _request.Filters ?? [];
        if (filters.Count > _options.MaximumFilters)
        {
            throw new ValidationException($"At most {_options.MaximumFilters} filters are allowed.");
        }

        var where = new List<string>(filters.Count);
        for (var filterIndex = 0; filterIndex < filters.Count; filterIndex++)
        {
            where.Add(BuildFilter(filters[filterIndex], filterIndex));
        }

        var sort = ResolveSort();
        var sql = $"SELECT {string.Join(", ", _source.Fields.Select(Projection))} FROM {_source.BaseView}";
        if (where.Count > 0)
        {
            sql += " WHERE " + string.Join(" AND ", where);
        }
        sql += $" ORDER BY {sort.Field.Column} {(sort.Direction == RawDataSortDirection.Descending ? "DESC" : "ASC")}";
        if (inline)
        {
            _parameters.Add(StatementParameter.Int("raw_export_take", options.ExportInlineRowCap + 1));
            sql += " LIMIT :raw_export_take";
        }

        return TenantScopedStatement.Create(
            tenant,
            sql,
            new StatementOptions
            {
                Kind = inline ? "raw_data_export_inline" : "raw_data_export_external",
                Disposition = inline
                    ? Microsoft.Azure.Databricks.Client.Models.SqlStatementDisposition.INLINE
                    : Microsoft.Azure.Databricks.Client.Models.SqlStatementDisposition.EXTERNAL_LINKS,
                InlineRowLimit = options.ExportInlineRowCap + 1,
                TotalBudget = options.ExportTotalBudget,
            },
            [.. _parameters]);
    }

    private string BuildFilter(RawDataFilter filter, int filterIndex)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (!_fields.TryGetValue(filter.Field, out var field) || !field.Filterable)
        {
            throw new ValidationException($"Field '{filter.Field}' is not filterable for source '{_source.Name}'.");
        }
        ArgumentNullException.ThrowIfNull(filter.Values);
        if (filter.Values.Count == 0)
        {
            throw new ValidationException($"Filter '{filter.Field}' requires at least one value.");
        }
        if (filter.Values.Count > _options.MaximumValuesPerFilter)
        {
            throw new ValidationException($"Filter '{filter.Field}' has too many values.");
        }

        if (filter.Operator == RawDataFilterOperator.In)
        {
            var names = filter.Values.Select((value, valueIndex) => AddParameter(field, value, filterIndex, valueIndex)).ToArray();
            return $"{field.Column} IN ({string.Join(", ", names.Select(name => ":" + name))})";
        }

        if (filter.Values.Count != 1)
        {
            throw new ValidationException($"Filter '{filter.Field}' with {filter.Operator} accepts exactly one value.");
        }

        var parameter = AddParameter(field, filter.Values[0], filterIndex, 0, filter.Operator == RawDataFilterOperator.Contains);
        return filter.Operator switch
        {
            RawDataFilterOperator.Equal => $"{field.Column} = :{parameter}",
            RawDataFilterOperator.Contains when field.Kind == RawDataKind.Text => $"{field.Column} LIKE CONCAT('%', :{parameter}, '%') ESCAPE '\\'",
            RawDataFilterOperator.Contains => throw new ValidationException($"Contains is only valid for text field '{field.Name}'."),
            RawDataFilterOperator.GreaterThan => $"{field.Column} > :{parameter}",
            RawDataFilterOperator.GreaterThanOrEqual => $"{field.Column} >= :{parameter}",
            RawDataFilterOperator.LessThan => $"{field.Column} < :{parameter}",
            RawDataFilterOperator.LessThanOrEqual => $"{field.Column} <= :{parameter}",
            _ => throw new ValidationException($"Filter operator '{filter.Operator}' is not supported."),
        };
    }

    private string AddParameter(RawDataField field, string input, int filterIndex, int valueIndex, bool escapeLike = false)
    {
        ArgumentNullException.ThrowIfNull(input);
        var name = $"raw_f{filterIndex}_v{valueIndex}";
        _parameters.Add(ParseParameter(field, name, escapeLike ? EscapeLike(input) : input));
        return name;
    }

    private static StatementParameter ParseParameter(RawDataField field, string name, string value) => field.Kind switch
    {
        RawDataKind.Text => StatementParameter.String(name, value),
        RawDataKind.Number when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => StatementParameter.Double(name, number),
        RawDataKind.Date when DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) => StatementParameter.Date(name, date),
        RawDataKind.Boolean when TryBoolean(value, out var boolean) => StatementParameter.Boolean(name, boolean),
        RawDataKind.YesNo when TryBoolean(value, out var yesNo) => StatementParameter.Boolean(name, yesNo),
        _ => throw new ValidationException($"Value '{value}' is not a valid {field.Kind} for field '{field.Name}'."),
    };

    private (RawDataField Field, RawDataSortDirection Direction) ResolveSort()
    {
        var sort = _request.Sort ?? _source.DefaultOrder;
        if (!_fields.TryGetValue(sort.Field, out var field) || !field.Sortable)
        {
            throw new ValidationException($"Field '{sort.Field}' is not sortable for source '{_source.Name}'.");
        }
        return (field, sort.Direction);
    }

    private void ValidatePaging()
    {
        if (_request.Skip < 0 || _request.Skip > _options.MaximumOffset)
        {
            throw new ValidationException($"Skip must be between 0 and {_options.MaximumOffset}.");
        }
        if (_request.Take < 1 || _request.Take > _options.MaximumPageSize)
        {
            throw new ValidationException($"Take must be between 1 and {_options.MaximumPageSize}.");
        }
    }

    private void ValidatePagingForExport()
    {
        if (_request.Skip != 0 || _request.Take != 100)
        {
            throw new ValidationException("Export requests do not accept paging; narrow the filters instead.");
        }
    }

    private string Projection(RawDataField field) => _source.NeutralizeCsvFormulas && field.Kind == RawDataKind.Text
        ? $"CASE WHEN {field.Column} RLIKE '^[=+\\-@]' THEN CONCAT(chr(39), {field.Column}) ELSE {field.Column} END AS {field.Column}"
        : field.Column;

    private static bool TryBoolean(string value, out bool result)
    {
        if (bool.TryParse(value, out result)) { return true; }
        if (string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)) { result = true; return true; }
        if (string.Equals(value, "no", StringComparison.OrdinalIgnoreCase)) { result = false; return true; }
        return false;
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}

/// <summary>Registers the raw-data statement builder after the host has registered LakeWright.Databricks.</summary>
public static class RawDataServiceCollectionExtensions
{
    /// <summary>Registers a scoped <see cref="IRawDataService"/> with trusted limits supplied by the host.</summary>
    public static Microsoft.Extensions.DependencyInjection.IServiceCollection AddLakeWrightRawData(
        this Microsoft.Extensions.DependencyInjection.IServiceCollection services,
        Action<RawDataOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new RawDataOptions();
        configure?.Invoke(options);
        options.Validate();
        services.AddScoped<IRawDataService>(provider => new RawDataService(
            provider.GetRequiredService<IStatementExecutor>(), options));
        services.TryAddSingleton<IRawDataExportOwnership, MemoryRawDataExportOwnership>();
        services.AddScoped<IRawDataExportService>(provider => new RawDataExportService(
            provider.GetRequiredService<IStatementExecutor>(),
            provider.GetRequiredService<ITenantScopedExport>(),
            provider.GetRequiredService<IRawDataExportOwnership>(),
            options));
        return services;
    }
}

internal static class RawDataSqlIdentifier
{
    public static void Validate(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!(char.IsLetter(value[0]) || value[0] == '_') || value.Any(character => !(char.IsLetterOrDigit(character) || character == '_')))
        {
            throw new ValidationException($"'{value}' is not a plain SQL identifier.");
        }
    }
}
