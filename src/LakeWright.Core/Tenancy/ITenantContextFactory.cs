namespace LakeWright.Core.Tenancy;

/// <summary>
/// Creates <see cref="TenantContext"/> instances for a resolver that has checked membership.
/// </summary>
/// <remarks>
/// A resolver registered through <c>AddLakeWrightTenancy&lt;TResolver&gt;()</c> receives this factory
/// directly. It is intentionally not registered in the service container, so a controller, page,
/// or background service cannot resolve it and mint a context from a caller-supplied tenant id.
/// </remarks>
public interface ITenantContextFactory
{
    TenantContext ForTenant(TenantId tenantId, string catalog, string schema);

    TenantContext ForTenant(TenantId tenantId, string catalog, string schema, string? scopeVersion);

    TenantContext ForTenant(TenantId tenantId, string catalog);

    TenantContext ForSharedTenant(
        TenantId tenantId,
        string catalog,
        string schema,
        string? scopeVersion = null,
        string tenantParameter = "tenant_id");
}
