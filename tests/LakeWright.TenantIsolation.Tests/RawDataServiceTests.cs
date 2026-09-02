using System.ComponentModel.DataAnnotations;
using LakeWright.Core.Tenancy;
using LakeWright.Databricks;
using LakeWright.Databricks.RawData;

namespace LakeWright.TenantIsolation.Tests;

[Trait("Category", "TenantIsolation")]
public sealed class RawDataServiceTests
{
    [Fact]
    public async Task Builds_one_tenant_scoped_statement_with_only_allow_list_identifiers_and_bound_values()
    {
        var executor = new CapturingExecutor();
        var service = new RawDataService(executor);
        var tenant = TenantContextFactory.ForTenant(TenantId.New(), "analytics");

        var page = await service.QueryAsync(
            tenant,
            Source(),
            new RawDataRequest(
                [new RawDataFilter("name", RawDataFilterOperator.Contains, ["50%_\\x"])],
                new RawDataSort("amount", RawDataSortDirection.Descending),
                Skip: 5,
                Take: 25),
            TestContext.Current.CancellationToken);

        executor.Calls.ShouldBe(1);
        executor.Statement.Tenant.ShouldBeSameAs(tenant);
        executor.Statement.Sql.ShouldBe("SELECT CASE WHEN name RLIKE '^[=+\\-@]' THEN CONCAT(chr(39), name) ELSE name END AS name, amount, created FROM orders_view WHERE name LIKE CONCAT('%', :raw_f0_v0, '%') ESCAPE '\\' ORDER BY amount DESC LIMIT :raw_take OFFSET :raw_skip");
        executor.Statement.Sql.ShouldNotContain("50%_");
        executor.Statement.Parameters.ShouldContain(StatementParameter.String("raw_f0_v0", "50\\%\\_\\\\x"));
        executor.Statement.Parameters.ShouldContain(StatementParameter.Int("raw_take", 25));
        executor.Statement.Parameters.ShouldContain(StatementParameter.Int("raw_skip", 5));
        page.Columns.Select(column => column.Name).ShouldBe(["name", "amount", "created"]);
    }

    [Fact]
    public async Task Rejects_request_controlled_identifiers_before_the_warehouse_call()
    {
        var executor = new CapturingExecutor();
        var service = new RawDataService(executor);

        await Should.ThrowAsync<ValidationException>(() => service.QueryAsync(
            TenantContextFactory.ForTenant(TenantId.New(), "analytics"),
            Source(),
            new RawDataRequest(Sort: new RawDataSort("amount DESC; DROP TABLE orders")),
            TestContext.Current.CancellationToken));

        executor.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Rejects_invalid_typed_values_and_excessive_pages_before_the_warehouse_call()
    {
        var executor = new CapturingExecutor();
        var service = new RawDataService(executor, new RawDataOptions { MaximumPageSize = 50 });
        var tenant = TenantContextFactory.ForTenant(TenantId.New(), "analytics");

        await Should.ThrowAsync<ValidationException>(() => service.QueryAsync(
            tenant,
            Source(),
            new RawDataRequest([new RawDataFilter("amount", RawDataFilterOperator.GreaterThan, ["not-a-number"])]),
            TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ValidationException>(() => service.QueryAsync(
            tenant,
            Source(),
            new RawDataRequest(Take: 51),
            TestContext.Current.CancellationToken));

        executor.Calls.ShouldBe(0);
    }

    private static RawDataSource Source() => new()
    {
        Name = "orders",
        BaseView = "orders_view",
        Fields =
        [
            new RawDataField { Name = "name", Column = "name", DisplayName = "Name", Kind = RawDataKind.Text, Filterable = true, Sortable = true },
            new RawDataField { Name = "amount", Column = "amount", DisplayName = "Amount", Kind = RawDataKind.Number, Filterable = true, Sortable = true },
            new RawDataField { Name = "created", Column = "created", DisplayName = "Created", Kind = RawDataKind.Date, Filterable = true, Sortable = true },
        ],
        DefaultOrder = new RawDataSort("created", RawDataSortDirection.Descending),
    };

    private sealed class CapturingExecutor : IStatementExecutor
    {
        public int Calls { get; private set; }
        public TenantScopedStatement Statement { get; private set; }

        public Task<StatementOutcome> ExecuteAsync(TenantScopedStatement statement, CancellationToken cancellationToken)
        {
            Calls++;
            Statement = statement;
            return Task.FromResult<StatementOutcome>(new StatementOutcome.Success([], [], 0, "statement-1"));
        }

        public Task<StatementOutcome> GetAsync(TenantContext tenant, string statementId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CancelAsync(TenantContext tenant, string statementId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
