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
}
