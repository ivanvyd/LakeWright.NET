using System.Net;
using LakeWright.Core.Cost;
using LakeWright.Core.Tenancy;
using LakeWright.Databricks;
using LakeWright.Multitenancy;
using LakeWright.Multitenancy.Cost;
using LakeWright.Multitenancy.Model;
using Microsoft.Azure.Databricks.Client.Models;
using Microsoft.Extensions.Time.Testing;
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
        request.WaitTimeout.ShouldBe("30s");
        request.OnWaitTimeout.ShouldBe(SqlStatementOnWaitTimeout.CANCEL);
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
    public async Task ReadAsync_rejects_a_report_that_would_require_repeated_system_table_scans()
    {
        var session = new StubStatementSession(Success([]));
        var reader = Reader(session);

        var exception = await Should.ThrowAsync<BillingUsageException>(async () =>
            await reader.ReadAsync(
                Acme(),
                From,
                Until,
                Enumerable.Range(1, BillingUsageLimits.MaxJobRunsPerReport + 1)
                    .Select(value => (long)value)
                    .ToArray(),
                TestContext.Current.CancellationToken));

        exception.Code.ShouldBe("REPORT_TOO_LARGE");
        session.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReadAsync_rejects_an_oversized_window_before_starting_a_statement()
    {
        var session = new StubStatementSession(Success([]));
        var time = new FakeTimeProvider(Until);

        var exception = await Should.ThrowAsync<BillingUsageException>(async () =>
            await Reader(session, time).ReadAsync(
                Acme(),
                Until.AddDays(-(BillingUsageLimits.MaxReportWindowDays + 1)),
                Until,
                [11],
                TestContext.Current.CancellationToken));

        exception.Code.ShouldBe("REPORT_WINDOW_TOO_LARGE");
        session.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReadAsync_rejects_a_distant_future_window_before_starting_a_statement()
    {
        var session = new StubStatementSession(Success([]));
        var time = new FakeTimeProvider(From);

        var exception = await Should.ThrowAsync<BillingUsageException>(async () =>
            await Reader(session, time).ReadAsync(
                Acme(),
                From.AddDays(1),
                From.AddDays(BillingUsageLimits.MaxFutureWindowDays + 1),
                [11],
                TestContext.Current.CancellationToken));

        exception.Code.ShouldBe("REPORT_WINDOW_IN_FUTURE");
        session.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReadAsync_limits_concurrent_statement_lifecycles()
    {
        var session = new BlockingStatementSession();
        var reader = Reader(session, maxConcurrentStatements: 1);
        var cancellationToken = TestContext.Current.CancellationToken;

        var first = reader.ReadAsync(Acme(), From, Until, [11], cancellationToken);
        await session.FirstRequestStarted;
        var second = reader.ReadAsync(Acme(), From, Until, [22], cancellationToken);

        session.RequestCount.ShouldBe(1);
        session.ReleaseRequests();
        await Task.WhenAll(first, second);

        session.RequestCount.ShouldBe(2);
        session.MaxActiveRequests.ShouldBe(1);
    }

    [Fact]
    public async Task ReadAsync_rejects_work_beyond_the_outstanding_statement_bound()
    {
        var session = new BlockingStatementSession();
        var reader = Reader(
            session,
            maxConcurrentStatements: 1,
            maxOutstandingStatements: 1);
        var cancellationToken = TestContext.Current.CancellationToken;

        var first = reader.ReadAsync(Acme(), From, Until, [11], cancellationToken);
        await session.FirstRequestStarted;

        var exception = await Should.ThrowAsync<BillingUsageException>(async () =>
            await reader.ReadAsync(Acme(), From, Until, [22], cancellationToken));

        exception.Code.ShouldBe("BILLING_BUSY");
        exception.IsTransient.ShouldBeTrue();
        session.RequestCount.ShouldBe(1);
        session.ReleaseRequests();
        await first;
    }

    [Fact]
    public async Task ReadAsync_cancels_an_accepted_statement_when_the_caller_cancels_during_creation()
    {
        var session = new BlockingStatementSession(
            new StatementOutcome.Pending("statement-accepted"));
        var reader = Reader(session, maxConcurrentStatements: 1);
        using var cancellation = new CancellationTokenSource();

        var read = reader.ReadAsync(Acme(), From, Until, [11], cancellation.Token);
        await session.FirstRequestStarted;
        cancellation.Cancel();

        read.IsCompleted.ShouldBeFalse();
        session.ReleaseRequests();
        await Should.ThrowAsync<OperationCanceledException>(async () => await read);

        session.CancelledStatementIds.ShouldBe(["statement-accepted"]);
        session.MaxActiveRequests.ShouldBe(1);
    }

    [Fact]
    public async Task ReadAsync_holds_admission_until_server_cancellation_after_an_uncertain_create()
    {
        var time = new FakeTimeProvider(Until);
        var session = new ThrowingCreateStatementSession();
        var reader = Reader(session, time, submissionWaitTimeoutSeconds: 5);

        var read = reader.ReadAsync(
            Acme(),
            From,
            Until,
            [11],
            TestContext.Current.CancellationToken);
        await Task.Yield();

        read.IsCompleted.ShouldBeFalse();
        time.Advance(TimeSpan.FromSeconds(5));
        var exception = await Should.ThrowAsync<BillingUsageException>(async () => await read);

        exception.Code.ShouldBe("STATEMENT_CREATE_UNCERTAIN");
        exception.IsTransient.ShouldBeTrue();
    }

    [Fact]
    public async Task ReadAsync_holds_admission_for_an_uncertain_request_failure_outcome()
    {
        var time = new FakeTimeProvider(Until);
        var session = new StubStatementSession(new StatementOutcome.Failure(
            "REQUEST_REJECTED",
            "ambiguous server failure",
            StatementId: null,
            IsTransient: true)
        {
            StatusCode = HttpStatusCode.InternalServerError
        });
        var reader = Reader(session, time, submissionWaitTimeoutSeconds: 5);

        var read = reader.ReadAsync(
            Acme(),
            From,
            Until,
            [11],
            TestContext.Current.CancellationToken);
        await Task.Yield();

        read.IsCompleted.ShouldBeFalse();
        time.Advance(TimeSpan.FromSeconds(5));
        var exception = await Should.ThrowAsync<BillingUsageException>(async () => await read);

        exception.Code.ShouldBe("STATEMENT_CREATE_UNCERTAIN");
        exception.IsTransient.ShouldBeTrue();
    }

    [Fact]
    public async Task ReadAsync_does_not_hold_admission_for_a_definitive_request_rejection()
    {
        var time = new FakeTimeProvider(Until);
        var session = new StubStatementSession(new StatementOutcome.Failure(
            "REQUEST_REJECTED",
            "permission denied",
            StatementId: null,
            IsTransient: false)
        {
            StatusCode = HttpStatusCode.Forbidden
        });

        var read = Reader(session, time, submissionWaitTimeoutSeconds: 5).ReadAsync(
            Acme(),
            From,
            Until,
            [11],
            TestContext.Current.CancellationToken);
        var exception = await Should.ThrowAsync<BillingUsageException>(async () => await read);

        exception.Code.ShouldBe("REQUEST_REJECTED");
    }

    [Fact]
    public async Task ReadAsync_prorates_the_same_quantity_at_report_and_price_boundaries()
    {
        var session = new StubStatementSession(Success([]));

        await Reader(session).ReadAsync(
            Acme(), From, Until, [11], TestContext.Current.CancellationToken);

        var sql = session.Requests.Single().Statement;
        sql.ShouldContain("greatest(u.usage_start_time, :from, p.price_start_time)");
        sql.ShouldContain("least(u.usage_end_time, :until, coalesce(p.price_end_time, u.usage_end_time))");
        sql.ShouldContain("u.usage_end_time > p.price_start_time");
        sql.ShouldContain("u.usage_start_time < p.price_end_time");
        sql.ShouldContain(":until > p.price_start_time");
        sql.ShouldContain(":from < p.price_end_time");
        sql.ShouldContain("THEN WindowQuantity ELSE 0 END");
        sql.ShouldContain("WindowQuantity * EffectiveListPrice");
        sql.Split("WindowQuantity * EffectiveListPrice").Length.ShouldBe(2);

        // A quantity of 8 over 00:00-04:00 contributes 2 to each of the two price intervals
        // intersecting a 01:00-03:00 report. The CTE emits one row per interval and both the DBU
        // and price sums consume that same apportioned quantity: 2 + 2, never 8 or 16.
        var quantity = 8m;
        var usageDuration = TimeSpan.FromHours(4);
        var firstPriceOverlap = TimeSpan.FromHours(1);
        var secondPriceOverlap = TimeSpan.FromHours(1);
        var expectedWindowQuantity = quantity * firstPriceOverlap.Ticks / usageDuration.Ticks
            + quantity * secondPriceOverlap.Ticks / usageDuration.Ticks;
        expectedWindowQuantity.ShouldBe(4m);
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
        session.Requests.Single().Statement.ShouldContain("u.usage_quantity");
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
    public async Task ReadAsync_cancels_a_pending_statement_during_the_poll_interval()
    {
        var time = new FakeTimeProvider(Until);
        var session = new StubStatementSession(new StatementOutcome.Pending("statement-1"));
        using var cancellation = new CancellationTokenSource();

        var read = Reader(session, time).ReadAsync(Acme(), From, Until, [11], cancellation.Token);
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () => await read);
        session.PolledStatementIds.ShouldBeEmpty();
        session.CancelledStatementIds.ShouldBe(["statement-1"]);
    }

    [Fact]
    public async Task ReadAsync_cancels_a_blocked_poll_when_the_caller_cancels()
    {
        var session = new StubStatementSession(new StatementOutcome.Pending("statement-1"))
        {
            BlockPollUntilCancelled = true
        };
        using var cancellation = new CancellationTokenSource();

        var read = Reader(session).ReadAsync(Acme(), From, Until, [11], cancellation.Token);
        await session.FirstPollStarted.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () => await read);
        session.CancelledStatementIds.ShouldBe(["statement-1"]);
    }

    [Fact]
    public async Task ReadAsync_keeps_caller_cancellation_when_best_effort_cancel_fails()
    {
        var session = new StubStatementSession(new StatementOutcome.Pending("statement-1"))
        {
            CancelException = new HttpRequestException("cancel transport failed"),
            BlockPollUntilCancelled = true
        };
        using var cancellation = new CancellationTokenSource();

        var read = Reader(session).ReadAsync(Acme(), From, Until, [11], cancellation.Token);
        await session.FirstPollStarted.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () => await read);
        session.CancelledStatementIds.ShouldBe(["statement-1"]);
    }

    [Fact]
    public async Task ReadAsync_cancels_a_pending_statement_at_the_overall_deadline()
    {
        var time = new FakeTimeProvider(Until);
        var session = new StubStatementSession(
            new StatementOutcome.Pending("statement-1"),
            new StatementOutcome.Pending("statement-1"));
        var read = Reader(session, time, pollingTimeoutSeconds: 1)
            .ReadAsync(Acme(), From, Until, [11], TestContext.Current.CancellationToken);
        await Task.Yield();

        time.Advance(TimeSpan.FromSeconds(1));

        var exception = await Should.ThrowAsync<BillingUsageException>(async () => await read);
        exception.Code.ShouldBe("POLL_TIMEOUT");
        exception.IsTransient.ShouldBeTrue();
        session.CancelledStatementIds.ShouldBe(["statement-1"]);
    }

    [Fact]
    public async Task ReadAsync_deadline_cancels_a_blocked_poll_request()
    {
        var session = new StubStatementSession(new StatementOutcome.Pending("statement-1"))
        {
            BlockPollUntilCancelled = true
        };
        var read = Reader(
                session,
                pollingTimeoutSeconds: 1,
                submissionWaitTimeoutSeconds: 1)
            .ReadAsync(Acme(), From, Until, [11], TestContext.Current.CancellationToken);
        await session.FirstPollStarted.WaitAsync(TestContext.Current.CancellationToken);

        var exception = await Should.ThrowAsync<BillingUsageException>(async () => await read);
        exception.Code.ShouldBe("POLL_TIMEOUT");
        exception.IsTransient.ShouldBeTrue();
        session.CancelledStatementIds.ShouldBe(["statement-1"]);
    }

    [Fact]
    public async Task ReadAsync_cancels_a_pending_statement_when_poll_transport_fails()
    {
        var session = new StubStatementSession(new StatementOutcome.Pending("statement-1"))
        {
            GetException = new HttpRequestException("poll transport failed")
        };

        var exception = await Should.ThrowAsync<HttpRequestException>(async () =>
            await Reader(session).ReadAsync(
                Acme(), From, Until, [11], TestContext.Current.CancellationToken));

        exception.Message.ShouldBe("poll transport failed");
        session.CancelledStatementIds.ShouldBe(["statement-1"]);
    }

    private static DatabricksBillingUsageReader Reader(
        IDatabricksStatementSession session,
        TimeProvider? timeProvider = null,
        int pollingTimeoutSeconds = 120,
        int maxConcurrentStatements = 4,
        int maxOutstandingStatements = 32,
        int submissionWaitTimeoutSeconds = 30) => new(
        session,
        new DatabricksOptions { WarehouseId = "warehouse-1", WorkspaceUrl = "https://example" },
        new BillingUsageOptions
        {
            WorkspaceId = "workspace-123",
            PollIntervalMilliseconds = 50,
            PollingTimeoutSeconds = pollingTimeoutSeconds,
            SubmissionWaitTimeoutSeconds = submissionWaitTimeoutSeconds,
            MaxConcurrentStatements = maxConcurrentStatements,
            MaxOutstandingStatements = maxOutstandingStatements
        },
        timeProvider ?? TimeProvider.System);

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
        private readonly TaskCompletionSource _firstPollStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public List<SqlStatement> Requests { get; } = [];
        public List<string> PolledStatementIds { get; } = [];
        public List<string> CancelledStatementIds { get; } = [];
        public Exception? GetException { get; init; }
        public Exception? CancelException { get; init; }
        public bool BlockPollUntilCancelled { get; init; }
        public Task FirstPollStarted => _firstPollStarted.Task;

        public Task<StatementOutcome> ExecuteAsync(
            SqlStatement request,
            TenantId tenantId,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_outcomes.Dequeue());
        }

        public async Task<StatementOutcome> GetAsync(
            TenantId tenantId,
            string statementId,
            CancellationToken cancellationToken)
        {
            PolledStatementIds.Add(statementId);
            _firstPollStarted.TrySetResult();
            if (GetException is not null)
            {
                throw GetException;
            }

            if (BlockPollUntilCancelled)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return _outcomes.Dequeue();
        }

        public Task CancelAsync(string statementId, CancellationToken cancellationToken)
        {
            CancelledStatementIds.Add(statementId);
            if (CancelException is not null)
            {
                throw CancelException;
            }
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingStatementSession : IDatabricksStatementSession
    {
        private readonly TaskCompletionSource _firstRequestStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseRequests = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeRequests;
        private int _maxActiveRequests;
        private int _requestCount;
        private readonly StatementOutcome _outcome;

        public BlockingStatementSession(StatementOutcome? outcome = null)
        {
            _outcome = outcome ?? Success([]);
        }

        public Task FirstRequestStarted => _firstRequestStarted.Task;
        public int RequestCount => Volatile.Read(ref _requestCount);
        public int MaxActiveRequests => Volatile.Read(ref _maxActiveRequests);
        public List<string> CancelledStatementIds { get; } = [];

        public async Task<StatementOutcome> ExecuteAsync(
            SqlStatement request,
            TenantId tenantId,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            var active = Interlocked.Increment(ref _activeRequests);
            UpdateMaximum(active);
            _firstRequestStarted.TrySetResult();
            try
            {
                await _releaseRequests.Task.WaitAsync(cancellationToken);
                return _outcome;
            }
            finally
            {
                Interlocked.Decrement(ref _activeRequests);
            }
        }

        public Task<StatementOutcome> GetAsync(
            TenantId tenantId,
            string statementId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No statement should require polling.");

        public Task CancelAsync(string statementId, CancellationToken cancellationToken)
        {
            CancelledStatementIds.Add(statementId);
            return Task.CompletedTask;
        }

        public void ReleaseRequests() => _releaseRequests.TrySetResult();

        private void UpdateMaximum(int active)
        {
            var observed = Volatile.Read(ref _maxActiveRequests);
            while (active > observed)
            {
                var replaced = Interlocked.CompareExchange(ref _maxActiveRequests, active, observed);
                if (replaced == observed)
                {
                    return;
                }

                observed = replaced;
            }
        }
    }

    private sealed class ThrowingCreateStatementSession : IDatabricksStatementSession
    {
        public Task<StatementOutcome> ExecuteAsync(
            SqlStatement request,
            TenantId tenantId,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("response lost after submission");

        public Task<StatementOutcome> GetAsync(
            TenantId tenantId,
            string statementId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No statement id was returned.");

        public Task CancelAsync(string statementId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No statement id was returned.");
    }
}

[Trait("Category", "TenantIsolation")]
[Collection(nameof(PostgresTests))]
public class BillingCostAttributionTests(PostgresFixture postgres)
{
    [Fact]
    public async Task ResolveAsync_rejects_an_oversized_window_before_querying_either_store()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        var now = DateTimeOffset.Parse("2026-09-01T00:00:00Z", null);
        var billing = Substitute.For<IBillingUsageReader>();

        var exception = await Should.ThrowAsync<BillingUsageException>(async () =>
            await new BillingCostAttribution(db, billing, new FakeTimeProvider(now)).ResolveAsync(
                Acme(),
                now.AddDays(-(BillingUsageLimits.MaxReportWindowDays + 1)),
                now,
                cancellationToken));

        exception.Code.ShouldBe("REPORT_WINDOW_TOO_LARGE");
        await billing.DidNotReceiveWithAnyArgs().ReadAsync(
            default!, default, default, default!, cancellationToken);
    }

    [Fact]
    public async Task ResolveAsync_rejects_a_distant_future_window_before_querying_either_store()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        var now = DateTimeOffset.Parse("2026-09-01T00:00:00Z", null);
        var billing = Substitute.For<IBillingUsageReader>();

        var exception = await Should.ThrowAsync<BillingUsageException>(async () =>
            await new BillingCostAttribution(db, billing, new FakeTimeProvider(now)).ResolveAsync(
                Acme(),
                now,
                now.AddDays(BillingUsageLimits.MaxFutureWindowDays + 1),
                cancellationToken));

        exception.Code.ShouldBe("REPORT_WINDOW_IN_FUTURE");
        await billing.DidNotReceiveWithAnyArgs().ReadAsync(
            default!, default, default, default!, cancellationToken);
    }

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

    [Fact]
    public async Task ResolveAsync_rejects_more_runs_than_one_billing_query_can_bound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        var from = DateTimeOffset.Parse("2026-08-01T00:00:00Z", null);
        db.Organizations.Add(Organization(AcmeId, "Acme", "acme", from));
        db.Operations.AddRange(Enumerable
            .Range(1, BillingUsageLimits.MaxJobRunsPerReport + 1)
            .Select(runId => Operation(AcmeId, "analysis", runId.ToString(), from)));
        await db.SaveChangesAsync(cancellationToken);
        var billing = Substitute.For<IBillingUsageReader>();

        var exception = await Should.ThrowAsync<BillingUsageException>(async () =>
            await new BillingCostAttribution(db, billing).ResolveAsync(
                Acme(), from, from.AddDays(1), cancellationToken));

        exception.Code.ShouldBe("REPORT_TOO_LARGE");
        await billing.DidNotReceiveWithAnyArgs().ReadAsync(
            default!, default, default, default!, cancellationToken);
    }

    [Fact]
    public async Task ResolveAsync_bounds_distinct_run_ids_not_duplicate_operation_rows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        var from = DateTimeOffset.Parse("2026-08-01T00:00:00Z", null);
        db.Organizations.Add(Organization(AcmeId, "Acme", "acme", from));
        db.Operations.AddRange(Enumerable
            .Range(1, BillingUsageLimits.MaxJobRunsPerReport)
            .Select(runId => Operation(AcmeId, "analysis", runId.ToString(), from)));
        db.Operations.AddRange(Enumerable
            .Range(0, 10)
            .Select(_ => Operation(AcmeId, "analysis", "1", from)));
        await db.SaveChangesAsync(cancellationToken);
        var billing = Substitute.For<IBillingUsageReader>();
        billing.ReadAsync(
                Arg.Any<TenantContext>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<IReadOnlyCollection<long>>(),
                Arg.Any<CancellationToken>())
            .Returns([]);

        await new BillingCostAttribution(db, billing).ResolveAsync(
            Acme(), from, from.AddDays(1), cancellationToken);

        await billing.Received(1).ReadAsync(
            Arg.Any<TenantContext>(),
            from,
            from.AddDays(1),
            Arg.Is<IReadOnlyCollection<long>>(ids =>
                ids.Count == BillingUsageLimits.MaxJobRunsPerReport),
            cancellationToken);
    }

    [Fact]
    public async Task ResolveAsync_rejects_one_run_recorded_for_conflicting_kinds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        var from = DateTimeOffset.Parse("2026-08-01T00:00:00Z", null);
        db.Organizations.Add(Organization(AcmeId, "Acme", "acme", from));
        db.Operations.AddRange(
            Operation(AcmeId, "analysis", "101", from),
            Operation(AcmeId, "export", "101", from));
        await db.SaveChangesAsync(cancellationToken);
        var billing = Substitute.For<IBillingUsageReader>();

        var exception = await Should.ThrowAsync<BillingUsageException>(async () =>
            await new BillingCostAttribution(db, billing).ResolveAsync(
                Acme(), from, from.AddDays(1), cancellationToken));

        exception.Code.ShouldBe("AMBIGUOUS_RUN");
        await billing.DidNotReceiveWithAnyArgs().ReadAsync(
            default!, default, default, default!, cancellationToken);
    }

    [Fact]
    public async Task ResolveAsync_orders_kinds_by_dbus_without_adding_unlike_currencies()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        var from = DateTimeOffset.Parse("2026-08-01T00:00:00Z", null);
        db.Organizations.Add(Organization(AcmeId, "Acme", "acme", from));
        db.Operations.AddRange(
            Operation(AcmeId, "more-dbus", "101", from),
            Operation(AcmeId, "more-money", "102", from));
        await db.SaveChangesAsync(cancellationToken);
        var billing = Substitute.For<IBillingUsageReader>();
        billing.ReadAsync(
                Arg.Any<TenantContext>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<IReadOnlyCollection<long>>(),
                Arg.Any<CancellationToken>())
            .Returns([
                new BillingRunUsage(101, 10m, new CurrencyAmount("EUR", 1m)),
                new BillingRunUsage(102, 2m, new CurrencyAmount("USD", 999m))
            ]);

        var summary = await new BillingCostAttribution(db, billing).ResolveAsync(
            Acme(), from, from.AddDays(1), cancellationToken);

        summary.ByKind.Select(row => row.Kind).ShouldBe(["more-dbus", "more-money"]);
        summary.EstimatedListCost.ShouldBe([
            new CurrencyAmount("EUR", 1m),
            new CurrencyAmount("USD", 999m)
        ]);
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
