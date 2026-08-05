using LakeWright.Core.Tenancy;

namespace LakeWright.Embedding;

/// <summary>
/// Mints a browser-safe, viewer-scoped token for an embedded AI/BI dashboard.
/// </summary>
public interface IDashboardTokenBroker
{
    /// <summary>
    /// Exchanges the application's service principal credentials for a token scoped to one
    /// dashboard and one viewer, carrying <paramref name="tenant"/> as the row filter.
    /// </summary>
    /// <param name="tenant">
    /// The resolved tenant. Its identifier becomes <c>external_value</c>, which dashboard datasets
    /// read as <c>__aibi_external_value</c>. The caller does not choose this — see
    /// <see cref="DashboardTokenBroker"/> for why.
    /// </param>
    /// <param name="dashboardId">The published dashboard to scope the token to.</param>
    /// <param name="viewerId">
    /// A stable, non-identifying handle for the viewer. It reaches Databricks audit logs, and
    /// Databricks requires that it carry no personally identifiable information.
    /// </param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    Task<EmbedToken> IssueAsync(
        TenantContext tenant,
        string dashboardId,
        string viewerId,
        CancellationToken cancellationToken = default);
}

/// <summary>A token safe to hand to a browser, and the instant it stops being usable.</summary>
/// <remarks>
/// Databricks issues these for one hour. <see cref="ExpiresAt"/> is computed from the response
/// rather than assumed, because a caller that caches on an assumed lifetime serves a dead token
/// the day the lifetime changes.
/// </remarks>
public sealed record EmbedToken(string AccessToken, DateTimeOffset ExpiresAt);
