namespace LakeWright.Core.Tenancy;

/// <summary>
/// Creates <see cref="TenantContext"/> instances for trusted in-repository implementations.
/// </summary>
/// <remarks>
/// <see cref="TenantContext"/>'s constructor is internal so that possession of one means the
/// membership check ran. External resolvers receive <see cref="ITenantContextFactory"/> through
/// <see cref="TenancyServiceCollectionExtensions.AddLakeWrightTenancy{TResolver}"/> instead.
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
    /// Creates a context that carries an <c>external_value</c> scope version, so a tenant whose
    /// access scope has narrowed or widened gets a different cache key in Databricks and stops
    /// seeing the old (now-stale) rows. See ADR 0017.
    /// </summary>
    public static TenantContext ForTenant(
        TenantId tenantId, string catalog, string schema, string? scopeVersion) =>
        TenantContext.Create(tenantId, catalog, schema, scopeVersion);

    /// <summary>
    /// Creates a context using the conventional schema name for the tenant.
    /// </summary>
    public static TenantContext ForTenant(TenantId tenantId, string catalog) =>
        TenantContext.Create(tenantId, catalog, UnityCatalogIdentifier.SchemaForTenant(tenantId));
}

internal sealed class ResolverTenantContextFactory : ITenantContextFactory
{
    public TenantContext ForTenant(TenantId tenantId, string catalog, string schema) =>
        TenantContext.Create(tenantId, catalog, schema);

    public TenantContext ForTenant(TenantId tenantId, string catalog, string schema, string? scopeVersion) =>
        TenantContext.Create(tenantId, catalog, schema, scopeVersion);

    public TenantContext ForTenant(TenantId tenantId, string catalog) =>
        TenantContext.Create(tenantId, catalog, UnityCatalogIdentifier.SchemaForTenant(tenantId));
}
