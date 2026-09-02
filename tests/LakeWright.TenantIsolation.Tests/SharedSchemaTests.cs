using LakeWright.Core.Tenancy;
using LakeWright.Databricks;
using Microsoft.Azure.Databricks.Client.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LakeWright.TenantIsolation.Tests;

[Trait("Category", "TenantIsolation")]
public class SharedSchemaTests
{
    [Fact]
    public void A_shared_schema_context_carries_the_required_tenant_parameter()
    {
        var context = new ResolverTenantContextFactory().ForSharedTenant(
            TenantId.Parse("0198f000-0000-7000-8000-0000000000c1"),
            "analytics",
            "shared");

        var location = context.Location.ShouldBeOfType<TenantLocation.SharedSchema>();
        location.TenantParameter.ShouldBe("tenant_id");
        context.Catalog.ShouldBe("analytics");
        context.Schema.ShouldBe("shared");
    }

    [Theory]
    [InlineData("SELECT * FROM events")]
    [InlineData("SELECT ':tenant_id' AS tenant_id")]
    [InlineData("SELECT * FROM events WHERE :tenant_id IS NOT NULL")]
    public void A_shared_schema_query_is_wrapped_in_a_tenant_predicate(string sql)
    {
        var statement = TenantScopedStatement.Create(SharedTenant(), sql);

        statement.SqlForExecution().ShouldBe(
            $"SELECT * FROM ({sql}) AS lakewright_tenant_scope WHERE lakewright_tenant_scope.tenant_id = :tenant_id");
    }

    [Fact]
    public void A_shared_schema_statement_binds_the_context_tenant()
    {
        var statement = TenantScopedStatement.Create(
            SharedTenant(),
            "SELECT * FROM events WHERE tenant_id = :tenant_id",
            StatementParameter.String("status", "open"));

        var parameters = statement.ParametersForExecution();

        parameters.ShouldContain(parameter => parameter.Name == "tenant_id"
            && parameter.Value == SharedTenant().TenantId.ToString()
            && parameter.Type == "STRING");
    }

    [Fact]
    public void A_caller_cannot_override_the_shared_schema_tenant_parameter()
    {
        var statement = TenantScopedStatement.Create(
            SharedTenant(),
            "SELECT * FROM events WHERE tenant_id = :tenant_id",
            StatementParameter.String("tenant_id", "other"));

        Should.Throw<TenantScopeMissingException>(() => statement.ParametersForExecution())
            .Message.ShouldContain("cannot be supplied by the caller");
    }

    [Theory]
    [InlineData("DELETE FROM events")]
    [InlineData("SELECT * FROM events;")]
    public void A_shared_schema_statement_that_cannot_be_safely_wrapped_is_refused(string sql)
    {
        var statement = TenantScopedStatement.Create(SharedTenant(), sql);

        Should.Throw<TenantScopeMissingException>(() => statement.SqlForExecution())
            .Message.ShouldContain("single SELECT or WITH query");
    }

    [Theory]
    [InlineData("SELECT ';' AS punctuation")]
    [InlineData("SELECT 1 -- ; inside a comment")]
    [InlineData("SELECT `;` AS punctuation")]
    public void A_semicolon_inside_non_executable_sql_is_allowed(string sql)
    {
        var statement = TenantScopedStatement.Create(SharedTenant(), sql);

        statement.SqlForExecution().ShouldContain("lakewright_tenant_scope");
    }

    [Fact]
    public void A_stacked_shared_schema_statement_is_refused_before_the_session()
    {
        var statement = TenantScopedStatement.Create(SharedTenant(), "SELECT * FROM events; SELECT 1");

        Should.Throw<TenantScopeMissingException>(() => statement.SqlForExecution())
            .Message.ShouldContain("one executable statement");
    }

    [Fact]
    public async Task The_executor_wraps_an_unscoped_shared_schema_statement_before_the_session()
    {
        var session = new RecordingSession();
        var executor = new DatabricksStatementExecutor(session, new DatabricksOptions { WarehouseId = "warehouse" });
        var statement = TenantScopedStatement.Create(SharedTenant(), "SELECT * FROM events");

        await executor.ExecuteAsync(statement, TestContext.Current.CancellationToken);

        session.Calls.ShouldBe(1);
        session.LastRequest!.Statement.ShouldContain("WHERE lakewright_tenant_scope.tenant_id = :tenant_id");
    }

