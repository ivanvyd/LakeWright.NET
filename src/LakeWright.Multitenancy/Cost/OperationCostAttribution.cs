using LakeWright.Core.Cost;
using LakeWright.Core.Tenancy;
using LakeWright.Multitenancy.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;


namespace LakeWright.Multitenancy.Cost;

/// <summary>
/// Computes per-tenant cost from the <c>operations</c> table, weighted by a configured warehouse
/// SKU's DBU rate.
/// </summary>
/// <remarks>
/// Reads only the application database. The threat model records that <c>system.billing.usage</c>
/// is the right answer and that this is the proxy available without a metastore-admin grant
/// (threat T5). The <see cref="ICostAttribution"/> interface exists so a real billing read can
/// replace this implementation without changing the call sites.
///
/// Only terminal-state operations are counted, because a non-terminal one has no
/// <see cref="Operation.CompletedAt"/> and an open-ended wall-clock duration would be nonsense as
/// a cost number. Reconciling in-flight work is the worker's job; this reader does not lie about
/// it.
///
/// The total elapsed time is summed in seconds in the database, not in the application, because
/// a 100k-row operations table pulled across the wire for an in-memory sum is the kind of
/// "small data" that is small until it isn't.
/// </remarks>
public sealed class OperationCostAttribution(
    LakeWrightDbContext db,
    IOptions<CostAttributionOptions> options) : ICostAttribution
{
    public async Task<TenantCostSummary> ResolveAsync(
        TenantContext tenant,
        DateTimeOffset from,
        DateTimeOffset until,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (from >= until) { throw new ArgumentException("from must be earlier than until.", nameof(from)); }

        // Aggregation runs in Postgres rather than the application. Each operation's elapsed
        // time is clipped to the window with LEAST/GREATEST so an operation that spans the
        // window edge is attributed only to the part inside [from, until); without the clip
        // the same operation would be counted in two adjacent windows. Tenant id is a parameter,
        // not text: the from / until bounds are bound the same way.
        var rows = await db.Database
            .SqlQueryRaw<CostRow>(
                """
                SELECT "Kind" AS Kind, COUNT(*)::int AS Operations,
                       COALESCE(SUM(GREATEST(0, EXTRACT(EPOCH FROM (LEAST("CompletedAt", {2}) - GREATEST("ClaimedAt", {1}))))), 0)::double precision AS ElapsedSeconds
                FROM operations
                WHERE "OrganizationId" = {0}
                  AND "ClaimedAt" IS NOT NULL
                  AND "CompletedAt" IS NOT NULL
                  AND "CompletedAt" > {1}
                  AND "ClaimedAt" < {2}
                GROUP BY "Kind"
                """,
                tenant.TenantId.Value, from, until)
            .ToListAsync(cancellationToken);

        var sku = options.Value.WarehouseSku;
        // Decimal division throughout, so a DbusPerHour like 0.6 yields exactly 0.0001̄6̄
        // (recurring) rather than a double-rounded approximation.
        var dbusPerSecond = (decimal)options.Value.DbusPerHour / 3600m;

        var byKind = rows
            .Select(r => new CostByKind(
                r.Kind,
                r.Operations,
                r.ElapsedSeconds,
                Math.Round((decimal)r.ElapsedSeconds * dbusPerSecond, 4)))
            .OrderByDescending(b => b.DbusConsumed)
            .ToList();

        var total = byKind.Sum(b => b.DbusConsumed);

        return new TenantCostSummary(
            tenant.TenantId,
            from,
            until,
            CostSource.Proxy,
            sku,
            Math.Round(total, 4),
            byKind);
    }

    private sealed class CostRow
    {
        public string Kind { get; set; } = string.Empty;
        public int Operations { get; set; }
        public double ElapsedSeconds { get; set; }
    }
}
