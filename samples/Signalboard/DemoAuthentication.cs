using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Azure.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Signalboard;

/// <summary>
/// Authenticates whoever the caller says they are, from a header.
/// </summary>
/// <remarks>
/// **Never do this in a product.** It exists so the sample runs with nothing but a Postgres
/// container: no identity provider to stand up, no client secret to obtain, no redirect URI to
/// register.
///
/// It is a fair demonstration in spite of that, because the thing being demonstrated is *tenant
/// isolation*, and isolation here does not depend on authentication being sound. This handler
/// believes any subject you claim, and you still cannot read another organization's operations,
/// because membership is resolved from the database rather than from anything the caller sent. If
/// the sample used real OIDC the isolation behaviour would be identical.
///
/// Replace it with `AddOpenIdConnect` and the rest of the application is unchanged. That is the
/// point of Lakewright not registering an identity provider itself.
/// </remarks>
public sealed class DemoAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Demo";
    public const string PrincipalHeader = "X-Demo-User";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(PrincipalHeader, out var principal)
            || string.IsNullOrWhiteSpace(principal.ToString()))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, principal.ToString())], SchemeName);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

public static class DemoAuthenticationExtensions
{
    public static IServiceCollection AddDemoAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(DemoAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, DemoAuthenticationHandler>(
                DemoAuthenticationHandler.SchemeName, _ => { });

        return services;
    }
}

/// <summary>
/// Hands Databricks whatever token you exported.
/// </summary>
/// <remarks>
/// A product registers <c>DefaultAzureCredential</c>: on Azure it holds no secret at all, and it
/// refreshes the Entra token before it expires (ADR 0006). This one cannot refresh, because a
/// pasted token is all it has — so it reports the real expiry from the token itself, and Databricks
/// calls fail honestly once it lapses instead of pretending to work.
///
/// A sample cannot assume Azure, which is the only reason this exists. Get a token with
/// <c>az account get-access-token --resource 2ff814a6-3304-4ab8-85cb-cd0e6f879c1d</c>.
/// </remarks>
public sealed class ConfiguredTokenCredential(IConfiguration configuration) : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new(Token, ExpiresOn());

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        ValueTask.FromResult(GetToken(requestContext, cancellationToken));

    private string Token => configuration["Databricks:Token"] ?? string.Empty;

    /// <summary>
    /// The <c>exp</c> claim, so the credential does not claim a lifetime the token does not have.
    /// </summary>
    /// <remarks>
    /// Reading the payload rather than trusting it: this decides when to stop using the token, not
    /// whether to accept it. Databricks validates the signature. An unparseable token is treated as
    /// already expired, which surfaces a bad paste immediately.
    /// </remarks>
    private DateTimeOffset ExpiresOn()
    {
        var parts = Token.Split('.');

        if (parts.Length != 3) { return DateTimeOffset.MinValue; }

        try
        {
            var payload = JsonDocument.Parse(Base64UrlTextEncoder.Decode(parts[1]));

            return payload.RootElement.TryGetProperty("exp", out var exp)
                ? DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64())
                : DateTimeOffset.MinValue;
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            return DateTimeOffset.MinValue;
        }
    }
}
