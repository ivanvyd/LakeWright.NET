using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LakeWright.Embedding;

/// <summary>
/// The default <see cref="IDashboardCatalog"/>, implemented as a direct call to the workspace's
/// <c>GET /api/2.0/lakeview/dashboards</c> endpoint authenticated as the ops service principal.
/// </summary>
/// <remarks>
/// <para>
/// Each call acquires a fresh ops workspace token via <see cref="IOpsTokenBroker"/>, then
/// issues the list request. That is two HTTP roundtrips per page; an upstream consumer that
/// wants a single roundtrip can add caching of the ops token (the same shape the embed
/// broker uses) without changing this class.
/// </para>
/// <para>
/// The vendor's response shape is read defensively: fields are optional because the workspace
/// has historically changed the list response between minor versions, and a field the
/// library does not care about (lifecycle state, etag) being missing should not fail the
/// list. The library only projects the fields it needs and ignores the rest.
/// </para>
/// </remarks>
public sealed class DashboardCatalog : IDashboardCatalog
{
    private readonly HttpClient _http;
    private readonly IOpsTokenBroker _opsTokens;

    public DashboardCatalog(HttpClient http, IOpsTokenBroker opsTokens)
    {
        _http = http;
        _opsTokens = opsTokens;
    }

    public async Task<DashboardCatalogPage> ListAsync(
        int? pageSize = null,
        string? pageToken = null,
        CancellationToken cancellationToken = default)
    {
        var token = await _opsTokens.AcquireAsync(cancellationToken).ConfigureAwait(false);

        var query = new List<string>();
        if (pageSize is { } size)
        {
            query.Add($"page_size={Uri.EscapeDataString(size.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
        }
        if (!string.IsNullOrEmpty(pageToken))
        {
            query.Add($"page_token={Uri.EscapeDataString(pageToken)}");
        }
        var url = "api/2.0/lakeview/dashboards" + (query.Count == 0 ? string.Empty : "?" + string.Join('&', query));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Databricks answered {(int)response.StatusCode} {response.ReasonPhrase}: {body}",
                inner: null,
                statusCode: response.StatusCode);
        }

        using var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var root = payload.RootElement;

        var dashboards = new List<DashboardSummary>();
        if (root.TryGetProperty("dashboards", out var dashboardsElement)
            && dashboardsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in dashboardsElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var id = entry.TryGetProperty("dashboard_id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                    ? idProp.GetString()
                    : null;
                if (string.IsNullOrEmpty(id))
                {
                    // The vendor does not document this field as optional, but the same
                    // response shape is reused by the drafts endpoint where the field is
                    // named differently. Skip entries we cannot identify.
                    continue;
                }

                var name = entry.TryGetProperty("display_name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                    ? nameProp.GetString() ?? string.Empty
                    : string.Empty;

                var path = entry.TryGetProperty("parent_path", out var pathProp) && pathProp.ValueKind == JsonValueKind.String
                    ? pathProp.GetString()
                    : null;

                DateTimeOffset? publishedAt = null;
                if (entry.TryGetProperty("published_at", out var publishedProp)
                    && publishedProp.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(publishedProp.GetString(), out var parsed))
                {
                    publishedAt = parsed;
                }

                dashboards.Add(new DashboardSummary(id, name, path, publishedAt));
            }
        }

        string? nextPageToken = null;
        if (root.TryGetProperty("next_page_token", out var tokenProp)
            && tokenProp.ValueKind == JsonValueKind.String)
        {
            var value = tokenProp.GetString();
            if (!string.IsNullOrEmpty(value))
            {
                nextPageToken = value;
            }
        }

        return new DashboardCatalogPage(dashboards, nextPageToken);
    }
}
