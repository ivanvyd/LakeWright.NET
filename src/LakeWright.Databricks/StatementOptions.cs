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

    /// <summary>Overall local budget for initial submission and subsequent polls.</summary>
    public TimeSpan TotalBudget { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Where completed rows are returned.</summary>
    public SqlStatementDisposition Disposition { get; set; } = SqlStatementDisposition.INLINE;

    /// <summary>Maximum rows requested for inline statements.</summary>
    public long InlineRowLimit { get; set; } = 10_000;

    internal void Validate()
    {
        if (PollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(PollInterval), "PollInterval must be positive.");
        }

        if (TotalBudget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(TotalBudget), "TotalBudget must be positive.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(WaitTimeout);
    }
}
