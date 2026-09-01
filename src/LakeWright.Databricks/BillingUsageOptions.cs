using System.ComponentModel.DataAnnotations;

namespace LakeWright.Databricks;

/// <summary>Configuration for privileged reads of Databricks billing system tables.</summary>
public sealed class BillingUsageOptions
{
    public const string SectionName = "DatabricksBilling";

    /// <summary>
    /// Workspace whose usage may be correlated with this application's operation records.
    /// </summary>
    [Required]
    public string WorkspaceId { get; set; } = string.Empty;

    /// <summary>Delay between Statement Execution polls after the initial wait expires.</summary>
    [Range(50, 10_000)]
    public int PollIntervalMilliseconds { get; set; } = 250;

    /// <summary>Overall deadline for a billing statement, including all polls.</summary>
    [Range(1, 900)]
    public int PollingTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Server-side wait before Databricks cancels a billing statement that has not completed.
    /// </summary>
    [Range(5, 50)]
    public int SubmissionWaitTimeoutSeconds { get; set; } = 30;

    /// <summary>Maximum billing statements this process may execute concurrently.</summary>
    [Range(1, 64)]
    public int MaxConcurrentStatements { get; set; } = 4;

    /// <summary>
    /// Maximum active and queued billing statements in this process. Requests beyond the bound
    /// fail with the transient code <c>BILLING_BUSY</c> instead of growing an unbounded queue.
    /// </summary>
    [Range(1, 1_024)]
    public int MaxOutstandingStatements { get; set; } = 32;
}
