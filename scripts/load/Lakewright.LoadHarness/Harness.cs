using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Threading.Channels;

namespace Lakewright.LoadHarness;

/// <summary>
/// Drives load at a target RPS for a fixed duration, capturing per-request latency and outcome.
/// </summary>
/// <remarks>
/// Two endpoint profiles, dispatched in a configured synthetic 80/20 mix:
/// /operations (POST start + claim loop) and /cost (GET). The harness uses simple in-process
/// pacing: one timer-driven coordinator releases a token every (1_000_000 / rps) microseconds, and
/// a configurable number of worker tasks pull tokens and fire requests. The HTTP client uses a
/// single connection pool with bounded concurrency, so a tail of slow requests on one endpoint
/// does not let other workers pile on.
/// </remarks>
public sealed class Harness
{
    internal static int _diagErrorLogged;

    private readonly HttpClient _client;
    private readonly PostgresSampler? _sampler;
    private readonly HarnessOptions _options;
    private readonly Guid _tenantId;
    private readonly string _principal;
    private int _nextEndpoint;

    public Harness(
        HttpClient client,
        PostgresSampler? sampler,
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

        var samplerResult = _sampler is null ? null : await _sampler.PeakSinceStartAsync();
        var peakConnections = samplerResult?.PeakConnections;
        var totalDuration = mainPhaseStopwatch.Elapsed;
        measurements = measurements with
        {
            TotalRequests = measurements.CompletedRequests,
            PeakPostgresConnections = peakConnections,
            DatabaseSamplerSamples = samplerResult?.SampleCount ?? 0,
            DatabaseSamplerFailures = samplerResult?.FailureCount ?? 0,
            ActualRps = measurements.CompletedRequests / Math.Max(0.001, totalDuration.TotalSeconds),
        };

        return measurements;
    }

    private async Task<Measurements> RunPhase(TimeSpan duration, bool isWarmup)
    {
        var phaseStopwatch = Stopwatch.StartNew();
        var opsLatenciesMs = new ConcurrentBag<double>();
        var opsOutcomes = new ConcurrentBag<bool>();
        var costLatenciesMs = new ConcurrentBag<double>();
        var costOutcomes = new ConcurrentBag<bool>();

        // Token-bucket pacing: emit one token per (1_000_000 / rps) microseconds. The coordinator
        // task pumps tokens; the workers consume them. The queue is bounded and every offered
        // arrival is classified as completed, dropped, or censored at the phase deadline.
        var tokenInterval = TimeSpan.FromMicroseconds(1_000_000.0 / _options.RequestsPerSecond);
        var stopAt = DateTimeOffset.UtcNow + duration;
        using var phaseCts = new CancellationTokenSource(duration);
        var phaseToken = phaseCts.Token;
        var tokens = Channel.CreateBounded<DateTimeOffset>(new BoundedChannelOptions(_options.MaxQueuedRequests)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = false
        });
        var scheduled = 0;
        var issued = 0;
        var dropped = 0;
        var completed = 0;
        var failed = 0;
        var censored = 0;
        var queuedAtDeadline = 0;

