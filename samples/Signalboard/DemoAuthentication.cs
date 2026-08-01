using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Azure.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
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

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(DemoTenants.PrincipalFor(principal.ToString()), SchemeName)));
    }
}

public static class DemoAuthenticationExtensions
{
    /// <summary>Scheme that picks between the cookie and the header per request.</summary>
    private const string Either = "Either";

    /// <summary>
    /// Cookies for the dashboard, a header for curl.
    /// </summary>
    /// <remarks>
    /// ADR 0007 chose cookie authentication so the browser never handles a token, and the header
    /// scheme stays because a sample you cannot drive from a terminal is hard to check. A policy
    /// scheme forwards to whichever the request actually carries, so neither has to know about the
    /// other.
    /// </remarks>
    public static IServiceCollection AddDemoAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(Either)
            .AddPolicyScheme(Either, Either, o => o.ForwardDefaultSelector = context =>
                context.Request.Headers.ContainsKey(DemoAuthenticationHandler.PrincipalHeader)
                    ? DemoAuthenticationHandler.SchemeName
                    : CookieAuthenticationDefaults.AuthenticationScheme)
            .AddScheme<AuthenticationSchemeOptions, DemoAuthenticationHandler>(
                DemoAuthenticationHandler.SchemeName, _ => { })
            .AddCookie(o =>
            {
                o.Cookie.Name = "signalboard";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.LoginPath = "/signin";

                // The dashboard is a Blazor page, so a redirect to the sign-in page is right. The
                // API under /organizations is not, and a 302 there would turn "you are not
                // authenticated" into a page of HTML that no client can read.
                o.Events.OnRedirectToLogin = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/organizations"))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    }

                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                };
            });

        return services;
    }

    /// <summary>
    /// The two form posts that sign a visitor in and out.
    /// </summary>
    /// <remarks>
    /// Endpoints rather than component code: writing an auth cookie needs the HTTP response, and a
    /// Blazor circuit has already sent its headers by the time an event handler runs.
    /// </remarks>
    public static IEndpointRouteBuilder MapDemoAuthentication(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapPost("/signin", async (HttpContext http, [FromForm] string principal) =>
        {
            // Only the seeded people, so the sign-in page cannot be used to mint an arbitrary
            // subject. The header scheme still accepts anything, which is the documented shortcut;
            // there is no reason for the browser path to widen it further.
            if (!DemoTenants.IsKnown(principal))
            {
                return Results.Redirect("/signin");
            }

            await http.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                DemoTenants.PrincipalFor(principal));

            return Results.Redirect("/operations");
        }).AllowAnonymous().DisableAntiforgery();

        routes.MapPost("/signout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/");
        }).AllowAnonymous().DisableAntiforgery();

        return routes;
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
