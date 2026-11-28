using System.Net;
using LakeWright.Conversations;
using LakeWright.Core.Tenancy;
using Microsoft.Extensions.Time.Testing;
using static LakeWright.TenantIsolation.Tests.GenieTestKit;

namespace LakeWright.TenantIsolation.Tests;

[Trait("Category", "TenantIsolation")]
public sealed class GenieDeadlineAndOwnershipTests
{
    private static readonly TenantContext Tenant = GenieTestKit.Tenant(AcmeId);
    private static readonly TenantContext OtherTenant = GenieTestKit.Tenant(GlobexId);

    [Fact]
    public async Task Creation_response_after_deadline_reports_unknown_acceptance_without_polling()
    {
        var time = new FakeTimeProvider();
        using var fixture = new GenieFixture((_, _) =>
        {
            time.Advance(TimeSpan.FromMilliseconds(11));
            return Task.FromResult(JsonResponse("""{"conversation_id":"conv-1","message_id":"msg-1"}"""));
        }, TimeSpan.FromMilliseconds(10), time: time);

        await Should.ThrowAsync<GenieResponseTimeoutException>(() => fixture.Client.AskAsync(
            Tenant, "owner-a", "question", TestContext.Current.CancellationToken));

        (await fixture.Ownership.ListAsync("owner-a", TestContext.Current.CancellationToken)).ShouldBeEmpty();
        fixture.Handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Accepted_conversation_is_owned_and_returns_timed_out_when_a_terminal_poll_arrives_late()
    {
        var time = new FakeTimeProvider();
        using var fixture = new GenieFixture((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return Task.FromResult(JsonResponse("""{"conversation_id":"conv-1","message_id":"msg-1"}"""));
            }

            time.Advance(TimeSpan.FromMilliseconds(11));
            return Task.FromResult(JsonResponse("""{"status":"COMPLETED"}"""));
        }, TimeSpan.FromMilliseconds(10), time: time);

        var answer = await fixture.Client.AskAsync(Tenant, "owner-a", "question", TestContext.Current.CancellationToken);

        answer.Outcome.ShouldBe(GenieOutcome.TimedOut);
        answer.ConversationId.ShouldBe("conv-1");
        (await fixture.Ownership.ListAsync("owner-a", TestContext.Current.CancellationToken)).ShouldBe(["conv-1"]);
    }

    [Fact]
    public async Task Continue_submits_another_question_after_a_poll_failure()
    {
        var polls = 0;
        var submissions = 0;
        using var fixture = new GenieFixture((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                submissions++;
                return Task.FromResult(JsonResponse("""{"conversation_id":"conv-1","message_id":"msg-1"}"""));
            }

            polls++;
            return Task.FromResult(polls == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : JsonResponse("""{"status":"COMPLETED"}"""));
        });

        await Should.ThrowAsync<HttpRequestException>(() => fixture.Client.AskAsync(
            Tenant, "owner-a", "question", TestContext.Current.CancellationToken));

