using System.Diagnostics.Metrics;

namespace LakeWright.Embedding;

/// <summary>Dependency-free telemetry for dashboard token minting.</summary>
public static class LakeWrightEmbeddingTelemetry
{
    public const string MeterName = "LakeWright.Embedding";

    private static readonly Meter Meter = new(MeterName);

    internal static readonly Histogram<double> MintDuration = Meter.CreateHistogram<double>(
        "lakewright.embedding.mint_duration",
        unit: "ms",
        description: "End-to-end dashboard token issue duration.");

    internal static readonly Counter<long> EmbedCacheHits = Meter.CreateCounter<long>(
        "lakewright.embedding.cache",
        unit: "{lookup}",
        description: "Workspace and viewer token cache lookups by leg and result.");
}
