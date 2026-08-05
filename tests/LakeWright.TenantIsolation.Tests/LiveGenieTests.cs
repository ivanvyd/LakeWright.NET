using LakeWright.Conversations;
using LakeWright.Core.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// Exercises the Genie Conversation API against a real Genie Agent.
/// </summary>
/// <remarks>
/// Excluded from the default run and from CI: this needs a workspace, an agent curated over real
/// tables, and a warehouse that bills while it answers.
///
/// The WireMock suite proves the tenant-to-agent mapping and the polling loop. Neither proves that
/// a question actually round-trips, or that the attachment shape this library reads is the shape
/// Databricks sends — the Conversation API is Public Preview, so that second one is the claim most
/// likely to go stale without warning.
///
/// Run with:
///   az login
///   DATABRICKS_HOST=https://... LAKEWRIGHT_GENIE_SPACE_ID=... \
///   dotnet test --filter Category=Live
/// </remarks>
[Trait("Category", "Live")]
public class LiveGenieTests
{
    private static readonly TenantId AcmeId = TenantId.Parse("0198f000-0000-7000-8000-00000000ac11");
    private static readonly TenantId GlobexId = TenantId.Parse("0198f000-0000-7000-8000-00000000617b");

    private static string Require(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException(
            $"{name} is required for Category=Live tests. See the remarks on {nameof(LiveGenieTests)}.");

    private static IGenieConversations Genie()
    {
        var services = new ServiceCollection();

        services.AddSingleton<Azure.Core.TokenCredential>(LiveCredential.Create());
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Genie:WorkspaceUrl"] = Require("DATABRICKS_HOST"),
                // Only Acme is mapped. Globex is deliberately absent, so the refusal below is a
                // real configuration state rather than a contrived one.
                [$"Genie:Spaces:{AcmeId}"] = Require("LAKEWRIGHT_GENIE_SPACE_ID"),
            })
            .Build());

        var configuration = services.BuildServiceProvider().GetRequiredService<IConfiguration>();
        services.AddLakeWrightGenie(configuration);

        return services.BuildServiceProvider().GetRequiredService<IGenieConversations>();
    }

    private static TenantContext Tenant(TenantId id) =>
        TenantContextFactory.ForTenant(id, Require("LAKEWRIGHT_CATALOG"), "analytics");

    [Fact]
    public async Task A_question_round_trips_and_comes_back_with_the_sql_it_ran()
    {
        // Arrange
        var genie = Genie();

        // Act
        var answer = await genie.AskAsync(
            Tenant(AcmeId),
            "How many rows are in the largest table?",
            TestContext.Current.CancellationToken);

        // Assert — the outcome, the prose, and the SQL Genie generated. The SQL is the part that
        // proves the attachment shape this library reads is the shape Databricks sends.
        answer.Outcome.ShouldBe(GenieOutcome.Completed);
        answer.Text.ShouldNotBeNullOrWhiteSpace();
        answer.GeneratedSql.ShouldNotBeNullOrWhiteSpace();
        answer.GeneratedSql!.ShouldContain("SELECT", Case.Insensitive);
        answer.ConversationId.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_follow_up_keeps_the_conversation()
    {
        // Arrange
        var genie = Genie();
        var first = await genie.AskAsync(
            Tenant(AcmeId), "How many rows are in the largest table?", TestContext.Current.CancellationToken);

        // Act
        var second = await genie.ContinueAsync(
            Tenant(AcmeId), first.ConversationId, "And which table was that?", TestContext.Current.CancellationToken);

        // Assert — same conversation, a second message in it.
        second.ConversationId.ShouldBe(first.ConversationId);
        second.MessageId.ShouldNotBe(first.MessageId);
        second.Outcome.ShouldBe(GenieOutcome.Completed);
    }

    [Fact]
    public async Task A_tenant_with_no_agent_is_refused_before_anything_is_asked()
    {
        // Arrange — Globex has no agent configured, which is the state an adopter is in before
        // they provision one. The refusal must not fall back to somebody else's agent.
        var genie = Genie();

        // Act
        var act = async () => await genie.AskAsync(
            Tenant(GlobexId), "How many rows are in the largest table?", TestContext.Current.CancellationToken);

        // Assert
        var error = await act.ShouldThrowAsync<InvalidOperationException>();
        error.Message.ShouldContain(GlobexId.ToString());
    }
}
