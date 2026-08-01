using System.ClientModel.Primitives;
using Azure.Core;

namespace LakeWright.AI;

/// <summary>
/// Sets the bearer token on every request from a <see cref="TokenCredential"/>.
/// </summary>
/// <remarks>
/// The OpenAI client authenticates with an <c>ApiKeyCredential</c>, which is a string it captures
/// once. Handing it a token read at registration time produces a client that works until the token
/// expires and then fails every call for the life of the process — the exact defect this project
/// found and fixed in <c>AddLakeWrightDatabricks</c>, where the Databricks SDK's own
/// <c>TokenCredential</c> overload solved it. There is no equivalent overload here, so the refresh
/// has to live in the pipeline.
///
/// Asking the credential per request is cheap: <see cref="TokenCredential"/> implementations cache
/// and refresh internally, so this is a dictionary lookup until the token nears expiry.
/// </remarks>
internal sealed class TokenCredentialAuthenticationPolicy(TokenCredential credential, string scope)
    : PipelinePolicy
{
    private readonly string[] _scopes = [scope];

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        var token = credential.GetToken(new TokenRequestContext(_scopes), message.CancellationToken);
        Apply(message, token.Token);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        var token = await credential
            .GetTokenAsync(new TokenRequestContext(_scopes), message.CancellationToken)
            .ConfigureAwait(false);

        Apply(message, token.Token);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    }

    private static void Apply(PipelineMessage message, string token) =>
        // Set, not add: the client has already written an Authorization header from the placeholder
        // credential, and adding a second one sends both.
        message.Request?.Headers.Set("Authorization", $"Bearer {token}");
}
