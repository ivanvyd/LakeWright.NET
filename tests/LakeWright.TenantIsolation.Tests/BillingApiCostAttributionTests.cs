using LakeWright.Core.Cost;
using LakeWright.Core.Tenancy;
using LakeWright.Databricks;
using LakeWright.Multitenancy.Cost;
using Microsoft.Azure.Databricks.Client;
using Microsoft.Azure.Databricks.Client.Models;
using Microsoft.Extensions.Options;
using static LakeWright.TenantIsolation.Tests.TestApi;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// The <c>system.billing.usage</c> cost reader against a stubbed Databricks client.
/// </summary>
/// <remarks>
/// The full path — a real Databricks call, a real billing grant, a real workspace — is a
/// <c>Category=Live</c> test the contributor who wires the billing reader runs once after
/// the grant is in place. The unit tests here pin the parts that can be pinned without
/// a workspace: the SQL has the right shape, the response is parsed in the right order,
/// the failure paths surface the right error codes, and the cost endpoint's discriminator
/// is <see cref="CostSource.Billing"/>.
/// </remarks>
[Trait("Category", "TenantIsolation")]
public class BillingApiCostAttributionTests
{
    [Fact]
    public async Task ResolveAsync_returns_billing_source_with_zero_dbus_for_an_empty_window()
    {
        // Arrange — the stub returns a successful query with zero rows. A tenant who has not
        // run anything in the window should see an empty breakdown, not a default zero with
        // an empty breakdown.
        var tenant = AcmeContext();
        var client = new StubDatabricksClient(rows: []);
        var attribution = new BillingApiCostAttribution(
            client,
            Options.Create(new DatabricksOptions
            {
                WorkspaceUrl = "https://example",
                WarehouseId = "warehouse-1",
            }));

        // Act
        var summary = await attribution.ResolveAsync(
            tenant,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
            TestContext.Current.CancellationToken);

        // Assert
        summary.Source.ShouldBe(CostSource.Billing);
        summary.DbusConsumed.ShouldBe(0m);
        summary.WarehouseSku.ShouldBeNull(
            "a billing read returns its rate from the data, so WarehouseSku is null even when " +
            "the configuration carries a value.");
        summary.ByKind.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_aggregates_rows_by_kind()
    {
        // Arrange — three rows: two analysis operations (4.0 DBU) and one report operation
        // (1.0 DBU). The parser groups them by Kind, sums the DBU, and orders by descending
        // spend so a glance shows where the money went.
        var tenant = AcmeContext();
        var client = new StubDatabricksClient(rows: new[]
        {
            new object?[] { "analysis", "1", "3600.0", "4.0" },
            new object?[] { "report",   "1", " 900.0", "1.0" },
            new object?[] { "analysis", "1", "7200.0", "8.0" },
        });
        var attribution = new BillingApiCostAttribution(
            client,
            Options.Create(new DatabricksOptions
            {
                WorkspaceUrl = "https://example",
                WarehouseId = "warehouse-1",
            }));

        // Act
        var summary = await attribution.ResolveAsync(
            tenant,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
            TestContext.Current.CancellationToken);

        // Assert
        summary.Source.ShouldBe(CostSource.Billing);
        summary.DbusConsumed.ShouldBe(13.0m);
        summary.ByKind.Count.ShouldBe(2);
        summary.ByKind[0].Kind.ShouldBe("analysis");
        summary.ByKind[0].Operations.ShouldBe(2);
        summary.ByKind[0].DbusConsumed.ShouldBe(12.0m);
        summary.ByKind[1].Kind.ShouldBe("report");
        summary.ByKind[1].DbusConsumed.ShouldBe(1.0m);
    }

    [Fact]
    public async Task ResolveAsync_raises_BillingQueryException_when_Databricks_returns_FAILED()
    {
        // Arrange — the stub returns FAILED with PERMISSION_DENIED, the exact code a workspace
        // without the metastore-admin grant produces. The cost endpoint maps the exception to
        // 502 with the code in the body.
        var tenant = AcmeContext();
        var client = new StubDatabricksClient(
            failure: new StatementExecutionError { ErrorCode = "PERMISSION_DENIED", Message = "no grant" });
        var attribution = new BillingApiCostAttribution(
            client,
            Options.Create(new DatabricksOptions
            {
                WorkspaceUrl = "https://example",
                WarehouseId = "warehouse-1",
            }));

        // Act + Assert
        var ex = await Should.ThrowAsync<BillingQueryException>(async () =>
            await attribution.ResolveAsync(
                tenant,
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
                TestContext.Current.CancellationToken));
        ex.Code.ShouldBe("PERMISSION_DENIED");
    }

    private static TenantContext AcmeContext() => TenantContextFactory.ForTenant(AcmeId, "analytics");

    /// <summary>
    /// A minimal <see cref="DatabricksClient"/> stub that returns a fixed successful response
    /// (with optional rows) or a fixed failure. Only the statement-execution path is exercised;
    /// every other property of the client is unused.
    /// </summary>
    private sealed class StubDatabricksClient(
        IReadOnlyList<object?[]>? rows = null,
        StatementExecutionError? failure = null) : DatabricksClient
    {
        public override IStatementExecutionApi SQL { get; } = new StubStatementExecution(rows, failure);
    }

    private sealed class StubStatementExecution(
        IReadOnlyList<object?[]>? rows,
        StatementExecutionError? failure) : IStatementExecutionApi
    {
        public Task<StatementExecution> Execute(SqlStatement request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (failure is not null)
            {
                return Task.FromResult(new StatementExecution
                {
                    Status = new StatementExecutionStatus { State = StatementExecutionState.FAILED, Error = failure },
                });
            }

            return Task.FromResult(new StatementExecution
            {
                Status = new StatementExecutionStatus { State = StatementExecutionState.SUCCEEDED },
                Manifest = new ResultManifest
                {
                    Schema = new ResultSchema
                    {
                        Columns = new List<ColumnInfo>
                        {
                            new() { Name = "Kind" },
                            new() { Name = "Operations" },
                            new() { Name = "ElapsedSeconds" },
                            new() { Name = "DbusConsumed" },
                        },
                    },
                },
                Result = new ResultData
                {
                    DataArray = (rows ?? []).Cast<IReadOnlyList<object?>>().ToList(),
                },
            });
        }

        public Task<StatementExecution> Get(string statementId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("billing read does not poll");

        public Task Cancel(string statementId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("billing read does not cancel");

        public Task<Chunk> GetResultChunk(string statementId, int chunkIndex, CancellationToken cancellationToken) =>
            throw new NotSupportedException("billing read does not chunk");
    }
}
