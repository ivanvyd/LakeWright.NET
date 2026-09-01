using LakeWright.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Testcontainers.PostgreSql;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// One Postgres container for the assembly, a fresh database per test.
/// </summary>
/// <remarks>
/// Container startup dominates the run, so it is shared. Databases are not: a test that observes
/// another test's rows is the same class of bug this suite exists to catch, and it would be
/// embarrassing to have it in the harness.
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithEnvironment("POSTGRES_MAX_CONNECTIONS", "200")
        .WithReuse(false)
        .Build();

    private int _databaseCounter;

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    public async Task<LakeWrightDbContext> NewDatabaseAsync()
    {
        var name = $"iso_{Interlocked.Increment(ref _databaseCounter)}";
        await _container.ExecScriptAsync($"CREATE DATABASE {name};");

        var builder = new Npgsql.NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = name,
            // A unique database is created for every test. Pooling those one-use connections
            // retained a pool per database for the lifetime of the process and eventually made
            // the partition suite fail for lack of server connections.
            Pooling = false
        };

        var options = new DbContextOptionsBuilder<LakeWrightDbContext>()
            .UseNpgsql(builder.ConnectionString)
            .Options;

        var db = new LakeWrightDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    /// <summary>
    /// A fresh context over an existing database. Concurrency tests need one connection each;
    /// sharing a context serialises them and the test passes without exercising anything.
    /// </summary>
    /// <remarks>
    /// The optional interceptor is how a test forces a race rather than hoping for one. Two tasks
    /// started together still tend to serialise — the second reads after the first has committed —
    /// so a test that merely runs them concurrently passes with the constraint under test removed.
    /// An interceptor that writes the competing row during <c>SaveChanges</c> makes the collision
    /// happen every time.
    /// </remarks>
    public static LakeWrightDbContext ContextFor(
        string connectionString,
        IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<LakeWrightDbContext>().UseNpgsql(connectionString);

        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        return new LakeWrightDbContext(builder.Options);
    }

    /// <summary>
    /// Opens a second context against the same database as the restricted application role.
    /// </summary>
    /// <remarks>
    /// The owner of a table keeps privileges that <c>REVOKE</c> does not remove, so a lockdown
    /// tested while connected as the owner passes without proving anything. These tests connect as
    /// the role the application actually uses.
    /// </remarks>
    public static LakeWrightDbContext AsApplicationRole(
        LakeWrightDbContext owner,
        string role,
        string password)
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(owner.Database.GetConnectionString())
        {
            Username = role,
            Password = password
        };

        var options = new DbContextOptionsBuilder<LakeWrightDbContext>()
            .UseNpgsql(builder.ConnectionString)
            .Options;

        return new LakeWrightDbContext(options);
    }
}

[CollectionDefinition(nameof(PostgresTests))]
public sealed class PostgresTests : ICollectionFixture<PostgresFixture>;
