namespace Lakewright.Multitenancy.Operations;

public sealed class OperationWorkerOptions
{
    public const string SectionName = "OperationWorker";

    /// <summary>How long to wait after finding nothing to do.</summary>
    public TimeSpan IdleDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>First polling interval. Doubles up to <see cref="MaxPollInterval"/>, with jitter.</summary>
    public TimeSpan InitialPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxPollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a claimed operation may sit without an external identifier before reconciliation
    /// treats it as orphaned.
    /// </summary>
    /// <remarks>
    /// Too short and reconciliation races a slow-but-alive worker; too long and a genuinely dead
    /// run sits unnoticed. It only needs to exceed the time between claiming and recording, which
    /// is one API call.
    /// </remarks>
    public TimeSpan ReconciliationGracePeriod { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Give up polling a run after this long and mark the operation failed.</summary>
    public TimeSpan RunTimeout { get; set; } = TimeSpan.FromHours(2);

    /// <summary>Databricks job the worker submits to.</summary>
    public long JobId { get; set; }
}
