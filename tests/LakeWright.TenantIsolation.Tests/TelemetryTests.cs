using LakeWright.Core.Tenancy;
using LakeWright.Multitenancy;
using LakeWright.Multitenancy.Model;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Time.Testing;
using static LakeWright.TenantIsolation.Tests.TestApi;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// The instruments an operator watches must actually record.
/// </summary>
/// <remarks>
/// Instrumentation is the easiest thing in a codebase to ship broken: nothing fails, a dashboard
/// just stays at zero, and by the time anyone notices the incident it was meant to explain is over.
/// These assert the values rather than the wiring.
/// </remarks>
[Collection(nameof(PostgresTests))]
public class TelemetryTests(PostgresFixture postgres)
{
    private static TenantContext Ctx() => TenantContextFactory.ForTenant(AcmeId, "analytics");

    [Fact]
    public async Task Starting_an_operation_is_counted_once_per_operation()
    {
        // Arrange — a replayed idempotency key starts nothing, so counting it would inflate the
        // number an operator uses to size the worker pool.
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SeededAsync();
        var store = new OperationStore(db, new AuditLog(db, TimeProvider.System), TimeProvider.System);
        using var collector = new MetricCollector<long>(
            null, LakeWrightTelemetry.MeterName, "lakewright.operations.started");

        // Act
        await store.CreateAsync(Ctx(), Alice, "analysis", "same", ct);
        await store.CreateAsync(Ctx(), Alice, "analysis", "same", ct);

        // Assert
        collector.GetMeasurementSnapshot().Sum(m => m.Value).ShouldBe(1);
        collector.LastMeasurement.ShouldNotBeNull()
            .Tags["kind"].ShouldBe("analysis");
    }

    [Fact]
    public async Task Completing_an_operation_records_which_state_it_reached()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SeededAsync();
        var store = new OperationStore(db, new AuditLog(db, TimeProvider.System), TimeProvider.System);
        var operation = await store.CreateAsync(Ctx(), Alice, "analysis", null, ct);
        using var collector = new MetricCollector<long>(
            null, LakeWrightTelemetry.MeterName, "lakewright.operations.completed");

        // Act
        await store.CompleteAsync(AcmeId, operation.Id, OperationState.Failed, "nope", ct);

        // Assert — a failure counted as a completion with no state tag hides the failure rate.
        collector.LastMeasurement.ShouldNotBeNull()
            .Tags["state"].ShouldBe(nameof(OperationState.Failed));
    }

    [Fact]
    public async Task Claiming_records_how_long_the_operation_waited()
    {
        // Arrange — the measurement that shows whether the claim loop is fair. A fake clock, so the
        // wait is a number this test chose rather than however long Postgres took.
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SeededAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-01T09:00:00Z", null));
        var store = new OperationStore(db, new AuditLog(db, clock), clock);
        await store.CreateAsync(Ctx(), Alice, "analysis", null, ct);

        using var collector = new MetricCollector<double>(
            null, LakeWrightTelemetry.MeterName, "lakewright.operations.queue_wait");

        // Act
        clock.Advance(TimeSpan.FromSeconds(90));
        await store.ClaimNextAsync(maxInFlightPerTenant: 4, ct);

        // Assert
        collector.LastMeasurement.ShouldNotBeNull().Value.ShouldBe(90, tolerance: 0.5);
    }

    [Fact]
    public async Task A_refused_tenant_is_counted()
    {
        // Arrange — this request answers 404, so nothing in an access log separates it from a stale
        // bookmark. The counter is what an operator can alert on.
        var ct = TestContext.Current.CancellationToken;
        var (host, client) = await StartAsync(postgres);
        using var _h = host;
        using var collector = new MetricCollector<long>(
            null, LakeWrightTelemetry.MeterName, "lakewright.tenant.access_denied");

        // Act
        await client.SendAsync(
            As(Bob, HttpMethod.Get, $"/organizations/{AcmeId.Value}/operations/{Guid.CreateVersion7()}"), ct);

        // Assert
        collector.GetMeasurementSnapshot().Sum(m => m.Value).ShouldBe(1);
    }

    private async Task<LakeWrightDbContext> SeededAsync()
    {
        var db = await postgres.NewDatabaseAsync();

        db.Organizations.Add(new Organization
        {
            Id = AcmeId,
            Name = "Acme",
            Slug = "acme",
            CreatedAt = DateTimeOffset.UtcNow,
            Schema = UnityCatalogIdentifier.SchemaForTenant(AcmeId),
            State = OrganizationState.Active
        });

        await db.SaveChangesAsync();
        return db;
    }
}
