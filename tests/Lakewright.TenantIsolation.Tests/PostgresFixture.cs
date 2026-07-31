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
}

[CollectionDefinition(nameof(PostgresTests))]
public sealed class PostgresTests : ICollectionFixture<PostgresFixture>;
