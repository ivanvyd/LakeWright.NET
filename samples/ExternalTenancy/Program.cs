using LakeWright.Core.Tenancy;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddLakeWrightTenancy<HeaderTenantResolver>();

await using var provider = services.BuildServiceProvider();
await using var scope = provider.CreateAsyncScope();
var resolver = scope.ServiceProvider.GetRequiredService<ITenantContextResolver>();
var tenant = await resolver.ResolveAsync(
    TenantId.Parse("0198f000-0000-7000-8000-0000000000e1"),
    "demo-user",
    CancellationToken.None);

Console.WriteLine(tenant is null
    ? "The caller is not a member of this tenant."
    : $"Resolved tenant {tenant.TenantId} in {tenant.Catalog}.{tenant.Schema}.");

internal sealed class HeaderTenantResolver(ITenantContextFactory contexts) : ITenantContextResolver
{
    public Task<TenantContext?> ResolveAsync(
        TenantId tenantId,
        string principalId,
        CancellationToken cancellationToken) =>
        Task.FromResult(principalId == "demo-user"
            ? contexts.ForTenant(tenantId, "analytics")
            : null);
}
