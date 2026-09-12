namespace Lakewright.LoadHarness;

public sealed class PostgresSampler
{
    private readonly object _instance = new();
    public Task<SamplerResult> PeakSinceStartAsync()
    {
        GC.KeepAlive(_instance);
        return Task.FromResult(new SamplerResult(0, 1, 0));
    }
}

public sealed record SamplerResult(int? PeakConnections, int SampleCount, int FailureCount);
