namespace Lakewright.LoadHarness;

/// <summary>
/// Configuration for a single harness run.
/// </summary>
/// <remarks>
/// Defaults match the SLO gates picked at planning: 500 RPS, 5 minutes, p99 &lt; 500 ms on
/// /operations, p99 &lt; 200 ms on /cost, error rate &lt; 0.1%, Postgres pool utilisation &lt; 80%.
/// Every value is overridable on the command line so a tighter production SLO is one flag away.
/// </remarks>
public sealed record HarnessOptions
{
    public int RequestsPerSecond { get; init; } = 500;
    public int DurationSeconds { get; init; } = 300;
    public int MaxConnections { get; init; } = 1024;
    /// <summary>Postgres <c>max_connections</c>. ADR 0015 anchors production at 200.</summary>
    public int PostgresMaxConnections { get; init; } = 200;
    /// <summary>EF Core / Npgsql per-process pool. ADR 0015 anchors production at 12.</summary>
    public int PostgresPoolSize { get; init; } = 12;
    public int OperationsPostP99SloMs { get; init; } = 500;
    public int CostGetP99SloMs { get; init; } = 200;
    public double ErrorRateSlo { get; init; } = 0.001;
    public double PoolUtilisationSlo { get; init; } = 0.8;
    public string PostgresImage { get; init; } = "postgres:17-alpine";
    public int SeedTenants { get; init; } = 2;

    public static HarnessOptions Parse(string[] args)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var arg in args)
        {
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }
            var eq = arg.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }
            dict[arg[2..eq]] = arg[(eq + 1)..];
        }

        return new HarnessOptions
        {
            RequestsPerSecond = Get(dict, "rps", Env("LW_HARNESS_RPS"), 500, int.Parse),
            DurationSeconds = Get(dict, "duration", Env("LW_HARNESS_DURATION"), 300, int.Parse),
            MaxConnections = Get(dict, "connections", Env("LW_HARNESS_CONNECTIONS"), 1024, int.Parse),
            PostgresMaxConnections = Get(dict, "pg-max-connections", Env("LW_HARNESS_PG_MAX_CONNECTIONS"), 200, int.Parse),
            PostgresPoolSize = Get(dict, "pg-pool", Env("LW_HARNESS_PG_POOL"), 12, int.Parse),
            OperationsPostP99SloMs = Get(dict, "p99-operations", Env("LW_HARNESS_P99_OPS"), 500, int.Parse),
            CostGetP99SloMs = Get(dict, "p99-cost", Env("LW_HARNESS_P99_COST"), 200, int.Parse),
            ErrorRateSlo = Get(dict, "error-rate", Env("LW_HARNESS_ERROR_RATE"), 0.001, double.Parse),
            PoolUtilisationSlo = Get(dict, "pool", Env("LW_HARNESS_POOL"), 0.8, double.Parse),
            PostgresImage = Get(dict, "pg-image", Env("LW_HARNESS_PG_IMAGE"), "postgres:17-alpine", s => s),
            SeedTenants = Get(dict, "seed", Env("LW_HARNESS_SEED"), 2, int.Parse),
        };

        static string? Env(string name) => Environment.GetEnvironmentVariable(name);

        static T Get<T>(Dictionary<string, string> dict, string key, string? env, T fallback, Func<string, T> parse)
        {
            if (dict.TryGetValue(key, out var v))
            {
                return parse(v);
            }
            if (!string.IsNullOrEmpty(env))
            {
                return parse(env);
            }
            return fallback;
        }
    }
}
