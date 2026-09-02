namespace LakeWright.Core.Tenancy;

/// <summary>
/// The resolved tenant for the current unit of work, and the Unity Catalog location its data
/// lives in.
/// </summary>
/// <remarks>
/// There is no public constructor. An instance comes from an <see cref="ITenantContextResolver"/>
/// that checked membership in a store the caller cannot influence. Registered resolvers receive
/// the factory required to mint one; no other service does. That is what stops a caller from
/// manufacturing a context for a tenant the current principal does not belong to.
///
/// See ADR 0002. Unity Catalog row filters resolve the caller with <c>session_user()</c>, so a
/// shared service principal makes them a no-op. Isolation lives here instead.
/// </remarks>
public sealed class TenantContext
{
    private TenantContext(TenantId tenantId, TenantLocation location, string? scopeVersion)
    {
        TenantId = tenantId;
        Location = location;
        ScopeVersion = scopeVersion;
    }

    public TenantId TenantId { get; }

    public TenantLocation Location { get; }

    /// <summary>Unity Catalog catalog holding this tenant's data.</summary>
    public string Catalog => Location.Catalog;

    /// <summary>Schema within <see cref="Catalog"/>. One schema per tenant, per ADR 0002.</summary>
    public string Schema => Location.Schema;

    /// <summary>
    /// Optional version of the tenant's access scope, used to compose the broker's
    /// <c>external_value</c> when a tenant's scope may change (e.g. a narrowed
    /// tenant set). A null value means no version is in use; the broker sends only
    /// the bare tenant id. See docs/decisions/0017-scope-version.md.
    /// </summary>
    public string? ScopeVersion { get; }

    /// <summary>
    /// Kept internal rather than public so the type system carries the authorisation claim described
    /// above. Registered resolvers reach it through <see cref="ITenantContextFactory"/>.
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

        return new TenantContext(tenantId, new TenantLocation.SchemaPerTenant(catalog, schema), scopeVersion);
    }

    internal static TenantContext CreateShared(
        TenantId tenantId,
        string catalog,
        string schema,
        string? scopeVersion = null,
        string tenantParameter = "tenant_id")
    {
        var context = Create(tenantId, catalog, schema, scopeVersion);
        if (string.IsNullOrWhiteSpace(tenantParameter)
            || tenantParameter.Any(character => !(character == '_' || char.IsLetterOrDigit(character))))
        {
            throw new ArgumentException("tenantParameter must be a plain SQL parameter identifier.", nameof(tenantParameter));
        }

        return new TenantContext(
            context.TenantId,
            new TenantLocation.SharedSchema(context.Catalog, context.Schema, tenantParameter),
            context.ScopeVersion);
    }

    public override string ToString() => $"{Catalog}.{Schema} ({TenantId})";
}
