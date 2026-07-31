using Lakewright.Core.Tenancy;

namespace Lakewright.Multitenancy.Model;

/// <summary>
/// An append-only record of something that happened.
/// </summary>
/// <remarks>
/// Every property is init-only and the DbContext maps no update or delete path, so there is no
/// way to amend an audit row through the model. That is the property an auditor tests, and it is
/// cheaper to have from the start than to retrofit once code exists that mutates them.
///
/// Written in the same transaction as the action it records. An audit trail that can be committed
/// separately from the thing it audits will eventually disagree with it.
/// </remarks>
public sealed class AuditEvent
{
    public required Guid Id { get; init; }

    /// <summary>Null for events outside a tenant, such as an organization being created.</summary>
    public TenantId? OrganizationId { get; init; }

    public required string PrincipalId { get; init; }

    /// <summary>Dotted action name, for example <c>organization.provisioned</c>.</summary>
    public required string Action { get; init; }

    public required string ResourceType { get; init; }

    public string? ResourceId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// Structured detail as JSON. Never put credentials, tokens or raw query text here: audit rows
    /// are the most widely read table in the system.
    /// </summary>
    public string? Detail { get; init; }
}
