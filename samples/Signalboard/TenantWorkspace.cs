using System.Security.Claims;
using LakeWright.Core.Tenancy;
using LakeWright.Multitenancy;
using LakeWright.Multitenancy.Model;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Signalboard;

/// <summary>
/// What the dashboard is allowed to see and do, for whoever is signed in.
/// </summary>
/// <remarks>
/// The dashboard runs on the server, so it could reach <see cref="LakeWrightDbContext"/> directly
/// and query whatever it liked. It goes through <see cref="ITenantContextResolver"/> and
/// <see cref="OperationStore"/> instead, which is the whole point: the page gets a
/// <see cref="TenantContext"/> only because the resolver confirmed membership, and the store
/// filters on it. A Blazor page that queried the tables itself would be a second, unguarded way
/// into tenant data sitting beside the guarded one.
///
/// Role is checked here as well as in the API. The dashboard does not go through
/// <c>MapLakeWrightOperations</c>, so the endpoint's authorization policy never runs for it, and a
/// UI that only hides the button is not an authorization control.
/// </remarks>
public sealed class TenantWorkspace(
    AuthenticationStateProvider authentication,
    LakeWrightDbContext db,
    ITenantContextResolver resolver,
    OperationStore operations)
{
    /// <summary>Who the visitor is, where they belong, and what they may do there.</summary>
    public sealed record Scope(TenantContext Tenant, string Organization, string PrincipalId, MembershipRole Role)
    {
        /// <summary>Roles are a floor, so Member and Admin both qualify.</summary>
        public bool CanStart => Role >= MembershipRole.Member;

        /// <summary>Where the API serves this operation, which is not where the dashboard lives.</summary>
        public string AddressOf(Guid operationId) =>
            $"/organizations/{Tenant.TenantId.Value}/operations/{operationId}";
    }

    /// <summary>Thrown where the API would answer 403.</summary>
    public sealed class RoleDeniedException(string message) : Exception(message);

    public async Task<Scope?> CurrentAsync(CancellationToken cancellationToken)
    {
        var state = await authentication.GetAuthenticationStateAsync();
        var principalId = state.User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrEmpty(principalId)) { return null; }

        // One organization per person in this sample. A real product lets you switch, and the
        // switch is a tenant resolution like any other rather than a client-side selection.
        var membership = await db.Memberships
            .Where(m => m.PrincipalId == principalId
                     && m.Organization!.State == OrganizationState.Active)
            .Select(m => new { m.OrganizationId, m.Role, Name = m.Organization!.Name })
            .FirstOrDefaultAsync(cancellationToken);

        if (membership is null) { return null; }

        var tenant = await resolver.ResolveAsync(membership.OrganizationId, principalId, cancellationToken);

        return tenant is null
            ? null
            : new Scope(tenant, membership.Name, principalId, membership.Role);
    }

    public async Task<IReadOnlyList<OperationRow>> RecentAsync(Scope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return await db.Operations
            .Where(o => o.OrganizationId == scope.Tenant.TenantId)
            .OrderByDescending(o => o.CreatedAt)
            .Take(25)
            .Select(o => new OperationRow(o.Id, o.Kind, o.State, o.PrincipalId, o.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task StartAsync(Scope scope, string kind, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (!scope.CanStart)
        {
            throw new RoleDeniedException(
                $"A {scope.Role} cannot start work. This is the same refusal the API answers with 403.");
        }

        await operations.CreateAsync(
            scope.Tenant, scope.PrincipalId, kind, clientRequestId: null, cancellationToken);
    }
}

/// <summary>One row of the dashboard.</summary>
/// <remarks>
/// Carries the principal so the table can say who started what, which the customer-facing
/// <c>OperationResponse</c> deliberately does not. Both stop short of the Databricks run id: an
/// interface that shows it invites an endpoint keyed on it.
/// </remarks>
public sealed record OperationRow(
    Guid Id,
    string Kind,
    OperationState State,
    string PrincipalId,
    DateTimeOffset CreatedAt);
