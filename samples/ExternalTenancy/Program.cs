using LakeWright.Core.Tenancy;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddLakeWrightTenancy<MembershipResolver>();

await using var provider = services.BuildServiceProvider();
await using var scope = provider.CreateAsyncScope();
var resolver = scope.ServiceProvider.GetRequiredService<ITenantContextResolver>();
var tenant = await resolver.ResolveAsync(
    MembershipResolver.Tenant,
    "demo-user",
    CancellationToken.None);

if (tenant is null) { throw new InvalidOperationException("The demo membership was not resolved."); }

var denied = await resolver.ResolveAsync(
    TenantId.Parse("0198f000-0000-7000-8000-0000000000e2"), "demo-user", CancellationToken.None);
if (denied is not null) { throw new InvalidOperationException("The caller resolved an unrelated tenant."); }

Console.WriteLine($"Resolved tenant {tenant.TenantId} in {tenant.Catalog}.{tenant.Schema}.");
Console.WriteLine("Denied the same principal access to an unrelated tenant.");

internal sealed class MembershipResolver(ITenantContextFactory contexts) : ITenantContextResolver
{
    internal static readonly TenantId Tenant = TenantId.Parse("0198f000-0000-7000-8000-0000000000e1");

    private static readonly Dictionary<(TenantId Tenant, string Principal), (string Catalog, string Schema)> Memberships = new()
    {
        [(Tenant, "demo-user")] = ("analytics", "tenant_demo"),
    };

    public Task<TenantContext?> ResolveAsync(
        TenantId tenantId,
        string principalId,
        CancellationToken cancellationToken) =>
        Task.FromResult(Memberships.TryGetValue((tenantId, principalId), out var membership)
            ? contexts.ForTenant(tenantId, membership.Catalog, membership.Schema)
            : null);
}
