using System.ComponentModel.DataAnnotations;

namespace LakeWright.Databricks.RawData;

/// <summary>One application-defined raw-data source backed by a view in the resolved tenant schema.</summary>
public sealed class RawDataSource
{
    /// <summary>Stable application name for this source.</summary>
    public required string Name { get; init; }

    /// <summary>Plain view identifier in the resolved tenant schema; never supplied by a request.</summary>
    public required string BaseView { get; init; }

    /// <summary>Columns this source permits a client to see, filter, or sort.</summary>
    public required IReadOnlyList<RawDataField> Fields { get; init; }

    /// <summary>Trusted default sort used when the request does not select an allowed sortable field.</summary>
    public required RawDataSort DefaultOrder { get; init; }

    /// <summary>Whether CSV values beginning with a formula prefix must be neutralized for this source.</summary>
    public bool NeutralizeCsvFormulas { get; init; } = true;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        RawDataSqlIdentifier.Validate(BaseView, nameof(BaseView));
        ArgumentNullException.ThrowIfNull(Fields);
        if (Fields.Count == 0)
        {
            throw new ValidationException("A raw-data source must define at least one field.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in Fields)
        {
            field.Validate();
            if (!names.Add(field.Name))
            {
                throw new ValidationException($"Raw-data source '{Name}' declares duplicate field '{field.Name}'.");
            }
        }

        var defaultField = Fields.SingleOrDefault(field => string.Equals(field.Name, DefaultOrder.Field, StringComparison.OrdinalIgnoreCase));
        if (defaultField is null || !defaultField.Sortable)
        {
            throw new ValidationException("The raw-data source default order must name a sortable field.");
        }
    }
}

/// <summary>One allow-listed column in a <see cref="RawDataSource"/>.</summary>
public sealed class RawDataField
{
    /// <summary>Stable request-facing key, distinct from the physical column where useful.</summary>
    public required string Name { get; init; }

    /// <summary>Plain physical column identifier rendered into trusted SQL.</summary>
    public required string Column { get; init; }

    /// <summary>Display label for a host grid or export header.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The field's accepted value type.</summary>
    public required RawDataKind Kind { get; init; }

    /// <summary>Whether requests may filter this field.</summary>
    public bool Filterable { get; init; }

    /// <summary>Whether requests may sort by this field.</summary>
    public bool Sortable { get; init; }

    internal void Validate()
    {
        RawDataSqlIdentifier.Validate(Name, nameof(Name));
        RawDataSqlIdentifier.Validate(Column, nameof(Column));
        ArgumentException.ThrowIfNullOrWhiteSpace(DisplayName);
    }
}

/// <summary>Supported field value types.</summary>
public enum RawDataKind
{
    Text,
    Number,
    Date,
    Boolean,
    YesNo,
}

/// <summary>A client request to filter, sort, and page an allow-listed source.</summary>
public sealed record RawDataRequest(
    IReadOnlyList<RawDataFilter>? Filters = null,
    RawDataSort? Sort = null,
    int Skip = 0,
    int Take = 100,
    bool Export = false);

/// <summary>One requested filter. Field and operator are validated against the source definition.</summary>
public sealed record RawDataFilter(string Field, RawDataFilterOperator Operator, IReadOnlyList<string> Values);

/// <summary>Supported filter semantics.</summary>
public enum RawDataFilterOperator
{
    Equal,
    In,
    Contains,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
}

/// <summary>An allow-listed sort direction.</summary>
public sealed record RawDataSort(string Field, RawDataSortDirection Direction = RawDataSortDirection.Ascending);

/// <summary>Sort direction rendered as a fixed SQL keyword.</summary>
public enum RawDataSortDirection
{
    Ascending,
    Descending,
}

/// <summary>Limits applied before a raw-data request becomes a warehouse statement.</summary>
public sealed class RawDataOptions
{
    /// <summary>Maximum filters accepted in one request.</summary>
    public int MaximumFilters { get; set; } = 12;

    /// <summary>Maximum values accepted in one <see cref="RawDataFilterOperator.In"/> filter.</summary>
    public int MaximumValuesPerFilter { get; set; } = 100;

    /// <summary>Maximum rows fetched for an interactive page.</summary>
    public int MaximumPageSize { get; set; } = 500;

    /// <summary>Maximum offset accepted before the host must offer a narrower filter or keyset pagination.</summary>
    public int MaximumOffset { get; set; } = 100_000;

    internal void Validate()
    {
        if (MaximumFilters < 0 || MaximumValuesPerFilter < 1 || MaximumPageSize < 1 || MaximumOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(RawDataOptions));
        }
    }
}

/// <summary>Executes a raw-data request through one tenant-scoped, parameterized statement.</summary>
public interface IRawDataService
{
    /// <summary>Runs a validated page query. Invalid input throws <see cref="ValidationException"/> before any warehouse call.</summary>
    Task<RawDataPage> QueryAsync(
        Core.Tenancy.TenantContext tenant,
        RawDataSource source,
        RawDataRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>A materialized page returned from an inline statement.</summary>
public sealed record RawDataPage(
    IReadOnlyList<RawDataColumn> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    long TotalRowCount);

/// <summary>One source-defined output column.</summary>
public sealed record RawDataColumn(string Name, string DisplayName, RawDataKind Kind);
