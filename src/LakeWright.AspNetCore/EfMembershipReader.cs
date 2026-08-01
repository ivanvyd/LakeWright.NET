using LakeWright.Core.Tenancy;
using LakeWright.Multitenancy;
using LakeWright.Multitenancy.Model;
using Microsoft.EntityFrameworkCore;

namespace LakeWright.AspNetCore;

/// <summary>Reads a principal's role from the application database.</summary>
/// <remarks>
/// Filters on active organizations for the same reason resolution does: a suspended tenant grants
/// no authority, and a role lookup that ignored state would let a suspended tenant's admin keep
/// passing an admin policy.
/// </remarks>
public sealed class EfMembershipReader(LakeWrightDbContext db) : IMembershipReader
{
    public async Task<MembershipRole?> FindRoleAsync(
        TenantId tenantId,
        string principalId,
        CancellationToken cancellationToken)
    {
        var roles = await db.Memberships
            .Where(m => m.OrganizationId == tenantId
                     && m.PrincipalId == principalId
                     && m.Organization!.State == OrganizationState.Active)
            .Select(m => m.Role)
            .ToListAsync(cancellationToken);

        return roles.Count == 0 ? null : roles[0];
    }
}
