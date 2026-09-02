namespace LakeWright.Core.Tenancy;

/// <summary>Where a tenant's data lives and how statement-level isolation is enforced.</summary>
public abstract record TenantLocation(string Catalog, string Schema)
{
    public sealed record SchemaPerTenant(string Catalog, string Schema) : TenantLocation(Catalog, Schema);

    public sealed record SharedSchema(string Catalog, string Schema, string TenantParameter = "tenant_id")
        : TenantLocation(Catalog, Schema);
}
