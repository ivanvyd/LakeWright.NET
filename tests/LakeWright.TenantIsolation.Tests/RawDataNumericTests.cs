using System.ComponentModel.DataAnnotations;
using LakeWright.Core.Tenancy;
using LakeWright.Databricks;
using LakeWright.Databricks.RawData;

namespace LakeWright.TenantIsolation.Tests;

[Trait("Category", "TenantIsolation")]
public sealed class RawDataNumericTests
{
    [Theory]
    [InlineData("9007199254740991")]
    [InlineData("9007199254740992")]
    [InlineData("9007199254740993")]
    [InlineData("9223372036854775807")]
    [InlineData("-9223372036854775808")]
    public async Task Integer_filters_preserve_every_digit(string value)
    {
        foreach (var operation in new[] { RawDataFilterOperator.Equal, RawDataFilterOperator.In, RawDataFilterOperator.GreaterThan })
        {
            var executor = new CapturingExecutor();
            await QueryAsync(executor, Field(RawDataKind.WholeNumber), value, operation);
            executor.Statement.Parameters.ShouldContain(new StatementParameter("raw_f0_v0", value, "BIGINT"));
            executor.Statement.Sql.ShouldNotContain(value);
        }
    }

    [Theory]
    [InlineData("999999999999999999999999999999999999.99", "999999999999999999999999999999999999.99")]
    [InlineData("-9007199254740993.01", "-9007199254740993.01")]
    [InlineData("+000123.4500", "123.45")]
    [InlineData("-0.000", "0")]
    public async Task Decimal_filters_do_not_round_through_clr_numeric_types(string value, string expected)
    {
        foreach (var operation in new[] { RawDataFilterOperator.Equal, RawDataFilterOperator.In, RawDataFilterOperator.LessThanOrEqual })
        {
            var executor = new CapturingExecutor();
            var page = await QueryAsync(executor, Field(RawDataKind.FixedPoint, 38, 2), value, operation);
            executor.Statement.Parameters.ShouldContain(new StatementParameter("raw_f0_v0", expected, "DECIMAL(38,2)"));
            page.Columns.Single().Precision.ShouldBe(38);
            page.Columns.Single().Scale.ShouldBe(2);
        }
    }

    [Theory]
    [InlineData(RawDataKind.WholeNumber, "9223372036854775808")]
    [InlineData(RawDataKind.WholeNumber, "-9223372036854775809")]
    [InlineData(RawDataKind.WholeNumber, "1.1")]
    [InlineData(RawDataKind.WholeNumber, "1e3")]
    [InlineData(RawDataKind.Number, "NaN")]
    [InlineData(RawDataKind.Number, "Infinity")]
    [InlineData(RawDataKind.Number, "1e309")]
    [InlineData(RawDataKind.FixedPoint, "1234.56")]
    [InlineData(RawDataKind.FixedPoint, "1.234")]
    [InlineData(RawDataKind.FixedPoint, "1,23")]
    [InlineData(RawDataKind.FixedPoint, "1e2")]
    [InlineData(RawDataKind.FixedPoint, "1.2.3")]
    [InlineData(RawDataKind.FixedPoint, "NaN")]
    [InlineData(RawDataKind.FixedPoint, ".1")]
    public async Task Invalid_or_inexact_values_are_denied_before_any_warehouse_call(RawDataKind kind, string value)
    {
        var executor = new CapturingExecutor();
        var field = kind == RawDataKind.FixedPoint ? Field(kind, 5, 2) : Field(kind);
        await Should.ThrowAsync<ValidationException>(() => QueryAsync(executor, field, value));
        executor.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, 0)]
    [InlineData(39, 2)]
    [InlineData(5, -1)]
    [InlineData(5, 6)]
    public async Task Decimal_metadata_is_validated_before_querying(int? precision, int? scale)
    {
        var executor = new CapturingExecutor();
        await Should.ThrowAsync<ValidationException>(() => QueryAsync(executor, Field(RawDataKind.FixedPoint, precision, scale), "1"));
        executor.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task All_fractional_decimal_and_approximate_number_keep_their_distinct_contracts()
    {
        var exact = new CapturingExecutor();
        await QueryAsync(exact, Field(RawDataKind.FixedPoint, 3, 3), "0.001");
        exact.Statement.Parameters.ShouldContain(new StatementParameter("raw_f0_v0", "0.001", "DECIMAL(3,3)"));
        var approximate = new CapturingExecutor();
        await QueryAsync(approximate, Field(RawDataKind.Number), "1.25e2");
        approximate.Statement.Parameters.ShouldContain(StatementParameter.Double("raw_f0_v0", 125));
    }

    private static RawDataField Field(RawDataKind kind, int? precision = null, int? scale = null) => new()
    {
        Name = "amount",
        Column = "amount",
        DisplayName = "Amount",
        Kind = kind,
        Precision = precision,
        Scale = scale,
        Filterable = true,
        Sortable = true,
    };

    private static Task<RawDataPage> QueryAsync(CapturingExecutor executor, RawDataField field, string value, RawDataFilterOperator operation = RawDataFilterOperator.Equal) =>
        new RawDataService(executor).QueryAsync(
            TenantContextFactory.ForTenant(TenantId.New(), "analytics"),
            new RawDataSource { Name = "orders", BaseView = "orders_view", Fields = [field], DefaultOrder = new RawDataSort("amount") },
            new RawDataRequest([new RawDataFilter("amount", operation, [value])]), TestContext.Current.CancellationToken);

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
