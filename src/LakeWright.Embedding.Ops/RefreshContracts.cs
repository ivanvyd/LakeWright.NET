using LakeWright.Core.Tenancy;

namespace LakeWright.Embedding.Ops;

/// <summary>Starts, joins, and checks tenant-scoped Lakeflow refresh jobs.</summary>
public interface IDashboardRefresher
{
    /// <summary>
    /// Starts a refresh for <paramref name="tenant"/>, or joins its earliest active refresh for
    /// the same job. The tenant is required so an active run belonging to another tenant can
    /// never be joined.
    /// </summary>
    Task<RefreshStart> StartOrJoinAsync(
        TenantContext tenant,
        RefreshJob job,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the state of runs after proving that each one belongs to <paramref name="tenant"/>.
    /// A caller must not accept a bare run id from a browser request as proof of ownership.
    /// </summary>
    Task<IReadOnlyList<RefreshRunStatus>> StatusAsync(
        TenantContext tenant,
        IReadOnlyCollection<long> runIds,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Records refresh-run ownership outside the workspace. The default is intentionally process-local:
/// an unrecorded run is invisible rather than readable. Multi-replica hosts replace it with durable
/// storage before exposing refresh-status endpoints.
/// </summary>
public interface IRefreshRunOwnership
{
    /// <summary>Records the tenant that owns a run returned by <see cref="IDashboardRefresher"/>.</summary>
    void Record(TenantContext tenant, long runId);

    /// <summary>Returns whether this application recorded the run for the supplied tenant.</summary>
    bool IsOwner(TenantContext tenant, long runId);
}

/// <summary>A job chosen by a stable Databricks id or a workspace job name.</summary>
public sealed record RefreshJob
{
    private RefreshJob(long? id, string? name)
    {
        Id = id;
        Name = name;
    }

    /// <summary>The stable job identifier, when supplied.</summary>
    public long? Id { get; }

    /// <summary>The configured job name, resolved through the Jobs API when supplied.</summary>
    public string? Name { get; }

    /// <summary>Creates a reference to a job by its stable workspace identifier.</summary>
    public static RefreshJob FromId(long id)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        return new RefreshJob(id, null);
    }

    /// <summary>Creates a reference to a job by its unique workspace name.</summary>
    public static RefreshJob FromName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new RefreshJob(null, name);
    }
}

/// <summary>Policy applied server-side before a refresh is launched.</summary>
public sealed class RefreshPolicy
{
    /// <summary>
    /// Minimum time between successful refreshes for the same tenant and job. This also defines
    /// the opaque idempotency bucket used to collapse concurrent requests across replicas.
    /// </summary>
    public TimeSpan MinimumInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Maximum active refresh runs permitted for one tenant and job.</summary>
    public int MaxConcurrentPerTenant { get; set; } = 1;

    internal void Validate()
    {
        if (MinimumInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumInterval), "MinimumInterval must be positive.");
        }
        if (MaxConcurrentPerTenant < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrentPerTenant), "MaxConcurrentPerTenant must be at least one.");
        }
    }
}

/// <summary>Options for dashboard refresh orchestration.</summary>
public sealed class DashboardRefreshOptions
{
    /// <summary>Configuration section used by <c>AddLakeWrightDashboardRefresh</c>.</summary>
    public const string SectionName = "LakeWright:DashboardRefresh";

    /// <summary>Refresh admission policy.</summary>
    public RefreshPolicy Policy { get; set; } = new();

    /// <summary>How long a job-name to id mapping may stay cached.</summary>
    public TimeSpan JobLookupCacheDuration { get; set; } = TimeSpan.FromMinutes(5);

    internal void Validate()
    {
        Policy.Validate();
        if (JobLookupCacheDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(JobLookupCacheDuration), "JobLookupCacheDuration must be positive.");
        }
    }
}

/// <summary>The result of a refresh start request.</summary>
public sealed record RefreshStart(long RunId, bool Joined, DateTimeOffset StartedAt);

/// <summary>The tenant-safe projection of a Lakeflow job run.</summary>
public sealed record RefreshRunStatus(
    long RunId,
    RefreshRunState State,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    IReadOnlyList<RefreshTaskStatus> Tasks,
    string? FailureReason);

/// <summary>Closed lifecycle states exposed by the refresher.</summary>
public enum RefreshRunState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>One task's summary within a refresh run.</summary>
public sealed record RefreshTaskStatus(string TaskKey, RefreshRunState State, string? FailureReason);

/// <summary>Raised when a refresh request is valid but policy does not admit another run.</summary>
public sealed class RefreshNotAdmittedException(string message) : InvalidOperationException(message);
