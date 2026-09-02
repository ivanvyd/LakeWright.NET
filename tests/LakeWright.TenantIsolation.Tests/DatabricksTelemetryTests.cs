using System.Diagnostics.Metrics;
using System.Reflection;
using LakeWright.Databricks;

namespace LakeWright.TenantIsolation.Tests;

[Trait("Category", "TenantIsolation")]
public sealed class DatabricksTelemetryTests
{
    [Fact]
    public void Statement_telemetry_has_a_stable_meter_and_no_tenant_instrument()
    {
        LakeWrightDatabricksTelemetry.MeterName.ShouldBe("LakeWright.Databricks");
        LakeWrightDatabricksTelemetry.ActivitySourceName.ShouldBe("LakeWright.Databricks");

        Instrument("StatementDuration").Name.ShouldBe("lakewright.statements.duration");
        Instrument("StatementOutcomes").Name.ShouldBe("lakewright.statements.outcomes");
    }

    private static Instrument Instrument(string fieldName) =>
        (Instrument)typeof(LakeWrightDatabricksTelemetry).GetField(
            fieldName,
            BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
}
