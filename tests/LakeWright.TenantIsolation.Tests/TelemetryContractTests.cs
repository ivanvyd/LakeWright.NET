using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using LakeWright.Multitenancy;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// The telemetry contract that the sample's OTel wiring depends on.
/// </summary>
/// <remarks>
/// <see cref="LakeWrightTelemetry.MeterName"/> and <see cref="LakeWrightTelemetry.ActivitySourceName"/>
/// are read by the sample's opt-in OTel subscription, which hard-codes the string
/// "LakeWright.Multitenancy" because the constant is <c>internal</c>. A maintainer who renames
/// either constant without updating the sample would break the wiring silently; a maintainer
/// who updates both would still want a test asserting the round-trip. This file pins the
/// constants and asserts that the four documented instruments are still registered on the meter.
/// </remarks>
[Trait("Category", "TenantIsolation")]
public class TelemetryContractTests
{
    [Fact]
    public void MeterName_is_the_value_the_sample_subscribes_to()
    {
        LakeWrightTelemetry.MeterName.ShouldBe("LakeWright.Multitenancy");
    }

    [Fact]
    public void ActivitySourceName_is_the_value_the_sample_subscribes_to()
    {
        LakeWrightTelemetry.ActivitySourceName.ShouldBe("LakeWright.Multitenancy");
    }

    [Fact]
    public void The_four_documented_instruments_are_registered_on_the_meter()
    {
        // Touching LakeWrightTelemetry triggers the static field initializers, which register
        // the four instruments. After that, an SDK MeterListener subscribed to the documented
        // meter name will observe them. The reflection here just asserts the four counters
        // and the histogram are still present and non-null.
        AssertInstrumentExists<Counter<long>>("OperationsStarted", "lakewright.operations.started", "{operation}");
        AssertInstrumentExists<Counter<long>>("OperationsCompleted", "lakewright.operations.completed", "{operation}");
        AssertInstrumentExists<Histogram<double>>("QueueWait", "lakewright.operations.queue_wait", "s");
        AssertInstrumentExists<Counter<long>>("TenantAccessDenied", "lakewright.tenant.access_denied", "{request}");
    }

    [Fact]
    public void A_meter_listener_subscribed_to_LakeWright_Multitenancy_observes_the_instruments()
    {
        // The actual round-trip the sample's OTel wiring depends on: an SDK MeterListener
        // subscribed to "LakeWright.Multitenancy" sees the library's instruments. This is
        // the same shape OpenTelemetry's AddMeter uses, just on the lower-level API.
        using var listener = new MeterListener();
        var seen = new List<string>();
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Meter.Name == LakeWrightTelemetry.MeterName)
            {
                seen.Add(instrument.Name);
            }
        };
        listener.RecordObservableInstruments();
        listener.Start();

        // Force the static field initializers to run by reading one of them.
        _ = LakewrightTelemetryProbe.OperationsStarted;

        // After listener.Start() and the first call into the static fields, the four
        // instruments should be visible to the listener. The listener does not see
        // pre-existing instruments registered before Start() by default; RecordObservableInstruments
        // before Start() ensures the listener captures newly-published instruments.
        seen.ShouldContain("lakewright.operations.started",
            "the listener must observe the library's instruments on the documented meter name");
    }

    private static void AssertInstrumentExists<T>(string fieldName, string expectedInstrumentName, string expectedUnit)
        where T : Instrument
    {
        var field = typeof(LakeWrightTelemetry).GetField(
            fieldName,
            BindingFlags.NonPublic | BindingFlags.Static);
        field.ShouldNotBeNull($"LakeWrightTelemetry.{fieldName} must exist for the contract to hold.");
        var instrument = field!.GetValue(null) as T;
        instrument.ShouldNotBeNull($"LakeWrightTelemetry.{fieldName} must be a {typeof(T).Name}.");
        instrument!.Name.ShouldBe(expectedInstrumentName);
        instrument.Unit.ShouldBe(expectedUnit);
    }
}

/// <summary>
/// A reflection probe that names the private static fields of <see cref="LakeWrightTelemetry"/>
/// so the test asserts against documented names rather than a copy of the strings.
/// </summary>
internal static class LakewrightTelemetryProbe
{
    public static Counter<long> OperationsStarted =>
        (Counter<long>)typeof(LakeWrightTelemetry).GetField(
            "OperationsStarted",
            BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
}
