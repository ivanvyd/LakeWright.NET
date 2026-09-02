using Azure.Core;
using LakeWright.Core.Jobs;
using LakeWright.Core.Tenancy;
using Microsoft.Azure.Databricks.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LakeWright.Databricks;

/// <summary>Registers tenant-scoped Databricks SQL clients.</summary>
/// <remarks>
/// Separate from ASP.NET Core so a worker or a stock net8 application can use the same guarded
/// executor without acquiring the web and persistence stack. Supply either a <see
/// cref="TokenCredential"/> or a workspace service-principal client ID and secret.
/// </remarks>
public static class DatabricksServiceCollectionExtensions
{
    public static IServiceCollection AddLakeWrightDatabricks(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var hasTokenCredential = services.Any(descriptor => descriptor.ServiceType == typeof(TokenCredential));

        services.AddOptions<DatabricksOptions>()
            .Bind(configuration.GetSection(DatabricksOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options =>
                {
                    var hasClientId = !string.IsNullOrWhiteSpace(options.ClientId);
                    var hasClientSecret = !string.IsNullOrWhiteSpace(options.ClientSecret);
                    return hasClientId == hasClientSecret &&
                        (hasTokenCredential || hasClientId) &&
                        !(hasTokenCredential && hasClientId);
                },
                "Configure either a TokenCredential or a client ID and secret.")
            .ValidateOnStart();

        TryAddSingletonTimeProvider(services);
        services.AddHttpClient("LakeWright.Databricks.Credentials", (provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<DatabricksOptions>>().Value;
            client.BaseAddress = new Uri(options.WorkspaceUrl.TrimEnd('/') + "/", UriKind.Absolute);
        });
        services.AddSingleton<ServicePrincipalDatabricksCredential>(provider =>
            new ServicePrincipalDatabricksCredential(
                provider.GetRequiredService<IHttpClientFactory>().CreateClient("LakeWright.Databricks.Credentials"),
                provider.GetRequiredService<IOptions<DatabricksOptions>>(),
                provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IDatabricksCredential>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<DatabricksOptions>>().Value;
            return !string.IsNullOrWhiteSpace(options.ClientId)
                ? provider.GetRequiredService<ServicePrincipalDatabricksCredential>()
                : new TokenCredentialDatabricksCredential(provider.GetRequiredService<TokenCredential>());
        });

        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<DatabricksOptions>>().Value;
            var credential = new DatabricksTokenCredential(
                provider.GetRequiredService<IDatabricksCredential>(),
                provider.GetRequiredService<TimeProvider>());
            return DatabricksClient.CreateClient(options.WorkspaceUrl, credential);
        });

        services.AddScoped<ITenantSchemaProvisioner, DatabricksSchemaProvisioner>();
        services.AddScoped<IStatementExecutor, DatabricksStatementExecutor>();
        services.AddHttpClient<ITenantScopedExport, DatabricksTenantScopedExport>();
        services.AddScoped<IJobSubmitter, DatabricksJobSubmitter>();

        return services;
    }

    private static void TryAddSingletonTimeProvider(IServiceCollection services)
    {
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }
}
