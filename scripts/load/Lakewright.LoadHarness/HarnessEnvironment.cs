using LakeWright.AspNetCore;
using LakeWright.Core.Tenancy;
using LakeWright.Multitenancy;
using LakeWright.Multitenancy.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;

// The sample's entry point is `Program` in the Signalboard assembly; the harness's own
// top-level statements generate an implicit `Program` here. Disambiguate by aliasing the
// sample's name to a local alias that names what it is.
using SampleProgram = global::Program;

namespace Lakewright.LoadHarness;

/// <summary>
/// The harness's environment: a testcontainers Postgres and the sample's host in-process
/// via <see cref="WebApplicationFactory{TEntryPoint}"/>.
/// </summary>
public sealed class HarnessEnvironment : IAsyncDisposable
{
    private readonly PostgreSqlContainer _postgres;
    private readonly WebApplicationFactory<SampleProgram> _factory;

    /// <summary>
    /// The IDs of the seeded tenants, in order. The harness drives traffic at tenants[0].
    /// The fixture populates this list during <see cref="CreateAsync"/>.
    /// </summary>
    public IReadOnlyList<Guid> SeededTenantIds { get; }

    private HarnessEnvironment(
        PostgreSqlContainer postgres,
        WebApplicationFactory<SampleProgram> factory,
        IReadOnlyList<Guid> seededTenantIds)
    {
        _postgres = postgres;
        _factory = factory;
        SeededTenantIds = seededTenantIds;
    }

    /// <summary>The connection string for the running Postgres container.</summary>
    public NpgsqlConnectionStringBuilder PostgresConnectionString =>
        new(_postgres.GetConnectionString());

    /// <summary>The HTTP client that talks to the in-process host.</summary>
    public HttpClient Client => _factory.CreateClient();

    /// <summary>
    /// Bring up Postgres, boot the host, and seed a configurable number of tenants.
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

        // Seed tenants on the harness's Postgres. The sample's seeding code targets its own
        // configured connection string, which is irrelevant here — we just need a known set of
        // tenants the harness can hit. The actual work happens in the API host below, which
        // uses the harness's connection string via configuration.
        var seedBuilder = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" };
        await using (var conn = new NpgsqlConnection(seedBuilder.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS pgcrypto;";
            await cmd.ExecuteNonQueryAsync();
        }

        var fixture = new HarnessPostgresFixture(connectionString, options.SeedTenants);
        var seed = await fixture.InitializeAsync();

        var factory = new WebApplicationFactory<SampleProgram>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                // The sample's host reads its content root at startup to find static web assets.
                // Without this override, the factory uses the harness's CWD and the static-web-assets
                // loader fails to find wwwroot/. The sample's directory is the only place the
                // content root exists; pin it there.
                builder.UseSolutionRelativeContentRoot(Path.Combine("samples", "Signalboard"));
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Lakewright"] = connectionString,
                        ["Multitenancy:Catalog"] = "analytics",
                    });
                });
            });

        return new HarnessEnvironment(postgres, factory, seed.TenantIds);
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
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
