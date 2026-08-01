using LakeWright.Core.Tenancy;
using LakeWright.Multitenancy.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace LakeWright.AspNetCore;

/// <summary>Policy names, so a typo in a string literal fails to compile rather than to authorize.</summary>
public static class TenantPolicies
{
    public const string Viewer = "lakewright:viewer";
    public const string Member = "lakewright:member";
    public const string Admin = "lakewright:admin";

    /// <summary>The policy for a role, for callers that hold a <see cref="MembershipRole"/>.</summary>
    public static string For(MembershipRole role) => role switch
    {
        MembershipRole.Viewer => Viewer,
        MembershipRole.Member => Member,
        MembershipRole.Admin => Admin,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unhandled membership role.")
    };
}

/// <summary>Requires at least <see cref="Minimum"/> in the resolved tenant.</summary>
/// <remarks>
/// Roles are ordered, so a requirement is a floor rather than an exact match: an Admin satisfies a
/// Member requirement. Expressing it as equality is how an admin ends up locked out of a member
/// endpoint.
/// </remarks>
public sealed class TenantRoleRequirement(MembershipRole minimum) : IAuthorizationRequirement
{
    public MembershipRole Minimum { get; } = minimum;
}

public sealed class TenantRoleHandler(
    ITenantContextAccessor tenants,
    IMembershipReader memberships,
    IHttpContextAccessor http) : AuthorizationHandler<TenantRoleRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        TenantRoleRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        // No resolved tenant means the middleware already answered 404, or the endpoint is not
        // tenant-scoped and should not carry a tenant policy. Either way, do not succeed.
        if (tenants.Current is not { } tenant) { return; }

        var principalId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                          ?? context.User.FindFirst("sub")?.Value;

        if (string.IsNullOrEmpty(principalId)) { return; }

        var cancellationToken = http.HttpContext?.RequestAborted ?? CancellationToken.None;
        var role = await memberships.FindRoleAsync(tenant.TenantId, principalId, cancellationToken);

        if (role is { } actual && actual >= requirement.Minimum)
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>Reads a principal's role in a tenant.</summary>
/// <remarks>
/// Separate from <see cref="ITenantContextResolver"/> because resolution answers "may this
/// principal reach this tenant at all" and this answers "with what authority". Folding them
/// together would mean every request paid for a role lookup it may not need.
/// </remarks>
public interface IMembershipReader
{
    Task<MembershipRole?> FindRoleAsync(
        TenantId tenantId,
        string principalId,
        CancellationToken cancellationToken);
}
