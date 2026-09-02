using LakeWright.Core.Tenancy;
using LakeWright.Databricks;
using Microsoft.Azure.Databricks.Client.Models;

namespace LakeWright.TenantIsolation.Tests;

[Trait("Category", "TenantIsolation")]
public sealed class StatementExecutionTests
{
    [Fact]
    public async Task A_pending_statement_is_polled_to_its_terminal_outcome_inside_its_budget()
    {
        var session = new SequencedSession(
            new StatementOutcome.Pending("statement-1"),
            new StatementOutcome.Success([], [], 0, "statement-1"));
        var executor = new DatabricksStatementExecutor(session, new DatabricksOptions { WarehouseId = "warehouse" });
        var statement = TenantScopedStatement.Create(
            TenantContextFactory.ForTenant(TenantId.New(), "analytics"),
            "SELECT 1",
            new StatementOptions
            {
                WaitTimeout = "30s",
                PollInterval = TimeSpan.FromMilliseconds(1),
                TotalBudget = TimeSpan.FromSeconds(1),
            });

        var result = await executor.ExecuteAsync(statement, TestContext.Current.CancellationToken);

        result.ShouldBeOfType<StatementOutcome.Success>();
        session.GetCalls.ShouldBe(1);
        session.Request!.WaitTimeout.ShouldBe("30s");
        session.Request.OnWaitTimeout.ShouldBe(SqlStatementOnWaitTimeout.CONTINUE);
    }

    [Fact]
    public async Task A_statement_that_outlives_its_budget_has_a_distinct_exception()
    {
        var session = new SequencedSession(new StatementOutcome.Pending("statement-1"));
        var executor = new DatabricksStatementExecutor(session, new DatabricksOptions { WarehouseId = "warehouse" });
        var statement = TenantScopedStatement.Create(
            TenantContextFactory.ForTenant(TenantId.New(), "analytics"),
            "SELECT 1",
            new StatementOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(1),
                TotalBudget = TimeSpan.FromMilliseconds(10),
            });

        var error = await Should.ThrowAsync<StatementBudgetExceededException>(() =>
            executor.ExecuteAsync(statement, TestContext.Current.CancellationToken));

        error.StatementId.ShouldBe("statement-1");
    }

    private sealed class SequencedSession(params StatementOutcome[] outcomes) : IDatabricksStatementSession
    {
        private readonly Queue<StatementOutcome> _outcomes = new(outcomes);

        public int GetCalls { get; private set; }

        public SqlStatement? Request { get; private set; }

        public Task<StatementOutcome> ExecuteAsync(SqlStatement request, TenantId tenantId, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(_outcomes.Dequeue());
        }

        public Task<StatementOutcome> GetAsync(TenantId tenantId, string statementId, CancellationToken cancellationToken)
        {
            GetCalls++;
            return Task.FromResult(_outcomes.Count == 0
                ? new StatementOutcome.Pending(statementId)
                : _outcomes.Dequeue());
        }

        public Task CancelAsync(string statementId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
