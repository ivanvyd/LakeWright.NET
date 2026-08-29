// Top-level statements are avoided here so this file does not generate an implicit `Program` class
// that collides with the sample assembly's `Program` (which WebApplicationFactory reaches into).
// The sample's entry point is aliased to a name that says what it is.

using Lakewright.LoadHarness;

namespace Lakewright.LoadHarness;

public static class LoadHarness
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && (args[0] is "-h" or "--help"))
        {
            PrintUsage();
            return 0;
        }

        var options = HarnessOptions.Parse(args);

        Console.WriteLine($"[harness] RPS={options.RequestsPerSecond} duration={options.DurationSeconds}s connections={options.MaxConnections} max_pool={options.MaxPoolSize}");

        await using var env = await HarnessEnvironment.CreateAsync(options);
        Console.WriteLine($"[harness] Postgres up at {env.PostgresConnectionString.Host}:{env.PostgresConnectionString.Port}, sample listening on in-process TestServer");

        await using var sampler = new PostgresSampler(env.PostgresConnectionString.ToString());
        await sampler.StartAsync();

        var harness = new Harness(env.Client, sampler, options, env.SeededTenantIds[0], "harness-user-1");
        var measurements = await harness.RunAsync();
        await sampler.DisposeAsync();

        var verdict = SloGate.Evaluate(measurements, options);

        Console.WriteLine();
        Console.WriteLine("== Load harness results ==");
        Console.WriteLine($"  /operations POST: p50={verdict.OperationsPostP50Ms:F1}ms p95={verdict.OperationsPostP95Ms:F1}ms p99={verdict.OperationsPostP99Ms:F1}ms errors={verdict.ErrorRateOperationsPost:P3} count={verdict.OperationsPostCount}");
        Console.WriteLine($"  /cost       GET : p50={verdict.CostGetP50Ms:F1}ms p95={verdict.CostGetP95Ms:F1}ms p99={verdict.CostGetP99Ms:F1}ms errors={verdict.ErrorRateCostGet:P3} count={verdict.CostGetCount}");
        Console.WriteLine($"  Postgres connections (peak)  : {verdict.PeakPostgresConnections} / {options.MaxPoolSize}  ({verdict.PeakPostgresConnectionUtilisation:P1})");
        Console.WriteLine();
        Console.WriteLine($"  SLO gates:");
        Console.WriteLine($"    /operations POST p99 < {options.OperationsPostP99SloMs}ms       : {(verdict.OperationsPostP99Passed ? "PASS" : "FAIL")}  ({verdict.OperationsPostP99Ms:F1}ms)");
        Console.WriteLine($"    /cost       GET  p99 < {options.CostGetP99SloMs}ms         : {(verdict.CostGetP99Passed ? "PASS" : "FAIL")}  ({verdict.CostGetP99Ms:F1}ms)");
        Console.WriteLine($"    error rate       < {options.ErrorRateSlo:P3}        : {(verdict.ErrorRatePassed ? "PASS" : "FAIL")}  ({verdict.CombinedErrorRate:P3})");
        Console.WriteLine($"    connection pool  < {options.PoolUtilisationSlo:P1} : {(verdict.PoolPassed ? "PASS" : "FAIL")}  ({verdict.PeakPostgresConnectionUtilisation:P1})");
        Console.WriteLine();

        return verdict.AllGatesPassed ? 0 : 1;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
            Lakewright.LoadHarness — drive the sample at a target RPS, assert SLO gates.

            Usage:
              dotnet run -- [options]

            Options (with defaults):
              --rps <int>              target requests per second (default 500)
              --duration <seconds>     run length, seconds (default 300 = 5 minutes)
              --connections <int>      HTTP client max connections per endpoint (default 1024)
              --max-pool <int>         EF Core / Npgsql max pool size (default 100)
              --p99-operations <ms>     SLO gate, /operations POST p99 in ms (default 500)
              --p99-cost <ms>           SLO gate, /cost GET p99 in ms (default 200)
              --error-rate <pct>        SLO gate, combined error rate 0..1 (default 0.001 = 0.1%)
              --pool <pct>              SLO gate, peak pool utilisation 0..1 (default 0.8)
              --pg-image <name>         Postgres image to use in testcontainers (default postgres:17-alpine)
              --seed <int>              number of seeded tenants (default 2)
            """);
    }
}
