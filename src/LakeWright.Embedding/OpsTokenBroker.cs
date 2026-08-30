using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LakeWright.Embedding;

/// <summary>
/// The default <see cref="IOpsTokenBroker"/>, implemented as a direct call to the workspace's
/// <c>/oidc/v1/token</c> endpoint with the ops service principal's OAuth credentials.
/// </summary>
/// <remarks>
/// <para>
/// Each call mints a fresh token. Caching the ops token is a separate piece of work (the same
/// <c>IWorkspaceTokenCache</c> abstraction introduced for the embed path, keyed on the ops
/// <c>ClientId</c>); this class does not cache because the embed cache lives in a separate
/// branch and pulling it in would create a cross-PR dependency. A consumer that needs cached
/// ops tokens today can wrap this broker in a decorator.
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

    public OpsTokenBroker(
        HttpClient http,
        IOptions<DashboardOpsOptions> options,
        TimeProvider time)
    {
        _http = http;
        _options = options.Value;
        _time = time;
    }

    public async Task<EmbedToken> AcquireAsync(CancellationToken cancellationToken = default)
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

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        using var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var root = payload.RootElement;

        if (!root.TryGetProperty("access_token", out var token) || token.GetString() is not { } value)
        {
            throw new InvalidOperationException("The token response carried no access_token.");
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
        throw new HttpRequestException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Databricks answered {(int)response.StatusCode} {response.ReasonPhrase}: {body}"),
            inner: null,
            statusCode: response.StatusCode);
    }
}
