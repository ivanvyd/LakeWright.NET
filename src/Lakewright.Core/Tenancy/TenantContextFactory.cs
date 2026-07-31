namespace Lakewright.Core.Tenancy;

/// <summary>
/// Creates <see cref="TenantContext"/> instances for resolver implementations.
/// </summary>
/// <remarks>
/// <see cref="TenantContext"/>'s constructor is internal so that possession of one means the
/// membership check ran. Resolvers live in other assemblies, so they need this seam. It is
/// deliberately the only one, and it is the thing to look at first in a security review.
/// </remarks>
public static class TenantContextFactory
{
    public static TenantContext ForTenant(TenantId tenantId, string catalog, string schema) =>
        TenantContext.Create(tenantId, catalog, schema);

    /// <summary>
    /// Creates a context using the conventional schema name for the tenant.
    /// </summary>
    public static TenantContext ForTenant(TenantId tenantId, string catalog) =>
        TenantContext.Create(tenantId, catalog, UnityCatalogIdentifier.SchemaForTenant(tenantId));
}
