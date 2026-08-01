using System.ComponentModel.DataAnnotations;
using Microsoft.Azure.Databricks.Client.Models;

namespace Lakewright.Databricks;

public sealed class DatabricksOptions
{
    public const string SectionName = "Databricks";

    [Required]
    public string WorkspaceUrl { get; set; } = string.Empty;

    [Required]
    public string WarehouseId { get; set; } = string.Empty;

    // No Catalog here. It lives on MultitenancyOptions, arrives on every call through
    // TenantContext.Catalog, and a second copy bound to a different config section was inert
    // configuration that a reader would reasonably have set and trusted.

    /// <summary>
    /// How long to wait before a statement becomes a polled operation. The API rejects anything
    /// above 50 seconds, and anything near it holds a request thread for no good reason.
    /// </summary>
    public string WaitTimeout { get; set; } = "30s";

    /// <summary>
    /// Where results are returned. <c>INLINE</c> puts rows in the response;
    /// <c>EXTERNAL_LINKS</c> returns presigned URLs and yields <see cref="StatementOutcome.LargeResult"/>.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>INLINE</c> because the queries a customer-facing product runs are dashboard
    /// sized, and inline rows are the only shape a request can return without a second fetch.
    /// Switch to <c>EXTERNAL_LINKS</c> for exports.
    /// </remarks>
    public SqlStatementDisposition Disposition { get; set; } = SqlStatementDisposition.INLINE;

    /// <summary>
    /// Row cap applied to <c>INLINE</c> statements.
    /// </summary>
    /// <remarks>
    /// Inline results hard-fail at 25 MiB and cancel the statement rather than truncating, so an
    /// unbounded interactive query fails outright instead of returning less. This turns that into
    /// a bounded answer.
    /// </remarks>
    public long InlineRowLimit { get; set; } = 10_000;
}
