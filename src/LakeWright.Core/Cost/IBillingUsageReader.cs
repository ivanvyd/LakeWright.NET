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
