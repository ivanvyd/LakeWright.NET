using Azure.Core;
using LakeWright.Conversations;
using LakeWright.Core.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMockRequest = WireMock.RequestBuilders.Request;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// Genie conversations, scoped to a tenant's own agent.
/// </summary>
/// <remarks>
/// The Conversation API takes no filter and no viewer identity, so the agent is the only tenancy
/// boundary there is. These assert that the agent in the URL always comes from the resolved tenant,
/// and that an unmapped tenant is refused rather than served from something else.
/// </remarks>
[Trait("Category", "TenantIsolation")]
public class GenieConversationTests : IDisposable
{
    private const string TestOwner = "test-owner";
    private const string AcmeSpace = "space-acme";
    private const string GlobexSpace = "space-globex";

    private static readonly TenantId AcmeId = TenantId.Parse("0198f000-0000-7000-8000-00000000ac11");
    private static readonly TenantId GlobexId = TenantId.Parse("0198f000-0000-7000-8000-00000000617b");
    private static readonly TenantId StrangerId = TenantId.Parse("0198f000-0000-7000-8000-0000000057a2");

    private readonly WireMockServer _workspace = WireMockServer.Start();

    public void Dispose()
    {
        _workspace.Stop();
        _workspace.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_tenant_with_no_agent_is_refused_rather_than_served_from_another()
    {
        // Arrange
        StubConversation(AcmeSpace, "COMPLETED");
        var genie = Conversations();

        // Act
        var act = async () => await genie.AskAsync(
            Tenant(StrangerId), TestOwner, "how many orders?", TestContext.Current.CancellationToken);

        // Assert — and nothing was asked of the workspace on its behalf.
        var error = await act.ShouldThrowAsync<InvalidOperationException>();
        error.Message.ShouldContain(StrangerId.ToString());
        _workspace.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_agent_in_the_path_comes_from_the_tenant()
    {
        // Arrange
        StubConversation(AcmeSpace, "COMPLETED");
        StubConversation(GlobexSpace, "COMPLETED");
        var genie = Conversations();

        // Act — the same question from two tenants.
        await genie.AskAsync(Tenant(AcmeId), TestOwner, "how many orders?", TestContext.Current.CancellationToken);
        await genie.AskAsync(Tenant(GlobexId), TestOwner, "how many orders?", TestContext.Current.CancellationToken);

        // Assert
        var starts = _workspace.LogEntries
            .Where(e => e.RequestMessage!.Path.EndsWith("start-conversation", StringComparison.Ordinal))
            .Select(e => e.RequestMessage!.Path)
            .ToArray();

        starts.ShouldBe([
            $"/api/2.0/genie/spaces/{AcmeSpace}/start-conversation",
            $"/api/2.0/genie/spaces/{GlobexSpace}/start-conversation",
        ]);
    }

    [Fact]
    public async Task An_unowned_conversation_is_invisible_before_any_workspace_call()
    {
        // Arrange — conversations created before ownership tracking must not become shared by
        // guessing their identifiers.
        StubConversation(AcmeSpace, "COMPLETED");
        var genie = Conversations();

        // Act
        var act = () => genie.ContinueAsync(
            Tenant(AcmeId), TestOwner, "conv-owned-by-globex", "and last month?", TestContext.Current.CancellationToken);

        await Should.ThrowAsync<ConversationOwnershipException>(act);
        _workspace.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_acknowledged_shared_space_is_used_for_initial_and_follow_up_questions()
    {
        const string sharedSpace = "staff-only-space";
        StubConversation(sharedSpace, "COMPLETED");
        var options = new GenieOptions
        {
            WorkspaceUrl = _workspace.Urls[0],
            SharedSpaceId = sharedSpace,
            AcknowledgeNoTenantIsolation = true,
        };
        var genie = new GenieConversations(
            new HttpClient { BaseAddress = new Uri(_workspace.Urls[0] + "/") },
            new StubCredential(),
            Options.Create(options),
            new FakeTimeProvider());

        var answer = await genie.AskAsync(
            Tenant(AcmeId), "staff-user", "how many orders?", TestContext.Current.CancellationToken);
        await genie.ContinueAsync(
            Tenant(GlobexId), "staff-user", answer.ConversationId, "and last month?", TestContext.Current.CancellationToken);

        _workspace.LogEntries
            .Select(entry => entry.RequestMessage!.Path)
            .ShouldContain($"/api/2.0/genie/spaces/{sharedSpace}/start-conversation");
        _workspace.LogEntries
            .Select(entry => entry.RequestMessage!.Path)
            .ShouldContain($"/api/2.0/genie/spaces/{sharedSpace}/conversations/{answer.ConversationId}/messages");
    }

    [Fact]
    public async Task Asking_records_an_owner_and_listing_never_exposes_another_owners_conversation()
    {
        StubConversation(AcmeSpace, "COMPLETED");
        var genie = Conversations();

        var answer = await genie.AskAsync(
            Tenant(AcmeId), "alice", "how many orders?", TestContext.Current.CancellationToken);

        (await genie.ListAsync("alice", TestContext.Current.CancellationToken)).ShouldBe([answer.ConversationId]);
        (await genie.ListAsync("mallory", TestContext.Current.CancellationToken)).ShouldBeEmpty();
        await Should.ThrowAsync<ConversationOwnershipException>(() => genie.ContinueAsync(
            Tenant(AcmeId), "mallory", answer.ConversationId, "and last month?", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Deleting_an_owned_conversation_removes_its_record_only_after_the_workspace_accepts_it()
    {
        StubConversation(AcmeSpace, "COMPLETED");
        _workspace
            .Given(WireMockRequest.Create()
                .WithPath($"/api/2.0/genie/spaces/{AcmeSpace}/conversations/conv-1")
                .UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(200));
        var genie = Conversations();

        var answer = await genie.AskAsync(
            Tenant(AcmeId), "alice", "how many orders?", TestContext.Current.CancellationToken);
        await genie.DeleteAsync(Tenant(AcmeId), "alice", answer.ConversationId, TestContext.Current.CancellationToken);

        (await genie.ListAsync("alice", TestContext.Current.CancellationToken)).ShouldBeEmpty();
        _workspace.LogEntries.Select(entry => entry.RequestMessage!.Path).ShouldContain(
            $"/api/2.0/genie/spaces/{AcmeSpace}/conversations/{answer.ConversationId}");
    }

    [Fact]
    public async Task Deleting_an_unowned_conversation_does_not_call_the_workspace()
    {
        var genie = Conversations();

        await Should.ThrowAsync<ConversationOwnershipException>(() => genie.DeleteAsync(
            Tenant(AcmeId), "mallory", "conv-owned-by-alice", TestContext.Current.CancellationToken));

        _workspace.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public void Shared_space_requires_an_explicit_no_isolation_acknowledgement_at_startup()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Genie:WorkspaceUrl"] = "https://workspace.example",
            ["Genie:SharedSpaceId"] = "staff-only-space",
        }).Build();
        services.AddLakeWrightGenie(configuration);

        var validate = () => services.BuildServiceProvider()
            .GetRequiredService<IStartupValidator>()
            .Validate();

        validate.ShouldThrow<OptionsValidationException>()
            .Message.ShouldContain("AcknowledgeNoTenantIsolation");
    }

    [Theory]
    [InlineData("COMPLETED", GenieOutcome.Completed)]
    [InlineData("FAILED", GenieOutcome.Failed)]
    [InlineData("CANCELLED", GenieOutcome.Cancelled)]
    public async Task Platform_states_map_to_the_closed_set(string status, GenieOutcome expected)
    {
        // Arrange
        StubConversation(AcmeSpace, status);
        var genie = Conversations();

        // Act
        var answer = await genie.AskAsync(
            Tenant(AcmeId), TestOwner, "how many orders?", TestContext.Current.CancellationToken);

        // Assert
        answer.Outcome.ShouldBe(expected);
    }

    [Fact]
    public async Task The_answer_carries_the_text_and_the_generated_sql()
    {
        // Arrange
        StubConversation(AcmeSpace, "COMPLETED");
        var genie = Conversations();

        // Act
        var answer = await genie.AskAsync(
            Tenant(AcmeId), TestOwner, "how many orders?", TestContext.Current.CancellationToken);

        // Assert
        answer.Text.ShouldBe("There were 42 orders.");
        answer.GeneratedSql.ShouldBe("SELECT count(*) FROM orders");
        answer.ConversationId.ShouldBe("conv-1");
    }

    [Fact]
    public async Task A_state_this_library_does_not_know_is_not_treated_as_terminal()
    {
        // Arrange — an in-progress state Databricks has not documented. Treating an unrecognised
        // state as terminal would return an empty answer for a question still being answered, so
        // polling continues.
        _workspace
            .Given(WireMockRequest.Create().WithPath("/api/2.0/genie/spaces/*/start-conversation").UsingPost())
            .RespondWith(Json("""{"conversation_id":"conv-1","message_id":"msg-1"}"""));

        _workspace
            .Given(WireMockRequest.Create().WithPath("/api/2.0/genie/spaces/*/conversations/*/messages/msg-1").UsingGet())
            .InScenario("poll")
            .WillSetStateTo("answered")
            .RespondWith(Json("""{"status":"SOMETHING_NEW_AND_UNDOCUMENTED"}"""));

        _workspace
            .Given(WireMockRequest.Create().WithPath("/api/2.0/genie/spaces/*/conversations/*/messages/msg-1").UsingGet())
            .InScenario("poll")
            .WhenStateIs("answered")
            .RespondWith(Json(CompletedMessage));

        var time = new FakeTimeProvider();
        var genie = Conversations(time);

        // Act
        var asking = genie.AskAsync(Tenant(AcmeId), TestOwner, "how many orders?", TestContext.Current.CancellationToken);
        await AdvanceUntilDone(time, asking);
        var answer = await asking;

        // Assert
        answer.Outcome.ShouldBe(GenieOutcome.Completed);
        answer.Text.ShouldBe("There were 42 orders.");
    }

    [Fact]
    public async Task A_question_that_never_finishes_times_out_rather_than_hanging()
    {
        // Arrange
        _workspace
            .Given(WireMockRequest.Create().WithPath("/api/2.0/genie/spaces/*/start-conversation").UsingPost())
            .RespondWith(Json("""{"conversation_id":"conv-1","message_id":"msg-1"}"""));

        _workspace
            .Given(WireMockRequest.Create().WithPath("/api/2.0/genie/spaces/*/conversations/*/messages/msg-1").UsingGet())
            .RespondWith(Json("""{"status":"EXECUTING_QUERY"}"""));

        var time = new FakeTimeProvider();
        var genie = Conversations(time);

        // Act
        var asking = genie.AskAsync(Tenant(AcmeId), TestOwner, "how many orders?", TestContext.Current.CancellationToken);
        await AdvanceUntilDone(time, asking);
        var answer = await asking;

        // Assert
        answer.Outcome.ShouldBe(GenieOutcome.TimedOut);
    }

    /// <summary>
    /// Drives the fake clock past each poll delay until the call returns. Without this the polling
    /// loop waits forever, because a fake clock does not move on its own.
    /// </summary>
    private static async Task AdvanceUntilDone(FakeTimeProvider time, Task pending)
    {
        for (var i = 0; i < 200 && !pending.IsCompleted; i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromMinutes(1));
        }
    }

    private const string CompletedMessage = """
    {
      "status": "COMPLETED",
      "attachments": [
        { "text": { "content": "There were 42 orders." } },
        { "query": { "query": "SELECT count(*) FROM orders" } }
      ]
    }
    """;

    private void StubConversation(string space, string status)
    {
        _workspace
            .Given(WireMockRequest.Create().WithPath($"/api/2.0/genie/spaces/{space}/start-conversation").UsingPost())
            .RespondWith(Json("""{"conversation_id":"conv-1","message_id":"msg-1"}"""));

        _workspace
            .Given(WireMockRequest.Create().WithPath($"/api/2.0/genie/spaces/{space}/conversations/*/messages").UsingPost())
            .RespondWith(Json("""{"conversation_id":"conv-1","message_id":"msg-1"}"""));

        _workspace
            .Given(WireMockRequest.Create().WithPath($"/api/2.0/genie/spaces/{space}/conversations/*/messages/msg-1").UsingGet())
            .RespondWith(Json(status == "COMPLETED" ? CompletedMessage : $$"""{"status":"{{status}}"}"""));
    }

    private static IResponseBuilder Json(string body) =>
        Response.Create().WithHeader("Content-Type", "application/json").WithBody(body);

    private GenieConversations Conversations(FakeTimeProvider? time = null)
    {
        var options = new GenieOptions { WorkspaceUrl = _workspace.Urls[0] };
        options.Spaces[AcmeId.ToString()] = AcmeSpace;
        options.Spaces[GlobexId.ToString()] = GlobexSpace;

        return new GenieConversations(
            new HttpClient { BaseAddress = new Uri(_workspace.Urls[0] + "/") },
            new StubCredential(),
            Options.Create(options),
            time ?? new FakeTimeProvider());
    }

    private static TenantContext Tenant(TenantId id) =>
        TenantContextFactory.ForTenant(id, "lakewright_dev", "analytics");

    private sealed class StubCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken cancellationToken) =>
            new("a-token", DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext context, CancellationToken cancellationToken) =>
            new(GetToken(context, cancellationToken));
    }
}
