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
    /// <remarks>
    /// Generated here, never supplied by a caller. A client-supplied value would let one tenant
    /// choose another tenant's job token. <see cref="ClientRequestId"/> is the caller-facing key
    /// and the two are kept apart for that reason.
    /// </remarks>
    public required string IdempotencyKey { get; init; }

    /// <summary>
    /// The caller's own <c>Idempotency-Key</c>, if it sent one. Null when it did not.
    /// </summary>
    /// <remarks>
    /// Unique per (tenant, principal), so a client that retries a timed-out POST gets the original
    /// operation back rather than starting a second Databricks run it will also be billed for.
    /// </remarks>
    public string? ClientRequestId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ClaimedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// Why the operation failed, in the platform's own wording.
    /// </summary>
    /// <remarks>
    /// **This is not sanitised.** It carries the Databricks termination message or code verbatim,
    /// because inventing our own wording for a platform failure loses the detail an operator needs.
    ///
    /// <see cref="State"/> is the product-facing half and is a closed set we own. This field is the
    /// diagnostic half. An endpoint that returns operation status to a customer must decide what to
    /// do with it rather than passing it through: an earlier version of this comment claimed the
    /// field was already product-facing, which would have led exactly there.
    /// </remarks>
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
