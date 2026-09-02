using Microsoft.Extensions.DependencyInjection;

namespace LakeWright.Core.Tenancy;

/// <summary>Registers a tenant resolver with the only factory that can mint tenant contexts.</summary>
public static class TenancyServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TResolver"/> and passes it a context factory without making
    /// that factory available from the service container.
    /// </summary>
    /// <param name="services">The composition-root service collection.</param>
    /// <param name="lifetime">The resolver lifetime. Scoped is the safe default for request membership checks.</param>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TResolver"/> has no public constructor that accepts an
    /// <see cref="ITenantContextFactory"/>.
    /// </exception>
    public static IServiceCollection AddLakeWrightTenancy<TResolver>(
        this IServiceCollection services,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TResolver : class, ITenantContextResolver
    {
        ArgumentNullException.ThrowIfNull(services);

        var acceptsFactory = typeof(TResolver).GetConstructors()
            .Any(constructor => constructor.GetParameters()
                .Any(parameter => parameter.ParameterType == typeof(ITenantContextFactory)));
        if (!acceptsFactory)
        {
            throw new InvalidOperationException(
                $"{typeof(TResolver).Name} needs a public constructor that takes an {nameof(ITenantContextFactory)}.");
        }

        if (!Enum.IsDefined(lifetime))
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        var factory = new ResolverTenantContextFactory();
        // Do not register the concrete resolver. It holds the minting factory; exposing it would
        // let unrelated application code bypass the resolver's membership decision.
        services.Add(new ServiceDescriptor(
            typeof(ITenantContextResolver),
            provider => ActivatorUtilities.CreateInstance<TResolver>(provider, factory),
            lifetime));

        return services;
    }
}
