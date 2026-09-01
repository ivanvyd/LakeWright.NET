using LakeWright.Core.Tenancy;

namespace LakeWright.Core.Cost;

/// <summary>
/// How a tenant's Databricks usage was measured.
/// </summary>
public enum CostSource
{
    /// <summary>
    /// Elapsed wall-clock time on the operation, weighted by the configured warehouse SKU's
    /// documented DBU rate. A proxy for cost rather than a reading of billing data, and the only
    /// one available without a metastore-admin grant on <c>system.billing.usage</c>.
    /// </summary>
    Proxy = 0,

    /// <summary>
    /// A read of <c>system.billing.usage</c> for tenant-owned job run ids selected from
    /// <c>operations.ExternalId</c>, correlated in application code.
    /// </summary>
    Billing = 1
}

/// <summary>
/// A tenant's accrued compute over a window, broken down by operation kind.
/// </summary>
/// <param name="TenantId">The tenant the cost belongs to.</param>
/// <param name="From">Inclusive start of the window (UTC).</param>
/// <param name="To">Exclusive end of the window (UTC).</param>
/// <param name="Source">Where the numbers came from.</param>
/// <param name="WarehouseSku">The Databricks warehouse SKU the rates apply to, or null when the source is a billing read.</param>
/// <param name="DbusConsumed">Total Databricks Units the tenant consumed in the window.</param>
/// <param name="ByKind">
/// Breakdown by the operation's <c>Kind</c> string. The list is sorted by <c>DbusConsumed</c>
/// descending, so a glance shows where the spend landed.
/// </param>
public sealed record TenantCostSummary(
    TenantId TenantId,
    DateTimeOffset From,
    DateTimeOffset To,
    CostSource Source,
    string? WarehouseSku,
    decimal DbusConsumed,
    IReadOnlyList<CostByKind> ByKind)
{
    /// <summary>
    /// Cost at Databricks' effective list price, grouped by the currency in
    /// <c>system.billing.list_prices</c>. Empty for proxy attribution.
    /// </summary>
    /// <remarks>
    /// This is deliberately a collection rather than one amount: billing data can span price
    /// rows in different currencies, and adding unlike currencies would produce a plausible but
    /// meaningless number. It is an init-only property so the original positional constructor
    /// remains source-compatible.
    /// </remarks>
    public IReadOnlyList<CurrencyAmount> EstimatedListCost { get; init; } = [];
}

/// <summary>
/// One row of a <see cref="TenantCostSummary"/>.
/// </summary>
/// <param name="Kind">The operation's <c>Kind</c> string.</param>
/// <param name="Operations">Number of operations of this kind in the window.</param>
/// <param name="ElapsedSeconds">
/// Total wall-clock seconds these operations held compute, summed across the window. Populated by
/// <see cref="CostSource.Proxy"/> and zero for a billing read, whose usage records do not report
/// the operation wall-clock duration.
/// </param>
/// <param name="DbusConsumed">DBU attributed to this kind.</param>
public sealed record CostByKind(
    string Kind,
    int Operations,
    double ElapsedSeconds,
    decimal DbusConsumed)
{
    /// <summary>Effective list-price cost for this operation kind, grouped by currency.</summary>
    public IReadOnlyList<CurrencyAmount> EstimatedListCost { get; init; } = [];
}

/// <summary>A monetary amount whose currency must travel with the value.</summary>
/// <param name="CurrencyCode">The ISO-style code reported by the Databricks price table.</param>
/// <param name="Amount">The amount in <paramref name="CurrencyCode"/>.</param>
public sealed record CurrencyAmount(string CurrencyCode, decimal Amount);

/// <summary>
/// Reports a tenant's Databricks compute consumption.
/// </summary>
/// <remarks>
/// Takes a <see cref="TenantContext"/> and nothing else, mirroring the rest of the data-reaching
/// surface in this library. A caller without a resolved context cannot reach this method, and a
/// caller with one can only see the tenant it resolved to.
///
/// The shipped implementation in <c>LakeWright.Multitenancy.Cost</c>
/// (<c>OperationCostAttribution</c>) weights <c>operations.ClaimedAt</c> to <c>CompletedAt</c>
/// by the warehouse SKU's DBU/hour rate, which is what the threat model calls available without
/// a grant. <see cref="CostSource.Billing"/> reads <c>system.billing.usage</c> and
/// <c>system.billing.list_prices</c> for tenant-owned job runs. It requires system-table grants,
/// so the proxy remains the default and an adopter opts into billing registration explicitly.
/// </remarks>
public interface ICostAttribution
{
    /// <summary>
    /// Returns the tenant's compute consumption over <c>[from, to)</c>.
    /// </summary>
    /// <param name="tenant">The tenant to report on. Filters every query.</param>
    /// <param name="from">Inclusive start, UTC. Must be earlier than <paramref name="until"/>.</param>
    /// <param name="until">Exclusive end, UTC.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<TenantCostSummary> ResolveAsync(
        TenantContext tenant,
        DateTimeOffset from,
        DateTimeOffset until,
        CancellationToken cancellationToken);
}
