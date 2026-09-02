using LakeWright.Core.Sql;
using LakeWright.Core.Tenancy;

namespace LakeWright.Databricks;

/// <summary>Scopes a shared-schema query through a tenant column projected by the query.</summary>
public sealed class ProjectedColumnScope : ITenantScopeStrategy
{
    /// <summary>The name used when a shared tenant context selects no explicit strategy.</summary>
    public const string DefaultName = "projected-column";

    public string Apply(string sql, TenantContext tenant)
    {
        var shared = RequireSharedSchema(tenant);
        var query = RequireSingleQuery(sql);
        return $"SELECT * FROM ({query}) AS lakewright_tenant_scope " +
            $"WHERE lakewright_tenant_scope.{shared.TenantParameter} = :{shared.TenantParameter}";
    }

    public IReadOnlyList<StatementParameter> Parameters(TenantContext tenant)
    {
        var shared = RequireSharedSchema(tenant);
        return [StatementParameter.Tenant(shared.TenantParameter, tenant.TenantId)];
    }

    internal static TenantLocation.SharedSchema RequireSharedSchema(TenantContext tenant) =>
        tenant.Location as TenantLocation.SharedSchema
        ?? throw new TenantScopeMissingException("A tenant scope strategy requires a shared-schema tenant context.");

    internal static string RequireSingleQuery(string sql)
    {
        var query = sql.Trim();
        if (!StartsWithQuery(query))
        {
            throw new TenantScopeMissingException(
                "Shared-schema statements must be a single SELECT or WITH query so LakeWright can apply its tenant predicate.");
        }

        if (SqlTokenScanner.ContainsExecutableCharacter(query, ';'))
        {
            throw new TenantScopeMissingException(
                "Shared-schema statements must be a single SELECT or WITH query with one executable statement; semicolons are not allowed outside literals, comments, or backtick identifiers.");
        }

        return query;
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
}
