using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LakeWright.Core.Tenancy;
using Microsoft.Extensions.Options;

namespace LakeWright.Embedding;

/// <summary>
/// The AI/BI external-embedding token exchange, in three legs.
/// </summary>
/// <remarks>
/// <para>
/// The tenant is not a parameter the caller chooses. It comes from a <see cref="TenantContext"/>,
/// which can only be produced by resolving membership against the application database, so
/// possession of one is proof of authorisation — the same property ADR 0002 gives the query layer.
/// That matters more here than it looks: <c>external_value</c> is signed into the token and becomes
/// <c>__aibi_external_value</c> in the dashboard's own SQL, so whoever picks it picks which
/// tenant's rows the viewer sees. A signature taking a caller-supplied string would move the
/// isolation boundary into every call site.
/// </para>
/// <para>
/// Leg three is the part the vendor samples show without explaining. The <c>/tokeninfo</c> response
/// is not a document to read — it is the body of the next request. Every field is echoed back as a
/// form parameter, and only <c>authorization_details</c> is re-serialised, because it arrives as
/// JSON and has to travel as a JSON *string*. Fields are copied blind rather than mapped onto a
/// record, so a field Databricks adds later is forwarded rather than silently dropped, which would
/// produce a token that is valid and wrongly scoped.
/// </para>
/// </remarks>
public sealed class DashboardTokenBroker : IDashboardTokenBroker
{
    /// <summary>
    /// Databricks documents this ceiling on the two values combined. Exceeding it fails the
    /// exchange with a message about neither of them, so it is checked here where the names are.
    /// </summary>
    private const int MaxViewerBytes = 1024;

    private readonly HttpClient _http;
    private readonly DashboardEmbeddingOptions _options;
    private readonly TimeProvider _time;
    private readonly IWorkspaceTokenCache? _workspaceCache;
    private readonly IEmbedTokenCache? _embedCache;

    public DashboardTokenBroker(
        HttpClient http,
        IOptions<DashboardEmbeddingOptions> options,
        TimeProvider time,
        IWorkspaceTokenCache? workspaceCache = null,
        IEmbedTokenCache? embedCache = null)
    {
        _http = http;
        _options = options.Value;
        _time = time;
        _workspaceCache = workspaceCache;
        _embedCache = embedCache;
    }

    public async Task<EmbedToken> IssueAsync(
        TenantContext tenant,
        string dashboardId,
        string viewerId,
        CancellationToken cancellationToken = default)
    {
        // ScopeVersion changes the external value so a scope change bypasses the vendor's
        // cached filter. See docs/decisions/0017-scope-version.md for the delimiter contract.
        var externalValue = string.IsNullOrEmpty(tenant.ScopeVersion)
            ? tenant.TenantId.ToString()
            : $"{tenant.TenantId.ToString()}~{tenant.ScopeVersion}";

        var size = Encoding.UTF8.GetByteCount(viewerId) + Encoding.UTF8.GetByteCount(externalValue);
        if (size > MaxViewerBytes)
        {
            throw new ArgumentException(
                $"external_viewer_id and external_value are {size} bytes together; Databricks allows {MaxViewerBytes}.",
                nameof(viewerId));
        }

        if (_embedCache is not null)
        {
            return await _embedCache.GetOrAddAsync(
                new EmbedCacheKey(tenant.TenantId, tenant.ScopeVersion, dashboardId, viewerId),
                ct => new ValueTask<EmbedToken>(IssueUncachedAsync(tenant, dashboardId, viewerId, externalValue, ct)),
                cancellationToken).ConfigureAwait(false);
        }

        return await IssueUncachedAsync(tenant, dashboardId, viewerId, externalValue, cancellationToken).ConfigureAwait(false);
    }

    private async Task<EmbedToken> IssueUncachedAsync(
        TenantContext tenant,
        string dashboardId,
        string viewerId,
        string externalValue,
        CancellationToken cancellationToken)
    {
        var basic = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}")));

        var workspaceToken = await AcquireWorkspaceTokenAsync(basic, cancellationToken).ConfigureAwait(false);

        var tokenInfo = await ReadTokenInfoAsync(
            workspaceToken.AccessToken,
            dashboardId,
            viewerId,
            externalValue,
            cancellationToken).ConfigureAwait(false);

        return await RequestTokenAsync(basic, tokenInfo, cancellationToken).ConfigureAwait(false);
    }

    private async Task<EmbedToken> AcquireWorkspaceTokenAsync(
        AuthenticationHeaderValue basic,
        CancellationToken cancellationToken)
    {
        if (_workspaceCache is not null)
        {
            return await _workspaceCache.GetOrAddAsync(
                _options.ClientId,
                ct => new ValueTask<EmbedToken>(RequestTokenAsync(
                    basic,
                    new Dictionary<string, string>
                    {
                        ["grant_type"] = "client_credentials",
                        ["scope"] = "all-apis",
                    },
                    ct)),
                cancellationToken).ConfigureAwait(false);
        }

        return await RequestTokenAsync(
            basic,
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["scope"] = "all-apis",
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<EmbedToken> RequestTokenAsync(
        AuthenticationHeaderValue basic,
        IDictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "oidc/v1/token")
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Authorization = basic;

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        using var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var root = payload.RootElement;

        if (!root.TryGetProperty("access_token", out var token) || token.GetString() is not { } value)
        {
            throw new InvalidOperationException("The token response carried no access_token.");
        }

        // Databricks issues one-hour tokens today. Reading the response rather than assuming that
        // is what stops a caching caller from serving a dead token if the lifetime ever changes.
        var lifetime = root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromHours(1);

        return new EmbedToken(value, _time.GetUtcNow().Add(lifetime));
    }

    private async Task<Dictionary<string, string>> ReadTokenInfoAsync(
        string workspaceToken,
        string dashboardId,
        string viewerId,
        string externalValue,
        CancellationToken cancellationToken)
    {
        var url =
            $"api/2.0/lakeview/dashboards/{Uri.EscapeDataString(dashboardId)}/published/tokeninfo" +
            $"?external_viewer_id={Uri.EscapeDataString(viewerId)}" +
            $"&external_value={Uri.EscapeDataString(externalValue)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", workspaceToken);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (JsonNode.Parse(body) is not JsonObject info)
        {
            throw new InvalidOperationException("The tokeninfo response was not a JSON object.");
        }

        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "client_credentials",
        };

        foreach (var (name, node) in info)
        {
            if (node is null)
            {
                continue;
            }

            // authorization_details arrives as JSON and must travel as a JSON string. Everything
            // else is already scalar and goes across as written.
            form[name] = name == "authorization_details"
                ? node.ToJsonString()
                : node.GetValueKind() == JsonValueKind.String
                    ? node.GetValue<string>()
                    : node.ToJsonString();
        }

        return form;
    }

    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // The body carries the reason — a dashboard that is not published, a service principal
        // without CAN RUN — and the status code alone does not distinguish them.
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Databricks answered {(int)response.StatusCode} {response.ReasonPhrase}: {body}"),
            inner: null,
            statusCode: response.StatusCode);
    }
}
