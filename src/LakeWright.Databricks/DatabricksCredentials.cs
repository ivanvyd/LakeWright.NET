using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using LakeWright.Core.Tokens;
using Microsoft.Extensions.Options;

namespace LakeWright.Databricks;

/// <summary>
/// Acquires an access token for a Databricks workspace.
/// </summary>
public interface IDatabricksCredential
{
    /// <summary>Gets an access token without exposing its storage or refresh mechanism.</summary>
    ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>Uses an Azure credential to acquire a Databricks access token.</summary>
public sealed class TokenCredentialDatabricksCredential(TokenCredential credential) : IDatabricksCredential
{
    private const string DatabricksScope = "2ff814a6-3304-4ab8-85cb-cd0e6f879c1d/.default";

    public async ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default) =>
        (await credential.GetTokenAsync(
            new TokenRequestContext([DatabricksScope]),
            cancellationToken).ConfigureAwait(false)).Token;
}

internal sealed class ServicePrincipalDatabricksCredential : IDatabricksCredential
{
    private readonly HttpClient _http;
    private readonly DatabricksOptions _options;
    private readonly MemoryTokenCache<string, WorkspaceToken> _cache;
    private readonly TimeProvider _time;

    public ServicePrincipalDatabricksCredential(
        HttpClient http,
        IOptions<DatabricksOptions> options,
        TimeProvider time)
    {
        _http = http;
        _options = options.Value;
        _time = time;
        _cache = new MemoryTokenCache<string, WorkspaceToken>(time, token => token.ExpiresAt);
    }

    public async ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        var token = await _cache.GetOrAddAsync(
            _options.ClientId!,
            GetUncachedAsync,
            cancellationToken).ConfigureAwait(false);
        return token.Value;
    }

    private async ValueTask<WorkspaceToken> GetUncachedAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "oidc/v1/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["scope"] = "all-apis",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}")));

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var accessToken = payload.RootElement.GetProperty("access_token").GetString();
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("Databricks did not return an access token.");
        }

        var expiresIn = payload.RootElement.GetProperty("expires_in").GetInt32();
        if (expiresIn <= 30)
        {
            throw new InvalidOperationException("Databricks returned an access token that expires too soon to use safely.");
        }

        return new WorkspaceToken(accessToken, _time.GetUtcNow().AddSeconds(expiresIn));
    }

    private sealed record WorkspaceToken(string Value, DateTimeOffset ExpiresAt);
}

internal sealed class DatabricksTokenCredential(IDatabricksCredential credential, TimeProvider time) : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new(
            credential.GetTokenAsync(cancellationToken).AsTask().GetAwaiter().GetResult(),
            time.GetUtcNow().AddMinutes(1));

    public async override ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken) =>
        new(await credential.GetTokenAsync(cancellationToken).ConfigureAwait(false), time.GetUtcNow().AddMinutes(1));
}
