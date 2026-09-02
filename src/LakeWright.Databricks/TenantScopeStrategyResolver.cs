using LakeWright.Core.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace LakeWright.Databricks;

internal interface ITenantScopeStrategyResolver
{
    ITenantScopeStrategy Resolve(TenantContext tenant);
}

internal sealed class TenantScopeStrategyResolver(IServiceProvider services) : ITenantScopeStrategyResolver
{
    public ITenantScopeStrategy Resolve(TenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (tenant.Location is not TenantLocation.SharedSchema shared)
        {
            throw new TenantScopeMissingException("A tenant scope strategy was requested for a schema-per-tenant context.");
        }

        var name = shared.ScopeStrategyName ?? ProjectedColumnScope.DefaultName;
        return services.GetKeyedService<ITenantScopeStrategy>(name)
            ?? throw new TenantScopeMissingException(
                $"Shared-schema tenant context selected scope strategy '{name}', but it is not registered.");
    }
}

internal sealed class DefaultTenantScopeStrategyResolver : ITenantScopeStrategyResolver
{
    public ITenantScopeStrategy Resolve(TenantContext tenant) => new ProjectedColumnScope();
}
