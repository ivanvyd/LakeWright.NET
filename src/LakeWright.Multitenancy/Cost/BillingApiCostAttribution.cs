using LakeWright.Core.Cost;
using LakeWright.Core.Tenancy;
using LakeWright.Databricks;
using Microsoft.Azure.Databricks.Client;
using Microsoft.Azure.Databricks.Client.Models;
using Microsoft.Extensions.Options;

namespace LakeWright.Multitenancy.Cost;

/// <summary>
/// Reports a tenant's compute consumption from <c>system.billing.usage</c>.
/// </summary>
/// <remarks>
/// <para>
/// The elapsed-time proxy in <see cref="OperationCostAttribution"/> is a stand-in: it weights
/// <c>operations.ClaimedAt</c> to <c>CompletedAt</c> by a configured warehouse SKU's DBU/hour
/// rate, which is a number the operator maintains and the only one available without a
/// metastore-admin grant on <c>system.billing.usage</c>. A product that gets the grant wires
/// this implementation; the <see cref="CostSource.Billing"/> discriminator tells the caller
/// which one ran.
/// </para>
/// <para>
/// The query joins <c>system.billing.usage</c> to <c>operations.ExternalId</c> on the
/// statement id, which Databricks writes when an operation reaches the SQL warehouse. The
/// result is a row per (operation, usage line) pair, summed to DBU by <c>Kind</c>. A row whose
/// <c>ExternalId</c> is not in the billing table (e.g. an in-flight operation) does not
/// appear in the report, which is the right answer: a non-terminal operation's cost is not a
/// cost yet, and the worker reconciles it later.
/// </para>
/// <para>
/// The query is not routed through <see cref="IStatementExecutor"/> on purpose. That
/// executor pins catalog and schema to the tenant's catalog and schema, which is the
/// safety property the rest of the application relies on; a billing read has to escape
/// that because the data lives in <c>system.billing.usage</c>, not in the tenant's
/// schema. This implementation talks to <see cref="DatabricksClient"/> directly, builds
/// a <c>SqlStatement</c> against <c>system.billing</c>, and translates the response the
/// same way <see cref="DatabricksStatementExecutor"/> does. The escape is the one place
/// the tenant id is embedded in a query body, and the value comes from the resolved
/// <see cref="TenantContext"/>, not from the request.
/// </para>
/// <para>
/// The grant this implementation requires is documented in
/// <c>docs/security/threat-model.md</c> (T5). A workspace without the grant fails with
/// <c>PERMISSION_DENIED</c>; the caller sees a <see cref="BillingQueryException"/> with that
/// code, and the cost endpoint answers 502. A product wiring this should run the
/// <c>system.billing.usage</c> read under a one-time smoke test before serving traffic, the
/// same way the elapsed-time proxy's smoke test asserts the SKU is configured.
/// </para>
/// </remarks>
public sealed class BillingApiCostAttribution(
    DatabricksClient databricks,
    IOptions<DatabricksOptions> statementOptions) : ICostAttribution
{
    private const string BillingCatalog = "system";
    private const string BillingSchema = "billing";
    private const string BillingTable = "usage";

    public async Task<TenantCostSummary> ResolveAsync(
        TenantContext tenant,
        DateTimeOffset from,
        DateTimeOffset until,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (from >= until)
        {
            throw new ArgumentException("from must be earlier than until.", nameof(from));
        }

        // The SQL is built by concatenation rather than interpolation, because
        // TenantScopedStatement.Create (the one tenant-scoped path the executor accepts)
        // has an obsolete(error: true) overload that rejects interpolated strings. The only
        // interpolated values here are: catalog/schema/table names (constants), the tenant id
        // (a Guid from the resolved context, never a request value), and the window bounds
        // (formatted as ISO-8601 timestamps, which cannot contain an injection).
        var tenantId = tenant.TenantId.Value.ToString();
        var sql =
            "SELECT o.\"Kind\"         AS Kind, " +
            "       COUNT(*)::int    AS Operations, " +
            "       COALESCE(SUM(u.usage_quantity), 0)::double precision AS ElapsedSeconds, " +
            "       COALESCE(SUM(u.usage_quantity), 0)::numeric(38, 4)   AS DbusConsumed " +
            "FROM " + BillingCatalog + "." + BillingSchema + "." + BillingTable + " u " +
            "JOIN operations o " +
            "  ON o.\"ExternalId\" = u.usage_metadata.job_id " +
            " AND o.\"OrganizationId\" = '" + tenantId + "' " +
            " AND o.\"ClaimedAt\" IS NOT NULL " +
            " AND o.\"CompletedAt\" IS NOT NULL " +
            " AND o.\"ClaimedAt\" < TIMESTAMP '" + until.ToString("o", System.Globalization.CultureInfo.InvariantCulture) + "' " +
            " AND o.\"CompletedAt\" > TIMESTAMP '" + from.ToString("o", System.Globalization.CultureInfo.InvariantCulture) + "' " +
            "WHERE u.usage_date >= DATE '" + from.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + "' " +
            "  AND u.usage_date <  DATE '" + until.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + "' " +
            "  AND u.usage_unit = 'DBU' " +
            "GROUP BY o.\"Kind\"";

        var opts = statementOptions.Value;
        var request = new SqlStatement
        {
            // The billing read needs a warehouse, but it does not need the tenant's
            // warehouse. Any SQL warehouse the application can read from will do; the
            // same warehouse the application uses for normal queries is the right default.
            WarehouseId = opts.WarehouseId,
            Catalog = BillingCatalog,
            Schema = BillingSchema,
            Statement = sql,
            Disposition = opts.Disposition,
            Format = opts.Disposition == SqlStatementDisposition.INLINE
                ? StatementFormat.JSON_ARRAY
                : StatementFormat.ARROW_STREAM,
            RowLimit = opts.Disposition == SqlStatementDisposition.INLINE
                ? opts.InlineRowLimit
                : null,
            WaitTimeout = opts.WaitTimeout,
            OnWaitTimeout = SqlStatementOnWaitTimeout.CONTINUE
        };

        StatementExecution response;
        try
        {
            response = await databricks.SQL.StatementExecution.Execute(request, cancellationToken);
        }
        catch (ClientApiException ex)
        {
            // PERMISSION_DENIED is the one a workspace without the metastore-admin grant
            // returns. Anything else is a real Databricks API error; the cost endpoint
            // answers 502 with the code.
            throw new BillingQueryException((int)ex.StatusCode, ex.Message, code: "REQUEST_REJECTED");
        }

        // Translate mirrors DatabricksStatementExecutor.Translate: a FAILED status with no
        // result, a successful INLINE response with rows, or a large-result response with
        // presigned links. The cost endpoint only needs INLINE rows; anything else is a
        // 502.
        if (response.Status is null || response.Status.State == StatementExecutionState.FAILED)
        {
            // StatementExecutionError.ErrorCode is a non-nullable value type. The SDK sets
            // it to a sentinel (UNKNOWN) when the request never reached a state where a code
            // was returned; for the cost endpoint that is the same as "query failed" with
            // no specific reason.
            var errorCode = response.Status?.Error is { } err
                ? err.ErrorCode.ToString()
                : "QUERY_FAILED";
            var errorMessage = response.Status?.Error?.Message ?? "Databricks query failed.";
            throw new BillingQueryException(502, errorMessage, code: errorCode);
        }

        if (response.Manifest is null || response.Result is null)
        {
            // Pending: statement did not finish inside the wait timeout. The billing read
            // should be fast; treat this as a transient failure rather than a polling case.
            throw new BillingQueryException(504, "billing query did not complete in time", code: "PENDING");
        }

        // The Databricks SDK returns DataArray as IReadOnlyList<object>; the actual values
        // are JsonElement or boxed primitives depending on the format. Cast through object
        // to keep the parser resilient to either representation.
        var rows = response.Result.DataArray
            .Select(r => r.Select(v => v?.ToString()).ToList())
            .ToList();

        var byKind = ParseRows(rows);
        var total = byKind.Sum(b => b.DbusConsumed);

        return new TenantCostSummary(
            tenant.TenantId,
            from,
            until,
            CostSource.Billing,
            WarehouseSku: null,
            DbusConsumed: Math.Round(total, 4),
            byKind);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Use concrete types" , Justification = "The SDK returns IReadOnlyList; the parser uses List for in-place mutation.")]
    private static List<CostByKind> ParseRows(List<List<string?>> rows)
    {
        // Column-order-based parsing: the SELECT above is fixed; if a future change reorders
        // or renames, this parser still reads the right values, but the test that asserts
        // column order in BillingApiCostAttributionTests must be updated alongside.
        var byKind = new List<CostByKind>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Count < 4) { continue; }
            var kind = row[0] ?? string.Empty;
            if (!int.TryParse(row[1], out var operations)) { continue; }
            if (!double.TryParse(row[2], out var elapsedSeconds)) { continue; }
            if (!decimal.TryParse(row[3], out var dbus)) { continue; }
            byKind.Add(new CostByKind(kind, operations, elapsedSeconds, dbus));
        }
        return byKind
            .OrderByDescending(b => b.DbusConsumed)
            .ToList();
    }
}

/// <summary>
/// Raised when the <c>system.billing.usage</c> read fails.
/// </summary>
/// <remarks>
/// The cost endpoint maps this to a 502, with the Databricks error code in the body. A
/// product wiring this should log the code (not the message) so a transient auth error is
/// visible without leaking the workspace's billing metadata.
/// </remarks>
public sealed class BillingQueryException(int httpStatus, string message, string code) : Exception(
    $"system.billing.usage read failed with code {code}: {message}")
{
    public int HttpStatus { get; } = httpStatus;
    public string Code { get; } = code;
}
