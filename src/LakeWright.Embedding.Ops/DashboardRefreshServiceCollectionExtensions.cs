using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using LakeWright.Core;
using LakeWright.Core.Features;

namespace LakeWright.Embedding.Ops;

/// <summary>Registers dashboard refresh orchestration authenticated by the existing ops principal.</summary>
public static class DashboardRefreshServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IDashboardRefresher"/>. <c>AddLakeWrightDashboardOps</c> must be called
    /// first because it supplies the separate operations credential.
    /// </summary>
    public static IServiceCollection AddLakeWrightDashboardRefresh(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<IHttpClientBuilder>? configureClient = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DashboardRefreshOptions>()
            .Bind(configuration.GetSection(DashboardRefreshOptions.SectionName))
            .Validate(options => options.Policy.MinimumInterval > TimeSpan.Zero, "LakeWright:DashboardRefresh:Policy:MinimumInterval must be positive.")
            .Validate(options => options.Policy.MaxConcurrentPerTenant >= 1, "LakeWright:DashboardRefresh:Policy:MaxConcurrentPerTenant must be at least one.")
            .Validate(options => options.JobLookupCacheDuration > TimeSpan.Zero, "LakeWright:DashboardRefresh:JobLookupCacheDuration must be positive.");
        LakeWrightOptions.ValidateOnStart<DashboardRefreshOptions>(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ILakeWrightFeatureGate, AlwaysOnFeatureGate>();
        services.TryAddSingleton<IRefreshRunOwnership, MemoryRefreshRunOwnership>();

        var builder = services.AddHttpClient<IJobsApi, DatabricksJobsApi>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<DashboardOpsOptions>>().Value;
            client.BaseAddress = new Uri(options.WorkspaceUrl.TrimEnd('/') + "/");
        });
        configureClient?.Invoke(builder);
        services.AddOptions<DashboardCacheBustOptions>();
        services.AddOptions<DashboardPublishVerifierOptions>()
            .Validate(options => options.CacheDuration > TimeSpan.Zero, "LakeWright:DashboardPublishVerifier:CacheDuration must be positive.");
        LakeWrightOptions.ValidateOnStart<DashboardPublishVerifierOptions>(services);
        services.AddOptions<DashboardMetadataCacheOptions>()
            .Validate(options => options.Duration > TimeSpan.Zero, "LakeWright:DashboardMetadataCache:Duration must be positive.");
        LakeWrightOptions.ValidateOnStart<DashboardMetadataCacheOptions>(services);
        services.AddOptions<WarehouseWarmOptions>()
            .Validate(options => options.MinimumInterval > TimeSpan.Zero, "LakeWright:WarehouseWarm:MinimumInterval must be positive.");
        LakeWrightOptions.ValidateOnStart<WarehouseWarmOptions>(services);
        var dashboardBuilder = services.AddHttpClient<IDashboardEditorApi, DatabricksDashboardEditorApi>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<DashboardOpsOptions>>().Value;
            client.BaseAddress = new Uri(options.WorkspaceUrl.TrimEnd('/') + "/");
        });
        configureClient?.Invoke(dashboardBuilder);
        var verifierBuilder = services.AddHttpClient<IPublishVerificationApi, DatabricksPublishVerificationApi>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<DashboardOpsOptions>>().Value;
            client.BaseAddress = new Uri(options.WorkspaceUrl.TrimEnd('/') + "/");
        });
        configureClient?.Invoke(verifierBuilder);
        var metadataBuilder = services.AddHttpClient<IDashboardMetadataApi, DatabricksDashboardMetadataApi>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<DashboardOpsOptions>>().Value;
            client.BaseAddress = new Uri(options.WorkspaceUrl.TrimEnd('/') + "/");
        });
        configureClient?.Invoke(metadataBuilder);
        var warmerBuilder = services.AddHttpClient<IWarehouseWarmApi, DatabricksWarehouseWarmApi>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<DashboardOpsOptions>>().Value;
            client.BaseAddress = new Uri(options.WorkspaceUrl.TrimEnd('/') + "/");
        });
        configureClient?.Invoke(warmerBuilder);
        services.AddSingleton<IDashboardRefresher, DashboardRefresher>();
        services.AddSingleton<IDashboardCacheBuster, DashboardCacheBuster>();
        services.AddSingleton<IDashboardPublishVerifier, DashboardPublishVerifier>();
        services.TryAddSingleton<IDashboardFilterBindingValidator, DashboardFilterBindingValidator>();
        services.TryAddSingleton<IDashboardMetadataCache>(provider => new MemoryDashboardMetadataCache(provider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IWarehouseWarmLimiter, MemoryWarehouseWarmLimiter>();
        services.AddSingleton<IDashboardMetadataCatalog>(provider => new DashboardMetadataCatalog(
            provider.GetRequiredService<IDashboardMetadataApi>(),
            provider.GetRequiredService<IDashboardCatalog>(),
            provider.GetRequiredService<IDashboardMetadataCache>(),
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DashboardMetadataCacheOptions>>().Value,
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILakeWrightFeatureGate>()));
        services.AddSingleton<IWarehouseWarmer>(provider => new WarehouseWarmer(
            provider.GetRequiredService<IWarehouseWarmApi>(),
            provider.GetRequiredService<IWarehouseWarmLimiter>(),
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<WarehouseWarmOptions>>().Value,
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILakeWrightFeatureGate>()));
        return services;
    }
}
