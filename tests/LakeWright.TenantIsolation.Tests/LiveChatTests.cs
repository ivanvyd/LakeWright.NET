using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.Core;
using LakeWright.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// Streaming chat against a real Databricks serving endpoint, with and without the shim.
/// </summary>
/// <remarks>
/// The shim's unit tests drive captured chunks, which proves the repair is right about the payload
/// but not that the payload is still what Databricks sends. This is the half that would notice the
/// day the platform fixes it — at which point the shim becomes a no-op and can go.
///
/// Run with:
///   az login
///   DATABRICKS_HOST=https://... LAKEWRIGHT_CHAT_MODEL=databricks-claude-sonnet-5 \
///   dotnet test --filter Category=Live
/// </remarks>
[Trait("Category", "Live")]
public class LiveChatTests
{
    private static string Require(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"{name} is required for Category=Live tests.");

    private static string Model =>
        Environment.GetEnvironmentVariable("LAKEWRIGHT_CHAT_MODEL") ?? "databricks-claude-sonnet-5";

    private static OpenAI.OpenAIClient RawClient(bool withShim)
    {
        var options = new OpenAI.OpenAIClientOptions
        {
            Endpoint = new Uri(new Uri(Require("DATABRICKS_HOST")), "/serving-endpoints")
        };

        if (withShim)
        {
            options.AddPolicy(new StreamingUsageRepairPolicy(), PipelinePosition.PerCall);
        }

        options.AddPolicy(
            new TokenCredentialAuthenticationPolicy(LiveCredential.Create(), "2ff814a6-3304-4ab8-85cb-cd0e6f879c1d/.default"),
            PipelinePosition.PerTry);

        return new OpenAI.OpenAIClient(new ApiKeyCredential("unused"), options);
    }

    [Fact]
    public async Task Streaming_round_trips_through_the_shim()
    {
        // Arrange — the M4 acceptance criterion, and the thing spike 03 could not do.
        var ct = TestContext.Current.CancellationToken;
        var chat = RawClient(withShim: true).GetChatClient(Model).AsIChatClient();

        // Act
        var text = new System.Text.StringBuilder();
        await foreach (var update in chat.GetStreamingResponseAsync("Reply with exactly: ok", cancellationToken: ct))
        {
            text.Append(update.Text);
        }

        // Assert
        text.ToString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Without_the_shim_the_same_call_still_fails()
    {
        // Arrange — the other half of the pair. If this ever starts passing, Databricks has fixed
        // the payload and the shim should be deleted rather than carried forever.
        var ct = TestContext.Current.CancellationToken;
        var chat = RawClient(withShim: false).GetChatClient(Model).AsIChatClient();

        // Act
        var read = async () =>
        {
            await foreach (var update in chat.GetStreamingResponseAsync("Reply with exactly: ok", cancellationToken: ct))
            {
                _ = update.Text;
            }
        };

        // Assert — the deserialiser types completion_tokens as a number and Databricks sends null.
        await read.ShouldThrowAsync<Exception>();
    }

    [Fact]
    public async Task The_registration_produces_a_working_client()
    {
        // Arrange — AddDatabricksChatClient is what an adopter calls, so it is what gets tested,
        // rather than a client this test assembled itself.
        var ct = TestContext.Current.CancellationToken;
        var services = new ServiceCollection();
        services.AddSingleton<TokenCredential>(LiveCredential.Create());
        services.AddDatabricksChatClient(new Uri(Require("DATABRICKS_HOST")), Model);

        await using var provider = services.BuildServiceProvider();

        // Act
        var response = await provider.GetRequiredService<IChatClient>()
            .GetResponseAsync("Reply with exactly: ok", cancellationToken: ct);

        // Assert
        response.Text.ShouldNotBeNullOrWhiteSpace();
    }
}
