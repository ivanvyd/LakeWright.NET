namespace LakeWright.Core.Tenancy;

/// <summary>Where a tenant's data lives and how statement-level isolation is enforced.</summary>
public abstract record TenantLocation(string Catalog, string Schema)
{
    public sealed record SchemaPerTenant(string Catalog, string Schema) : TenantLocation(Catalog, Schema);

    public sealed record SharedSchema(
        string Catalog,
        string Schema,
        string TenantParameter = "tenant_id",
        string? ScopeStrategyName = null)
        : TenantLocation(Catalog, Schema)
    {
        public SharedSchema(string catalog, string schema, string tenantParameter)
            : this(catalog, schema, tenantParameter, null)
        {
        }
    }
}