    [Fact]
    public async Task The_executor_binds_the_context_tenant_before_calling_the_session()
    {
        var session = new RecordingSession();
        var executor = new DatabricksStatementExecutor(session, new DatabricksOptions { WarehouseId = "warehouse" });
        var statement = TenantScopedStatement.Create(
            SharedTenant(),
            "SELECT * FROM events WHERE tenant_id = :tenant_id");

        await executor.ExecuteAsync(statement, TestContext.Current.CancellationToken);

        session.Calls.ShouldBe(1);
        session.LastRequest!.Parameters.ShouldContain(parameter => parameter.Name == "tenant_id"
            && parameter.Value == SharedTenant().TenantId.ToString());
        session.LastRequest.Statement.ShouldContain("WHERE lakewright_tenant_scope.tenant_id = :tenant_id");
    }

    [Fact]
    public async Task The_export_wraps_a_shared_schema_query_before_calling_the_session()
    {
        var session = new RecordingSession();
        using var http = new HttpClient();
        var export = new DatabricksTenantScopedExport(
            session,
            new DatabricksOptions { WarehouseId = "warehouse" },
            http,
            NullLogger<DatabricksTenantScopedExport>.Instance);
        var statement = TenantScopedStatement.Create(SharedTenant(), "SELECT * FROM events");

        async Task StreamAsync()
        {
            await foreach (var _ in export.StreamAsync(statement, TestContext.Current.CancellationToken)) { }
        }

        await Should.ThrowAsync<InvalidOperationException>(StreamAsync);
        session.Calls.ShouldBe(1);
        session.LastRequest!.Statement.ShouldContain("WHERE lakewright_tenant_scope.tenant_id = :tenant_id");
        session.LastRequest.Parameters.ShouldContain(parameter => parameter.Name == "tenant_id"
            && parameter.Value == SharedTenant().TenantId.ToString());
    }

    [Fact]
    public async Task The_export_refuses_a_caller_supplied_shared_schema_tenant_before_the_session()
    {
        var session = new RecordingSession();
        using var http = new HttpClient();
        var export = new DatabricksTenantScopedExport(
            session,
            new DatabricksOptions { WarehouseId = "warehouse" },
            http,
            NullLogger<DatabricksTenantScopedExport>.Instance);
        var statement = TenantScopedStatement.Create(
            SharedTenant(),
            "SELECT * FROM events",
            StatementParameter.String("tenant_id", "other"));

        async Task StreamAsync()
        {
            await foreach (var _ in export.StreamAsync(statement, TestContext.Current.CancellationToken)) { }
        }

        await Should.ThrowAsync<TenantScopeMissingException>(StreamAsync);
        session.Calls.ShouldBe(0);
    }

    [Fact]
    public void A_scope_table_strategy_wraps_and_binds_the_resolved_tenant()
    {
        var tenant = new ResolverTenantContextFactory().ForSharedTenant(
            TenantId.Parse("0198f000-0000-7000-8000-0000000000c1"),
            "analytics",
            "shared",
            scopeStrategyName: "scope-table");
        var strategy = new ScopeTableScope(new ScopeTableScopeOptions
        {
            ScopeTable = "analytics.tenant_entities",
            TenantColumn = "tenant_id",
            ScopeTypeColumn = "entity_type",
            Mappings = [new ScopeTableMapping("entity_id", "entity_id", "account")],
        });

        var scoped = TenantScopedStatement.Create(tenant, "SELECT entity_id, amount FROM events")
            .ScopedForExecution(strategy);

        scoped.Sql.ShouldContain("FROM analytics.tenant_entities AS lakewright_scope");
        scoped.Sql.ShouldContain("lakewright_tenant_scope.entity_id = lakewright_scope.entity_id");
        scoped.Parameters.ShouldContain(parameter => parameter.Name == "tenant_id"
            && parameter.Value == tenant.TenantId.ToString());
        scoped.Parameters.ShouldContain(parameter => parameter.Name == "lakewright_scope_type_0"
            && parameter.Value == "account");
    }

