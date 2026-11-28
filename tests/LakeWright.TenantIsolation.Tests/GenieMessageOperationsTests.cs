using System.Net;
using System.Text;
using Azure.Core;
using LakeWright.Conversations;
using LakeWright.Core.Tenancy;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace LakeWright.TenantIsolation.Tests;

[Trait("Category", "TenantIsolation")]
public sealed class GenieMessageOperationsTests
{
    private static readonly TenantContext Tenant = TenantContextFactory.ForTenant(
        TenantId.Parse("0198f000-0000-7000-8000-00000000ac11"), "analytics");
    private static readonly TenantContext OtherTenant = TenantContextFactory.ForTenant(
        TenantId.Parse("0198f000-0000-7000-8000-00000000617b"), "analytics");

    [Fact]
    public async Task Accepted_identifiers_can_be_checkpointed_and_resumed_by_a_new_client_without_another_post()
    {
        using var fixture = new Fixture((request, _) => Task.FromResult(
            request.Method == HttpMethod.Post
                ? Json("""{"conversation_id":"conv","message_id":"msg"}""")
                : Json("""{"conversation_id":"conv","message_id":"msg","status":"COMPLETED","attachments":[{"text":{"content":"answer"}}]}""")));

        var accepted = await fixture.Client.StartConversationAsync(Tenant, "owner", "question", TestContext.Current.CancellationToken);
        accepted.ShouldBe(new GenieAcceptedMessage("conv", "msg"));
        fixture.Handler.Methods.ShouldBe([HttpMethod.Post]);
        (await fixture.Ownership.ListAsync("owner", TestContext.Current.CancellationToken)).ShouldBe(["conv"]);

        var resumed = await fixture.NewClient().WaitForResponseAsync(
            Tenant, "owner", accepted.ConversationId, accepted.MessageId, TestContext.Current.CancellationToken);
        resumed.Outcome.ShouldBe(GenieOutcome.Completed);
        resumed.Text.ShouldBe("answer");
        fixture.Handler.Methods.ShouldBe([HttpMethod.Post, HttpMethod.Get]);
        fixture.Handler.Paths.Last().ShouldBe("/api/2.0/genie/spaces/space/conversations/conv/messages/msg");
    }

    [Fact]
    public async Task A_follow_up_returns_its_message_before_any_poll_and_can_be_read_without_resubmission()
    {
        using var fixture = new Fixture((request, _) => Task.FromResult(
            request.Method == HttpMethod.Post
                ? Json("""{"conversation_id":"conv","message_id":"follow-up"}""")
                : Json("""{"status":"EXECUTING_QUERY"}""")));
        await fixture.Ownership.RecordAsync("conv", "owner", TestContext.Current.CancellationToken);

        var accepted = await fixture.Client.SendMessageAsync(Tenant, "owner", "conv", "question", TestContext.Current.CancellationToken);
        accepted.MessageId.ShouldBe("follow-up");
        fixture.Handler.Methods.ShouldBe([HttpMethod.Post]);
        var snapshot = await fixture.Client.GetMessageAsync(Tenant, "owner", "conv", accepted.MessageId, TestContext.Current.CancellationToken);
        snapshot.IsTerminal.ShouldBeFalse();
        snapshot.Status.ShouldBe("EXECUTING_QUERY");
        fixture.Handler.Methods.ShouldBe([HttpMethod.Post, HttpMethod.Get]);
    }

