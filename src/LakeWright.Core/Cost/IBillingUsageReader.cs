using LakeWright.Core.Tenancy;

namespace LakeWright.Core.Cost;

/// <summary>Reads priced Databricks billing rows for tenant-owned Lakeflow job runs.</summary>
/// <remarks>
/// The tenant context and the run identifiers are both mandatory. Implementations read a global
/// system table, but the caller first resolves ownership in its transactional store and supplies
/// only identifiers owned by this tenant. This keeps the system-table escape explicit without
/// teaching the transactional layer about a Databricks client.
/// </remarks>
public interface IBillingUsageReader
{
    Task<IReadOnlyList<BillingRunUsage>> ReadAsync(
        TenantContext tenant,
        DateTimeOffset from,
        DateTimeOffset until,
        IReadOnlyCollection<long> jobRunIds,
        CancellationToken cancellationToken);
}

/// <summary>Resource limits shared by billing attribution implementations.</summary>
public static class BillingUsageLimits
{
    /// <summary>
    /// Maximum distinct Databricks job runs in one report. The billing reader performs one
    /// account-wide system-table query, so callers must narrow the window when this limit is hit.
    /// </summary>
    public const int MaxJobRunsPerReport = 500;

    /// <summary>Maximum time covered by one account-wide billing query.</summary>
    public const int MaxReportWindowDays = 31;

    /// <summary>Maximum future drift accepted for a report's upper bound.</summary>
    public const int MaxFutureWindowDays = 1;

    /// <summary>Rejects report windows that could cause an unbounded billing-table scan.</summary>
    public static void ValidateReportWindow(
        DateTimeOffset from,
        DateTimeOffset until,
        DateTimeOffset now)
    {
        if (from >= until)
        {
            throw new ArgumentException("from must be earlier than until.", nameof(from));
        }

        if (until > now.AddDays(MaxFutureWindowDays))
        {
            throw new BillingUsageException("REPORT_WINDOW_IN_FUTURE", isTransient: false);
        }

        if (until - from > TimeSpan.FromDays(MaxReportWindowDays))
        {
            throw new BillingUsageException("REPORT_WINDOW_TOO_LARGE", isTransient: false);
        }
    }
}

/// <summary>Priced usage attributed to one Lakeflow job run.</summary>
/// <param name="JobRunId">The Databricks job run identifier.</param>
/// <param name="DbusConsumed">Net DBU quantity, including correction records.</param>
/// <param name="EstimatedListCost">Cost at the effective list price for one currency.</param>
public sealed record BillingRunUsage(
    long JobRunId,
    decimal DbusConsumed,
    CurrencyAmount EstimatedListCost);

/// <summary>A safe, provider-neutral failure from a billing usage read.</summary>
public sealed class BillingUsageException(string code, bool isTransient) : Exception(
    $"The billing usage read failed with code {code}.")
{
    public string Code { get; } = code;
    public bool IsTransient { get; } = isTransient;
}
