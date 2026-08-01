namespace LakeWright.Multitenancy.Operations;

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

    /// <summary>
    /// How many operations one tenant may have in flight at once, across every worker.
    /// </summary>
    /// <remarks>
    /// The ceiling on what a single tenant can spend on Databricks compute at any moment, and the
    /// reason one tenant's backlog cannot occupy every worker (threats T5 and T6). A concurrency
    /// limit rather than a currency budget: billing data arrives hours after the compute is bought,
    /// which is too late to stop a runaway loop.
    ///
    /// Work over the cap waits rather than failing. A tenant that queues a hundred operations still
    /// gets all hundred, just not all at once.
    /// </remarks>
    public int MaxInFlightPerTenant { get; set; } = 4;

    /// <summary>Give up polling a run after this long and mark the operation failed.</summary>
    public TimeSpan RunTimeout { get; set; } = TimeSpan.FromHours(2);

    /// <summary>
    /// Databricks job to submit for each operation kind.
    /// </summary>
    /// <remarks>
    /// Keyed by <see cref="Model.Operation.Kind"/>, which is the product's own word for the work.
    /// This was a single job id, so every kind ran the same job no matter what the caller asked
    /// for — a product with both an analysis and an export had no way to express that without
    /// forking the worker.
    ///
    /// A kind with no entry fails the operation with that reason rather than falling back to some
    /// other tenant's job. Running the wrong job is worse than running none.
    /// </remarks>
    public Dictionary<string, long> Jobs { get; } = new(StringComparer.Ordinal);
}
