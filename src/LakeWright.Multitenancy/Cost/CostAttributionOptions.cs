using System.ComponentModel.DataAnnotations;

namespace LakeWright.Multitenancy.Cost;

/// <summary>
/// Configuration for <see cref="OperationCostAttribution"/>.
/// </summary>
public sealed class CostAttributionOptions
{
    public const string SectionName = "LakeWright:CostAttribution";

    /// <summary>
    /// The Databricks warehouse SKU the proxy cost is computed against.
    /// </summary>
    /// <remarks>
    /// The DBU/hour rate differs by SKU. Picking one fixes the proxy to that one, and a product
    /// running more than one warehouse should either pick a representative SKU or move to a real
    /// billing-table read. The default is a serverless 2X-Small, which is the warehouse the
    /// reference deployment ships with.
    /// </remarks>
    [Required]
    public string WarehouseSku { get; set; } = "2X-Small Serverless";

    /// <summary>
    /// Databricks Units per wall-clock hour for <see cref="WarehouseSku"/>.
    /// </summary>
    /// <remarks>
    /// Pinned by configuration rather than hard-coded, because rates change with platform pricing
    /// updates and the right answer to "is this current?" is whatever the operator just
    /// confirmed, not whatever the library last knew.
    /// </remarks>
    [Range(0.0, double.MaxValue)]
    public double DbusPerHour { get; set; } = 0.30;
}
