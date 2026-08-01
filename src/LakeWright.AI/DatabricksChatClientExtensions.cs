using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace LakeWright.AI;

/// <summary>
/// Registers Databricks model serving as an <see cref="IChatClient"/>.
/// </summary>
/// <remarks>
/// Databricks serves an OpenAI-compatible API, so this is the stock OpenAI client pointed at a
/// workspace rather than a bespoke integration — chat and tool calling work through it unchanged
/// (spike 03). What the workspace does *not* get right is streaming usage, which
/// <see cref="StreamingUsageRepairPolicy"/> corrects on the way out.
///
/// Deliberately not part of <c>AddLakeWrightDatabricks</c>. A product that queries a warehouse and
/// runs jobs has no reason to take a dependency on an AI client, and this is the module ADR 0008
/// calls optional.
/// </remarks>
public static class DatabricksChatClientExtensions
{
    /// <summary>Databricks resource id, the audience an Entra token must be issued for.</summary>
    private const string DatabricksScope = "2ff814a6-3304-4ab8-85cb-cd0e6f879c1d/.default";

    public static IServiceCollection AddDatabricksChatClient(
        this IServiceCollection services,
        Uri workspaceUrl,
        string modelName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(workspaceUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        services.AddSingleton<IChatClient>(provider =>
        {
            var credential = provider.GetRequiredService<TokenCredential>();

            var options = new OpenAI.OpenAIClientOptions
            {
                Endpoint = new Uri(workspaceUrl, "/serving-endpoints")
            };

            // PerCall, not PerTry: rewriting the body once per attempt is enough, and a retry
            // produces a fresh response to rewrite anyway.
            options.AddPolicy(new StreamingUsageRepairPolicy(), PipelinePosition.PerCall);

            // PerTry, because the point is that a retry after a 401 carries a fresh token.
            options.AddPolicy(
                new TokenCredentialAuthenticationPolicy(credential, DatabricksScope),
                PipelinePosition.PerTry);

            // The placeholder is never sent: the policy above overwrites the header on every
            // request. OpenAIClient requires *some* credential, and reading a real token here
            // would capture it for the life of the process — which is the bug the policy exists
            // to prevent, and one this project already shipped once.
            return new OpenAI.OpenAIClient(new ApiKeyCredential("unused"), options)
                .GetChatClient(modelName)
                .AsIChatClient();
        });

        return services;
    }
}
