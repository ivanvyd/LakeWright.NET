using System.Security.Claims;
using System.Text.Encodings.Web;
using LakeWright.AspNetCore;
using LakeWright.Core.Tenancy;
using LakeWright.Multitenancy;
using LakeWright.Multitenancy.Cost;
using LakeWright.Multitenancy.Model;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Lakewright.LoadHarness;

/// <summary>
/// The harness's environment: a testcontainers Postgres and a minimal in-process host built
/// directly via <see cref="HostBuilder"/> + <see cref="TestServer"/>, mirroring the pattern in
/// the test suite (<c>tests/LakeWright.TenantIsolation.Tests/TestApi.cs</c>).
/// </summary>
/// <remarks>
/// The harness does not use <see cref="WebApplicationFactory{TEntryPoint}"/> with the sample's
/// <c>Program</c> as the entry point. That setup runs the sample's full startup
/// (appsettings.json config, demo auth scheme, demo seed) and competes with the harness's
/// own in-memory config and seed; the first end-to-end run showed 100% errors because the
/// host's <c>LakewrightDbContext</c> connection could not see the harness's freshly-committed
/// rows. The manual-host pattern removes that lifecycle ambiguity and matches what the
/// existing test suite already does.
/// </remarks>
public sealed class HarnessEnvironment : IAsyncDisposable
{
    private readonly PostgreSqlContainer _postgres;
    private readonly IHost _host;

    /// <summary>
    /// The IDs of the seeded tenants, in order. The harness drives traffic at tenants[0].
    /// The fixture populates this list during <see cref="CreateAsync"/>.
    /// </summary>
    public IReadOnlyList<Guid> SeededTenantIds { get; }

    /// <summary>The principal used for the harness's authenticated requests.</summary>
    public string Principal { get; }

    private HarnessEnvironment(
        PostgreSqlContainer postgres,
        IHost host,
        IReadOnlyList<Guid> seededTenantIds,
        string principal)
    {
        _postgres = postgres;
        _host = host;
        SeededTenantIds = seededTenantIds;
        Principal = principal;
    }

    /// <summary>The connection string for the running Postgres container.</summary>
    public NpgsqlConnectionStringBuilder PostgresConnectionString =>
        new(_postgres.GetConnectionString());

    /// <summary>The HTTP client that talks to the in-process host.</summary>
    public HttpClient Client => _host.GetTestClient();

    /// <summary>
    /// Bring up Postgres, build the host manually, seed the harness's tenants, and return a
    /// ready-to-use environment.
    /// </summary>
    public static async Task<HarnessEnvironment> CreateAsync(HarnessOptions options)
    {
        var postgres = new PostgreSqlBuilder(options.PostgresImage)
            // The harness measures pool utilisation against max_connections. Set a known
            // value so the SLO gate is meaningful: at 100 connections and 80% utilisation,
            // the harness is telling us we have 80 of 100 in use at peak.
            .WithEnvironment("POSTGRES_MAX_CONNECTIONS", options.MaxPoolSize.ToString())
            .WithReuse(false)
            .Build();
        await postgres.StartAsync();

        var connectionString = postgres.GetConnectionString();

        // pgcrypto is required for the model's gen_random_uuid() calls. EnsureCreatedAsync
        // does not create extensions; without this, the schema creation below fails on a fresh
        // Postgres 17 image. The CREATE EXTENSION IF NOT EXISTS is idempotent.
        var seedBuilder = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" };
        await using (var conn = new NpgsqlConnection(seedBuilder.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS pgcrypto;";
            await cmd.ExecuteNonQueryAsync();
        }

        // Seed the harness's tenant + membership in the testcontainers Postgres so the host
        // (which connects to the same Postgres) sees the rows. One principal is enough for the
        // harness's purposes; the demo seed is not used because we built the host ourselves
        // without running Program.cs.
        var fixture = new HarnessPostgresFixture(connectionString, options.SeedTenants);
        var seed = await fixture.InitializeAsync();
        var principal = seed.Principals[0];

        // Build the host manually, mirroring tests/.../TestApi.cs.StartAsync. The connection
        // string is passed directly to UseNpgsql rather than via config, so there is no need for
        // ConfigureAppConfiguration. This bypasses the sample's Program entirely, removing
        // the lifecycle ambiguity the previous WebApplicationFactory<Program> path had.
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
                    services.AddLakeWrightCostAttribution();
                    services.Configure<MultitenancyOptions>(o => o.Catalog = "analytics");

                    // The harness's auth scheme is the same header-based pattern the test
                    // suite's StubAuth uses. It accepts the same X-Harness-Principal header
                    // value as the principal, and nothing else. This decouples the harness
                    // from the sample's demo auth (which lives in samples/Signalboard and would
                    // require running Program.Main, which we explicitly avoid here).
                    services.AddAuthentication(HarnessAuth.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, HarnessAuth>(
                            HarnessAuth.SchemeName, _ => { });

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
                    app.UseEndpoints(e =>
                    {
                        e.MapLakeWrightOperations();
                        e.MapLakeWrightCost();
                    });
                }))
            .StartAsync();

