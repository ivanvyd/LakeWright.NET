using LakeWright.Core.Cost;
using LakeWright.Core.Tenancy;
using LakeWright.Databricks;
using LakeWright.Multitenancy;
using LakeWright.Multitenancy.Cost;
using LakeWright.Multitenancy.Model;
using Microsoft.Azure.Databricks.Client.Models;
using NSubstitute;
using static LakeWright.TenantIsolation.Tests.TestApi;

namespace LakeWright.TenantIsolation.Tests;

[Trait("Category", "TenantIsolation")]
public class DatabricksBillingUsageReaderTests
{
    private static readonly DateTimeOffset From =
        DateTimeOffset.Parse("2026-08-01T00:00:00Z", null);
    private static readonly DateTimeOffset Until =
        DateTimeOffset.Parse("2026-09-01T00:00:00Z", null);

    [Fact]
    public async Task ReadAsync_binds_workspace_run_and_window_values()
    {
        var session = new StubStatementSession(Success([]));
        var reader = Reader(session);

        var rows = await reader.ReadAsync(
            Acme(),
            From,
            Until,
            [22, 11],
            TestContext.Current.CancellationToken);

        rows.ShouldBeEmpty();
        var request = session.Requests.Single();
        request.Catalog.ShouldBe("system");
        request.Schema.ShouldBe("billing");
        request.Disposition.ShouldBe(SqlStatementDisposition.INLINE);
        request.Statement.ShouldContain("u.workspace_id = :workspace_id");
        request.Statement.ShouldContain("u.usage_metadata.job_run_id");
        request.Statement.ShouldContain("p.pricing.effective_list.default");
        request.Statement.ShouldContain("u.usage_date >= :from_date");
        request.Statement.ShouldContain("u.usage_date <= :until_date");
        request.Statement.ShouldNotContain("workspace-123");
        request.Statement.ShouldNotContain("11,22");

        Parameters(request)["workspace_id"].ShouldBe("workspace-123");
        Parameters(request)["job_run_ids"].ShouldBe("11,22");
        Parameters(request)["from_date"].ShouldBe("2026-08-01");
        Parameters(request)["until_date"].ShouldBe("2026-09-01");
    }

    [Fact]
    public async Task ReadAsync_chunks_large_run_sets_without_changing_the_SQL()
    {
        var session = new StubStatementSession(Success([]), Success([]));
        var reader = Reader(session);

        await reader.ReadAsync(
            Acme(),
            From,
            Until,
            Enumerable.Range(1, 501).Select(value => (long)value).ToArray(),
            TestContext.Current.CancellationToken);

        session.Requests.Count.ShouldBe(2);
        session.Requests.Select(request => request.Statement).Distinct().Count().ShouldBe(1);
        Parameters(session.Requests[0])["job_run_ids"].Split(',').Length.ShouldBe(500);
        Parameters(session.Requests[1])["job_run_ids"].ShouldBe("501");
    }

    [Fact]
    public async Task ReadAsync_polls_a_pending_statement_to_completion()
    {
        var session = new StubStatementSession(
            new StatementOutcome.Pending("statement-1"),
            Success([["11", "2.5", "USD", "0.75"]]));
        var reader = Reader(session);

        var rows = await reader.ReadAsync(
            Acme(),
            From,
            Until,
            [11],
            TestContext.Current.CancellationToken);

        session.PolledStatementIds.ShouldBe(["statement-1"]);
        rows.ShouldBe([
            new BillingRunUsage(11, 2.5m, new CurrencyAmount("USD", 0.75m))
        ]);
    }

    [Fact]
    public async Task ReadAsync_preserves_net_correction_values_and_normalizes_currency()
    {
        var session = new StubStatementSession(Success([
            ["11", "-1.2500", "usd", "-0.3125"]
        ]));

        var rows = await Reader(session).ReadAsync(
            Acme(),
            From,
            Until,
            [11],
            TestContext.Current.CancellationToken);

        rows.Single().ShouldBe(
            new BillingRunUsage(11, -1.25m, new CurrencyAmount("USD", -0.3125m)));
        session.Requests.Single().Statement.ShouldContain("SUM(u.usage_quantity");
    }

