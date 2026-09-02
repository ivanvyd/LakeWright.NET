namespace LakeWright.Core.Tenancy;

/// <summary>Where a tenant's data lives.</summary>
public abstract record TenantLocation(string Catalog, string Schema)
{
    public sealed record SchemaPerTenant(string Catalog, string Schema) : TenantLocation(Catalog, Schema);
}
