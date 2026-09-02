using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LakeWright.Core.Features;
using Microsoft.Extensions.Options;

namespace LakeWright.Embedding;

/// <summary>
/// The default <see cref="IOpsTokenBroker"/>, implemented as a direct call to the workspace's
/// <c>/oidc/v1/token</c> endpoint with the ops service principal's OAuth credentials.
/// </summary>
/// <remarks>
/// <para>
/// The default cache collapses concurrent exchanges and serves the workspace-issued token until
/// shortly before its expiry. Consumers can replace <see cref="IOpsTokenCache"/> when they need
/// a different in-process policy.
/// </para>
/// <para>
/// The lifetime is read from the response's <c>expires_in</c>, falling back to one hour if
/// the field is absent. The embed broker does the same; reading rather than assuming is what
/// keeps a caching caller from serving a dead token if the lifetime ever changes.
/// </para>
/// </remarks>
public sealed class OpsTokenBroker : IOpsTokenBroker
{
    private readonly HttpClient _http;
    private readonly DashboardOpsOptions _options;
    private readonly TimeProvider _time;
    private readonly IOpsTokenCache? _cache;
    private readonly ILakeWrightFeatureGate _features;

    public OpsTokenBroker(
        HttpClient http,
        IOptions<DashboardOpsOptions> options,
        TimeProvider time,
        IOpsTokenCache? cache = null,
        ILakeWrightFeatureGate? features = null)
    {
        _http = http;
        _options = options.Value;
        _time = time;
        _cache = cache;
        _features = features ?? new AlwaysOnFeatureGate();
    }

    public async Task<EmbedToken> AcquireAsync(CancellationToken cancellationToken = default)
    {
        _features.EnsureEnabled(LakeWrightFeatures.Operations);
        if (_cache is not null)
        {
            return await _cache.GetOrAddAsync(
                _options.ClientId,
                ct => new ValueTask<EmbedToken>(AcquireUncachedAsync(ct)),
                cancellationToken).ConfigureAwait(false);
        }

        return await AcquireUncachedAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<EmbedToken> AcquireUncachedAsync(CancellationToken cancellationToken)
    {
        var basic = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}")));

        using var request = new HttpRequestMessage(HttpMethod.Post, "oidc/v1/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["scope"] = "all-apis",
            }),
        };
        request.Headers.Authorization = basic;

        using var response = await EmbeddingHttp.SendAsync(_http, request, cancellationToken).ConfigureAwait(false);
        await ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        using var payload = EmbeddingHttp.ParseJson(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), "the ops OAuth token exchange");
        var root = payload.RootElement;

        if (!root.TryGetProperty("access_token", out var token) || token.GetString() is not { } value)
        {
            throw new WorkspaceRejectedException(System.Net.HttpStatusCode.BadGateway, "The token exchange response carried no access_token.");
        }

        var lifetime = root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromHours(1);

        return new EmbedToken(value, _time.GetUtcNow().Add(lifetime));
    }

    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new WorkspaceRejectedException(
            response.StatusCode,
            body.Length <= 1024 ? body : body[..1024]);
    }
}
