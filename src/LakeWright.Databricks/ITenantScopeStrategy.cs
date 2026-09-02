using LakeWright.Core.Tenancy;

namespace LakeWright.Databricks;

/// <summary>
/// Applies the library-owned predicate that constrains a shared-schema statement to its resolved
/// tenant.
/// </summary>
public interface ITenantScopeStrategy
{
    /// <summary>Wraps <paramref name="sql"/> with the strategy's tenant constraint.</summary>
    string Apply(string sql, TenantContext tenant);

    /// <summary>Returns parameters owned by the resolved tenant context.</summary>
    IReadOnlyList<StatementParameter> Parameters(TenantContext tenant);
}
