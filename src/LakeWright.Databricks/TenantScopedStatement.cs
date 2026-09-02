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
        IReadOnlyList<StatementParameter> parameters,
        StatementOptions? options)
    {
        Tenant = tenant;
        Sql = sql;
        Parameters = parameters;
        Options = options;
    }

    public TenantContext Tenant { get; }

    public string Sql { get; }

    public IReadOnlyList<StatementParameter> Parameters { get; }

    /// <summary>Optional per-call lifecycle settings, supplied by trusted application code.</summary>
    public StatementOptions? Options { get; }

    /// <summary>Builds a statement scoped to <paramref name="tenant"/>.</summary>
    /// <param name="tenant">The resolved tenant. Supplies catalog and schema.</param>
    /// <param name="sql">
    /// A constant SQL query. Values belong in <paramref name="parameters"/>, never in here.
    /// The compiler rejects an interpolated literal, but it cannot reject a string built at
    /// runtime by concatenation or <c>string.Format</c>, which is the residual injection surface.
    /// Treat this parameter as "must be a constant" even though the type system only enforces
    /// "must not be interpolated in place".
    /// </param>
    /// <param name="parameters">Values bound by the server, not interpolated.</param>
    public static TenantScopedStatement Create(
        TenantContext tenant,
        string sql,
        params StatementParameter[] parameters)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        return new TenantScopedStatement(tenant, sql, parameters, options: null);
    }

    /// <summary>Builds a statement with explicit local polling and result settings.</summary>
    public static TenantScopedStatement Create(
        TenantContext tenant,
        string sql,
        StatementOptions options,
        params StatementParameter[] parameters)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return new TenantScopedStatement(tenant, sql, parameters, options);
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
