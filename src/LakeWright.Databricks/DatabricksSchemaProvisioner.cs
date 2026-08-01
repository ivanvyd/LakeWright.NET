using LakeWright.Core.Tenancy;
using Microsoft.Azure.Databricks.Client;
using Microsoft.Azure.Databricks.Client.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LakeWright.Databricks;

/// <summary>
/// Creates and drops a tenant's Unity Catalog schema.
/// </summary>
/// <remarks>
/// The only DDL this library issues, and the only statement it sends without a
/// <see cref="TenantContext"/> — see <see cref="ITenantSchemaProvisioner"/> for why that exception
/// exists and why it is a separate interface.
///
/// It does not go through <c>TenantScopedStatement</c>, which cannot express this: that type
/// requires a resolved tenant, and provisioning runs before one exists. So the guard the rest of
/// the library gets for free has to be applied by hand here, and it is the first thing this does.
/// A schema name is an identifier, and Unity Catalog has no bind form for identifiers, so the
/// value is interpolated and <see cref="UnityCatalogIdentifier.Validate"/> is the only thing
/// between a caller and injected DDL.
/// </remarks>
public sealed partial class DatabricksSchemaProvisioner(
    DatabricksClient client,
    IOptions<DatabricksOptions> options,
    ILogger<DatabricksSchemaProvisioner> logger) : ITenantSchemaProvisioner
{
    public Task CreateAsync(string catalog, string schema, CancellationToken cancellationToken) =>
        // IF NOT EXISTS because provisioning is retried after a partial failure, and a create that
        // throws on an existing schema turns a recoverable state into a stuck one.
        ExecuteAsync(catalog, schema, $"CREATE SCHEMA IF NOT EXISTS", cancellationToken);

    public Task DropAsync(string catalog, string schema, CancellationToken cancellationToken) =>
        // CASCADE: the schema holds the tenant's tables and the point of the call is that they go.
        // IF EXISTS so that re-running a half-finished deletion completes rather than fails.
        ExecuteAsync(catalog, schema, $"DROP SCHEMA IF EXISTS", cancellationToken, suffix: " CASCADE");

    private async Task ExecuteAsync(
        string catalog,
        string schema,
        string verb,
        CancellationToken cancellationToken,
        string suffix = "")
    {
        UnityCatalogIdentifier.Validate(catalog, nameof(catalog));
        UnityCatalogIdentifier.Validate(schema, nameof(schema));

        var response = await client.SQL.StatementExecution.Execute(
            new SqlStatement
            {
                WarehouseId = options.Value.WarehouseId,
                Statement = $"{verb} `{catalog}`.`{schema}`{suffix}",
                WaitTimeout = options.Value.WaitTimeout
            },
            cancellationToken);

        var state = response.Status?.State;

        if (state != StatementExecutionState.SUCCEEDED)
        {
            LogFailed(catalog, schema, state?.ToString() ?? "unknown");

            // Thrown rather than returned. Every other Databricks call in this library translates
            // a failure into an outcome the caller decides about, because a failed query is a
            // product event. A schema that did not get created is not: provisioning cannot
            // continue, and the caller has nothing useful to decide.
            throw new InvalidOperationException(
                $"Schema DDL for {catalog}.{schema} ended in {state?.ToString() ?? "an unknown state"}. "
                + response.Status?.Error?.ErrorCode);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Schema DDL for {Catalog}.{Schema} ended in {State}")]
    private partial void LogFailed(string catalog, string schema, string state);
}
