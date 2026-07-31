using Lakewright.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Lakewright.TenantIsolation.Tests;

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
        .WithReuse(false)
        .Build();

    private int _databaseCounter;

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    public async Task<LakewrightDbContext> NewDatabaseAsync()
    {
        var name = $"iso_{Interlocked.Increment(ref _databaseCounter)}";
        await _container.ExecScriptAsync($"CREATE DATABASE {name};");

        var builder = new Npgsql.NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = name
        };

        var options = new DbContextOptionsBuilder<LakewrightDbContext>()
            .UseNpgsql(builder.ConnectionString)
            .Options;

        var db = new LakewrightDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    /// <summary>
    /// Opens a second context against the same database as the restricted application role.
    /// </summary>
    /// <remarks>
    /// The owner of a table keeps privileges that <c>REVOKE</c> does not remove, so a lockdown
    /// tested while connected as the owner passes without proving anything. These tests connect as
    /// the role the application actually uses.
    /// </remarks>
    /// <summary>
    /// A fresh context over an existing database. Concurrency tests need one connection each;
    /// sharing a context serialises them and the test passes without exercising anything.
    /// </summary>
    public static LakewrightDbContext ContextFor(string connectionString) =>
        new(new DbContextOptionsBuilder<LakewrightDbContext>()
            .UseNpgsql(connectionString)
            .Options);

    public static LakewrightDbContext AsApplicationRole(
        LakewrightDbContext owner,
        string role,
        string password)
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(owner.Database.GetConnectionString())
        {
            Username = role,
            Password = password
        };

        var options = new DbContextOptionsBuilder<LakewrightDbContext>()
            .UseNpgsql(builder.ConnectionString)
            .Options;

        return new LakewrightDbContext(options);
    }
}

[CollectionDefinition(nameof(PostgresTests))]
public sealed class PostgresTests : ICollectionFixture<PostgresFixture>;
