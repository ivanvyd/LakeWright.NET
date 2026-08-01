using System.Security.Claims;
using System.Text.Encodings.Web;
using LakeWright.AspNetCore;
using LakeWright.Core.Tenancy;
using LakeWright.Multitenancy;
using LakeWright.Multitenancy.Model;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// The operations API over a real database, seeded with two organizations and three people.
/// </summary>
/// <remarks>
/// Wired by hand rather than through <c>AddLakeWright</c> so a test can vary one part of the
/// pipeline. The order in <c>Configure</c> is the order the guides tell adopters to use, and a test
/// that quietly reorders it would stop testing what ships.
/// </remarks>
public static class TestApi
{
    public static readonly TenantId AcmeId = TenantId.Parse("0198f000-0000-7000-8000-0000000000aa");
    public static readonly TenantId GlobexId = TenantId.Parse("0198f000-0000-7000-8000-0000000000bb");

    public const string Alice = "auth0|alice";   // Admin at Acme
    public const string Bob = "auth0|bob";       // Admin at Globex
    public const string Vera = "auth0|vera";     // Viewer at Acme

    /// <summary>
    /// Authenticates as whoever the test names in a header.
    /// </summary>
    /// <remarks>
    /// A stub so these tests exercise tenancy rather than an identity provider. It is generous on
    /// purpose: it authenticates anyone who asks. If isolation still holds under an auth handler
    /// that trusts the caller's word about *who they are*, it holds because membership is checked,
    /// which is the claim being tested.
    /// </remarks>
    private sealed class StubAuth(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Stub";
        public const string PrincipalHeader = "X-Test-Principal";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(PrincipalHeader, out var principal) || principal.Count == 0)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, principal.ToString())], SchemeName);

            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }

    public static async Task<(IHost Host, HttpClient Client)> StartAsync(PostgresFixture postgres)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        await using var seed = await postgres.NewDatabaseAsync();
        var now = DateTimeOffset.UtcNow;

        seed.Organizations.AddRange(
            new Organization { Id = AcmeId, Name = "Acme", Slug = "acme", CreatedAt = now, Schema = UnityCatalogIdentifier.SchemaForTenant(AcmeId), State = OrganizationState.Active },
            new Organization { Id = GlobexId, Name = "Globex", Slug = "globex", CreatedAt = now, Schema = UnityCatalogIdentifier.SchemaForTenant(GlobexId), State = OrganizationState.Active });

        seed.Memberships.AddRange(
            new Membership { Id = Guid.CreateVersion7(), OrganizationId = AcmeId, PrincipalId = Alice, Role = MembershipRole.Admin, CreatedAt = now },
            new Membership { Id = Guid.CreateVersion7(), OrganizationId = AcmeId, PrincipalId = Vera, Role = MembershipRole.Viewer, CreatedAt = now },
            new Membership { Id = Guid.CreateVersion7(), OrganizationId = GlobexId, PrincipalId = Bob, Role = MembershipRole.Admin, CreatedAt = now });

        await seed.SaveChangesAsync();
        var connectionString = seed.Database.GetConnectionString()!;

        var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddDbContext<LakeWrightDbContext>(o => o.UseNpgsql(connectionString));
                    services.AddScoped<ITenantContextResolver, EfTenantContextResolver>();
                    services.AddScoped<IMembershipReader, EfMembershipReader>();
                    services.AddSingleton(TimeProvider.System);
                    services.AddScoped<AuditLog>();
                    services.AddScoped<OperationStore>();
                    services.AddHttpContextAccessor();
                    services.AddScoped<ITenantContextAccessor, HttpTenantContextAccessor>();
                    services.AddScoped<IAuthorizationHandler, TenantRoleHandler>();
                    services.Configure<MultitenancyOptions>(o => o.Catalog = "analytics");

                    services.AddAuthentication(StubAuth.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, StubAuth>(StubAuth.SchemeName, _ => { });

                    services.AddAuthorizationBuilder()
                        .AddPolicy(TenantPolicies.Viewer, p => p.AddRequirements(new TenantRoleRequirement(MembershipRole.Viewer)))
                        .AddPolicy(TenantPolicies.Member, p => p.AddRequirements(new TenantRoleRequirement(MembershipRole.Member)))
                        .AddPolicy(TenantPolicies.Admin, p => p.AddRequirements(new TenantRoleRequirement(MembershipRole.Admin)))
                        .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

                    services.AddRouting();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseLakeWrightTenancy();
                    app.UseAuthorization();
                    app.UseEndpoints(e => e.MapLakeWrightOperations());
                }))
            .StartAsync();

        return (host, host.GetTestClient());
    }

    public static HttpRequestMessage As(string principal, HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(StubAuth.PrincipalHeader, principal);
        return request;
    }
}
