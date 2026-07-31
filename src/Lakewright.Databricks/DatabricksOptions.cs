using System.ComponentModel.DataAnnotations;

namespace Lakewright.Databricks;

public sealed class DatabricksOptions
{
    public const string SectionName = "Databricks";

    [Required]
    public string WorkspaceUrl { get; set; } = string.Empty;

    [Required]
    public string WarehouseId { get; set; } = string.Empty;

    /// <summary>
    /// Unity Catalog catalog holding tenant schemas. One catalog per environment; tenants are
    /// separated by schema within it. See ADR 0002.
    /// </summary>
    [Required]
    public string Catalog { get; set; } = string.Empty;

    /// <summary>
    /// How long to wait before a statement becomes a polled operation. The API rejects anything
    /// above 50 seconds, and anything near it holds a request thread for no good reason.
    /// </summary>
    public string WaitTimeout { get; set; } = "30s";
}
