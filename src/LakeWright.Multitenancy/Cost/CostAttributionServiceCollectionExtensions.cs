using LakeWright.Core.Cost;
using Microsoft.Extensions.DependencyInjection;

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
        services.AddSingleton<ICostAttribution, OperationCostAttribution>();
        return services;
    }
}