    [Fact]
    public void A_scope_table_strategy_refuses_a_query_without_a_mapped_fact_key()
    {
        var strategy = new ScopeTableScope(new ScopeTableScopeOptions
        {
            ScopeTable = "analytics.tenant_entities",
            Mappings = [new ScopeTableMapping("entity_id", "entity_id")],
        });

        Should.Throw<TenantScopeMissingException>(() => strategy.Apply("SELECT amount FROM events", SharedTenant()))
            .Message.ShouldContain("entity_id");
    }

    [Fact]
    public async Task The_executor_uses_the_strategy_selected_by_the_resolved_context()
    {
        var services = new ServiceCollection();
        services.AddLakeWrightDatabricks(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Databricks:WorkspaceUrl"] = "https://workspace.example",
            ["Databricks:WarehouseId"] = "warehouse",
        }).Build());
        services.AddLakeWrightScopeTableScope(new ScopeTableScopeOptions
        {
            ScopeTable = "analytics.tenant_entities",
            Mappings = [new ScopeTableMapping("entity_id", "entity_id")],
        });
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var session = new RecordingSession();
        var executor = new DatabricksStatementExecutor(
            session,
            new DatabricksOptions { WarehouseId = "warehouse" },
            scope.ServiceProvider.GetRequiredService<ITenantScopeStrategyResolver>());
        var tenant = new ResolverTenantContextFactory().ForSharedTenant(
            TenantId.Parse("0198f000-0000-7000-8000-0000000000c1"),
            "analytics",
            "shared",
            scopeStrategyName: "scope-table");

        await executor.ExecuteAsync(
            TenantScopedStatement.Create(tenant, "SELECT entity_id, amount FROM events"),
            TestContext.Current.CancellationToken);

        session.Calls.ShouldBe(1);
        session.LastRequest!.Statement.ShouldContain("FROM analytics.tenant_entities AS lakewright_scope");
    }

    [Fact]
    public async Task An_unknown_strategy_on_the_resolved_context_is_refused_before_the_session()
    {
        var services = new ServiceCollection();
        services.AddLakeWrightDatabricks(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Databricks:WorkspaceUrl"] = "https://workspace.example",
            ["Databricks:WarehouseId"] = "warehouse",
        }).Build());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var session = new RecordingSession();
        var executor = new DatabricksStatementExecutor(
            session,
            new DatabricksOptions { WarehouseId = "warehouse" },
            scope.ServiceProvider.GetRequiredService<ITenantScopeStrategyResolver>());
        var tenant = new ResolverTenantContextFactory().ForSharedTenant(
            TenantId.Parse("0198f000-0000-7000-8000-0000000000c1"),
            "analytics",
            "shared",
            scopeStrategyName: "not-registered");

        await Should.ThrowAsync<TenantScopeMissingException>(() => executor.ExecuteAsync(
            TenantScopedStatement.Create(tenant, "SELECT tenant_id, amount FROM events"),
            TestContext.Current.CancellationToken));

        session.Calls.ShouldBe(0);
    }

    private static TenantContext SharedTenant() => new ResolverTenantContextFactory().ForSharedTenant(
        TenantId.Parse("0198f000-0000-7000-8000-0000000000c1"),
        "analytics",
        "shared");

    private sealed class RecordingSession : IDatabricksStatementSession
    {
        public int Calls { get; private set; }
        public SqlStatement? LastRequest { get; private set; }

        public Task<StatementOutcome> ExecuteAsync(SqlStatement request, TenantId tenantId, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult<StatementOutcome>(new StatementOutcome.Success([], [], 0, "statement"));
        }

        public Task<StatementOutcome> GetAsync(TenantId tenantId, string statementId, CancellationToken cancellationToken) =>
            Task.FromResult<StatementOutcome>(new StatementOutcome.Pending(statementId));

        public Task CancelAsync(string statementId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
