using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace LakeWright.Databricks;

/// <summary>Dependency-free metrics and tracing for tenant-scoped statement execution.</summary>
public static class LakeWrightDatabricksTelemetry
{
    public const string MeterName = "LakeWright.Databricks";
    public const string ActivitySourceName = "LakeWright.Databricks";

    internal static readonly ActivitySource Source = new(ActivitySourceName);
    private static readonly Meter Meter = new(MeterName);

    internal static readonly Histogram<double> StatementDuration = Meter.CreateHistogram<double>(
        "lakewright.statements.duration",
        unit: "s",
        description: "End-to-end statement execution time, including continuation polling.");

    internal static readonly Counter<long> StatementOutcomes = Meter.CreateCounter<long>(
        "lakewright.statements.outcomes",
        unit: "{statement}",
        description: "Terminal statement outcomes by state and statement kind.");

    internal static readonly Histogram<double> WarehouseWait = Meter.CreateHistogram<double>(
        "lakewright.statements.warehouse_wait",
        unit: "s",
        description: "Elapsed time after Databricks reports a pending statement; includes warehouse queue and continued execution.");

    internal static readonly Counter<long> ExportRows = Meter.CreateCounter<long>(
        "lakewright.exports.rows",
        unit: "{row}",
        description: "Rows emitted from tenant-scoped external-link exports.");

    internal static readonly Counter<long> ExportBytes = Meter.CreateCounter<long>(
        "lakewright.exports.bytes",
        unit: "By",
        description: "External-link export response bytes read by the JSON parser.");

    internal static void RecordStatement(StatementOutcome outcome, string kind, TimeSpan elapsed)
    {
        var state = outcome switch
        {
            StatementOutcome.Success => "succeeded",
            StatementOutcome.LargeResult => "succeeded",
            StatementOutcome.Failure => "failed",
            StatementOutcome.Pending => "pending",
            _ => "unknown",
        };
        // Use the explicit tag overloads. Passing TagList by value selected an overload that
        // dropped all tags with the target framework's metrics implementation, making the
        // instruments look low-cardinality while silently losing their only useful dimension.
        StatementDuration.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("statement.kind", kind));
        StatementOutcomes.Add(1,
            new KeyValuePair<string, object?>("statement.kind", kind),
            new KeyValuePair<string, object?>("state", state));
    }

    internal static void RecordBudgetExceeded(string kind, TimeSpan elapsed)
    {
        StatementDuration.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("statement.kind", kind));
        StatementOutcomes.Add(1,
            new KeyValuePair<string, object?>("statement.kind", kind),
            new KeyValuePair<string, object?>("state", "budget_exceeded"));
    }
}
