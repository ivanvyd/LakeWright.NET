namespace Lakewright.Databricks;

/// <summary>
/// Runs tenant-scoped statements against Databricks SQL.
/// </summary>
/// <remarks>
/// Every method takes a <see cref="TenantScopedStatement"/>. There is deliberately no overload
/// accepting raw SQL, a catalog, or a schema: adding one would reintroduce the failure mode the
/// type exists to prevent.
/// </remarks>
public interface IStatementExecutor
{
    Task<StatementOutcome> ExecuteAsync(
        TenantScopedStatement statement,
        CancellationToken cancellationToken);

    /// <summary>Fetches the current state of a statement returned as <see cref="StatementOutcome.Pending"/>.</summary>
    Task<StatementOutcome> GetAsync(string statementId, CancellationToken cancellationToken);

    Task CancelAsync(string statementId, CancellationToken cancellationToken);
}
