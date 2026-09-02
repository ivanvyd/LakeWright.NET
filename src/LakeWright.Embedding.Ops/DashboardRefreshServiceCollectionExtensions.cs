using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
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
            .Validate(options => options.JobLookupCacheDuration > TimeSpan.Zero, "LakeWright:DashboardRefresh:JobLookupCacheDuration must be positive.")
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ILakeWrightFeatureGate, AlwaysOnFeatureGate>();
        services.TryAddSingleton<IRefreshRunOwnership, MemoryRefreshRunOwnership>();

        var builder = services.AddHttpClient<IJobsApi, DatabricksJobsApi>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<DashboardOpsOptions>>().Value;
            client.BaseAddress = new Uri(options.WorkspaceUrl.TrimEnd('/') + "/");
        });
        configureClient?.Invoke(builder);
        services.AddSingleton<IDashboardRefresher, DashboardRefresher>();
        return services;
    }
}