        return new HarnessEnvironment(postgres, host, seed.TenantIds, principal);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        await _postgres.DisposeAsync();
    }
}

/// <summary>
/// The harness's auth handler. Accepts any subject via <c>X-Harness-Principal</c>. Mirrors
/// <c>StubAuth</c> in the test suite.
/// </summary>
internal sealed class HarnessAuth(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Harness";
    public const string PrincipalHeader = "X-Harness-Principal";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(PrincipalHeader, out var principal)
            || string.IsNullOrWhiteSpace(principal.ToString()))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, principal.ToString())],
            authenticationType: SchemeName,
            nameType: ClaimTypes.Name,
            roleType: ClaimTypes.Role);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

/// <summary>
/// Records the principal and tenant ids the seed loop just created, so the harness can use them
/// in its requests without re-querying the database.
/// </summary>
internal sealed class SeedResult
{
    public required IReadOnlyList<Guid> TenantIds { get; init; }
    public required IReadOnlyList<string> Principals { get; init; }
}

/// <summary>
/// Stand-in for the test fixture that brings up the harness's database. Seeds organisations
/// and memberships so the harness has stable tenant IDs to hit.
/// </summary>
internal sealed class HarnessPostgresFixture
{
    private readonly string _connectionString;
    private readonly int _seedTenants;
    private int _dbCounter;

    public HarnessPostgresFixture(string connectionString, int seedTenants)
    {
        _connectionString = connectionString;
        _seedTenants = seedTenants;
    }

    public async Task<SeedResult> InitializeAsync()
    {
        // One database per harness run, isolated from the others. Matches the test project
        // convention of fresh databases per fixture.
        var dbName = $"harness_{Interlocked.Increment(ref _dbCounter)}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(_connectionString) { Database = "postgres" };
        await using (var conn = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE {dbName};";
            await cmd.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(_connectionString) { Database = dbName };
        var options = new DbContextOptionsBuilder<LakeWrightDbContext>()
            .UseNpgsql(builder.ConnectionString)
            .Options;

        await using var db = new LakeWrightDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var now = DateTimeOffset.UtcNow;
        var orgs = new List<Organization>();
        var memberships = new List<Membership>();
        var tenantIds = new List<Guid>();
        var principals = new List<string>();
        for (var i = 0; i < _seedTenants; i++)
        {
            var orgId = TenantId.New();
            var principal = $"harness-user-{i + 1}";
            tenantIds.Add(orgId.Value);
            principals.Add(principal);
            var org = new Organization
            {
                Id = orgId,
                Name = $"Tenant {i + 1}",
                Slug = $"tenant-{i + 1}",
                CreatedAt = now,
                Schema = UnityCatalogIdentifier.SchemaForTenant(orgId),
                State = OrganizationState.Active,
            };
            orgs.Add(org);
            memberships.Add(new Membership
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = orgId,
                PrincipalId = principal,
                Role = MembershipRole.Viewer,
                CreatedAt = now,
            });
        }
        db.Organizations.AddRange(orgs);
        db.Memberships.AddRange(memberships);
        await db.SaveChangesAsync();

        return new SeedResult { TenantIds = tenantIds, Principals = principals };
    }
}
