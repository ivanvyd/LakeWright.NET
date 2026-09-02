using LakeWright.Core.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

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
        IConfiguration configuration,
        Action<IHttpClientBuilder>? configureClient = null)
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

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ILakeWrightFeatureGate, AlwaysOnFeatureGate>();

        // The token caches default to in-memory (ADR 0018). Both implementations are singletons:
        // a per-request cache would lose its entries between calls. A consumer that wants a
        // different backing store (Redis, distributed) registers their own
        // IWorkspaceTokenCache / IEmbedTokenCache before calling this method, and the
        // TryAddSingleton calls below keep their registration.
        services.TryAddSingleton<IWorkspaceTokenCache>(sp => new MemoryWorkspaceTokenCache(sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IEmbedTokenCache>(sp => new MemoryEmbedTokenCache(sp.GetRequiredService<TimeProvider>()));

        var clientBuilder = services.AddHttpClient<IDashboardTokenBroker, DashboardTokenBroker>((provider, client) =>
        {
            var options = provider
                .GetRequiredService<IOptions<DashboardEmbeddingOptions>>()
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
        configureClient?.Invoke(clientBuilder);
        services.TryAddTransient<IWorkspaceTokenProbe>(provider =>
            (IWorkspaceTokenProbe)provider.GetRequiredService<IDashboardTokenBroker>());

        return services;
    }

    /// <summary>
    /// Registers <see cref="IOpsTokenBroker"/> and <see cref="IDashboardCatalog"/>, bound to the
    /// <c>DashboardOps</c> configuration section.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from <see cref="AddLakeWrightDashboardEmbedding"/> on purpose (ADR 0024). A product
    /// that only embeds dashboards does not register the ops side and never carries an ops secret
    /// in its configuration. A product that needs the catalog (or any future ops path) registers
    /// both methods; the two <see cref="HttpClient"/> registrations are independent.
    /// </para>
    /// <para>
    /// Validation on start means a half-filled ops section fails at boot rather than on the first
    /// catalog request. The same trade-off as the embed side (ADR 0009).
    /// </para>
    /// </remarks>
    public static IServiceCollection AddLakeWrightDashboardOps(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<IHttpClientBuilder>? configureClient = null)
    {
        services.AddOptions<DashboardOpsOptions>()
            .Bind(configuration.GetSection("DashboardOps"))
            .Validate(o => !string.IsNullOrWhiteSpace(o.WorkspaceUrl), "DashboardOps:WorkspaceUrl is required.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.ClientId), "DashboardOps:ClientId is required.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.ClientSecret), "DashboardOps:ClientSecret is required.")
            .ValidateOnStart();

        // Either registration can stand alone, while an application-provided clock remains the
        // clock used by both token caches.
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ILakeWrightFeatureGate, AlwaysOnFeatureGate>();
        services.TryAddSingleton<IOpsTokenCache>(sp => new MemoryOpsTokenCache(sp.GetRequiredService<TimeProvider>()));

        var tokenClientBuilder = services.AddHttpClient<IOpsTokenBroker, OpsTokenBroker>((provider, client) =>
        {
            var options = provider
                .GetRequiredService<IOptions<DashboardOpsOptions>>()
                .Value;
            client.BaseAddress = new Uri(options.WorkspaceUrl.TrimEnd('/') + "/");
        });
        configureClient?.Invoke(tokenClientBuilder);

        var catalogClientBuilder = services.AddHttpClient<IDashboardCatalog, DashboardCatalog>((provider, client) =>
        {
            var options = provider
                .GetRequiredService<IOptions<DashboardOpsOptions>>()
                .Value;
            client.BaseAddress = new Uri(options.WorkspaceUrl.TrimEnd('/') + "/");
        });
        configureClient?.Invoke(catalogClientBuilder);

        return services;
    }
}
