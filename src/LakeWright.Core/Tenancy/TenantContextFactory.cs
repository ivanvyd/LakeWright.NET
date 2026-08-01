namespace LakeWright.Core.Tenancy;

/// <summary>
/// Creates <see cref="TenantContext"/> instances for resolver implementations.
/// </summary>
/// <remarks>
/// <see cref="TenantContext"/>'s constructor is internal so that possession of one means the
/// membership check ran. Resolvers live in other assemblies, so they need this seam.
///
/// It is <c>internal</c>, exposed only to the resolver assembly and the isolation tests through
/// <c>InternalsVisibleTo</c>. It was public in the first version, which meant any caller could
/// manufacture a context for any tenant with no membership check and get full query access to that
/// tenant's schema. A security review demonstrated exactly that with a working proof of concept.
/// The comment claiming this was "the thing to look at first in a security review" was there; the
/// access modifier that would have made it true was not.
/// </remarks>
internal static class TenantContextFactory
{
    public static TenantContext ForTenant(TenantId tenantId, string catalog, string schema) =>
        TenantContext.Create(tenantId, catalog, schema);

    /// <summary>
    /// Creates a context using the conventional schema name for the tenant.
    /// </summary>
    public static TenantContext ForTenant(TenantId tenantId, string catalog) =>
        TenantContext.Create(tenantId, catalog, UnityCatalogIdentifier.SchemaForTenant(tenantId));
}
