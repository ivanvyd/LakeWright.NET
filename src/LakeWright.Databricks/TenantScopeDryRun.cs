using LakeWright.Core.Tenancy;

namespace LakeWright.Databricks;

/// <summary>Performs a credential-free dry run of the default shared-schema tenant wrapper.</summary>
public static class TenantScopeDryRun
{
    /// <summary>
    /// Validates one SQL query and returns the wrapper LakeWright would use for a projected tenant
    /// column. It never connects to a workspace and deliberately uses a parameter placeholder,
    /// never a tenant value.
    /// </summary>
    public static TenantScopeDryRunResult Inspect(string sql, string tenantParameter = "tenant_id")
    {
        try
        {
            UnityCatalogIdentifier.Validate(tenantParameter, nameof(tenantParameter));
            var query = ProjectedColumnScope.RequireSingleQuery(sql);
            return new TenantScopeDryRunResult(
                Passed: true,
                Reason: string.Empty,
                ScopedSql: $"SELECT * FROM ({query}) AS lakewright_tenant_scope " +
                    $"WHERE lakewright_tenant_scope.{tenantParameter} = :{tenantParameter}");
        }
        catch (ArgumentException exception)
        {
            return new TenantScopeDryRunResult(false, exception.Message, null);
        }
        catch (TenantScopeMissingException exception)
        {
            return new TenantScopeDryRunResult(false, exception.Message, null);
        }
    }
}

/// <summary>Outcome of a credential-free projected-column scope check.</summary>
public sealed record TenantScopeDryRunResult(bool Passed, string Reason, string? ScopedSql);
