namespace Lakewright.LoadHarness;

/// <summary>
/// The SLO verdict: percentiles, error rate, pool utilisation, and a per-gate PASS/FAIL.
/// </summary>
public sealed record Verdict
{
    public required double OperationsPostP50Ms { get; init; }
    public required double OperationsPostP95Ms { get; init; }
    public required double OperationsPostP99Ms { get; init; }
    public required double ErrorRateOperationsPost { get; init; }
    public required int OperationsPostCount { get; init; }
    public required double CostGetP50Ms { get; init; }
    public required double CostGetP95Ms { get; init; }
    public required double CostGetP99Ms { get; init; }
    public required double ErrorRateCostGet { get; init; }
    public required int CostGetCount { get; init; }
    public required int PeakPostgresConnections { get; init; }
    public required double PeakPostgresConnectionUtilisation { get; init; }
    public required double CombinedErrorRate { get; init; }

    public required double OperationsPostP99SloMs { get; init; }
    public required double CostGetP99SloMs { get; init; }
    public required double ErrorRateSlo { get; init; }
    public required double PoolUtilisationSlo { get; init; }

    public bool OperationsPostP99Passed => OperationsPostP99Ms < OperationsPostP99SloMs;
    public bool CostGetP99Passed => CostGetP99Ms < CostGetP99SloMs;
    public bool ErrorRatePassed => CombinedErrorRate < ErrorRateSlo;
    public bool PoolPassed => PeakPostgresConnectionUtilisation < PoolUtilisationSlo;

    public bool AllGatesPassed => OperationsPostP99Passed && CostGetP99Passed && ErrorRatePassed && PoolPassed;
}

/// <summary>
/// Computes the verdict from a <see cref="Measurements"/> and a <see cref="HarnessOptions"/>.
/// </summary>
public static class SloGate
{
    public static Verdict Evaluate(Measurements m, HarnessOptions o)
    {
        var opsP50 = Percentile(m.OperationsPostLatenciesMs, 0.50);
        var opsP95 = Percentile(m.OperationsPostLatenciesMs, 0.95);
        var opsP99 = Percentile(m.OperationsPostLatenciesMs, 0.99);
        var costP50 = Percentile(m.CostGetLatenciesMs, 0.50);
        var costP95 = Percentile(m.CostGetLatenciesMs, 0.95);
        var costP99 = Percentile(m.CostGetLatenciesMs, 0.99);

        var opsErr = m.OperationsPostCount == 0
            ? 1.0
            : 1.0 - ((double)m.OperationsPostSuccess / m.OperationsPostCount);
        var costErr = m.CostGetCount == 0
            ? 1.0
            : 1.0 - ((double)m.CostGetSuccess / m.CostGetCount);
        var total = m.OperationsPostCount + m.CostGetCount;
        var combinedErr = total == 0
            ? 1.0
            : (opsErr * m.OperationsPostCount + costErr * m.CostGetCount) / total;
        var poolUtil = o.MaxPoolSize == 0
            ? 0
            : (double)m.PeakPostgresConnections / o.MaxPoolSize;

        return new Verdict
        {
            OperationsPostP50Ms = opsP50,
            OperationsPostP95Ms = opsP95,
            OperationsPostP99Ms = opsP99,
            ErrorRateOperationsPost = opsErr,
            OperationsPostCount = m.OperationsPostCount,
            CostGetP50Ms = costP50,
            CostGetP95Ms = costP95,
            CostGetP99Ms = costP99,
            ErrorRateCostGet = costErr,
            CostGetCount = m.CostGetCount,
            PeakPostgresConnections = m.PeakPostgresConnections,
            PeakPostgresConnectionUtilisation = poolUtil,
            CombinedErrorRate = combinedErr,
            OperationsPostP99SloMs = o.OperationsPostP99SloMs,
            CostGetP99SloMs = o.CostGetP99SloMs,
            ErrorRateSlo = o.ErrorRateSlo,
            PoolUtilisationSlo = o.PoolUtilisationSlo,
        };
    }

    /// <summary>Nearest-rank percentile. Returns 0 for an empty sample.</summary>
    public static double Percentile(IReadOnlyList<double> samples, double p)
    {
        if (samples.Count == 0)
        {
            return 0;
        }
        var sorted = samples.OrderBy(s => s).ToArray();
        var rank = (int)Math.Ceiling(p * sorted.Length);
        var idx = Math.Clamp(rank - 1, 0, sorted.Length - 1);
        return sorted[idx];
    }
}
