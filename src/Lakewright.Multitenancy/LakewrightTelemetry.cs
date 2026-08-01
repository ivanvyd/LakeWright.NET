using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Lakewright.Multitenancy;

/// <summary>
/// The meter and activity source this library publishes.
/// </summary>
/// <remarks>
/// Plain <c>System.Diagnostics</c> instruments, with no OpenTelemetry dependency. A library that
/// references the SDK picks its adopter's exporter, its version, and its upgrade schedule; these
/// types are in the framework, and an application subscribes to them with two lines:
///
/// <code>
/// builder.Services.AddOpenTelemetry()
///     .WithMetrics(m => m.AddMeter(LakewrightTelemetry.MeterName))
///     .WithTracing(t => t.AddSource(LakewrightTelemetry.ActivitySourceName));
/// </code>
///
/// <b>No tenant identifier on any metric.</b> It is the first tag anyone reaches for and it is a
/// cardinality bomb: one time series per tenant per instrument, in a system whose whole purpose is
/// having many tenants. A thousand tenants turns four instruments into four thousand series, and
/// the bill for that arrives at the observability vendor. Tenant lands on <em>spans</em> instead,
/// where sampling bounds the volume, and per-tenant totals come from <c>operations</c> and
/// <c>audit_events</c>, which are already indexed for it.
/// </remarks>
public static class LakewrightTelemetry
{
    public const string MeterName = "Lakewright.Multitenancy";
    public const string ActivitySourceName = "Lakewright.Multitenancy";

    internal static readonly ActivitySource Source = new(ActivitySourceName);

    private static readonly Meter Meter = new(MeterName);

    internal static readonly Counter<long> OperationsStarted = Meter.CreateCounter<long>(
        "lakewright.operations.started",
        unit: "{operation}",
        description: "Operations accepted, including those that replayed an idempotency key.");

    internal static readonly Counter<long> OperationsCompleted = Meter.CreateCounter<long>(
        "lakewright.operations.completed",
        unit: "{operation}",
        description: "Operations that reached a terminal state, tagged with which one.");

    /// <summary>
    /// How long an operation waited between being accepted and being claimed.
    /// </summary>
    /// <remarks>
    /// The number that shows whether the claim loop is fair. A rising p99 against a flat median is
    /// one tenant's backlog pushing everyone else back, which is the shape of threat T6; the
    /// in-flight ceiling is meant to keep them together.
    /// </remarks>
    internal static readonly Histogram<double> QueueWait = Meter.CreateHistogram<double>(
        "lakewright.operations.queue_wait",
        unit: "s",
        description: "Seconds between an operation being accepted and a worker claiming it.");

    /// <summary>
    /// Refused tenant resolutions.
    /// </summary>
    /// <remarks>
    /// These answer 404, so nothing in an access log distinguishes them from a stale bookmark. A
    /// step change here is someone walking identifiers. The audit table holds who and which tenant;
    /// this is the signal you can alert on without querying it.
    /// </remarks>
    internal static readonly Counter<long> TenantAccessDenied = Meter.CreateCounter<long>(
        "lakewright.tenant.access_denied",
        unit: "{request}",
        description: "Requests refused because the principal is not a member of the tenant.");
}