    [Fact]
    public async Task A_failed_get_can_be_retried_without_replaying_the_submission()
    {
        var reads = 0;
        using var fixture = new Fixture((request, _) => Task.FromResult(
            request.Method == HttpMethod.Post
                ? Json("""{"conversation_id":"conv","message_id":"msg"}""")
                : ++reads == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Json("""{"status":"COMPLETED"}""")));
        var accepted = await fixture.Client.StartConversationAsync(Tenant, "owner", "question", TestContext.Current.CancellationToken);

        await Should.ThrowAsync<HttpRequestException>(() => fixture.Client.WaitForResponseAsync(
            Tenant, "owner", accepted.ConversationId, accepted.MessageId, TestContext.Current.CancellationToken));
        var result = await fixture.NewClient().WaitForResponseAsync(Tenant, "owner", "conv", "msg", TestContext.Current.CancellationToken);
        result.Outcome.ShouldBe(GenieOutcome.Completed);
        fixture.Handler.Methods.Count(method => method == HttpMethod.Post).ShouldBe(1);
        fixture.Handler.Methods.Count(method => method == HttpMethod.Get).ShouldBe(2);
    }

    [Theory]
    [InlineData("COMPLETED", GenieOutcome.Completed, true)]
    [InlineData("FAILED", GenieOutcome.Failed, true)]
    [InlineData("CANCELLED", GenieOutcome.Cancelled, true)]
    [InlineData("QUERY_RESULT_EXPIRED", GenieOutcome.Completed, true)]
    [InlineData("FUTURE_PENDING", GenieOutcome.Unknown, false)]
    public async Task A_single_read_preserves_status_and_terminal_state(string status, GenieOutcome outcome, bool terminal)
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json($$"""{"status":"{{status}}"}""")));
        await fixture.Ownership.RecordAsync("conv", "owner", TestContext.Current.CancellationToken);
        var snapshot = await fixture.Client.GetMessageAsync(Tenant, "owner", "conv", "msg", TestContext.Current.CancellationToken);
        snapshot.Status.ShouldBe(status);
        snapshot.Outcome.ShouldBe(outcome);
        snapshot.IsTerminal.ShouldBe(terminal);
        fixture.Handler.Methods.ShouldBe([HttpMethod.Get]);
    }

    [Fact]
    public async Task An_unfamiliar_state_with_an_error_is_terminal()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json("""{"status":"FUTURE_FAILURE","error":{"type":"FAILED"}}""")));
        await fixture.Ownership.RecordAsync("conv", "owner", TestContext.Current.CancellationToken);
        var snapshot = await fixture.Client.GetMessageAsync(Tenant, "owner", "conv", "msg", TestContext.Current.CancellationToken);
        snapshot.Outcome.ShouldBe(GenieOutcome.Unknown);
        snapshot.IsTerminal.ShouldBeTrue();
        (await fixture.Client.WaitForResponseAsync(Tenant, "owner", "conv", "msg", TestContext.Current.CancellationToken)).Outcome.ShouldBe(GenieOutcome.Unknown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wrong_or_missing_ownership_refuses_reads_waits_and_follow_ups_before_credentials(bool recorded)
    {
        using var fixture = new Fixture((_, _) => throw new InvalidOperationException("Transport must not run."));
        if (recorded)
        {
            await fixture.Ownership.RecordAsync("conv", "other-owner", TestContext.Current.CancellationToken);
        }
        await Should.ThrowAsync<ConversationOwnershipException>(() => fixture.Client.GetMessageAsync(Tenant, "owner", "conv", "msg", TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ConversationOwnershipException>(() => fixture.Client.WaitForResponseAsync(Tenant, "owner", "conv", "msg", TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ConversationOwnershipException>(() => fixture.Client.SendMessageAsync(Tenant, "owner", "conv", "question", TestContext.Current.CancellationToken));
        fixture.Credential.Calls.ShouldBe(0);
        fixture.Handler.Methods.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unmapped_tenant_cannot_submit_or_read_an_owned_conversation()
    {
        using var fixture = new Fixture((_, _) => throw new InvalidOperationException("Transport must not run."));
        await fixture.Ownership.RecordAsync("conv", "owner", TestContext.Current.CancellationToken);
        await Should.ThrowAsync<InvalidOperationException>(() => fixture.Client.StartConversationAsync(OtherTenant, "owner", "question", TestContext.Current.CancellationToken));
        await Should.ThrowAsync<InvalidOperationException>(() => fixture.Client.GetMessageAsync(OtherTenant, "owner", "conv", "msg", TestContext.Current.CancellationToken));
        await Should.ThrowAsync<InvalidOperationException>(() => fixture.Client.WaitForResponseAsync(OtherTenant, "owner", "conv", "msg", TestContext.Current.CancellationToken));
        fixture.Credential.Calls.ShouldBe(0);
        fixture.Handler.Methods.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("conversation_id", "different")]
    [InlineData("message_id", "different")]
    [InlineData("id", "different")]
    [InlineData("space_id", "different")]
    public async Task A_read_rejects_conflicting_returned_identifiers(string field, string value)
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json($$"""{"status":"COMPLETED","{{field}}":"{{value}}"}""")));
        await fixture.Ownership.RecordAsync("conv", "owner", TestContext.Current.CancellationToken);
        await Should.ThrowAsync<InvalidOperationException>(() => fixture.Client.GetMessageAsync(Tenant, "owner", "conv", "msg", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Conflicting_duplicate_identity_fields_are_rejected()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json("""{"conversation_id":"different","conversation_id":"conv","status":"COMPLETED"}""")));
        await fixture.Ownership.RecordAsync("conv", "owner", TestContext.Current.CancellationToken);
        await Should.ThrowAsync<InvalidOperationException>(() => fixture.Client.WaitForResponseAsync(Tenant, "owner", "conv", "msg", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_follow_up_cannot_redirect_to_a_different_accepted_conversation()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json("""{"conversation_id":"different","message_id":"msg"}""")));
        await fixture.Ownership.RecordAsync("conv", "owner", TestContext.Current.CancellationToken);
        await Should.ThrowAsync<InvalidOperationException>(() => fixture.Client.SendMessageAsync(Tenant, "owner", "conv", "question", TestContext.Current.CancellationToken));
        fixture.Handler.Methods.ShouldBe([HttpMethod.Post]);
        (await fixture.Ownership.ListAsync("owner", TestContext.Current.CancellationToken)).ShouldBe(["conv"]);
    }

    [Fact]
    public async Task An_ambiguous_submission_is_not_retried()
    {
        using var fixture = new Fixture((_, _) => throw new HttpRequestException("Connection closed after request."));
        await Should.ThrowAsync<HttpRequestException>(() => fixture.Client.StartConversationAsync(Tenant, "owner", "question", TestContext.Current.CancellationToken));
        fixture.Handler.Methods.ShouldBe([HttpMethod.Post]);
        (await fixture.Ownership.ListAsync("owner", TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancellation_before_submission_makes_no_transport_or_credential_call()
    {
        using var fixture = new Fixture((_, _) => throw new InvalidOperationException("Transport must not run."));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => fixture.Client.StartConversationAsync(Tenant, "owner", "question", cancellation.Token));
        fixture.Credential.Calls.ShouldBe(0);
        fixture.Handler.Methods.ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancelling_an_in_flight_wait_keeps_the_accepted_message_resumable()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var block = true;
        using var fixture = new Fixture(async (_, token) =>
        {
            if (block)
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return Json("""{"status":"COMPLETED"}""");
        });
        await fixture.Ownership.RecordAsync("conv", "owner", TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var waiting = fixture.Client.WaitForResponseAsync(Tenant, "owner", "conv", "msg", cancellation.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => waiting);
        block = false;
        (await fixture.NewClient().WaitForResponseAsync(Tenant, "owner", "conv", "msg", TestContext.Current.CancellationToken)).Outcome.ShouldBe(GenieOutcome.Completed);
        fixture.Handler.Methods.All(method => method == HttpMethod.Get).ShouldBeTrue();
    }

    [Fact]
    public async Task Wait_timeout_retains_known_ids_and_a_single_read_reports_a_read_timeout()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json("""{"status":"COMPLETED"}""")));
        fixture.Handler.ResponseDelay = () => fixture.Time.Advance(TimeSpan.FromSeconds(6));
        await fixture.Ownership.RecordAsync("conv", "owner", TestContext.Current.CancellationToken);
        var answer = await fixture.Client.WaitForResponseAsync(Tenant, "owner", "conv", "msg", TestContext.Current.CancellationToken);
        answer.ShouldBe(new GenieAnswer("conv", "msg", GenieOutcome.TimedOut, null, null));
        var read = await Should.ThrowAsync<TimeoutException>(() => fixture.Client.GetMessageAsync(Tenant, "owner", "conv", "msg", TestContext.Current.CancellationToken));
        read.ShouldNotBeOfType<GenieResponseTimeoutException>();
    }

    [Fact]
    public async Task A_per_request_http_timeout_during_a_wait_polls_again_instead_of_looking_like_cancellation()
    {
        var gets = 0;
        using var fixture = new Fixture((_, _) => ++gets == 1
            ? throw HttpClientTimeout()
            : Task.FromResult(Json("""{"status":"COMPLETED"}""")));
        await fixture.Ownership.RecordAsync("conv", "owner", TestContext.Current.CancellationToken);

        var waiting = fixture.Client.WaitForResponseAsync(Tenant, "owner", "conv", "msg", TestContext.Current.CancellationToken);
        for (var step = 0; step < 10 && !waiting.IsCompleted; step++)
        {
            // Releases the one-second poll backoff; the five-second deadline is never reached.
            await Task.Delay(10, TestContext.Current.CancellationToken);
            fixture.Time.Advance(TimeSpan.FromMilliseconds(500));
        }

        var answer = await waiting;
        answer.Outcome.ShouldBe(GenieOutcome.Completed);
        fixture.Handler.Methods.ShouldBe([HttpMethod.Get, HttpMethod.Get]);
    }

    [Fact]
    public async Task A_per_request_http_timeout_reports_a_read_timeout_or_unknown_acceptance()
    {
        using var fixture = new Fixture((_, _) => throw HttpClientTimeout());
        await fixture.Ownership.RecordAsync("conv", "owner", TestContext.Current.CancellationToken);

        var read = await Should.ThrowAsync<TimeoutException>(() => fixture.Client.GetMessageAsync(Tenant, "owner", "conv", "msg", TestContext.Current.CancellationToken));
        read.ShouldNotBeOfType<GenieResponseTimeoutException>();
        await Should.ThrowAsync<GenieResponseTimeoutException>(() => fixture.Client.SendMessageAsync(Tenant, "owner", "conv", "question", TestContext.Current.CancellationToken));
        await Should.ThrowAsync<GenieResponseTimeoutException>(() => fixture.Client.StartConversationAsync(Tenant, "owner", "question", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("""{"conversation_id":"..","message_id":"msg"}""")]
    [InlineData("""{"conversation_id":"conv","message_id":" "}""")]
    [InlineData("""{"conversation_id":"conv"}""")]
    public async Task An_invalid_accepted_identity_is_a_platform_error_not_a_caller_error(string body)
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json(body)));
        var failure = await Should.ThrowAsync<InvalidOperationException>(() => fixture.Client.StartConversationAsync(Tenant, "owner", "question", TestContext.Current.CancellationToken));
        failure.ShouldBeOfType<InvalidOperationException>();
        (await fixture.Ownership.ListAsync("owner", TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    public async Task Invalid_ids_are_rejected_before_credentials_or_transport(string id)
    {
        using var fixture = new Fixture((_, _) => throw new InvalidOperationException("Transport must not run."));
        await fixture.Ownership.RecordAsync("conv", "owner", TestContext.Current.CancellationToken);
        await Should.ThrowAsync<ArgumentException>(() => fixture.Client.GetMessageAsync(Tenant, "owner", "conv", id, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentException>(() => fixture.Client.WaitForResponseAsync(Tenant, "owner", id, "msg", TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentException>(() => fixture.Client.SendMessageAsync(Tenant, "owner", id, "question", TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentException>(() => fixture.Client.DeleteAsync(Tenant, "owner", id, TestContext.Current.CancellationToken));
        fixture.Credential.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task An_existing_interface_implementation_retains_its_original_contract()
    {
        IGenieConversations legacy = new LegacyAdapter();
        (await legacy.ListAsync("owner", TestContext.Current.CancellationToken)).ShouldBeEmpty();
        await Should.ThrowAsync<NotSupportedException>(() => legacy.StartConversationAsync(Tenant, "owner", "question", TestContext.Current.CancellationToken));
        await Should.ThrowAsync<NotSupportedException>(() => legacy.SendMessageAsync(Tenant, "owner", "conv", "question", TestContext.Current.CancellationToken));
        await Should.ThrowAsync<NotSupportedException>(() => legacy.GetMessageAsync(Tenant, "owner", "conv", "msg", TestContext.Current.CancellationToken));
        await Should.ThrowAsync<NotSupportedException>(() => legacy.WaitForResponseAsync(Tenant, "owner", "conv", "msg", TestContext.Current.CancellationToken));
    }

    /// <summary>What <see cref="HttpClient.Timeout"/> throws: a cancellation the caller did not ask for.</summary>
    private static TaskCanceledException HttpClientTimeout() =>
        new("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException());

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _http;
        private readonly GenieOptions _options = new() { ResponseTimeout = TimeSpan.FromSeconds(5) };
        public RecordingHandler Handler { get; }
        public CountingCredential Credential { get; } = new();
        public TestOwnership Ownership { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public IGenieConversations Client { get; }

        public Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            Handler = new RecordingHandler(respond);
            _http = new HttpClient(Handler) { BaseAddress = new Uri("https://workspace.invalid/") };
            _options.Spaces.Add(Tenant.TenantId.ToString(), "space");
            Client = NewClient();
        }

        public GenieConversations NewClient() => new(_http, Credential, Options.Create(_options), Time, Ownership);
        public void Dispose() => _http.Dispose();
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<HttpMethod> Methods { get; } = [];
        public List<string> Paths { get; } = [];
        public Action? ResponseDelay { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);
            Paths.Add(request.RequestUri!.AbsolutePath);
            var response = await respond(request, cancellationToken);
            ResponseDelay?.Invoke();
            return response;
        }
    }

    private sealed class TestOwnership : IConversationOwnership
    {
        private readonly Dictionary<string, string> _owners = [];
        public ValueTask RecordAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _owners.Add(conversationId, ownerKey);
            return ValueTask.CompletedTask;
        }
        public ValueTask<bool> IsOwnerAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_owners.TryGetValue(conversationId, out var owner) && owner == ownerKey);
        }
        public ValueTask<IReadOnlyList<string>> ListAsync(string ownerKey, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<string>>([.. _owners.Where(pair => pair.Value == ownerKey).Select(pair => pair.Key)]);
        public ValueTask RemoveAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default)
        {
            if (_owners.TryGetValue(conversationId, out var owner) && owner == ownerKey)
            {
                _owners.Remove(conversationId);
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingCredential : TokenCredential
    {
        public int Calls { get; private set; }
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return new AccessToken("synthetic-token", DateTimeOffset.MaxValue);
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class LegacyAdapter : IGenieConversations
    {
        public Task<GenieAnswer> AskAsync(TenantContext tenant, string ownerKey, string question, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GenieAnswer> ContinueAsync(TenantContext tenant, string ownerKey, string conversationId, string question, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<string>> ListAsync(string ownerKey, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<string>>([]);
        public Task DeleteAsync(TenantContext tenant, string ownerKey, string conversationId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
