using System.Diagnostics;
using System.Threading.RateLimiting;
using LakeWright.Multitenancy;

namespace LakeWright.AspNetCore;

/// <summary>
/// Bounds how many refused tenant resolutions one principal can write to the audit table.
/// </summary>
/// <remarks>
/// Each refusal writes an audit row, and the only gate in front of it is an authenticated
/// principal. Without a bound, one principal walking identifiers turns every request into a
/// database write. Within the budget every refusal is recorded as before. Past it, one
/// <c>tenant.access_denied_suppressed</c> row per window records that the principal ran out, and
/// the <c>lakewright.tenant.access_denied</c> counter still counts every refusal. The response is
/// the same 404 either way. The budget is held in memory, so it applies per application instance:
/// N replicas allow N times as many rows. Configure it with
/// <c>services.Configure&lt;TenantDenialAuditOptions&gt;</c>; an invalid value fails at startup.
/// </remarks>
public sealed class TenantDenialAuditOptions
{
    /// <summary>Refusals recorded individually per principal in each window. Default 20.</summary>
    public int PermitLimit { get; set; } = 20;

    /// <summary>Length of the fixed window. Default one minute.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(PermitLimit, 1, nameof(PermitLimit));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Window, TimeSpan.Zero, nameof(Window));
    }
}

internal enum DenialAuditDecision
{
    Record,
    RecordSuppression,
    Skip,
}

internal sealed class TenantDenialAuditBudget
{
    private readonly PartitionedRateLimiter<string> _rows;
    private readonly PartitionedRateLimiter<string> _suppressionMarkers;

    public TenantDenialAuditBudget(TenantDenialAuditOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _rows = PerPrincipal(options.PermitLimit, options.Window);
        _suppressionMarkers = PerPrincipal(1, options.Window);
    }

    public DenialAuditDecision Decide(string principalId)
    {
        using var row = _rows.AttemptAcquire(principalId);
        if (row.IsAcquired)
        {
            return DenialAuditDecision.Record;
        }

        using var marker = _suppressionMarkers.AttemptAcquire(principalId);
        return marker.IsAcquired ? DenialAuditDecision.RecordSuppression : DenialAuditDecision.Skip;
    }

    public static string? ActionFor(DenialAuditDecision decision) => decision switch
    {
        DenialAuditDecision.Record => AuditActions.TenantAccessDenied,
        DenialAuditDecision.RecordSuppression => AuditActions.TenantAccessDeniedSuppressed,
        DenialAuditDecision.Skip => null,
        _ => throw new UnreachableException(),
    };

    // Idle partitions are removed by the limiter itself, so principals that stop probing do not
    // accumulate.
    private static PartitionedRateLimiter<string> PerPrincipal(int permits, TimeSpan window) =>
        PartitionedRateLimiter.Create<string, string>(principalId => RateLimitPartition.GetFixedWindowLimiter(
            principalId,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permits,
                Window = window,
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
}