    [Theory]
    [InlineData("not-a-run", "2.5", "USD", "0.75")]
    [InlineData("11", "2,5", "USD", "0.75")]
    [InlineData("11", "2.5", "", "0.75")]
    [InlineData("11", "2.5", "USD", "not-money")]
    public async Task ReadAsync_rejects_malformed_billing_rows(
        string runId,
        string dbus,
        string currency,
        string cost)
    {
        var session = new StubStatementSession(Success([[runId, dbus, currency, cost]]));

        var exception = await Should.ThrowAsync<BillingUsageException>(async () =>
            await Reader(session).ReadAsync(
                Acme(),
                From,
                Until,
                [11],
                TestContext.Current.CancellationToken));

        exception.Code.ShouldBe("INVALID_ROW");
    }

    [Fact]
    public async Task ReadAsync_surfaces_statement_failure_without_the_provider_message()
    {
        var session = new StubStatementSession(new StatementOutcome.Failure(
            "PERMISSION_DENIED",
            "message may contain tenant data",
            "statement-1",
            IsTransient: false));

        var exception = await Should.ThrowAsync<BillingUsageException>(async () =>
            await Reader(session).ReadAsync(
                Acme(),
                From,
                Until,
                [11],
                TestContext.Current.CancellationToken));

        exception.Code.ShouldBe("PERMISSION_DENIED");
        exception.Message.ShouldNotContain("tenant data");
    }

    [Fact]
    public async Task ReadAsync_cancels_a_pending_statement_when_the_caller_cancels()
    {
        var session = new StubStatementSession(new StatementOutcome.Pending("statement-1"));
        using var cancellation = new CancellationTokenSource();

        var read = Reader(session).ReadAsync(Acme(), From, Until, [11], cancellation.Token);
        await Task.Delay(10, TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () => await read);
        session.CancelledStatementIds.ShouldBe(["statement-1"]);
    }

    private static DatabricksBillingUsageReader Reader(IDatabricksStatementSession session) => new(
        session,
        new DatabricksOptions { WarehouseId = "warehouse-1", WorkspaceUrl = "https://example" },
        new BillingUsageOptions
        {
            WorkspaceId = "workspace-123",
            PollIntervalMilliseconds = 50
        },
        TimeProvider.System);

    private static TenantContext Acme() => TenantContextFactory.ForTenant(AcmeId, "analytics");

    private static Dictionary<string, string> Parameters(SqlStatement request) =>
        request.Parameters.ToDictionary(parameter => parameter.Name, parameter => parameter.Value);

    private static StatementOutcome.Success Success(IReadOnlyList<IReadOnlyList<string?>> rows) =>
        new(
            ["JobRunId", "DbusConsumed", "CurrencyCode", "EstimatedListCost"],
            rows,
            rows.Count,
            "statement-1");

    private sealed class StubStatementSession(params StatementOutcome[] outcomes)
        : IDatabricksStatementSession
    {
        private readonly Queue<StatementOutcome> _outcomes = new(outcomes);

        public List<SqlStatement> Requests { get; } = [];
        public List<string> PolledStatementIds { get; } = [];
        public List<string> CancelledStatementIds { get; } = [];

        public Task<StatementOutcome> ExecuteAsync(
            SqlStatement request,
            TenantId tenantId,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_outcomes.Dequeue());
        }

        public Task<StatementOutcome> GetAsync(
            TenantId tenantId,
            string statementId,
            CancellationToken cancellationToken)
        {
            PolledStatementIds.Add(statementId);
            return Task.FromResult(_outcomes.Dequeue());
        }

        public Task CancelAsync(string statementId, CancellationToken cancellationToken)
        {
            CancelledStatementIds.Add(statementId);
            return Task.CompletedTask;
        }
    }
}

[Trait("Category", "TenantIsolation")]
[Collection(nameof(PostgresTests))]
public class BillingCostAttributionTests(PostgresFixture postgres)
{
    [Fact]
    public async Task ResolveAsync_correlates_in_application_and_counts_distinct_runs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        var from = DateTimeOffset.Parse("2026-08-01T00:00:00Z", null);
        var until = from.AddDays(1);
        await SeedAsync(db, from, cancellationToken);

        var billing = Substitute.For<IBillingUsageReader>();
        billing.ReadAsync(
                Arg.Any<TenantContext>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<IReadOnlyCollection<long>>(),
                Arg.Any<CancellationToken>())
            .Returns([
                new BillingRunUsage(101, 2m, new CurrencyAmount("USD", 0.50m)),
                new BillingRunUsage(101, -0.5m, new CurrencyAmount("USD", -0.125m)),
                new BillingRunUsage(102, 1m, new CurrencyAmount("USD", 0.25m))
            ]);