        (await fixture.Ownership.ListAsync("owner-a", TestContext.Current.CancellationToken)).ShouldBe(["conv-1"]);
        var resumed = await fixture.Client.ContinueAsync(
            Tenant, "owner-a", "conv-1", "continue", TestContext.Current.CancellationToken);
        resumed.Outcome.ShouldBe(GenieOutcome.Completed);
        submissions.ShouldBe(2);
    }

    [Fact]
    public async Task Separate_submission_returns_accepted_ids_after_caller_cancellation_during_ownership_persistence()
    {
        using var cancellation = new CancellationTokenSource();
        var ownership = new CancellingOwnership(cancellation);
        using var fixture = new GenieFixture(
            (_, _) => Task.FromResult(JsonResponse("""{"conversation_id":"conv-1","message_id":"msg-1"}""")),
            ownership: ownership);

        var accepted = await fixture.Client.StartConversationAsync(Tenant, "owner-a", "question", cancellation.Token);

        accepted.ShouldBe(new GenieAcceptedMessage("conv-1", "msg-1"));
        cancellation.IsCancellationRequested.ShouldBeTrue();
        (await ownership.ListAsync("owner-a", TestContext.Current.CancellationToken)).ShouldBe(["conv-1"]);
        fixture.Handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Ownership_store_failure_after_acceptance_is_observable_and_stops_before_polling()
    {
        using var fixture = new GenieFixture((request, _) => Task.FromResult(
            request.Method == HttpMethod.Post
                ? JsonResponse("{\"conversation_id\":\"conv-1\",\"message_id\":\"msg-1\"}")
                : JsonResponse("{\"status\":\"COMPLETED\"}")), ownership: new FailingOwnership());

        var failure = await Should.ThrowAsync<ConversationOwnershipPersistenceException>(() => fixture.Client.AskAsync(
            Tenant, "owner-a", "question", TestContext.Current.CancellationToken));

        failure.ConversationId.ShouldBe("conv-1");
        failure.MessageId.ShouldBe("msg-1");
        fixture.Handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Ownership_persistence_is_bounded_after_acceptance()
    {
        var time = new FakeTimeProvider();
        using var fixture = new GenieFixture(
            (_, _) => Task.FromResult(JsonResponse("{\"conversation_id\":\"conv-1\",\"message_id\":\"msg-1\"}")),
            TimeSpan.FromMilliseconds(10),
            new BlockingOwnership(time),
            time);

        var asking = fixture.Client.AskAsync(Tenant, "owner-a", "question", TestContext.Current.CancellationToken);
        await Task.Delay(5, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMilliseconds(11));

        var failure = await Should.ThrowAsync<ConversationOwnershipPersistenceException>(() => asking);
        failure.ConversationId.ShouldBe("conv-1");
        failure.MessageId.ShouldBe("msg-1");
        fixture.Handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Caller_cancellation_after_acceptance_keeps_the_conversation_owned()
    {
        using var cancellation = new CancellationTokenSource();
        var ownership = new CancellingOwnership(cancellation);
        using var fixture = new GenieFixture((request, _) => Task.FromResult(
            request.Method == HttpMethod.Post
                ? JsonResponse("{\"conversation_id\":\"conv-1\",\"message_id\":\"msg-1\"}")
                : JsonResponse("{\"status\":\"COMPLETED\"}")), ownership: ownership);

        await Should.ThrowAsync<OperationCanceledException>(() => fixture.Client.AskAsync(
            Tenant, "owner-a", "question", cancellation.Token));

        (await ownership.ListAsync("owner-a", TestContext.Current.CancellationToken)).ShouldBe(["conv-1"]);
    }

    [Fact]
    public async Task The_same_owner_cannot_reach_a_conversation_from_another_mapped_tenant_before_credentials()
    {
        using var fixture = new GenieFixture(
            (request, _) => request.RequestUri!.AbsolutePath.EndsWith("start-conversation", StringComparison.Ordinal)
                ? Task.FromResult(JsonResponse("""{"conversation_id":"conv","message_id":"msg"}"""))
                : throw new InvalidOperationException("Transport must not run."),
            ownership: BuiltInOwnership());
        fixture.MapSpace(GlobexId, "space-globex");
        var accepted = await fixture.Client.StartConversationAsync(Tenant, "owner", "question", TestContext.Current.CancellationToken);
        var credentialCalls = fixture.Credential.Calls;

        await Should.ThrowAsync<ConversationOwnershipException>(() => fixture.Client.ContinueAsync(
            OtherTenant, "owner", accepted.ConversationId, "question", TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ConversationOwnershipException>(() => fixture.Client.SendMessageAsync(
            OtherTenant, "owner", accepted.ConversationId, "question", TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ConversationOwnershipException>(() => fixture.Client.GetMessageAsync(
            OtherTenant, "owner", accepted.ConversationId, accepted.MessageId, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ConversationOwnershipException>(() => fixture.Client.WaitForResponseAsync(
            OtherTenant, "owner", accepted.ConversationId, accepted.MessageId, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ConversationOwnershipException>(() => fixture.Client.DeleteAsync(
            OtherTenant, "owner", accepted.ConversationId, TestContext.Current.CancellationToken));

        fixture.Credential.Calls.ShouldBe(credentialCalls);
        fixture.Handler.Paths.ShouldBe(["/api/2.0/genie/spaces/space/start-conversation"]);
        (await fixture.Ownership.ListAsync("owner", TestContext.Current.CancellationToken)).ShouldBe(["conv"]);
    }

    [Fact]
    public async Task The_tenant_a_conversation_was_started_in_can_still_use_every_operation()
    {
        using var fixture = new GenieFixture(
            (request, _) => Task.FromResult(
                request.Method == HttpMethod.Post ? JsonResponse("""{"conversation_id":"conv","message_id":"msg"}""")
                : request.Method == HttpMethod.Get ? JsonResponse("""{"status":"COMPLETED"}""")
                : new HttpResponseMessage(HttpStatusCode.OK)),
            ownership: BuiltInOwnership());
        fixture.MapSpace(GlobexId, "space-globex");
        var accepted = await fixture.Client.StartConversationAsync(Tenant, "owner", "question", TestContext.Current.CancellationToken);

        (await fixture.Client.ContinueAsync(Tenant, "owner", "conv", "question", TestContext.Current.CancellationToken))
            .Outcome.ShouldBe(GenieOutcome.Completed);
        (await fixture.Client.SendMessageAsync(Tenant, "owner", "conv", "question", TestContext.Current.CancellationToken))
            .ShouldBe(accepted);
        (await fixture.Client.GetMessageAsync(Tenant, "owner", "conv", "msg", TestContext.Current.CancellationToken))
            .IsTerminal.ShouldBeTrue();
        (await fixture.Client.WaitForResponseAsync(Tenant, "owner", "conv", "msg", TestContext.Current.CancellationToken))
            .Outcome.ShouldBe(GenieOutcome.Completed);
        await fixture.Client.DeleteAsync(Tenant, "owner", "conv", TestContext.Current.CancellationToken);

        fixture.Handler.Paths.ShouldAllBe(path => path.StartsWith("/api/2.0/genie/spaces/space/", StringComparison.Ordinal));
        fixture.Handler.Methods.Last().ShouldBe(HttpMethod.Delete);
        (await fixture.Ownership.ListAsync("owner", TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_record_written_without_a_tenant_is_still_accepted_for_its_owner()
    {
        using var fixture = new GenieFixture(
            (_, _) => Task.FromResult(JsonResponse("""{"status":"COMPLETED"}""")),
            ownership: BuiltInOwnership());
        await fixture.Ownership.RecordAsync("conv", "owner", TestContext.Current.CancellationToken);

        (await fixture.Client.GetMessageAsync(Tenant, "owner", "conv", "msg", TestContext.Current.CancellationToken))
            .Outcome.ShouldBe(GenieOutcome.Completed);
        await Should.ThrowAsync<ConversationOwnershipException>(() => fixture.Client.GetMessageAsync(
            Tenant, "other-owner", "conv", "msg", TestContext.Current.CancellationToken));
        fixture.Handler.Methods.ShouldBe([HttpMethod.Get]);
    }

    [Fact]
    public async Task A_conversation_started_in_a_mapped_tenant_cannot_be_continued_through_the_shared_space()
    {
        using var fixture = new GenieFixture(
            (request, _) => request.RequestUri!.AbsolutePath.EndsWith("start-conversation", StringComparison.Ordinal)
                ? Task.FromResult(JsonResponse("""{"conversation_id":"conv","message_id":"msg"}"""))
                : throw new InvalidOperationException("Transport must not run."),
            ownership: BuiltInOwnership());
        fixture.ShareSpace("staff-only-space");
        var accepted = await fixture.Client.StartConversationAsync(Tenant, "owner", "question", TestContext.Current.CancellationToken);

        await Should.ThrowAsync<ConversationOwnershipException>(() => fixture.Client.ContinueAsync(
            OtherTenant, "owner", accepted.ConversationId, "question", TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ConversationOwnershipException>(() => fixture.Client.GetMessageAsync(
            OtherTenant, "owner", accepted.ConversationId, accepted.MessageId, TestContext.Current.CancellationToken));

        fixture.Handler.Paths.ShouldBe(["/api/2.0/genie/spaces/space/start-conversation"]);
    }

    [Fact]
    public async Task A_conversation_started_through_the_shared_space_cannot_be_reached_from_a_mapped_tenant()
    {
        using var fixture = new GenieFixture(
            (request, _) => request.RequestUri!.AbsolutePath.EndsWith("start-conversation", StringComparison.Ordinal)
                ? Task.FromResult(JsonResponse("""{"conversation_id":"conv","message_id":"msg"}"""))
                : throw new InvalidOperationException("Transport must not run."),
            ownership: BuiltInOwnership());
        fixture.ShareSpace("staff-only-space");
        var accepted = await fixture.Client.StartConversationAsync(OtherTenant, "owner", "question", TestContext.Current.CancellationToken);
        var credentialCalls = fixture.Credential.Calls;

        await Should.ThrowAsync<ConversationOwnershipException>(() => fixture.Client.GetMessageAsync(
            Tenant, "owner", accepted.ConversationId, accepted.MessageId, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ConversationOwnershipException>(() => fixture.Client.ContinueAsync(
            Tenant, "owner", accepted.ConversationId, "question", TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ConversationOwnershipException>(() => fixture.Client.SendMessageAsync(
            Tenant, "owner", accepted.ConversationId, "question", TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ConversationOwnershipException>(() => fixture.Client.WaitForResponseAsync(
            Tenant, "owner", accepted.ConversationId, accepted.MessageId, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ConversationOwnershipException>(() => fixture.Client.DeleteAsync(
            Tenant, "owner", accepted.ConversationId, TestContext.Current.CancellationToken));

        fixture.Credential.Calls.ShouldBe(credentialCalls);
        fixture.Handler.Paths.ShouldBe(["/api/2.0/genie/spaces/staff-only-space/start-conversation"]);
    }

    [Fact]
    public async Task The_built_in_store_binds_a_conversation_to_the_tenant_it_was_recorded_in()
    {
        var ownership = BuiltInOwnership();
        await ownership.RecordAsync(AcmeId, "conv", "owner", TestContext.Current.CancellationToken);

        (await ownership.IsOwnerAsync(AcmeId, "conv", "owner", TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await ownership.IsOwnerAsync(GlobexId, "conv", "owner", TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await ownership.IsOwnerAsync(AcmeId, "conv", "other-owner", TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await ownership.IsOwnerAsync("conv", "owner", TestContext.Current.CancellationToken)).ShouldBeTrue();

        await ownership.RecordAsync(AcmeId, "conv", "owner", TestContext.Current.CancellationToken);
        await ownership.RecordAsync("conv", "owner", TestContext.Current.CancellationToken);
        await Should.ThrowAsync<ConversationOwnershipException>(async () => await ownership.RecordAsync(
            GlobexId, "conv", "owner", TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ConversationOwnershipException>(async () => await ownership.RecordAsync(
            AcmeId, "conv", "other-owner", TestContext.Current.CancellationToken));
        (await ownership.IsOwnerAsync(GlobexId, "conv", "owner", TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task The_built_in_store_keeps_a_record_without_a_tenant_valid_for_its_owner_in_any_tenant()
    {
        var ownership = BuiltInOwnership();
        await ownership.RecordAsync("conv", "owner", TestContext.Current.CancellationToken);

        await ownership.RecordAsync(AcmeId, "conv", "owner", TestContext.Current.CancellationToken);
        (await ownership.IsOwnerAsync(AcmeId, "conv", "owner", TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await ownership.IsOwnerAsync(GlobexId, "conv", "owner", TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await ownership.IsOwnerAsync(AcmeId, "conv", "other-owner", TestContext.Current.CancellationToken)).ShouldBeFalse();
        await Should.ThrowAsync<ConversationOwnershipException>(async () => await ownership.RecordAsync(
            AcmeId, "conv", "other-owner", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_built_in_store_lists_and_removes_tenant_bound_records_by_owner()
    {
        var ownership = BuiltInOwnership();
        await ownership.RecordAsync(AcmeId, "conv-a", "owner", TestContext.Current.CancellationToken);
        await ownership.RecordAsync(GlobexId, "conv-b", "owner", TestContext.Current.CancellationToken);

        (await ownership.ListAsync("owner", TestContext.Current.CancellationToken)).ShouldBe(["conv-a", "conv-b"]);
        await ownership.RemoveAsync("conv-a", "other-owner", TestContext.Current.CancellationToken);
        (await ownership.IsOwnerAsync(AcmeId, "conv-a", "owner", TestContext.Current.CancellationToken)).ShouldBeTrue();
        await ownership.RemoveAsync("conv-a", "owner", TestContext.Current.CancellationToken);
        (await ownership.ListAsync("owner", TestContext.Current.CancellationToken)).ShouldBe(["conv-b"]);
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
}
