using LakeWright.Core.Tenancy;
using LakeWright.Databricks;
using Microsoft.Azure.Databricks.Client.Models;
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
    [InlineData("SELECT ':tenant_id'")]
    [InlineData("-- :tenant_id\nSELECT * FROM events")]
    [InlineData("/* :tenant_id */ SELECT * FROM events")]
    [InlineData("SELECT `:tenant_id` FROM events")]
    public void A_shared_schema_statement_without_a_real_tenant_token_is_refused(string sql)
    {
        var statement = TenantScopedStatement.Create(SharedTenant(), sql);

        Should.Throw<TenantScopeMissingException>(() => statement.ParametersForExecution())
            .Message.ShouldContain(":tenant_id");
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

    [Fact]
    public async Task The_executor_refuses_an_unscoped_shared_schema_statement_before_the_session()
    {
        var session = new RecordingSession();
        var executor = new DatabricksStatementExecutor(session, new DatabricksOptions { WarehouseId = "warehouse" });
        var statement = TenantScopedStatement.Create(SharedTenant(), "SELECT * FROM events");

        await Should.ThrowAsync<TenantScopeMissingException>(
            () => executor.ExecuteAsync(statement, TestContext.Current.CancellationToken));

        session.Calls.ShouldBe(0);
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
    }

    [Fact]
    public async Task The_export_refuses_an_unscoped_shared_schema_statement_before_the_session()
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

        await Should.ThrowAsync<TenantScopeMissingException>(StreamAsync);
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
