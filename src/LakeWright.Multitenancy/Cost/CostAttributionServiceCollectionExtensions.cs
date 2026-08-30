using LakeWright.Core.Cost;
using LakeWright.Databricks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LakeWright.Multitenancy.Cost;

public static class CostAttributionServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="OperationCostAttribution"/> as the implementation of
    /// <see cref="ICostAttribution"/>.
    /// </summary>
    /// <remarks>
    /// Does not bind configuration. The proxy SKU and DBU rate are a product decision an adopter
    /// owns, and the binding belongs at the same boundary the rest of the options bindings live
    /// at — <c>AddLakeWrightMultitenancy</c>. This helper only registers the service.
    /// </remarks>
    public static IServiceCollection AddLakeWrightCostAttribution(this IServiceCollection services)
    {
        // Scoped, not singleton: the implementation depends on LakeWrightDbContext, which is
        // registered as scoped. A scoped-from-singleton registration fails at service-provider
        // build time with "Cannot consume scoped service from singleton", which the dashboard
        // isolation test caught in the first run.
        services.AddScoped<ICostAttribution, OperationCostAttribution>();
        return services;
    }

    /// <summary>
    /// Registers <see cref="BillingApiCostAttribution"/> as the implementation of
    /// <see cref="ICostAttribution"/>.
    /// </summary>
    /// <remarks>
    /// Requires the calling workspace to have a metastore-admin grant on
    /// <c>system.billing.usage</c>; without it, every cost call returns
    /// <c>PERMISSION_DENIED</c> and the cost endpoint answers 502. A product wiring this should
    /// run a one-time smoke test against the workspace before flipping the registration.
    ///
    /// Reads <see cref="DatabricksOptions"/> for the warehouse and disposition the billing
    /// query runs against. The same warehouse the application uses for normal queries is the
    /// right default; a separate billing-only warehouse is unnecessary.
    /// </remarks>
    public static IServiceCollection AddLakeWrightBillingCostAttribution(this IServiceCollection services)
    {
        // DatabricksClient is registered by AddLakeWrightDatabricks. The cost reader takes
        // it as a direct dependency because it has to escape the tenant-scoped catalog/
        // schema the IStatementExecutor enforces; that escape is the one place the safety
        // model is intentionally relaxed, and it is documented on the type.
        services.AddScoped<ICostAttribution, BillingApiCostAttribution>();
        return services;
    }
}
