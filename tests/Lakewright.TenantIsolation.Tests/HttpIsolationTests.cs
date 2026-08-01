using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Lakewright.AspNetCore;
using Lakewright.Core.Tenancy;
using Lakewright.Multitenancy;
using Lakewright.Multitenancy.Model;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lakewright.TenantIsolation.Tests;

/// <summary>
/// Cross-tenant isolation over HTTP, which is where a customer actually meets it.
/// </summary>
/// <remarks>
/// The store-level tests prove a query cannot reach another tenant's rows. These prove the same
/// thing through routing, authentication and authorization, because an endpoint can leak by
/// answering the wrong status code just as easily as by returning the wrong rows.
/// </remarks>
[Trait("Category", "TenantIsolation")]
[Collection(nameof(PostgresTests))]
public class HttpIsolationTests(PostgresFixture postgres)
{
    private static readonly TenantId AcmeId = TenantId.Parse("0198f000-0000-7000-8000-0000000000aa");
    private static readonly TenantId GlobexId = TenantId.Parse("0198f000-0000-7000-8000-0000000000bb");

    private const string Alice = "auth0|alice";   // Admin at Acme
    private const string Bob = "auth0|bob";       // Admin at Globex
    private const string Vera = "auth0|vera";     // Viewer at Acme

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

    private static async Task<(IHost Host, HttpClient Client)> StartAsync(PostgresFixture postgres)
    {
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
                    services.AddDbContext<LakewrightDbContext>(o => o.UseNpgsql(connectionString));
                    services.AddScoped<ITenantContextResolver, EfTenantContextResolver>();
                    services.AddScoped<IMembershipReader, EfMembershipReader>();
                    services.AddScoped<OperationStore>();
                    services.AddHttpContextAccessor();
                    services.AddScoped<ITenantContextAccessor, HttpTenantContextAccessor>();
                    services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, TenantRoleHandler>();
                    services.Configure<MultitenancyOptions>(o => o.Catalog = "analytics");

                    services.AddAuthentication(StubAuth.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, StubAuth>(StubAuth.SchemeName, _ => { });

                    services.AddAuthorizationBuilder()
                        .AddPolicy(TenantPolicies.Viewer, p => p.AddRequirements(new TenantRoleRequirement(MembershipRole.Viewer)))
                        .AddPolicy(TenantPolicies.Member, p => p.AddRequirements(new TenantRoleRequirement(MembershipRole.Member)))
                        .AddPolicy(TenantPolicies.Admin, p => p.AddRequirements(new TenantRoleRequirement(MembershipRole.Admin)))
                        .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                            .RequireAuthenticatedUser().Build());

                    services.AddRouting();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseLakewrightTenancy();
                    app.UseAuthorization();
                    app.UseEndpoints(e => e.MapLakewrightOperations());
                }))
            .StartAsync();

        return (host, host.GetTestClient());
    }

    private static HttpRequestMessage As(string principal, HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(StubAuth.PrincipalHeader, principal);
        return request;
    }

    [Fact]
    public async Task An_operation_belonging_to_another_tenant_is_not_found()
    {
        // Arrange — Alice creates an operation at Acme; Bob knows its id.
        var ct = TestContext.Current.CancellationToken;
        var (host, client) = await StartAsync(postgres);
        using var _h = host;

        var start = As(Alice, HttpMethod.Post, $"/organizations/{AcmeId.Value}/operations");
        start.Content = JsonContent.Create(new StartOperationRequest("analysis"));
        var created = await client.SendAsync(start, ct);
        var operation = await created.Content.ReadFromJsonAsync<OperationResponse>(ct);

        // Act — Bob asks for it under his own organization, and under Acme's.
        var underGlobex = await client.SendAsync(
            As(Bob, HttpMethod.Get, $"/organizations/{GlobexId.Value}/operations/{operation!.Id}"), ct);
        var underAcme = await client.SendAsync(
            As(Bob, HttpMethod.Get, $"/organizations/{AcmeId.Value}/operations/{operation.Id}"), ct);

        // Assert — 404 both ways, never 403. A 403 would confirm the operation exists.
        created.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        underGlobex.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        underAcme.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_member_of_no_organization_cannot_reach_it_at_all()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (host, client) = await StartAsync(postgres);
        using var _h = host;

        // Act
        var response = await client.SendAsync(
            As("auth0|stranger", HttpMethod.Get, $"/organizations/{AcmeId.Value}/operations/{Guid.CreateVersion7()}"), ct);

        // Assert — the tenant middleware refuses before authorization is consulted.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_unauthenticated_request_is_refused_by_the_fallback_policy()
    {
        // Arrange — no principal header, so the stub returns NoResult.
        var ct = TestContext.Current.CancellationToken;
        var (host, client) = await StartAsync(postgres);
        using var _h = host;

        // Act
        var response = await client.GetAsync(
            new Uri($"/organizations/{AcmeId.Value}/operations/{Guid.CreateVersion7()}", UriKind.Relative), ct);

        // Assert — endpoints are protected by default rather than by remembering [Authorize].
        response.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_viewer_can_read_but_cannot_start_work()
    {
        // Arrange — Vera is a Viewer at Acme. Roles are a floor, so Viewer fails a Member policy.
        var ct = TestContext.Current.CancellationToken;
        var (host, client) = await StartAsync(postgres);
        using var _h = host;

        var start = As(Vera, HttpMethod.Post, $"/organizations/{AcmeId.Value}/operations");
        start.Content = JsonContent.Create(new StartOperationRequest("analysis"));

        // Act
        var starting = await client.SendAsync(start, ct);
        var reading = await client.SendAsync(
            As(Vera, HttpMethod.Get, $"/organizations/{AcmeId.Value}/operations/{Guid.CreateVersion7()}"), ct);

        // Assert — starting is forbidden; reading a missing operation is merely not found.
        starting.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        reading.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_malformed_organization_id_is_not_found_rather_than_a_server_error()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (host, client) = await StartAsync(postgres);
        using var _h = host;

        // Act
        var response = await client.SendAsync(
            As(Alice, HttpMethod.Get, $"/organizations/not-a-guid/operations/{Guid.CreateVersion7()}"), ct);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
