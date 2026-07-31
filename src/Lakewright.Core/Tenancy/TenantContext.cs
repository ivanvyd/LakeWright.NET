namespace Lakewright.Core.Tenancy;

/// <summary>
/// The resolved tenant for the current unit of work, and the Unity Catalog location its data
/// lives in.
/// </summary>
/// <remarks>
/// There is no public constructor. An instance can only come from
/// <see cref="ITenantContextResolver"/>, which resolves membership from the application
/// database. That is what stops a caller from manufacturing a context for a tenant the
/// current principal does not belong to, and it is why the Databricks query layer can treat
/// possession of a <see cref="TenantContext"/> as proof of authorisation.
///
/// See ADR 0002. Unity Catalog row filters resolve the caller with <c>session_user()</c>, so a
/// shared service principal makes them a no-op. Isolation lives here instead.
/// </remarks>
public sealed class TenantContext
{
    private TenantContext(TenantId tenantId, string catalog, string schema)
    {
        TenantId = tenantId;
        Catalog = catalog;
        Schema = schema;
    }

    public TenantId TenantId { get; }

    /// <summary>Unity Catalog catalog holding this tenant's data.</summary>
    public string Catalog { get; }

    /// <summary>Schema within <see cref="Catalog"/>. One schema per tenant, per ADR 0002.</summary>
    public string Schema { get; }

    /// <summary>
    /// Only <see cref="Lakewright.Core"/> and the multitenancy implementation may create a
    /// context. Kept internal rather than public so the type system carries the authorisation
    /// claim described above.
    /// </summary>
    internal static TenantContext Create(TenantId tenantId, string catalog, string schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        // A catalog or schema name that is not a plain identifier would have to be quoted to be
        // used, and anything needing quoting here came from somewhere it should not have.
        UnityCatalogIdentifier.Validate(catalog, nameof(catalog));
        UnityCatalogIdentifier.Validate(schema, nameof(schema));

        return new TenantContext(tenantId, catalog, schema);
    }

    public override string ToString() => $"{Catalog}.{Schema} ({TenantId})";
}
