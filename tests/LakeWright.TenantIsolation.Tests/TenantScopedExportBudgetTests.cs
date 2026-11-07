using System.Net;
using LakeWright.Core.Tenancy;
using LakeWright.Databricks;
using Microsoft.Azure.Databricks.Client.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace LakeWright.TenantIsolation.Tests;

// Budget tests control time and chunk I/O. TenantScopedExportTests retains real HTTP integration.
[Trait("Category", "TenantIsolation")]
public sealed class TenantScopedExportBudgetTests
{
    [Fact]
    public async Task A_pending_export_is_polled_to_external_links_within_its_budget()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var session = new SequencedSession(new StatementOutcome.Pending("statement-1"), Result());
        using var handler = new ChunkHandler(time, TimeSpan.Zero);
        using var http = new HttpClient(handler);
        var export = Export(session, http, time);
        await using var rows = export.StreamAsync(Statement(), ct).GetAsyncEnumerator(ct);

        var pending = rows.MoveNextAsync().AsTask();
        pending.IsCompleted.ShouldBeFalse();
        time.Advance(TimeSpan.FromMilliseconds(1));
        (await pending.WaitAsync(TimeSpan.FromSeconds(5), ct)).ShouldBeTrue();
        rows.Current.Column.ShouldNotBeNull();
        (await rows.MoveNextAsync()).ShouldBeTrue();
        rows.Current.Values.ShouldBe(["1"]);
        (await rows.MoveNextAsync()).ShouldBeFalse();
        session.GetCalls.ShouldBe(1);
        session.Request!.OnWaitTimeout.ShouldBe(SqlStatementOnWaitTimeout.CONTINUE);
        handler.Requests.ShouldBe(1);
    }

    [Fact]
    public async Task A_pending_export_that_outlives_its_budget_has_a_distinct_exception()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var session = new SequencedSession(new StatementOutcome.Pending("statement-1"));
        using var handler = new ChunkHandler(time, TimeSpan.Zero);
        using var http = new HttpClient(handler);
        await using var rows = Export(session, http, time).StreamAsync(Statement(), ct).GetAsyncEnumerator(ct);

        var pending = rows.MoveNextAsync().AsTask();
        time.Advance(TimeSpan.FromSeconds(1));
        var error = await Should.ThrowAsync<StatementBudgetExceededException>(() => pending);
        error.StatementId.ShouldBe("statement-1");
        handler.Requests.ShouldBe(0);
    }

    [Fact]
    public async Task Downloading_chunks_remains_inside_the_export_budget()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var session = new SequencedSession(Result());
        using var handler = new ChunkHandler(time, TimeSpan.FromSeconds(1));
        using var http = new HttpClient(handler);
        await using var rows = Export(session, http, time).StreamAsync(Statement(), ct).GetAsyncEnumerator(ct);

        (await rows.MoveNextAsync()).ShouldBeTrue();
        var error = await Should.ThrowAsync<StatementBudgetExceededException>(() => rows.MoveNextAsync().AsTask());
        error.StatementId.ShouldBe("statement-1");
        handler.Requests.ShouldBe(1);
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved_while_the_export_waits_to_poll()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var session = new SequencedSession(new StatementOutcome.Pending("statement-1"));
        using var handler = new ChunkHandler(time, TimeSpan.Zero);
        using var http = new HttpClient(handler);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var rows = Export(session, http, time).StreamAsync(Statement(), caller.Token).GetAsyncEnumerator(caller.Token);

        var pending = rows.MoveNextAsync().AsTask();
        await caller.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => pending);
        session.GetCalls.ShouldBe(0);
        handler.Requests.ShouldBe(0);
    }

    private static DatabricksTenantScopedExport Export(SequencedSession session, HttpClient http, TimeProvider time) =>
        new(session, new DatabricksOptions { WarehouseId = "warehouse" }, http,
            NullLogger<DatabricksTenantScopedExport>.Instance, time);

    private static TenantScopedStatement Statement() => TenantScopedStatement.Create(
        TenantContextFactory.ForTenant(TenantId.New(), "analytics"),
        "SELECT id FROM customers",
        new StatementOptions { PollInterval = TimeSpan.FromMilliseconds(1), TotalBudget = TimeSpan.FromSeconds(1) });

    private static StatementOutcome.LargeResult Result() => new(
        ["id"], [new Uri("https://chunks.invalid/chunk-0.json")], 1, "statement-1");

    private sealed class ChunkHandler(FakeTimeProvider time, TimeSpan downloadTime) : HttpMessageHandler
    {
        internal int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri!.AbsoluteUri.ShouldBe("https://chunks.invalid/chunk-0.json");
            Requests++;
            time.Advance(downloadTime);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""[["1"]]"""),
            });
        }
    }

    private sealed class SequencedSession(params StatementOutcome[] outcomes) : IDatabricksStatementSession
    {
        private readonly Queue<StatementOutcome> _outcomes = new(outcomes);
        internal int GetCalls { get; private set; }
        internal SqlStatement? Request { get; private set; }

        public Task<StatementOutcome> ExecuteAsync(SqlStatement request, TenantId tenantId, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(_outcomes.Dequeue());
        }

        public Task<StatementOutcome> GetAsync(TenantId tenantId, string statementId, CancellationToken cancellationToken)
        {
            GetCalls++;
            return Task.FromResult(_outcomes.Count == 0 ? new StatementOutcome.Pending(statementId) : _outcomes.Dequeue());
        }

        public Task CancelAsync(string statementId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
