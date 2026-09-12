using Lakewright.LoadHarness;

namespace LakeWright.TenantIsolation.Tests;

public class LoadHarnessGateTests
{
    private static HarnessOptions Options => new()
    {
        ErrorRateSlo = .001,
        MinimumAchievedRate = .95,
        MinimumSamplesPerEndpoint = 2,
        OperationsPostP99SloMs = 500,
        CostGetP99SloMs = 200,
        PostgresMaxConnections = 200,
        PoolUtilisationSlo = .8,
        RequestsPerSecond = 10
    };

    private static Measurements Measurements(
        int scheduled = 10, int issued = 4, int dropped = 0,
        bool operationsOk = true, bool costsOk = true) => new()
        {
            ScheduledRequests = scheduled,
            IssuedRequests = issued,
            DroppedRequests = dropped,
            CompletedRequests = 4,
            FailedRequests = (operationsOk ? 0 : 2) + (costsOk ? 0 : 2),
            CensoredRequests = scheduled - dropped - 4,
            QueuedAtDeadline = scheduled - dropped - 4,
            ActualRps = 10,
            OperationsPostLatenciesMs = [10, 10],
            OperationsPostSuccess = operationsOk ? 2 : 0,
            OperationsPostCount = 2,
            CostGetLatenciesMs = [10, 10],
            CostGetSuccess = costsOk ? 2 : 0,
            CostGetCount = 2,
            PeakPostgresConnections = 1,
            DatabaseSamplerSamples = 1
        };

    [Fact]
    public void Complete_traffic_with_required_database_evidence_passes()
    {
        SloGate.Evaluate(Measurements(scheduled: 4, issued: 4), Options).AllGatesPassed.ShouldBeTrue();
    }

    [Fact]
    public void Missing_database_evidence_fails_required_profile_but_is_explicitly_optional_for_loopback()
    {
        var measurements = Measurements(scheduled: 4, issued: 4) with
        {
            PeakPostgresConnections = null,
            DatabaseSamplerSamples = 0
        };
        var required = SloGate.Evaluate(measurements, Options);
        required.PoolEvaluated.ShouldBeFalse();
        required.PoolCoveragePassed.ShouldBeFalse();
        required.AllGatesPassed.ShouldBeFalse();

        var optional = SloGate.Evaluate(measurements, Options with { DatabaseSamplingRequired = false });
        optional.PoolEvaluated.ShouldBeFalse();
        optional.PoolPassed.ShouldBeNull();
        optional.AllGatesPassed.ShouldBeTrue();
    }

    [Fact]
    public void Partial_database_sampling_failure_cannot_prove_a_required_gate()
    {
        var measurements = Measurements(scheduled: 4, issued: 4) with { DatabaseSamplerFailures = 1 };
        var verdict = SloGate.Evaluate(measurements, Options);
        verdict.PoolPassed.ShouldBe(true);
        verdict.PoolCoveragePassed.ShouldBeFalse();
        verdict.AllGatesPassed.ShouldBeFalse();
    }

    [Fact]
    public void Completing_all_scheduled_work_does_not_hide_an_underperforming_generator()
    {
        var measurements = Measurements(scheduled: 4, issued: 4) with { ActualRps = 1 };
        var verdict = SloGate.Evaluate(measurements, Options);
        verdict.AchievedRate.ShouldBe(1);
        verdict.ThroughputPassed.ShouldBeFalse();
        verdict.AllGatesPassed.ShouldBeFalse();
    }

    [Fact]
    public void Partial_and_total_transport_failures_fail_the_error_gate()
    {
        SloGate.Evaluate(Measurements(operationsOk: false), Options).ErrorRatePassed.ShouldBeFalse();
        SloGate.Evaluate(Measurements(operationsOk: false, costsOk: false), Options).AllGatesPassed.ShouldBeFalse();
    }

    [Fact]
    public void Dropped_backlog_and_under_delivery_fail_the_throughput_gate()
    {
        var verdict = SloGate.Evaluate(Measurements(scheduled: 100, issued: 10, dropped: 90), Options);
        verdict.ThroughputPassed.ShouldBeFalse();
        verdict.ErrorRatePassed.ShouldBeFalse();
        verdict.AllGatesPassed.ShouldBeFalse();
    }

    [Fact]
    public void Empty_endpoint_sample_cannot_pass_a_zero_percentile()
    {
        var m = Measurements() with { CostGetCount = 0, CostGetSuccess = 0, CostGetLatenciesMs = [] };
        var verdict = SloGate.Evaluate(m, Options);
        verdict.SamplesPassed.ShouldBeFalse();
        verdict.AllGatesPassed.ShouldBeFalse();
    }

    [Fact]
    public void Timeout_is_a_failed_issued_request_and_is_in_the_denominator()
    {
        var m = Measurements(scheduled: 5, issued: 5) with
        {
            OperationsPostCount = 3,
            OperationsPostSuccess = 2,
            OperationsPostLatenciesMs = [1, 1, 5000],
            CostGetCount = 2,
            CostGetSuccess = 2,
            CompletedRequests = 5,
            FailedRequests = 1,
            CensoredRequests = 0,
            QueuedAtDeadline = 0
        };
        var verdict = SloGate.Evaluate(m, Options);
        verdict.ErrorRatePassed.ShouldBeFalse();
        verdict.OperationsPostP99Passed.ShouldBeFalse();
    }

    [Fact]
    public void Censored_arrivals_cannot_pass_even_when_completion_ratio_meets_the_floor()
    {
        var m = Measurements(scheduled: 1_000, issued: 1_000) with
        {
            OperationsPostCount = 950,
            OperationsPostSuccess = 950,
            OperationsPostLatenciesMs = Enumerable.Repeat(1d, 950).ToArray(),
            CostGetCount = 0,
            CostGetSuccess = 0,
            CostGetLatenciesMs = [],
            CompletedRequests = 950,
            FailedRequests = 0,
            CensoredRequests = 50,
            InFlightAtDeadline = 50,
            QueuedAtDeadline = 0,
            ActualRps = 10
        };

        var verdict = SloGate.Evaluate(m, Options);
        verdict.ThroughputPassed.ShouldBeTrue();
        verdict.AccountingPassed.ShouldBeFalse();
        verdict.AllGatesPassed.ShouldBeFalse();
    }

    [Fact]
    public void Option_parser_preserves_fraction_and_count_units()
    {
        var options = HarnessOptions.Parse(["--min-achieved=0.90", "--min-samples=12", "--queue=7", "--connections=3"]);
        options.MinimumAchievedRate.ShouldBe(.90);
        options.MinimumSamplesPerEndpoint.ShouldBe(12);
        options.MaxQueuedRequests.ShouldBe(7);
        options.MaxConnections.ShouldBe(3);
    }
}
