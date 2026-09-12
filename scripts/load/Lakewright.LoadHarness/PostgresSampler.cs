using System.Collections.Concurrent;

namespace Lakewright.LoadHarness;

/// <summary>
/// Background sampler that records the highest Postgres connection count observed during the
/// harness run.
/// </summary>
/// <remarks>
/// Reads <c>pg_stat_activity</c> every second. Postgres exposes the server-wide client connection
/// count via this view; this is not Npgsql pool occupancy.
/// </remarks>
public sealed class PostgresSampler : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly ConcurrentQueue<int> _samples = new();
    private int _failures;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public PostgresSampler(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunLoop(_cts.Token));
        // Give the loop a moment to do its first sample.
        await Task.Delay(50);
    }

    public async Task<SamplerResult> PeakSinceStartAsync()
    {
        await StopAsync();
        var samples = _samples.ToArray();
        return new SamplerResult(
            samples.Length == 0 ? null : samples.Max(),
            samples.Length,
            Volatile.Read(ref _failures));
    }

    private async Task RunLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var conn = new Npgsql.NpgsqlConnection(_connectionString);
                await conn.OpenAsync(ct);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM pg_stat_activity WHERE state IS NOT NULL";
                var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
                _samples.Enqueue(count);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                Interlocked.Increment(ref _failures);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
            catch (TaskCanceledException) { return; }
        }
    }

    private async Task StopAsync()
    {
        if (_cts is null || _loop is null)
        {
            return;
        }
        _cts.Cancel();
        try { await _loop; } catch { /* expected on cancel */ }
        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}

/// <summary>Sampler coverage; a missing peak is unavailable, never zero.</summary>
public sealed record SamplerResult(int? PeakConnections, int SampleCount, int FailureCount);
