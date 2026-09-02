using System.Diagnostics.Metrics;
using System.Reflection;
using LakeWright.Core.Tenancy;
using LakeWright.Databricks;
using Microsoft.Azure.Databricks.Client.Models;

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
        Instrument("WarehouseWait").Name.ShouldBe("lakewright.statements.warehouse_wait");
        Instrument("ExportRows").Name.ShouldBe("lakewright.exports.rows");
        Instrument("ExportBytes").Name.ShouldBe("lakewright.exports.bytes");
    }

    [Fact]
    public async Task A_polled_statement_emits_only_low_cardinality_telemetry_tags()
    {
        var measurements = new List<Measurement>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == LakeWrightDatabricksTelemetry.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
            measurements.Add(new Measurement(instrument.Name, measurement, CopyTags(tags))));
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            measurements.Add(new Measurement(instrument.Name, measurement, CopyTags(tags))));
        listener.Start();

        var session = new SequencedSession(
            new StatementOutcome.Pending("statement-1"),
            new StatementOutcome.Success([], [], 0, "statement-1"));
        var executor = new DatabricksStatementExecutor(
            session,
            new DatabricksOptions { WarehouseId = "warehouse" });
        var statement = TenantScopedStatement.Create(
            TenantContextFactory.ForTenant(TenantId.New(), "analytics"),
            "SELECT 1",
            new StatementOptions
            {
                Kind = "report",
                PollInterval = TimeSpan.FromMilliseconds(1),
                TotalBudget = TimeSpan.FromSeconds(1),
            });

        await executor.ExecuteAsync(statement, TestContext.Current.CancellationToken);
        listener.Dispose();

        measurements.Select(measurement => measurement.Name).ShouldContain("lakewright.statements.duration");
        measurements.Select(measurement => measurement.Name).ShouldContain("lakewright.statements.outcomes");
        measurements.Select(measurement => measurement.Name).ShouldContain("lakewright.statements.warehouse_wait");
        measurements.ShouldAllBe(measurement => measurement.Tags.Keys.All(
            key => !key.Contains("tenant", StringComparison.OrdinalIgnoreCase)));
        // MeterListener observes every concurrent test that records to this process-wide meter.
        // Assert the measurements produced by this statement, rather than incorrectly requiring
        // unrelated tests to use this test's statement kind.
        var reportMeasurements = measurements.Where(HasReportKind).ToArray();
        reportMeasurements.Select(measurement => measurement.Name).ShouldContain("lakewright.statements.duration");
        reportMeasurements.Select(measurement => measurement.Name).ShouldContain("lakewright.statements.outcomes");
        reportMeasurements.Select(measurement => measurement.Name).ShouldContain("lakewright.statements.warehouse_wait");
    }

    private static Instrument Instrument(string fieldName) =>
        (Instrument)typeof(LakeWrightDatabricksTelemetry).GetField(
            fieldName,
            BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static Dictionary<string, object?> CopyTags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new Dictionary<string, object?>(tags.Length, StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            copy.Add(tag.Key, tag.Value);
        }

        return copy;
    }

    private static bool HasReportKind(Measurement measurement) =>
        measurement.Tags.TryGetValue("statement.kind", out var kind) && Equals(kind, "report");

    private sealed record Measurement(string Name, object Value, Dictionary<string, object?> Tags);

    private sealed class SequencedSession(params StatementOutcome[] outcomes) : IDatabricksStatementSession
    {
        private readonly Queue<StatementOutcome> _outcomes = new(outcomes);

        public Task<StatementOutcome> ExecuteAsync(SqlStatement request, TenantId tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(_outcomes.Dequeue());

        public Task<StatementOutcome> GetAsync(TenantId tenantId, string statementId, CancellationToken cancellationToken) =>
            Task.FromResult(_outcomes.Dequeue());

        public Task CancelAsync(string statementId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
