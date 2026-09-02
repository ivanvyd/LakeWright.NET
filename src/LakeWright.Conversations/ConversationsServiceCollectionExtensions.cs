using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using LakeWright.Core;
using LakeWright.Core.Features;

namespace LakeWright.Conversations;

public static class ConversationsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IGenieConversations"/>, bound to the <c>Genie</c> configuration section.
    /// </summary>
    /// <remarks>
    /// Needs a <c>TokenCredential</c> in the container, like <c>AddLakeWrightDatabricks</c>: this
    /// path is secretless per ADR 0006, unlike dashboard embedding, which Databricks gives no
    /// credential other than a service principal secret.
    /// </remarks>
    public static IServiceCollection AddLakeWrightGenie(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Validated by hand rather than by data annotations, which would mean a package reference
        // for one string check. Spaces is deliberately not required: a deployment may configure
        // tenants later, and the refusal to answer an unmapped tenant belongs at the call, where
        // the tenant is known, rather than at boot, where it is not.
        services.AddOptions<GenieOptions>()
            .Bind(configuration.GetSection("Genie"))
            .Validate(o => !string.IsNullOrWhiteSpace(o.WorkspaceUrl), "Genie:WorkspaceUrl is required.");
        LakeWrightOptions.ValidateOnStart<GenieOptions>(services);
        services.AddSingleton<IValidateOptions<GenieOptions>>(provider =>
            new GenieSharedSpaceOptionsValidator(provider.GetService<ILoggerFactory>()));

        services.AddSingleton(TimeProvider.System);
        services.TryAddSingleton<ILakeWrightFeatureGate, AlwaysOnFeatureGate>();
        services.TryAddSingleton<IConversationOwnership, MemoryConversationOwnership>();

        services.AddHttpClient<IGenieConversations, GenieConversations>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<GenieOptions>>().Value;

            // Trailing slash: without it a relative request path replaces the last segment rather
            // than appending to it, and every call would go to the workspace root.
            client.BaseAddress = new Uri(options.WorkspaceUrl.TrimEnd('/') + "/");

            // A single question can legitimately run for minutes. The polling loop enforces the
            // real ceiling; this only stops one HTTP call hanging past it.
            client.Timeout = TimeSpan.FromMinutes(2);

            // Deliberately no RedactLoggedHeaders — see the note in LakeWright.Embedding. Naming
            // headers un-redacts the ones you did not name, so the default is the safe setting.
        });

        return services;
    }
}
