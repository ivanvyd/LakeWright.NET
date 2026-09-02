using LakeWright.Core.Tenancy;
using LakeWright.Multitenancy.Model;
using Microsoft.EntityFrameworkCore;

namespace LakeWright.Multitenancy;

/// <summary>
/// Resolves the tenant by looking up membership in the application database.
/// </summary>
/// <remarks>
/// The tenant identifier arrives from the request. The membership does not. That asymmetry is the
/// control: a caller may name any organization, and only the database decides whether they are in
/// it. See ADR 0002.
/// </remarks>
public sealed class EfTenantContextResolver(
    LakeWrightDbContext db,
    Microsoft.Extensions.Options.IOptions<MultitenancyOptions> options,
    ITenantContextFactory contexts) : ITenantContextResolver
{
    private readonly MultitenancyOptions _options = options.Value;

    public async Task<TenantContext?> ResolveAsync(
        TenantId tenantId,
        string principalId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);

        // Joined rather than fetched separately so that "is a member" and "which schema" cannot
        // disagree, and so a suspended organization cannot be reached by a valid member.
        var resolved = await db.Memberships
            .Where(m => m.OrganizationId == tenantId
                     && m.PrincipalId == principalId
                     && m.Organization!.State == OrganizationState.Active)
            .Select(m => new { m.Organization!.Schema })
            .SingleOrDefaultAsync(cancellationToken);

        if (resolved is null)
        {
            // Deliberately one outcome for "no such organization", "not a member", and "not
            // active". Distinguishing them tells a caller whether an organization they cannot
            // reach exists.
            return null;
        }

        return contexts.ForTenant(tenantId, _options.Catalog, resolved.Schema);
    }

    internal async Task<TenantContext?> ResolveSystemOwnedAsync(
        TenantId tenantId,
        string catalog,
        CancellationToken cancellationToken)
    {
        var schema = await db.Organizations
            .Where(organization => organization.Id == tenantId
                && organization.State == OrganizationState.Active)
            .Select(organization => organization.Schema)
            .SingleOrDefaultAsync(cancellationToken);

        return schema is null ? null : contexts.ForTenant(tenantId, catalog, schema);
    }
}

public sealed class MultitenancyOptions
{
    public const string SectionName = "Multitenancy";

    /// <summary>Unity Catalog catalog holding every tenant schema for this environment.</summary>
    public string Catalog { get; set; } = string.Empty;
}
