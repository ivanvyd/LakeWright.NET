using Microsoft.Azure.Databricks.Client.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LakeWright.Databricks;

/// <summary>
/// Runs statements through <c>Microsoft.Azure.Databricks.Client</c>, translating its two failure
/// modes into <see cref="StatementOutcome"/>.
/// </summary>
public sealed class DatabricksStatementExecutor : IStatementExecutor
{
    private readonly IDatabricksStatementSession _session;
    private readonly DatabricksOptions _options;
    private readonly ITenantScopeStrategyResolver _scopeStrategies;

    public DatabricksStatementExecutor(
        Microsoft.Azure.Databricks.Client.DatabricksClient client,
        IOptions<DatabricksOptions> options,
        ILogger<DatabricksStatementExecutor> logger)
        : this(new DatabricksStatementSession(client, logger), options.Value)
    {
    }

    internal DatabricksStatementExecutor(
        IDatabricksStatementSession session,
        DatabricksOptions options,
        ITenantScopeStrategyResolver? scopeStrategies = null)
    {
        _session = session;
        _options = options;
        _scopeStrategies = scopeStrategies ?? new DefaultTenantScopeStrategyResolver();
    }

    public async Task<StatementOutcome> ExecuteAsync(
        TenantScopedStatement statement,
        CancellationToken cancellationToken)
    {
        // A struct always has an implicit parameterless constructor, so `default` bypasses both
        // Create factories and arrives here with a null Tenant. Without this the failure is a
        // NullReferenceException three lines down, which reads as a bug in the wrong place.
        ArgumentNullException.ThrowIfNull(statement.Tenant);

        var scoped = statement.Tenant.Location is Core.Tenancy.TenantLocation.SharedSchema
            ? statement.ScopedForExecution(_scopeStrategies.Resolve(statement.Tenant))
            : new ScopedStatementForExecution(statement.Sql, statement.Parameters);
        var request = new SqlStatement
        {
            WarehouseId = _options.WarehouseId,

            // Catalog and schema come from the tenant context, never from the caller.
            Catalog = statement.Tenant.Catalog,
            Schema = statement.Tenant.Schema,

            Statement = scoped.Sql,
            Parameters = [.. scoped.Parameters.Select(p => new SqlStatementParameter
            {
                Name = p.Name,
                Value = p.Value,
                Type = p.Type
            })],

            // INLINE returns rows in the response; EXTERNAL_LINKS returns presigned URLs and
            // leaves DataArray null. Getting this pair wrong is how the first version returned
            // zero rows for every successful query, so disposition and format move together.
            Disposition = _options.Disposition,
            Format = _options.Disposition == SqlStatementDisposition.INLINE
                ? StatementFormat.JSON_ARRAY
                : StatementFormat.ARROW_STREAM,

            // INLINE hard-fails at 25 MiB and cancels the statement rather than truncating, so a
            // row limit is what keeps an interactive query from dying instead of degrading.
            RowLimit = _options.Disposition == SqlStatementDisposition.INLINE
                ? _options.InlineRowLimit
                : null,
            WaitTimeout = _options.WaitTimeout,
            OnWaitTimeout = SqlStatementOnWaitTimeout.CONTINUE
        };

        return await _session.ExecuteAsync(request, statement.Tenant.TenantId, cancellationToken);
    }

    public async Task<StatementOutcome> GetAsync(
        Core.Tenancy.TenantContext tenant,
        string statementId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        return await _session.GetAsync(tenant.TenantId, statementId, cancellationToken);
    }

    public Task CancelAsync(
        Core.Tenancy.TenantContext tenant,
        string statementId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return _session.CancelAsync(statementId, cancellationToken);
    }
}
