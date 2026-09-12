using System.Net;
using System.Text;
using Azure.Core;
using LakeWright.Conversations;
using LakeWright.Core.Tenancy;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace LakeWright.TenantIsolation.Tests;

[Trait("Category", "TenantIsolation")]
public sealed class GenieDeadlineAndOwnershipTests
{
    private static readonly TenantContext Tenant = TenantContextFactory.ForTenant(
        TenantId.Parse("0198f000-0000-7000-8000-00000000ac11"), "analytics");

    [Fact]
    public async Task Creation_response_after_deadline_reports_unknown_acceptance_without_polling()
    {
        var time = new FakeTimeProvider();
        var ownership = new TestOwnership();
        var handler = new DelegateHandler((request, _) =>
        {
            time.Advance(TimeSpan.FromMilliseconds(11));
            return Response("""{"conversation_id":"conv-1","message_id":"msg-1"}""");
        });
        var genie = Conversations(handler, time, ownership, TimeSpan.FromMilliseconds(10));

        await Should.ThrowAsync<GenieResponseTimeoutException>(() => genie.AskAsync(
            Tenant, "owner-a", "question", TestContext.Current.CancellationToken));

        (await ownership.ListAsync("owner-a", TestContext.Current.CancellationToken)).ShouldBeEmpty();
        handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Accepted_conversation_is_owned_and_returns_timed_out_when_a_terminal_poll_arrives_late()
    {
        var time = new FakeTimeProvider();
        var ownership = new TestOwnership();
        var handler = new DelegateHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return Response("""{"conversation_id":"conv-1","message_id":"msg-1"}""");
            }

            time.Advance(TimeSpan.FromMilliseconds(11));
            return Response("""{"status":"COMPLETED"}""");
        });
        var genie = Conversations(handler, time, ownership, TimeSpan.FromMilliseconds(10));

        var answer = await genie.AskAsync(Tenant, "owner-a", "question", TestContext.Current.CancellationToken);

        answer.Outcome.ShouldBe(GenieOutcome.TimedOut);
        answer.ConversationId.ShouldBe("conv-1");
        (await ownership.ListAsync("owner-a", TestContext.Current.CancellationToken)).ShouldBe(["conv-1"]);
    }

    [Fact]
    public async Task Accepted_conversation_remains_resumable_after_a_poll_failure()
    {
        var polls = 0;
        var ownership = new TestOwnership();
        var handler = new DelegateHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return Response("""{"conversation_id":"conv-1","message_id":"msg-1"}""");
            }

            polls++;
            return polls == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Response("""{"status":"COMPLETED"}""");
        });
        var genie = Conversations(handler, new FakeTimeProvider(), ownership, TimeSpan.FromSeconds(5));

        await Should.ThrowAsync<HttpRequestException>(() => genie.AskAsync(
            Tenant, "owner-a", "question", TestContext.Current.CancellationToken));

        (await ownership.ListAsync("owner-a", TestContext.Current.CancellationToken)).ShouldBe(["conv-1"]);
        var resumed = await genie.ContinueAsync(
            Tenant, "owner-a", "conv-1", "continue", TestContext.Current.CancellationToken);
        resumed.Outcome.ShouldBe(GenieOutcome.Completed);
    }

    [Fact]
    public async Task Ownership_store_failure_after_acceptance_is_observable_and_stops_before_polling()
    {
        var handler = new DelegateHandler((request, _) =>
            request.Method == HttpMethod.Post
                ? Response("{\"conversation_id\":\"conv-1\",\"message_id\":\"msg-1\"}")
                : Response("{\"status\":\"COMPLETED\"}"));
        var genie = Conversations(handler, new FakeTimeProvider(), new FailingOwnership(), TimeSpan.FromSeconds(5));

        var failure = await Should.ThrowAsync<ConversationOwnershipPersistenceException>(() => genie.AskAsync(
            Tenant, "owner-a", "question", TestContext.Current.CancellationToken));

        failure.ConversationId.ShouldBe("conv-1");
        failure.MessageId.ShouldBe("msg-1");
        handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Ownership_persistence_is_bounded_after_acceptance()
    {
        var time = new FakeTimeProvider();
        var handler = new DelegateHandler((request, _) =>
            Response("{\"conversation_id\":\"conv-1\",\"message_id\":\"msg-1\"}"));
        var genie = Conversations(handler, time, new BlockingOwnership(time), TimeSpan.FromMilliseconds(10));

        var asking = genie.AskAsync(Tenant, "owner-a", "question", TestContext.Current.CancellationToken);
        await Task.Delay(5, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMilliseconds(11));

        var failure = await Should.ThrowAsync<ConversationOwnershipPersistenceException>(() => asking);
        failure.ConversationId.ShouldBe("conv-1");
        failure.MessageId.ShouldBe("msg-1");
        handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Caller_cancellation_after_acceptance_keeps_the_conversation_owned()
    {
        using var cancellation = new CancellationTokenSource();
        var ownership = new CancellingOwnership(cancellation);
        var handler = new DelegateHandler((request, _) =>
            request.Method == HttpMethod.Post
                ? Response("{\"conversation_id\":\"conv-1\",\"message_id\":\"msg-1\"}")
                : Response("{\"status\":\"COMPLETED\"}"));
        var genie = Conversations(handler, new FakeTimeProvider(), ownership, TimeSpan.FromSeconds(5));

        await Should.ThrowAsync<OperationCanceledException>(() => genie.AskAsync(
            Tenant, "owner-a", "question", cancellation.Token));

        (await ownership.ListAsync("owner-a", TestContext.Current.CancellationToken)).ShouldBe(["conv-1"]);
    }

    private static GenieConversations Conversations(
        HttpMessageHandler handler,
        FakeTimeProvider time,
        IConversationOwnership ownership,
        TimeSpan timeout)
    {
        var options = new GenieOptions { WorkspaceUrl = "https://workspace.example", ResponseTimeout = timeout };
        options.Spaces[Tenant.TenantId.ToString()] = "space-a";
        return new GenieConversations(
            new HttpClient(handler) { BaseAddress = new Uri("https://workspace.example/") },
            new StubCredential(),
            Options.Create(options),
            time,
            ownership);
    }

    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(send(request, cancellationToken));
        }
    }

    private sealed class FailingOwnership : IConversationOwnership
    {
        public ValueTask RecordAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new InvalidOperationException("store unavailable"));

        public ValueTask<bool> IsOwnerAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);

        public ValueTask<IReadOnlyList<string>> ListAsync(string ownerKey, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<string>>([]);

        public ValueTask RemoveAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class BlockingOwnership(FakeTimeProvider time) : IConversationOwnership
    {
        public async ValueTask RecordAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default) =>
            await Task.Delay(Timeout.InfiniteTimeSpan, time, cancellationToken);

        public ValueTask<bool> IsOwnerAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);

        public ValueTask<IReadOnlyList<string>> ListAsync(string ownerKey, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<string>>([]);

        public ValueTask RemoveAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class TestOwnership : IConversationOwnership
    {
        private readonly Dictionary<string, string> _owners = new(StringComparer.Ordinal);

        public ValueTask RecordAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default)
        {
            if (_owners.TryGetValue(conversationId, out var owner) && owner != ownerKey)
            {
                throw new ConversationOwnershipException(conversationId);
            }

            _owners[conversationId] = ownerKey;
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> IsOwnerAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_owners.TryGetValue(conversationId, out var owner) && owner == ownerKey);

        public ValueTask<IReadOnlyList<string>> ListAsync(string ownerKey, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<string>>([.. _owners.Where(item => item.Value == ownerKey).Select(item => item.Key)]);

        public ValueTask RemoveAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default)
        {
            if (_owners.TryGetValue(conversationId, out var owner) && owner == ownerKey)
            {
                _owners.Remove(conversationId);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancellingOwnership(CancellationTokenSource cancellation) : IConversationOwnership
    {
        private readonly TestOwnership _inner = new();

        public async ValueTask RecordAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default)
        {
            await _inner.RecordAsync(conversationId, ownerKey, cancellationToken);
            cancellation.Cancel();
        }

        public ValueTask<bool> IsOwnerAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default) =>
            _inner.IsOwnerAsync(conversationId, ownerKey, cancellationToken);

        public ValueTask<IReadOnlyList<string>> ListAsync(string ownerKey, CancellationToken cancellationToken = default) =>
            _inner.ListAsync(ownerKey, cancellationToken);

        public ValueTask RemoveAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default) =>
            _inner.RemoveAsync(conversationId, ownerKey, cancellationToken);
    }

    private sealed class StubCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken cancellationToken) =>
            new("token", DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken) =>
            new(GetToken(context, cancellationToken));
    }
}
