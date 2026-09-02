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
}
