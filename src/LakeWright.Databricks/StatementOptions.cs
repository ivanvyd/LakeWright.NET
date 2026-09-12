using Microsoft.Azure.Databricks.Client.Models;

namespace LakeWright.Databricks;

/// <summary>Controls the server wait and local polling lifecycle for a statement.</summary>
public sealed class StatementOptions
{
    /// <summary>Server-side wait requested on the initial statement submission.</summary>
    public string WaitTimeout { get; set; } = "30s";

    /// <summary>Whether a server timeout returns a pollable statement rather than cancelling it.</summary>
    public SqlStatementOnWaitTimeout OnWaitTimeout { get; set; } = SqlStatementOnWaitTimeout.CONTINUE;

    /// <summary>Delay between terminal-state polls after the server returns a pending statement.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Overall local budget for submission, polls, and result reads. For an export it starts on
    /// first enumeration and includes time spent by the consumer between rows.
    /// </summary>
    public TimeSpan TotalBudget { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Where completed rows are returned.</summary>
    public SqlStatementDisposition Disposition { get; set; } = SqlStatementDisposition.INLINE;

    /// <summary>Maximum rows requested for inline statements.</summary>
    public long InlineRowLimit { get; set; } = 10_000;

    /// <summary>Low-cardinality caller-defined category for telemetry, never a tenant identifier.</summary>
    public string Kind { get; set; } = "query";

    internal void Validate()
    {
        if (PollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(PollInterval), "PollInterval must be positive.");
        }

        if (TotalBudget <= TimeSpan.Zero || TotalBudget.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(TotalBudget), "TotalBudget must be positive and fit a cancellation timer.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(WaitTimeout);
        ArgumentException.ThrowIfNullOrWhiteSpace(Kind);
        if (!WaitTimeout.EndsWith('s')
            || !int.TryParse(WaitTimeout.AsSpan(0, WaitTimeout.Length - 1),
                System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            || (seconds != 0 && (seconds < 5 || seconds > 50)))
        {
            throw new ArgumentException("WaitTimeout must be 0s or an integer from 5s through 50s.", nameof(WaitTimeout));
        }
        if (InlineRowLimit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(InlineRowLimit));
        }
        if (Disposition is not (SqlStatementDisposition.INLINE or SqlStatementDisposition.EXTERNAL_LINKS))
        {
            throw new ArgumentOutOfRangeException(nameof(Disposition));
        }
        if (OnWaitTimeout is not (SqlStatementOnWaitTimeout.CONTINUE or SqlStatementOnWaitTimeout.CANCEL))
        {
            throw new ArgumentOutOfRangeException(nameof(OnWaitTimeout));
        }
    }
}
