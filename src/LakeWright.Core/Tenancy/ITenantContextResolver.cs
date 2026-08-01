namespace LakeWright.Core.Tenancy;

/// <summary>
/// Resolves the tenant for the current principal.
/// </summary>
/// <remarks>
/// Implementations must resolve membership from the application database. A tenant identifier
/// carried in a token claim, a header, a route value or a query string is a request from the
/// caller, not a fact, and treating it as a fact is the whole of the cross-tenant vulnerability
/// this project exists to prevent.
/// </remarks>
public interface ITenantContextResolver
{
    /// <summary>
    /// Returns the context for <paramref name="tenantId"/> if the current principal is a member
    /// of it, otherwise <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Null covers both "no such tenant" and "not a member of it". Callers surface it as 404.
    /// Distinguishing the two would confirm the existence of another tenant's resources.
    /// </remarks>
    Task<TenantContext?> ResolveAsync(
        TenantId tenantId,
        string principalId,
        CancellationToken cancellationToken);
}

/// <summary>Ambient access to the tenant resolved for the current request.</summary>
public interface ITenantContextAccessor
{
    /// <summary>The resolved context, or null outside a tenant-scoped request.</summary>
    TenantContext? Current { get; }
}
