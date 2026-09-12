// Top-level statements are avoided here so this file does not generate an implicit `Program` class
// that collides with the sample assembly's `Program` (which WebApplicationFactory reaches into).
// The sample's entry point is aliased to a name that says what it is.

using System.Net;
using System.Text.RegularExpressions;
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

        if (!string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            var baseUri = new Uri(options.BaseUrl, UriKind.Absolute);
            if (!baseUri.IsLoopback)
            {
                throw new ArgumentException("The real-network profile accepts only a loopback --base-url.");
            }
            using var sockets = new SocketsHttpHandler
            {
                MaxConnectionsPerServer = options.MaxConnections,
                UseCookies = true,
                CookieContainer = new CookieContainer()
            };
            using var client = new HttpClient(sockets) { BaseAddress = baseUri };
            await SignInLoopbackAsync(client, "demo|alice");
            // A loopback caller exercises Signalboard's browser contract with the fixture's real
            // membership. It first posts the page's antiforgery token and then carries the issued
            // cookie; the terminal-only X-Demo-User shortcut is deliberately disabled here.
            var networkOptions = options with { UseDemoHeader = false, DatabaseSamplingRequired = false };
            var networkHarness = new Harness(client, null, networkOptions, Guid.Parse("0198f000-0000-7000-8000-00000000ac11"), "demo|alice");
            var networkMeasurements = await networkHarness.RunAsync();
            var networkVerdict = SloGate.Evaluate(networkMeasurements, networkOptions);
            await WriteResultAsync(networkOptions, networkMeasurements, networkVerdict, "loopback-sockets");
            return networkVerdict.AllGatesPassed ? 0 : 1;
        }

        Console.WriteLine($"[harness] RPS={options.RequestsPerSecond} duration={options.DurationSeconds}s connections={options.MaxConnections} pg_max={options.PostgresMaxConnections} pool={options.PostgresPoolSize}");

        await using var env = await HarnessEnvironment.CreateAsync(options);
        Console.WriteLine($"[harness] Postgres up at {env.PostgresConnectionString.Host}:{env.PostgresConnectionString.Port}, effective max_connections={env.EffectivePostgresMaxConnections}, application MaxPoolSize={options.PostgresPoolSize}; sample listening on in-process TestServer");

        await using var sampler = new PostgresSampler(env.PostgresConnectionString.ToString());
        await sampler.StartAsync();

        var harness = new Harness(env.Client, sampler, options, env.SeededTenantIds[0], "harness-user-1");
        var measurements = await harness.RunAsync();
        await sampler.DisposeAsync();

        var verdict = SloGate.Evaluate(measurements, options);
        await WriteResultAsync(options, measurements, verdict, "in-process-testserver");

        Console.WriteLine();
        Console.WriteLine("== Load harness results ==");
        Console.WriteLine($"  /operations POST: p50={verdict.OperationsPostP50Ms:F1}ms p95={verdict.OperationsPostP95Ms:F1}ms p99={verdict.OperationsPostP99Ms:F1}ms errors={verdict.ErrorRateOperationsPost:P3} count={verdict.OperationsPostCount}");
        Console.WriteLine($"  /cost       GET : p50={verdict.CostGetP50Ms:F1}ms p95={verdict.CostGetP95Ms:F1}ms p99={verdict.CostGetP99Ms:F1}ms errors={verdict.ErrorRateCostGet:P3} count={verdict.CostGetCount}");
        Console.WriteLine(verdict.PoolEvaluated
            ? $"  Postgres connections (peak)  : {verdict.PeakPostgresConnections} / {options.PostgresMaxConnections}  ({verdict.PeakPostgresConnectionUtilisation:P1})"
            : "  Postgres connections (peak)  : unavailable for this profile");
        Console.WriteLine($"  arrivals scheduled/issued/completed/dropped/censored: {measurements.ScheduledRequests}/{measurements.IssuedRequests}/{measurements.CompletedRequests}/{measurements.DroppedRequests}/{measurements.CensoredRequests}; completed={verdict.AchievedRate:P1}, rate={verdict.ActualRateRatio:P1} ({measurements.ActualRps:F1} RPS)");
        Console.WriteLine();
        Console.WriteLine($"  SLO gates:");
        Console.WriteLine($"    /operations POST p99 < {options.OperationsPostP99SloMs}ms       : {(verdict.OperationsPostP99Passed ? "PASS" : "FAIL")}  ({verdict.OperationsPostP99Ms:F1}ms)");
        Console.WriteLine($"    /cost       GET  p99 < {options.CostGetP99SloMs}ms         : {(verdict.CostGetP99Passed ? "PASS" : "FAIL")}  ({verdict.CostGetP99Ms:F1}ms)");
        Console.WriteLine($"    error rate       < {options.ErrorRateSlo:P3}        : {(verdict.ErrorRatePassed ? "PASS" : "FAIL")}  ({verdict.CombinedErrorRate:P3})");
        Console.WriteLine(verdict.PoolEvaluated
            ? $"    server connections < {options.PoolUtilisationSlo:P1} : {(verdict.PoolCoveragePassed ? "PASS" : "FAIL")}  ({verdict.PeakPostgresConnectionUtilisation:P1})"
            : "    server connections                  : NOT EVALUATED (no database sampler)");
        Console.WriteLine($"    completion/rate  >= {verdict.MinimumAchievedRate:P1}       : {(verdict.ThroughputPassed ? "PASS" : "FAIL")}  ({verdict.AchievedRate:P1}/{verdict.ActualRateRatio:P1})");
        Console.WriteLine($"    accounting reconciliation                 : {(verdict.AccountingPassed ? "PASS" : "FAIL")}");
        Console.WriteLine($"    endpoint samples >= {verdict.MinimumSamplesPerEndpoint}          : {(verdict.SamplesPassed ? "PASS" : "FAIL")}  ({verdict.OperationsPostCount}/{verdict.CostGetCount})");
        Console.WriteLine();

        return verdict.AllGatesPassed ? 0 : 1;
    }

    private static async Task WriteResultAsync(HarnessOptions options, Measurements measurements, Verdict verdict, string profile)
    {
        if (string.IsNullOrWhiteSpace(options.ResultsPath))
        {
            return;
        }
        var directory = Path.GetDirectoryName(Path.GetFullPath(options.ResultsPath));
        Directory.CreateDirectory(directory!);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        await File.WriteAllTextAsync(options.ResultsPath, System.Text.Json.JsonSerializer.Serialize(new
        {
            profile,
            options,
            measurements,
            verdict,
            process = new
            {
                id = process.Id,
                workingSetBytes = process.WorkingSet64,
                totalProcessorTimeMs = process.TotalProcessorTime.TotalMilliseconds,
                threadCount = process.Threads.Count
            }
        }));
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
            Lakewright.LoadHarness — drive the sample at a target RPS, assert SLO gates.

            Usage:
              dotnet run -- [options]

            Options (with defaults):
              --rps=<int>                 target arrivals per second (default 500)
              --duration=<seconds>        main phase length (default 300; plus 10s warmup)
              --connections=<int>         maximum requests in flight (default 8)
              --queue=<int>               bounded arrivals awaiting dispatch (default 64)
              --min-achieved=<fraction>   minimum completion and actual/requested rates (default 0.95)
              --min-samples=<int>         minimum completed samples per endpoint (default 20)
              --pg-max-connections=<int>  PostgreSQL server connection limit (default 200)
              --pg-pool=<int>             application Npgsql maximum pool size (default 12)
              --p99-operations=<ms>       operation p99 threshold (default 500)
              --p99-cost=<ms>             cost p99 threshold (default 200)
              --error-rate=<fraction>     failed/dropped arrival threshold (default 0.001 = 0.1%)
              --pool=<fraction>           sampled server connection utilisation threshold (default 0.8)
              --pg-image=<name>           PostgreSQL image (default postgres:17-alpine)
              --seed=<int>                seeded tenant count (default 2; traffic targets the first)
              --base-url=<url>            loopback Signalboard URL; real sockets and cookie sign-in
              --results=<path>            machine-readable JSON artifact
            """);
    }

    private static async Task SignInLoopbackAsync(HttpClient client, string principal)
    {
        using var page = await client.GetAsync("/signin");
        page.EnsureSuccessStatusCode();
        var html = await page.Content.ReadAsStringAsync();
        var match = Regex.Match(html, "<input[^>]+name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            throw new InvalidOperationException("The loopback sign-in page did not render an antiforgery token.");
        }

        using var form = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("principal", principal),
            new KeyValuePair<string, string>("__RequestVerificationToken", WebUtility.HtmlDecode(match.Groups[1].Value))
        ]);
        using var signedIn = await client.PostAsync("/signin", form);
        signedIn.EnsureSuccessStatusCode();
    }
}