        var coordinator = Task.Run(async () =>
        {
            var next = DateTimeOffset.UtcNow;
            while (!phaseToken.IsCancellationRequested && DateTimeOffset.UtcNow < stopAt)
            {
                next += tokenInterval;
                if (next >= stopAt)
                {
                    return;
                }
                var delay = next - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    try { await Task.Delay(delay, phaseToken); }
                    catch (TaskCanceledException) { return; }
                }
                Interlocked.Increment(ref scheduled);
                // A bounded arrival queue makes under-delivery observable rather than storing an
                // arbitrarily large backlog that workers cannot issue before the phase ends.
                if (!tokens.Writer.TryWrite(next))
                {
                    Interlocked.Increment(ref dropped);
                }
            }
        });

        var workerCount = _options.MaxConnections;
        var workers = Enumerable.Range(0, workerCount).Select(workerId => Task.Run(async () =>
        {
            await using var local = new RequestRunner(_client, _tenantId, _principal, _options.UseDemoHeader, () => Interlocked.Increment(ref _nextEndpoint));
            while (!phaseToken.IsCancellationRequested && DateTimeOffset.UtcNow < stopAt)
            {
                if (!tokens.Reader.TryRead(out _))
                {
                    try { await Task.Delay(1, phaseToken); }
                    catch (TaskCanceledException) { break; }
                    continue;
                }
                Interlocked.Increment(ref issued);
                var (op, ok, ms) = await local.FireOne(phaseToken);
                if (ok is null || phaseToken.IsCancellationRequested)
                {
                    Interlocked.Increment(ref censored);
                    continue;
                }
                Interlocked.Increment(ref completed);
                if (!ok.Value)
                {
                    Interlocked.Increment(ref failed);
                }
                if (op == "operations")
                {
                    opsLatenciesMs.Add(ms);
                    opsOutcomes.Add(ok.Value);
                }
                else
                {
                    costLatenciesMs.Add(ms);
                    costOutcomes.Add(ok.Value);
                }
            }
        })).ToArray();

        await Task.WhenAll(workers.Append(coordinator));
        while (tokens.Reader.TryRead(out _))
        {
            Interlocked.Increment(ref censored);
            Interlocked.Increment(ref queuedAtDeadline);
        }

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
            ScheduledRequests = scheduled,
            IssuedRequests = issued,
            DroppedRequests = dropped,
            CompletedRequests = completed,
            FailedRequests = failed,
            CensoredRequests = censored,
            // Classify issued requests after all workers observe the cutoff. A cancellation
            // callback snapshot races synchronous HTTP cancellation/completion continuations.
            InFlightAtDeadline = issued - completed,
            QueuedAtDeadline = queuedAtDeadline,
            ActualRps = completed / Math.Max(0.001, phaseStopwatch.Elapsed.TotalSeconds),
        };
    }

    internal Task<Measurements> RunPhaseForTestsAsync(TimeSpan duration) => RunPhase(duration, isWarmup: false);
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
    private readonly bool _useDemoHeader;
    private readonly Func<int> _nextEndpoint;

    public RequestRunner(HttpClient client, Guid tenantId, string principal, bool useDemoHeader, Func<int> nextEndpoint)
    {
        _client = client;
        _tenantId = tenantId;
        _principal = principal;
        _useDemoHeader = useDemoHeader;
        _nextEndpoint = nextEndpoint;
    }

    public async Task<(string Op, bool? Ok, double Milliseconds)> FireOne(CancellationToken cancellationToken)
    {
        // Alternate between /operations and /cost with a deterministic 80/20 synthetic mix so
        // both endpoint gates receive samples in one run.
        var isOperations = (_nextEndpoint() % 5) != 0;

        // The sample's demo auth scheme reads the principal from X-Demo-User. Set that header
        // on every request so the handler returns Success and the membership lookup proceeds; an
        // unauthenticated request would 401 and inflate the error rate. The harness is run
        // against the sample's host in-process, so it uses the sample's existing scheme rather
        // than introducing a parallel one.
        using var req = new HttpRequestMessage();
        if (_useDemoHeader)
        {
            req.Headers.Add("X-Demo-User", _principal);
        }
        var op = isOperations ? "operations" : "cost";
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (isOperations)
            {
                req.Method = HttpMethod.Post;
                req.RequestUri = new Uri($"/organizations/{_tenantId}/operations", UriKind.Relative);
                req.Content = new StringContent("{\"kind\":\"analysis\"}", System.Text.Encoding.UTF8, "application/json");
            }
            else
            {
                req.Method = HttpMethod.Get;
                req.RequestUri = new Uri($"/organizations/{_tenantId}/cost", UriKind.Relative);
            }
            using var response = await _client.SendAsync(req, cancellationToken);
            return (op, response.IsSuccessStatusCode, stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (op, null, stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            if (Interlocked.Increment(ref Harness._diagErrorLogged) == 1)
            {
                Console.WriteLine($"[diag-worker] {ex.GetType().Name}: {ex.Message}");
            }
            return (op, false, stopwatch.Elapsed.TotalMilliseconds);
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
    public int? PeakPostgresConnections { get; init; }
    public int DatabaseSamplerSamples { get; init; }
    public int DatabaseSamplerFailures { get; init; }
    public int TotalRequests { get; init; }
    public double ActualRps { get; init; }
    public int ScheduledRequests { get; init; }
    public int IssuedRequests { get; init; }
    public int DroppedRequests { get; init; }
    public int CompletedRequests { get; init; }
    public int FailedRequests { get; init; }
    public int CensoredRequests { get; init; }
    public int InFlightAtDeadline { get; init; }
    public int QueuedAtDeadline { get; init; }
}
