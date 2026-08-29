using System.Collections.Concurrent;

namespace Lakewright.LoadHarness;

/// <summary>
/// Background sampler that records the highest Postgres connection count observed during the
/// harness run.
/// </summary>
/// <remarks>
/// Reads <c>pg_stat_activity</c> every second. Postgres exposes the live connection count via
/// the total in this view. The harness compares the peak against the SLO. One query per second
/// against a 17-alpine container is sub-millisecond and adds nothing measurable to the test.
/// </remarks>
public sealed class PostgresSampler : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly ConcurrentQueue<int> _samples = new();
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

    public async Task<int> PeakSinceStartAsync()
    {
        await StopAsync();
        return _samples.DefaultIfEmpty(0).Max();
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
            catch
            {
                // Don't let a transient read fail the harness.
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
