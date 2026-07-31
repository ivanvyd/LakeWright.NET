using Lakewright.Core.Tenancy;

namespace Lakewright.Multitenancy.Model;

/// <summary>
/// A unit of work that outlives the request that started it.
/// </summary>
/// <remarks>
/// This is the record that makes long-running Databricks work safe, per ADR 0005, and it does two
/// jobs that are easy to conflate.
///
/// It survives a restart: the external identifier is written here as soon as Databricks issues it,
/// so a worker that dies mid-flight leaves a row that reconciliation can match to a run instead of
/// an orphan that gets submitted twice.
///
/// And it establishes ownership. A statement identifier on its own says nothing about who may see
/// its results, so an endpoint keyed on one is a cross-tenant read. Callers look up the operation
/// for the resolved tenant and take the external identifier from the row, never from the request.
/// </remarks>
public sealed class Operation
{
    public required Guid Id { get; init; }

    /// <summary>Owning tenant. Every lookup filters on this.</summary>
    public required TenantId OrganizationId { get; init; }

    public required string PrincipalId { get; init; }

    /// <summary>What this operation is, in product terms rather than platform terms.</summary>
    public required string Kind { get; init; }

    public required OperationState State { get; set; }

    /// <summary>
    /// Databricks statement or run identifier. Null until the platform issues one, which is the
    /// window reconciliation exists to close.
    /// </summary>
    public string? ExternalId { get; set; }

    /// <summary>
    /// Sent to Databricks so a retry cannot start a second run. Capped at 64 characters by the
    /// Jobs API, and it has no documented deduplication window, which is why reconciliation is
    /// required rather than optional.
    /// </summary>
    public required string IdempotencyKey { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ClaimedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Product-facing failure text. Never a raw platform message.</summary>
    public string? Error { get; set; }

    public Organization? Organization { get; init; }
}

/// <summary>
/// Product-facing operation states.
/// </summary>
/// <remarks>
/// A closed set that we own. Databricks documents its run states as extensible, so platform states
/// are mapped into this at the boundary with an explicit unknown arm. Customers never see a
/// platform state.
/// </remarks>
public enum OperationState
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4
}
