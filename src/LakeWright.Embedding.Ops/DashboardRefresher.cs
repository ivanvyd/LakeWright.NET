using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using LakeWright.Core.Features;
using LakeWright.Core.Tenancy;
using Microsoft.Extensions.Options;

namespace LakeWright.Embedding.Ops;

/// <summary>Databricks Jobs API implementation of <see cref="IDashboardRefresher"/>.</summary>
internal sealed class DashboardRefresher(
    IJobsApi jobs,
    IOptions<DashboardRefreshOptions> options,
    TimeProvider timeProvider,
    IRefreshRunOwnership ownership,
    ILakeWrightFeatureGate features) : IDashboardRefresher
{
    private readonly DashboardRefreshOptions _options = options.Value;

    public async Task<RefreshStart> StartOrJoinAsync(
        TenantContext tenant,
        RefreshJob job,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(job);
        features.EnsureEnabled(LakeWrightFeatures.Operations);
        _options.Validate();
        if (idempotencyKey is { Length: > 64 })
        {
            throw new ArgumentException("The Jobs API caps idempotency tokens at 64 characters.", nameof(idempotencyKey));
        }

        var jobId = await jobs.ResolveJobIdAsync(job, cancellationToken).ConfigureAwait(false);
        var active = (await jobs.ListRunsAsync(jobId, activeOnly: true, cancellationToken).ConfigureAwait(false))
            .Where(run => TenantMatches(run, tenant))
            .OrderBy(run => run.StartedAt ?? DateTimeOffset.MinValue)
            .ToArray();
        if (active.Length > 0)
        {
            if (active.Length > _options.Policy.MaxConcurrentPerTenant)
            {
                throw new RefreshNotAdmittedException("The tenant already exceeds the configured concurrent-refresh limit.");
            }

            var joined = new RefreshStart(active[0].RunId, Joined: true, active[0].StartedAt ?? timeProvider.GetUtcNow());
            ownership.Record(tenant, joined.RunId);
            return joined;
        }

        var completed = (await jobs.ListRunsAsync(jobId, activeOnly: false, cancellationToken).ConfigureAwait(false))
            .Where(run => TenantMatches(run, tenant) && run.State == RefreshRunState.Succeeded && run.EndedAt is not null)
            .OrderByDescending(run => run.EndedAt)
            .ToArray();
        if (completed.FirstOrDefault() is { EndedAt: { } lastSuccess }
            && timeProvider.GetUtcNow() - lastSuccess < _options.Policy.MinimumInterval)
        {
            throw new RefreshNotAdmittedException("The most recent successful refresh is still within the configured minimum interval.");
        }

        var token = idempotencyKey ?? CreateBucketToken(tenant, jobId, timeProvider.GetUtcNow(), _options.Policy.MinimumInterval);
        try
        {
            var runId = await jobs.RunNowAsync(jobId, tenant, token, cancellationToken).ConfigureAwait(false);
            var started = new RefreshStart(runId, Joined: false, timeProvider.GetUtcNow());
            ownership.Record(tenant, started.RunId);
            return started;
        }
        catch (JobsApiException exception) when (exception.IsMissingJob && job.Name is not null)
        {
            jobs.Invalidate(job);
            var resolvedAgain = await jobs.ResolveJobIdAsync(job, cancellationToken).ConfigureAwait(false);
            var runId = await jobs.RunNowAsync(resolvedAgain, tenant, token, cancellationToken).ConfigureAwait(false);
            var started = new RefreshStart(runId, Joined: false, timeProvider.GetUtcNow());
            ownership.Record(tenant, started.RunId);
            return started;
        }
    }

    public async Task<IReadOnlyList<RefreshRunStatus>> StatusAsync(
        TenantContext tenant,
        IReadOnlyCollection<long> runIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(runIds);
        features.EnsureEnabled(LakeWrightFeatures.Operations);
        if (runIds.Any(id => id <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(runIds), "Run identifiers must be positive.");
        }

        var statuses = new List<RefreshRunStatus>(runIds.Count);
        foreach (var runId in runIds.Distinct())
        {
            if (!ownership.IsOwner(tenant, runId))
            {
                throw new UnauthorizedAccessException("The refresh run is not recorded for the resolved tenant.");
            }

            var run = await jobs.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
            if (!TenantMatches(run, tenant))
            {
                throw new UnauthorizedAccessException("The refresh run does not belong to the resolved tenant.");
            }

            statuses.Add(new RefreshRunStatus(
                run.RunId,
                run.State,
                run.StartedAt,
                run.EndedAt,
                run.Tasks,
                run.FailureReason));
        }

        return statuses;
    }

    private static bool TenantMatches(JobsRun run, TenantContext tenant) =>
        string.Equals(run.TenantId, tenant.TenantId.ToString(), StringComparison.Ordinal);

    private static string CreateBucketToken(TenantContext tenant, long jobId, DateTimeOffset now, TimeSpan interval)
    {
        var bucket = now.Ticks / interval.Ticks;
        var material = $"{tenant.TenantId}|{jobId}|{bucket}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant()[..64];
    }
}

internal sealed class MemoryRefreshRunOwnership : IRefreshRunOwnership
{
    private readonly ConcurrentDictionary<long, TenantId> _owners = new();

    public void Record(TenantContext tenant, long runId)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runId);
        _owners[runId] = tenant.TenantId;
    }

    public bool IsOwner(TenantContext tenant, long runId)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return _owners.TryGetValue(runId, out var owner) && owner == tenant.TenantId;
    }
}
