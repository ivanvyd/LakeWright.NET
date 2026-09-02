using LakeWright.Databricks;

namespace LakeWright.TenantIsolation.Tests;

[Trait("Category", "TenantIsolation")]
public sealed class TenantScopeDryRunTests
{
    [Fact]
    public void Produces_the_same_parameterized_projected_column_wrapper_without_connecting()
    {
        var result = TenantScopeDryRun.Inspect(" SELECT id, tenant_id FROM events ");

        result.Passed.ShouldBeTrue();
        result.ScopedSql.ShouldBe(
            "SELECT * FROM (SELECT id, tenant_id FROM events) AS lakewright_tenant_scope WHERE lakewright_tenant_scope.tenant_id = :tenant_id");
    }

    [Fact]
    public void Rejects_an_executable_semicolon_before_any_workspace_operation()
    {
        var result = TenantScopeDryRun.Inspect("SELECT id, tenant_id FROM events; SELECT 1");

        result.Passed.ShouldBeFalse();
        result.Reason.ShouldContain("semicolons");
    }
}
