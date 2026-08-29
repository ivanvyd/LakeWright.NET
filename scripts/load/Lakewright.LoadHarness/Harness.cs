using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;

namespace Lakewright.LoadHarness;

/// <summary>
/// Drives load at a target RPS for a fixed duration, capturing per-request latency and outcome.
/// </summary>
/// <remarks>
/// Two endpoint profiles, dispatched in proportion that matches the kit's actual mix:
/// /operations (POST start + claim loop) and /cost (GET). The harness uses simple in-process
/// pacing: one timer-driven coordinator releases a token every (1_000_000 / rps) microseconds, and
/// a configurable number of worker tasks pull tokens and fire requests. The HTTP client uses a
/// single connection pool with bounded concurrency, so a tail of slow requests on one endpoint
/// does not let other workers pile on.
/// </remarks>
public sealed class Harness
{
    private readonly HttpClient _client;
    private readonly PostgresSampler _sampler;
    private readonly HarnessOptions _options;
    private readonly Guid _tenantId;
    private readonly string _principal;

    public Harness(
        HttpClient client,
        PostgresSampler sampler,
        HarnessOptions options,
        Guid tenantId,
        string principal)
    {
        _client = client;
        _sampler = sampler;
        _options = options;
        _tenantId = tenantId;
        _principal = principal;
    }

    public async Task<Measurements> RunAsync()
    {
        Console.WriteLine($"[harness] warming up for 10s...");
        await RunPhase(TimeSpan.FromSeconds(10), isWarmup: true);
        Console.WriteLine($"[harness] running main phase for {_options.DurationSeconds}s at {_options.RequestsPerSecond} RPS...");

        var mainPhaseStopwatch = Stopwatch.StartNew();
        var measurements = await RunPhase(TimeSpan.FromSeconds(_options.DurationSeconds), isWarmup: false);
        mainPhaseStopwatch.Stop();

        var peakConnections = await _sampler.PeakSinceStartAsync();
        var totalDuration = mainPhaseStopwatch.Elapsed;
        measurements = measurements with
        {
            TotalRequests = measurements.OperationsPostCount + measurements.CostGetCount,
            PeakPostgresConnections = peakConnections,
            ActualRps = (measurements.OperationsPostCount + measurements.CostGetCount) / Math.Max(0.001, totalDuration.TotalSeconds),
        };

        return measurements;
    }

    private async Task<Measurements> RunPhase(TimeSpan duration, bool isWarmup)
    {
        var opsLatenciesMs = new ConcurrentBag<double>();
        var opsOutcomes = new ConcurrentBag<bool>();
        var costLatenciesMs = new ConcurrentBag<double>();
        var costOutcomes = new ConcurrentBag<bool>();

        // Token-bucket pacing: emit one token per (1_000_000 / rps) microseconds. The coordinator
        // task pumps tokens; the workers consume them. Token count is uncapped.
        var tokenInterval = TimeSpan.FromMicroseconds(1_000_000.0 / _options.RequestsPerSecond);
        var stopAt = DateTimeOffset.UtcNow + duration;
        var tokens = new ConcurrentQueue<DateTimeOffset>();

        var coordinator = Task.Run(async () =>
        {
            var next = DateTimeOffset.UtcNow;
            while (DateTimeOffset.UtcNow < stopAt)
            {
                next += tokenInterval;
                var delay = next - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    try { await Task.Delay(delay, CancellationToken.None); }
                    catch (TaskCanceledException) { return; }
                }
                tokens.Enqueue(next);
            }
        });

        // Eight workers is enough to overlap the sample's I/O without saturating the TestServer's
        // single-threaded request pump. More workers would just queue inside the test host.
        var workerCount = 8;
        var workers = Enumerable.Range(0, workerCount).Select(workerId => Task.Run(async () =>
        {
            await using var local = new RequestRunner(_client, _tenantId, _principal);
            while (DateTimeOffset.UtcNow < stopAt)
            {
                if (!tokens.TryDequeue(out _))
                {
                    await Task.Delay(1);
                    continue;
                }
                var sw = Stopwatch.StartNew();
                try
                {
                    var (op, ok) = await local.FireOne();
                    sw.Stop();
                    var ms = sw.Elapsed.TotalMilliseconds;
                    if (op == "operations")
                    {
                        opsLatenciesMs.Add(ms);
                        opsOutcomes.Add(ok);
                    }
                    else
                    {
                        costLatenciesMs.Add(ms);
                        costOutcomes.Add(ok);
                    }
                }
                catch
                {
                    // Defensive: never let a worker die mid-run.
                }
            }
        })).ToArray();

        await Task.WhenAll(workers.Append(coordinator));

        if (isWarmup)
        {
            return new Measurements();
        }

        return new Measurements
        {
            OperationsPostCount = opsOutcomes.Count,
            OperationsPostSuccess = opsOutcomes.Count(b => b),
            OperationsPostLatenciesMs = opsLatenciesMs.ToArray(),
            CostGetCount = costOutcomes.Count,
            CostGetSuccess = costOutcomes.Count(b => b),
            CostGetLatenciesMs = costLatenciesMs.ToArray(),
        };
    }
}

/// <summary>
/// One worker's worth of HTTP plumbing. Holds a typed client and a "which endpoint to hit next"
/// counter that alternates between /operations and /cost.
/// </summary>
internal sealed class RequestRunner : IAsyncDisposable
{
    private readonly HttpClient _client;
    private readonly Guid _tenantId;
    private readonly string _principal;
    private int _toggle;

    public RequestRunner(HttpClient client, Guid tenantId, string principal)
    {
        _client = client;
        _tenantId = tenantId;
        _principal = principal;
    }

    public async Task<(string Op, bool Ok)> FireOne()
    {
        // Alternate between /operations and /cost with a roughly 80/20 split: the kit's own
        // telemetry says /operations dominates traffic in practice. The split is approximate.
        var isOperations = (Interlocked.Increment(ref _toggle) % 5) != 0;

        // The sample's demo auth scheme reads the principal from X-Demo-User. Set the same
        // header on every request so the harness is hitting authenticated endpoints; an
        // unauthenticated request would 401 and inflate the error rate.
        using var req = new HttpRequestMessage();
        req.Headers.Add("X-Demo-User", _principal);

        if (isOperations)
        {
            req.Method = HttpMethod.Post;
            req.RequestUri = new Uri($"/organizations/{_tenantId}/operations", UriKind.Relative);
            req.Content = new StringContent("{\"kind\":\"analysis\"}", System.Text.Encoding.UTF8, "application/json");
            var resp = await _client.SendAsync(req);
            return ("operations", resp.IsSuccessStatusCode);
        }
        else
        {
            req.Method = HttpMethod.Get;
            req.RequestUri = new Uri($"/organizations/{_tenantId}/cost", UriKind.Relative);
            var resp = await _client.SendAsync(req);
            return ("cost", resp.IsSuccessStatusCode);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Aggregated measurements from a single run. The verdict is computed from this.</summary>
public sealed record Measurements
{
    public int OperationsPostCount { get; init; }
    public int OperationsPostSuccess { get; init; }
    public double[] OperationsPostLatenciesMs { get; init; } = [];
    public int CostGetCount { get; init; }
    public int CostGetSuccess { get; init; }
    public double[] CostGetLatenciesMs { get; init; } = [];
    public int PeakPostgresConnections { get; init; }
    public int TotalRequests { get; init; }
    public double ActualRps { get; init; }
}
