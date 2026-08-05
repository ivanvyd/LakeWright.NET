using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LakeWright.Embedding;

public static class EmbeddingServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IDashboardTokenBroker"/>, bound to the <c>DashboardEmbedding</c>
    /// configuration section.
    /// </summary>
    /// <remarks>
    /// Opt-in, like the AI module and for the same reason (ADR 0009): a product that never embeds
    /// a dashboard should not carry a client secret in its configuration, and validation on
    /// start means a half-filled section fails at boot rather than on a viewer's first request.
    /// </remarks>
    public static IServiceCollection AddLakeWrightDashboardEmbedding(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Validated by hand rather than by data annotations, which would mean a package reference
        // for three string checks. The messages name the setting, because "options validation
        // failed" at boot tells an operator nothing about which value is missing.
        services.AddOptions<DashboardEmbeddingOptions>()
            .Bind(configuration.GetSection("DashboardEmbedding"))
            .Validate(o => !string.IsNullOrWhiteSpace(o.WorkspaceUrl), "DashboardEmbedding:WorkspaceUrl is required.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.ClientId), "DashboardEmbedding:ClientId is required.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.ClientSecret), "DashboardEmbedding:ClientSecret is required.")
            .ValidateOnStart();

        services.AddSingleton(TimeProvider.System);

        services.AddHttpClient<IDashboardTokenBroker, DashboardTokenBroker>((provider, client) =>
        {
            var options = provider
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<DashboardEmbeddingOptions>>()
                .Value;

            // Trailing slash: without it a relative request path replaces the last segment rather
            // than appending to it, and every call would go to the workspace root.
            client.BaseAddress = new Uri(options.WorkspaceUrl.TrimEnd('/') + "/");

            // Deliberately no RedactLoggedHeaders. Since .NET 9, IHttpClientFactory redacts every
            // header value in its Trace logs unless that method is called — and calling it with a
            // list *un-redacts* everything not named. So the obvious hardening here would widen the
            // exposure it looks like it closes. Query strings are redacted by default too, which is
            // what keeps external_viewer_id and external_value out of the logs.
        });

        return services;
    }
}
