using Microsoft.Extensions.DependencyInjection;

namespace LakeWright.Core.Tenancy;

/// <summary>Registers a tenant resolver with the only factory that can mint tenant contexts.</summary>
public static class TenancyServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TResolver"/> and passes it a context factory without making
    /// that factory available from the service container.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TResolver"/> has no public constructor that accepts an
    /// <see cref="ITenantContextFactory"/>.
    /// </exception>
    public static IServiceCollection AddLakeWrightTenancy<TResolver>(this IServiceCollection services)
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

        var factory = new ResolverTenantContextFactory();
        services.AddScoped<TResolver>(provider =>
            ActivatorUtilities.CreateInstance<TResolver>(provider, factory));
        services.AddScoped<ITenantContextResolver>(provider => provider.GetRequiredService<TResolver>());

        return services;
    }
}
