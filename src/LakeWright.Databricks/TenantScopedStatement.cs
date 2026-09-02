using LakeWright.Core.Tenancy;

namespace LakeWright.Databricks;

/// <summary>
/// A SQL statement bound to exactly one tenant.
/// </summary>
/// <remarks>
/// This is the only thing <see cref="IStatementExecutor"/> accepts, and it cannot be constructed
/// without a <see cref="TenantContext"/>, which in turn cannot be constructed without a
/// membership check. That chain is the isolation guarantee, expressed in the type system rather
/// than in a runtime guard someone can forget to call.
///
/// Catalog and schema come from the context. There is no overload that takes them from the
/// caller, because an endpoint that accepts a schema name from a request is the same
/// vulnerability wearing a different hat.
/// </remarks>
public readonly struct TenantScopedStatement
{
    private TenantScopedStatement(
        TenantContext tenant,
        string sql,
        IReadOnlyList<StatementParameter> parameters)
    {
        Tenant = tenant;
        Sql = sql;
        Parameters = parameters;
    }

    public TenantContext Tenant { get; }

    public string Sql { get; }

    public IReadOnlyList<StatementParameter> Parameters { get; }

    internal IReadOnlyList<StatementParameter> ParametersForExecution()
    {
        if (Tenant.Location is not TenantLocation.SharedSchema shared)
        {
            return Parameters;
        }

        var token = ":" + shared.TenantParameter;
        if (Parameters.Any(parameter => string.Equals(parameter.Name, shared.TenantParameter, StringComparison.OrdinalIgnoreCase)))
        {
            throw new TenantScopeMissingException(
                $"{token} is supplied by the tenant context and cannot be supplied by the caller.");
        }

        return [.. Parameters, StatementParameter.Tenant(shared.TenantParameter, Tenant.TenantId)];
    }

    internal string SqlForExecution()
    {
        if (Tenant.Location is not TenantLocation.SharedSchema shared)
        {
            return Sql;
        }

        var query = Sql.Trim();
        if (!StartsWithQuery(query) || query.EndsWith(';'))
        {
            throw new TenantScopeMissingException(
                "Shared-schema statements must be a single SELECT or WITH query so LakeWright can apply its tenant predicate.");
        }

        // A marker anywhere in caller SQL does not prove it filters rows: `:tenant_id IS NOT
        // NULL` is true for every tenant. Scope the result ourselves instead, so accidental
        // omissions and inert references cannot reach the shared schema unfiltered. The query
        // must expose the configured tenant column; otherwise the warehouse rejects it rather
        // than returning rows that have not been constrained.
        return $"SELECT * FROM ({query}) AS lakewright_tenant_scope " +
            $"WHERE lakewright_tenant_scope.{shared.TenantParameter} = :{shared.TenantParameter}";
    }

    private static bool StartsWithQuery(string sql)
    {
        var firstWhitespace = sql.IndexOfAny([' ', '\t', '\r', '\n']);
        if (firstWhitespace < 0)
        {
            return false;
        }

        var firstKeyword = sql[..firstWhitespace];
        return string.Equals(firstKeyword, "SELECT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(firstKeyword, "WITH", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Builds a statement scoped to <paramref name="tenant"/>.</summary>
    /// <param name="tenant">The resolved tenant. Supplies catalog and schema.</param>
    /// <param name="sql">
    /// A constant SQL query. Values belong in <paramref name="parameters"/>, never in here.
    /// The compiler rejects an interpolated literal, but it cannot reject a string built at
    /// runtime by concatenation or <c>string.Format</c>, which is the residual injection surface.
    /// Treat this parameter as "must be a constant" even though the type system only enforces
    /// "must not be interpolated in place". A shared-schema context accepts one SELECT or WITH
    /// query and wraps its results with its own tenant predicate; the query must project the
    /// configured tenant column.
    /// </param>
    /// <param name="parameters">Values bound by the server, not interpolated.</param>
    public static TenantScopedStatement Create(
        TenantContext tenant,
        string sql,
        params StatementParameter[] parameters)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        return new TenantScopedStatement(tenant, sql, parameters);
    }

    /// <summary>
    /// Never call this. It exists so that passing an interpolated string fails to compile.
    /// </summary>
    /// <param name="tenant">Unused.</param>
    /// <param name="sql">Unused.</param>
    /// <param name="parameters">Unused.</param>
    /// <remarks>
    /// An interpolated string literal binds to the handler overload in preference to the
    /// <see cref="string"/> one, so <c>Create(ctx, $"SELECT * FROM t WHERE id = {id}")</c> is a
    /// compile error rather than an injection. See <see cref="BlockedSqlInterpolation"/> for why
    /// a <see cref="FormattableString"/> overload does not achieve this.
    /// </remarks>
    [Obsolete(
        "Interpolating into SQL is an injection risk. Pass a constant statement and supply " +
        "values as StatementParameter arguments.",
        error: true)]
    public static TenantScopedStatement Create(
        TenantContext tenant,
        BlockedSqlInterpolation sql,
        params StatementParameter[] parameters) =>
        throw new InvalidOperationException("Unreachable: this overload does not compile.");
}
