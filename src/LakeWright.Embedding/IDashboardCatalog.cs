namespace LakeWright.Embedding;

/// <summary>
/// Lists published AI/BI dashboards in a workspace.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="IDashboardTokenBroker"/>, which mints a per-viewer token for a
/// dashboard the caller already knows about. The catalog answers the question "which
/// dashboards exist in this workspace?" and returns enough metadata to decide which one to
/// embed. It authenticates as the ops service principal (ADR 0019) because listing is a
/// backend operation; the embed principal is locked down to a token-minting role and
/// should not see the dashboard set.
/// </para>
/// <para>
/// The library is intentionally not opinionated about *which* dashboards a tenant is
/// allowed to embed: that is the application's per-tenant assignment model and the
/// <c>private-project.DatabricksDashboardCatalog</c> reference in the gap analysis is the shape of a
/// complete one. The library only exposes the workspace list and lets the caller intersect
/// it with its own assignment table.
/// </para>
/// </remarks>
public interface IDashboardCatalog
{
    /// <summary>
    /// Lists every published dashboard in the workspace, paginated by the workspace's own
    /// <c>page_token</c> semantics.
    /// </summary>
    /// <param name="pageSize">
    /// How many dashboards to ask the workspace for in a single request. The workspace caps
    /// this; pass <c>null</c> to accept the workspace default.
    /// </param>
    /// <param name="pageToken">
    /// The opaque token returned by the previous call's <see cref="DashboardCatalogPage.NextPageToken"/>,
    /// or <c>null</c> to start at the beginning.
    /// </param>
    /// <param name="cancellationToken">Cancels the list request.</param>
    Task<DashboardCatalogPage> ListAsync(
        int? pageSize = null,
        string? pageToken = null,
        CancellationToken cancellationToken = default);
}

/// <summary>One page of dashboard catalog results.</summary>
/// <param name="Dashboards">
/// The dashboards returned in this page. Order is the workspace's order, which is not
/// guaranteed to be stable across calls.
/// </param>
/// <param name="NextPageToken">
/// The token to pass to the next call's <c>pageToken</c> parameter to continue paging, or
/// <c>null</c> if this is the last page.
/// </param>
public sealed record DashboardCatalogPage(
    IReadOnlyList<DashboardSummary> Dashboards,
    string? NextPageToken);

/// <summary>
/// A published AI/BI dashboard, as much as the workspace's list endpoint exposes.
/// </summary>
/// <remarks>
/// The vendor's list endpoint returns the fields below plus a number of write-side fields
/// (lifecycle state, etag, etc.) that the library does not need and does not promise to
/// surface. Adding a field is cheap; removing or renaming one would be a breaking change,
/// so the projection is intentionally narrow.
/// </remarks>
/// <param name="Id">The dashboard's stable identifier; the same value the embed broker accepts.</param>
/// <param name="DisplayName">The dashboard's display name as authored in the workspace.</param>
/// <param name="ParentPath">The workspace folder path the dashboard lives under, if any.</param>
/// <param name="PublishedAt">The instant the dashboard was last published.</param>
public sealed record DashboardSummary(
    string Id,
    string DisplayName,
    string? ParentPath,
    DateTimeOffset? PublishedAt);