        var summary = await new BillingCostAttribution(db, billing).ResolveAsync(
            Acme(),
            from,
            until,
            cancellationToken);

        summary.Source.ShouldBe(CostSource.Billing);
        summary.DbusConsumed.ShouldBe(2.5m);
        summary.EstimatedListCost.ShouldBe([new CurrencyAmount("USD", 0.625m)]);
        summary.ByKind.Single().Operations.ShouldBe(2);
        summary.ByKind.Single().EstimatedListCost.ShouldBe([
            new CurrencyAmount("USD", 0.625m)
        ]);

        await billing.Received(1).ReadAsync(
            Arg.Is<TenantContext>(context => context.TenantId == AcmeId),
            from,
            until,
            Arg.Is<IReadOnlyCollection<long>>(ids =>
                ids.Order().SequenceEqual(new long[] { 101, 102 })),
            cancellationToken);
    }

    [Fact]
    public async Task ResolveAsync_refuses_a_run_not_owned_by_the_tenant()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        var from = DateTimeOffset.Parse("2026-08-01T00:00:00Z", null);
        var until = from.AddDays(1);
        await SeedAsync(db, from, cancellationToken);

        var billing = Substitute.For<IBillingUsageReader>();
        billing.ReadAsync(
                Arg.Any<TenantContext>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<IReadOnlyCollection<long>>(),
                Arg.Any<CancellationToken>())
            .Returns([
                new BillingRunUsage(999, 10m, new CurrencyAmount("USD", 2.5m))
            ]);

        var exception = await Should.ThrowAsync<BillingUsageException>(async () =>
            await new BillingCostAttribution(db, billing).ResolveAsync(
                Acme(),
                from,
                until,
                cancellationToken));

        exception.Code.ShouldBe("UNEXPECTED_RUN");
    }

    [Fact]
    public async Task ResolveAsync_refuses_a_malformed_stored_run_id()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        var from = DateTimeOffset.Parse("2026-08-01T00:00:00Z", null);
        var until = from.AddDays(1);
        db.Organizations.Add(Organization(AcmeId, "Acme", "acme", from));
        db.Operations.Add(Operation(AcmeId, "analysis", "not-a-run", from));
        await db.SaveChangesAsync(cancellationToken);

        var billing = Substitute.For<IBillingUsageReader>();
        var exception = await Should.ThrowAsync<BillingUsageException>(async () =>
            await new BillingCostAttribution(db, billing).ResolveAsync(
                Acme(),
                from,
                until,
                cancellationToken));

        exception.Code.ShouldBe("INVALID_OPERATION_RUN_ID");
        await billing.DidNotReceiveWithAnyArgs().ReadAsync(
            default!, default, default, default!, cancellationToken);
    }

    private static TenantContext Acme() => TenantContextFactory.ForTenant(AcmeId, "analytics");

    private static async Task SeedAsync(
        LakeWrightDbContext db,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        db.Organizations.AddRange(
            Organization(AcmeId, "Acme", "acme", now),
            Organization(GlobexId, "Globex", "globex", now));
        db.Operations.AddRange(
            Operation(AcmeId, "analysis", "101", now),
            Operation(AcmeId, "analysis", "102", now),
            Operation(GlobexId, "other-tenant", "999", now));
        await db.SaveChangesAsync(cancellationToken);
    }

    private static Organization Organization(
        TenantId id,
        string name,
        string slug,
        DateTimeOffset now) => new()
        {
            Id = id,
            Name = name,
            Slug = slug,
            CreatedAt = now,
            Schema = UnityCatalogIdentifier.SchemaForTenant(id),
            State = OrganizationState.Active
        };

    private static Operation Operation(
        TenantId tenantId,
        string kind,
        string externalId,
        DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = tenantId,
            PrincipalId = Alice,
            Kind = kind,
            State = OperationState.Succeeded,
            ExternalId = externalId,
            IdempotencyKey = Guid.CreateVersion7().ToString("N"),
            CreatedAt = now,
            ClaimedAt = now,
            CompletedAt = now.AddHours(1)
        };
}
