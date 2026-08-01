using LakeWright.Core.Tenancy;

namespace LakeWright.Databricks;

/// <summary>
/// Runs tenant-scoped statements against Databricks SQL.
/// </summary>
/// <remarks>
/// <see cref="ExecuteAsync"/> takes a <see cref="TenantScopedStatement"/> and nothing else, so a
/// caller cannot supply SQL, a catalog or a schema of its own.
///
/// <see cref="GetAsync"/> and <see cref="CancelAsync"/> address a statement that is already
/// running, by an identifier Databricks issued. **They do not establish ownership.** Requiring a
/// <see cref="TenantContext"/> keeps the tenant in scope at the call site, but this layer has no
/// record of which tenant a statement id belongs to, so it cannot check one against the other.
///
/// Ownership is enforced above: the operation record stores the tenant and the external statement
/// id together, and the caller looks the statement up by operation for the resolved tenant rather
/// than passing an id from the request. Until that record exists (ADR 0005), any endpoint keyed on
/// a statement id is a cross-tenant read waiting to happen, because a statement id obtained from a
/// log line or a support ticket is otherwise sufficient to poll another tenant's results.
/// </remarks>
public interface IStatementExecutor
{
    Task<StatementOutcome> ExecuteAsync(
        TenantScopedStatement statement,
        CancellationToken cancellationToken);

    /// <summary>
    /// Fetches the current state of a statement returned as <see cref="StatementOutcome.Pending"/>.
    /// </summary>
    /// <param name="tenant">
    /// The tenant the caller has resolved. Not checked against <paramref name="statementId"/>; see
    /// the remarks on this interface for where ownership is actually enforced.
    /// </param>
    /// <param name="statementId">Identifier issued by Databricks.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<StatementOutcome> GetAsync(
        TenantContext tenant,
        string statementId,
        CancellationToken cancellationToken);

    /// <summary>Cancels a running statement. Same ownership caveat as <see cref="GetAsync"/>.</summary>
    Task CancelAsync(
        TenantContext tenant,
        string statementId,
        CancellationToken cancellationToken);
}
