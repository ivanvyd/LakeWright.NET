using Lakewright.Core.Tenancy;

namespace Lakewright.Core.Jobs;

/// <summary>
/// The state of a Lakeflow job run, as this application understands it.
/// </summary>
/// <remarks>
/// Deliberately a closed set that we own. Databricks documents run lifecycle states as extensible,
/// so platform states are mapped into this at the boundary and an unrecognised one becomes
/// <see cref="Running"/> rather than an exception. Treating a state Databricks added last week as a
/// failure turns their release into our outage. See ADR 0005.
/// </remarks>
public abstract record RunOutcome
{
    private RunOutcome() { }

    /// <summary>The run was accepted. <paramref name="RunId"/> is what reconciliation matches on.</summary>
    public sealed record Submitted(long RunId) : RunOutcome;

    public sealed record Running(long RunId) : RunOutcome;

    public sealed record Succeeded(long RunId) : RunOutcome;

    /// <summary>
    /// The run finished without succeeding, or was rejected outright.
    /// <paramref name="RunId"/> is null when the submission itself failed.
    /// </summary>
    public sealed record Failed(long? RunId, string Reason, bool IsTransient) : RunOutcome;

    public sealed record Cancelled(long RunId) : RunOutcome;
}
