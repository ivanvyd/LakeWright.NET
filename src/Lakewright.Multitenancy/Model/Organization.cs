using Lakewright.Core.Tenancy;

namespace Lakewright.Multitenancy.Model;

/// <summary>A tenant. One organization, one Unity Catalog schema.</summary>
public sealed class Organization
{
    public required TenantId Id { get; init; }

    public required string Name { get; set; }

    /// <summary>
    /// Slug used in URLs. Not the tenant key: a slug can be changed by its owner, and anything
    /// user-changeable that also selects data is a rename away from a cross-tenant read.
    /// </summary>
    public required string Slug { get; set; }

    /// <summary>
    /// The Unity Catalog schema holding this organization's data. Stored rather than derived so
    /// that changing the derivation rule later cannot silently repoint existing tenants.
    /// </summary>
    public required string Schema { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public OrganizationState State { get; set; } = OrganizationState.Provisioning;

    public ICollection<Membership> Memberships { get; init; } = [];
}

public enum OrganizationState
{
    /// <summary>Row exists, Databricks schema does not yet. No query may run against it.</summary>
    Provisioning = 0,
    Active = 1,
    Suspended = 2,
    /// <summary>Scheduled for deletion. Reads are refused; the schema drop has not necessarily run.</summary>
    PendingDeletion = 3
}

/// <summary>Links a principal to an organization. The only source of truth for tenant access.</summary>
public sealed class Membership
{
    public required Guid Id { get; init; }

    public required TenantId OrganizationId { get; init; }

    /// <summary>
    /// Stable identifier from the identity provider, typically the OIDC <c>sub</c>. Not the email
    /// address, which is reassignable.
    /// </summary>
    public required string PrincipalId { get; init; }

    public required MembershipRole Role { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }

    public Organization? Organization { get; init; }
}

public enum MembershipRole
{
    Viewer = 0,
    Member = 1,
    Admin = 2
}
