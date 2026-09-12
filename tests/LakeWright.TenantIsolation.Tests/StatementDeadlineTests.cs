using LakeWright.Core.Tenancy;
using LakeWright.Databricks;
using Microsoft.Azure.Databricks.Client.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace LakeWright.TenantIsolation.Tests;

[Trait("Category", "TenantIsolation")]
public sealed class StatementDeadlineTests
{
    [Fact]
    public async Task A_blocked_submission_observes_the_total_deadline_and_reports_an_unknown_id()
    {
        var time = new FakeTimeProvider();
        var session = new Session(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Success();
        });
        var pending = Executor(session, time).ExecuteAsync(Statement(), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(1));
        var error = await Should.ThrowAsync<StatementBudgetExceededException>(() => pending);
        error.StatementId.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(999, false)]
    [InlineData(1000, true)]
    [InlineData(1001, true)]
    public async Task A_terminal_response_is_checked_at_the_deadline(int elapsedMilliseconds, bool expired)
    {
        var time = new FakeTimeProvider();
        var session = new Session(_ =>
        {
            time.Advance(TimeSpan.FromMilliseconds(elapsedMilliseconds));
            return Task.FromResult<StatementOutcome>(Success());
        });
        var pending = Executor(session, time).ExecuteAsync(Statement(), TestContext.Current.CancellationToken);
        if (expired)
        {
            (await Should.ThrowAsync<StatementBudgetExceededException>(() => pending)).StatementId.ShouldBe("statement-1");
        }
        else { (await pending).ShouldBeOfType<StatementOutcome.Success>(); }
    }

    [Fact]
    public async Task A_delay_consuming_the_remaining_budget_does_not_send_another_poll()
    {
        var time = new FakeTimeProvider();
        var session = new Session(_ => Task.FromResult<StatementOutcome>(new StatementOutcome.Pending("statement-1")));
        var pending = Executor(session, time).ExecuteAsync(Statement(), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(1));
        (await Should.ThrowAsync<StatementBudgetExceededException>(() => pending)).StatementId.ShouldBe("statement-1");
        session.Polls.ShouldBe(0);
    }

    [Fact]
    public async Task A_blocked_poll_uses_the_remaining_budget_and_preserves_the_statement_id()
    {
        var time = new FakeTimeProvider();
        var session = new Session(_ => Task.FromResult<StatementOutcome>(new StatementOutcome.Pending("statement-1")))
        {
            Poll = async token => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Success(); },
        };
        var statement = Statement(new StatementOptions { PollInterval = TimeSpan.FromMilliseconds(100), TotalBudget = TimeSpan.FromSeconds(1) });
        var pending = Executor(session, time).ExecuteAsync(statement, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMilliseconds(100));
        await session.PollStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMilliseconds(900));
        (await Should.ThrowAsync<StatementBudgetExceededException>(() => pending)).StatementId.ShouldBe("statement-1");
    }

    [Fact]
    public async Task Caller_cancellation_is_not_relabelled_a_budget_expiration()
    {
        var time = new FakeTimeProvider();
        var session = new Session(async token => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Success(); });
        using var caller = new CancellationTokenSource();
        var pending = Executor(session, time).ExecuteAsync(Statement(), caller.Token);
        caller.Cancel();
        var error = await Should.ThrowAsync<OperationCanceledException>(() => pending);
        error.CancellationToken.ShouldBe(caller.Token);
    }

    [Fact]
    public async Task Export_budget_starts_at_enumeration_and_includes_consumer_pauses()
    {
        var time = new FakeTimeProvider();
        var session = new Session(_ => Task.FromResult<StatementOutcome>(
            new StatementOutcome.LargeResult(["value"], [new Uri("https://example.invalid/chunk")], 1, "statement-1")));
        using var http = new HttpClient();
        var exporter = new DatabricksTenantScopedExport(session, new DatabricksOptions { WarehouseId = "warehouse" }, http,
            NullLogger<DatabricksTenantScopedExport>.Instance, time);
        var stream = exporter.StreamAsync(Statement(), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromHours(1));
        await using var rows = stream.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        (await rows.MoveNextAsync()).ShouldBeTrue();
        rows.Current.Column.ShouldNotBeNull();
        time.Advance(TimeSpan.FromSeconds(1));
        (await Should.ThrowAsync<StatementBudgetExceededException>(() => rows.MoveNextAsync().AsTask())).StatementId.ShouldBe("statement-1");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_blocked_export_body_observes_cancellation_and_disposes_its_stream(bool cancelCaller)
    {
        var time = new FakeTimeProvider();
        var session = new Session(_ => Task.FromResult<StatementOutcome>(
            new StatementOutcome.LargeResult(["value"], [new Uri("https://example.invalid/chunk")], 1, "statement-1")));
        using var body = new BlockedBody();
        using var http = new HttpClient(new BodyHandler(body));
        using var caller = new CancellationTokenSource();
        var exporter = new DatabricksTenantScopedExport(session, new DatabricksOptions { WarehouseId = "warehouse" }, http,
            NullLogger<DatabricksTenantScopedExport>.Instance, time);
        await using var rows = exporter.StreamAsync(Statement(), caller.Token).GetAsyncEnumerator(caller.Token);
        (await rows.MoveNextAsync()).ShouldBeTrue();
        var pending = rows.MoveNextAsync().AsTask();
        await body.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        if (cancelCaller)
        {
            caller.Cancel();
            (await Should.ThrowAsync<OperationCanceledException>(() => pending)).CancellationToken.ShouldBe(caller.Token);
        }
        else
        {
            time.Advance(TimeSpan.FromSeconds(1));
            (await Should.ThrowAsync<StatementBudgetExceededException>(() => pending)).StatementId.ShouldBe("statement-1");
        }
        body.Disposed.ShouldBeTrue();
    }

    [Theory]
    [InlineData("1s")]
    [InlineData("51s")]
    [InlineData("-1s")]
    [InlineData("5.0s")]
    [InlineData("garbage")]
    public async Task Invalid_server_wait_values_fail_before_submission(string wait)
    {
        var session = new Session(_ => throw new InvalidOperationException("Must not submit"));
        await Should.ThrowAsync<ArgumentException>(() => Executor(session, TimeProvider.System).ExecuteAsync(
            Statement(new StatementOptions { WaitTimeout = wait }), TestContext.Current.CancellationToken));
    }

    private static StatementOutcome.Success Success() => new([], [], 0, "statement-1");
    private static DatabricksStatementExecutor Executor(Session session, TimeProvider time) => new(session, new DatabricksOptions { WarehouseId = "warehouse" }, time);
    private static TenantScopedStatement Statement(StatementOptions? options = null) => TenantScopedStatement.Create(
        TenantContextFactory.ForTenant(TenantId.New(), "analytics"), "SELECT 1", options ?? new StatementOptions { TotalBudget = TimeSpan.FromSeconds(1) });

    private sealed class Session(Func<CancellationToken, Task<StatementOutcome>> execute) : IDatabricksStatementSession
    {
        public int Polls { get; private set; }
        public TaskCompletionSource PollStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<CancellationToken, Task<StatementOutcome>> Poll { get; init; } = _ => Task.FromResult<StatementOutcome>(Success());
        public Task<StatementOutcome> ExecuteAsync(SqlStatement request, TenantId tenantId, CancellationToken cancellationToken) => execute(cancellationToken);
        public Task<StatementOutcome> GetAsync(TenantId tenantId, string statementId, CancellationToken cancellationToken)
        {
            Polls++;
            var result = Poll(cancellationToken);
            PollStarted.SetResult();
            return result;
        }
        public Task CancelAsync(string statementId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class BodyHandler(Stream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(body) });
    }

    private sealed class BlockedBody : MemoryStream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
