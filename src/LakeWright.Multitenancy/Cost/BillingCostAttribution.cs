using System.Globalization;
using LakeWright.Core.Cost;
using LakeWright.Core.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LakeWright.Multitenancy.Cost;

/// <summary>
/// Correlates tenant-owned operation records with priced Databricks job-run billing usage.
/// </summary>
public sealed class BillingCostAttribution(
    LakeWrightDbContext db,
    IBillingUsageReader billing) : ICostAttribution
{
    public async Task<TenantCostSummary> ResolveAsync(
        TenantContext tenant,
        DateTimeOffset from,
        DateTimeOffset until,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (from >= until)
        {
            throw new ArgumentException("from must be earlier than until.", nameof(from));
        }

        var candidates = await db.Operations
            .AsNoTracking()
            .Where(operation => operation.OrganizationId == tenant.TenantId
                && operation.ExternalId != null
                && operation.ClaimedAt != null
                && operation.CompletedAt != null
                && operation.CompletedAt > from
                && operation.ClaimedAt < until)
            .GroupBy(operation => operation.ExternalId)
            .Select(group => new
            {
                ExternalId = group.Key,
                FirstKind = group.Min(operation => operation.Kind),
                LastKind = group.Max(operation => operation.Kind)
            })
            .Take(BillingUsageLimits.MaxJobRunsPerReport + 1)
            .ToListAsync(cancellationToken);

        if (candidates.Count > BillingUsageLimits.MaxJobRunsPerReport)
        {
            throw new BillingUsageException("REPORT_TOO_LARGE", isTransient: false);
        }

        var ownedRuns = new Dictionary<long, string>();
        foreach (var candidate in candidates)
        {
            var kind = candidate.FirstKind;
            if (kind is null
                || candidate.LastKind is null
                || !string.Equals(kind, candidate.LastKind, StringComparison.Ordinal))
            {
                throw new BillingUsageException("AMBIGUOUS_RUN", isTransient: false);
            }

            if (!long.TryParse(
                    candidate.ExternalId,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var runId)
                || runId <= 0)
            {
                throw new BillingUsageException("INVALID_OPERATION_RUN_ID", isTransient: false);
            }

            if (ownedRuns.TryGetValue(runId, out var existingKind)
                && !string.Equals(existingKind, kind, StringComparison.Ordinal))
            {
                throw new BillingUsageException("AMBIGUOUS_RUN", isTransient: false);
            }

            ownedRuns[runId] = kind;
        }

        if (ownedRuns.Count == 0)
        {
            return Empty(tenant, from, until);
        }

        var usage = await billing.ReadAsync(
            tenant,
            from,
            until,
            ownedRuns.Keys,
            cancellationToken);

        if (usage.FirstOrDefault(row => !ownedRuns.ContainsKey(row.JobRunId)) is not null)
        {
            throw new BillingUsageException("UNEXPECTED_RUN", isTransient: false);
        }

        var byKind = usage
            .GroupBy(row => ownedRuns[row.JobRunId], StringComparer.Ordinal)
            .Select(group => new CostByKind(
                group.Key,
                group.Select(row => row.JobRunId).Distinct().Count(),
                ElapsedSeconds: 0,
                DbusConsumed: Math.Round(group.Sum(row => row.DbusConsumed), 4))
            {
                EstimatedListCost = CurrencyTotals(group)
            })
            .OrderByDescending(row => row.DbusConsumed)
            .ThenBy(row => row.Kind, StringComparer.Ordinal)
            .ToArray();

        return new TenantCostSummary(
            tenant.TenantId,
            from,
            until,
            CostSource.Billing,
            WarehouseSku: null,
            DbusConsumed: Math.Round(usage.Sum(row => row.DbusConsumed), 4),
            byKind)
        {
            EstimatedListCost = CurrencyTotals(usage)
        };
    }

    private static TenantCostSummary Empty(
        TenantContext tenant,
        DateTimeOffset from,
        DateTimeOffset until) => new(
            tenant.TenantId,
            from,
            until,
            CostSource.Billing,
            WarehouseSku: null,
            DbusConsumed: 0,
            ByKind: []);

    private static CurrencyAmount[] CurrencyTotals(IEnumerable<BillingRunUsage> usage) =>
        usage
            .GroupBy(row => row.EstimatedListCost.CurrencyCode, StringComparer.Ordinal)
            .Select(group => new CurrencyAmount(
                group.Key,
                Math.Round(group.Sum(row => row.EstimatedListCost.Amount), 4)))
            .OrderBy(amount => amount.CurrencyCode, StringComparer.Ordinal)
            .ToArray();
}
