namespace LakeWright.Core.Tenancy;

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
    private TenantContext(TenantId tenantId, string catalog, string schema, string? scopeVersion)
    {
        TenantId = tenantId;
        Catalog = catalog;
        Schema = schema;
        ScopeVersion = scopeVersion;
    }

    public TenantId TenantId { get; }

    /// <summary>Unity Catalog catalog holding this tenant's data.</summary>
    public string Catalog { get; }

    /// <summary>Schema within <see cref="Catalog"/>. One schema per tenant, per ADR 0002.</summary>
    public string Schema { get; }

    /// <summary>
    /// Optional version of the tenant's access scope, used to compose the broker's
    /// <c>external_value</c> when a tenant's scope may change (e.g. a narrowed
    /// tenant set). A null value means no version is in use; the broker sends only
    /// the bare tenant id. See ADR 0016.
    /// </summary>
    public string? ScopeVersion { get; }

    /// <summary>
    /// Only <see cref="LakeWright.Core"/> and the multitenancy implementation may create a
    /// context. Kept internal rather than public so the type system carries the authorisation
    /// claim described above.
    /// </summary>
    internal static TenantContext Create(TenantId tenantId, string catalog, string schema, string? scopeVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        // A catalog or schema name that is not a plain identifier would have to be quoted to be
        // used, and anything needing quoting here came from somewhere it should not have.
        UnityCatalogIdentifier.Validate(catalog, nameof(catalog));
        UnityCatalogIdentifier.Validate(schema, nameof(schema));

        // The version is a caller-supplied value that must not contain reserved characters.
        // The broker uses `~` as the delimiter; reserved `|` and `:` and the delimiter itself
        // would split a claim incorrectly. A GUID-derived version (e.g. the md5 of the scope)
        // avoids the reserved characters naturally; an explicit constraint is a safe second line.
        if (scopeVersion is not null && (scopeVersion.Contains('|') || scopeVersion.Contains(':') || scopeVersion.Contains('~')))
        {
            throw new ArgumentException(
                $"scopeVersion must not contain '|', ':', or '~' (found '{scopeVersion}'). Those are reserved characters in the Databricks external_value claim and would corrupt the claim.",
                nameof(scopeVersion));
        }

        return new TenantContext(tenantId, catalog, schema, scopeVersion);
    }

    public override string ToString() => $"{Catalog}.{Schema} ({TenantId})";
}
