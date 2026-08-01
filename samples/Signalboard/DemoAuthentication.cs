using System.Security.Claims;
using System.Text.Encodings.Web;
using Lakewright.AspNetCore;
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
/// Reads the Databricks token from the environment.
/// </summary>
/// <remarks>
/// The reference deployment uses a managed identity and holds no secret (ADR 0006). A sample cannot
/// assume Azure, so it reads whatever token you export — typically
/// <c>az account get-access-token --resource 2ff814a6-3304-4ab8-85cb-cd0e6f879c1d</c>. Empty is
/// fine: the worker is not started without a workspace configured.
/// </remarks>
public sealed class EnvironmentTokenSource(IConfiguration configuration) : IDatabricksTokenSource
{
    public string GetToken() => configuration["Databricks:Token"] ?? string.Empty;
}
