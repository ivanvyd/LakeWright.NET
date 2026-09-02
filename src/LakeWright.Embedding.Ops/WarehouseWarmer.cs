using System.Net.Http.Headers;
using LakeWright.Core.Features;

namespace LakeWright.Embedding.Ops;

/// <summary>Starts a configured SQL warehouse before a portal opens a dashboard.</summary>
public interface IWarehouseWarmer
{
    /// <summary>Requests a start only when warming is enabled and the warehouse is not rate-limited.</summary>
    Task<WarehouseWarmResult> WarmAsync(string warehouseId, CancellationToken cancellationToken = default);
}

/// <summary>The outcome of a warehouse-warm request.</summary>
public enum WarehouseWarmResult
{
    Disabled,
    RateLimited,
    Requested,
}

/// <summary>Controls the optional pre-warm path. It is disabled unless a host opts in.</summary>
public sealed class WarehouseWarmOptions
{
    /// <summary>Enables requests to the warehouse start endpoint.</summary>
    public bool Enabled { get; set; }

    /// <summary>Minimum time between start requests for the same warehouse.</summary>
    public TimeSpan MinimumInterval { get; set; } = TimeSpan.FromMinutes(5);

    internal void Validate()
    {
        if (MinimumInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumInterval), "MinimumInterval must be positive.");
        }
    }
}

/// <summary>Host-replaceable rate limiter for warehouse pre-warm requests.</summary>
public interface IWarehouseWarmLimiter
{
    /// <summary>Returns whether a request is admitted and records a successful admission.</summary>
    bool TryAcquire(string warehouseId, DateTimeOffset now, TimeSpan minimumInterval);
}

internal interface IWarehouseWarmApi
{
    Task RequestStartAsync(string warehouseId, CancellationToken cancellationToken);
}

internal sealed class WarehouseWarmer(
    IWarehouseWarmApi api,
    IWarehouseWarmLimiter limiter,
    WarehouseWarmOptions options,
    TimeProvider timeProvider,
    ILakeWrightFeatureGate features) : IWarehouseWarmer
{
    public async Task<WarehouseWarmResult> WarmAsync(string warehouseId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(warehouseId);
        options.Validate();
        if (!options.Enabled)
        {
            return WarehouseWarmResult.Disabled;
        }

        features.EnsureEnabled(LakeWrightFeatures.Operations);
        if (!limiter.TryAcquire(warehouseId, timeProvider.GetUtcNow(), options.MinimumInterval))
        {
            return WarehouseWarmResult.RateLimited;
        }

        await api.RequestStartAsync(warehouseId, cancellationToken).ConfigureAwait(false);
        return WarehouseWarmResult.Requested;
    }
}

internal sealed class MemoryWarehouseWarmLimiter : IWarehouseWarmLimiter
{
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _lastRequested = new(StringComparer.Ordinal);

    public bool TryAcquire(string warehouseId, DateTimeOffset now, TimeSpan minimumInterval)
    {
        lock (_lock)
        {
            if (_lastRequested.TryGetValue(warehouseId, out var lastRequested)
                && now - lastRequested < minimumInterval)
            {
                return false;
            }

            _lastRequested[warehouseId] = now;
            return true;
        }
    }
}

internal sealed class DatabricksWarehouseWarmApi(HttpClient http, IOpsTokenBroker tokens) : IWarehouseWarmApi
{
    public async Task RequestStartAsync(string warehouseId, CancellationToken cancellationToken)
    {
        var token = await tokens.AcquireAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"api/2.0/sql/warehouses/{Uri.EscapeDataString(warehouseId)}/start");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new DashboardVerificationApiException((int)response.StatusCode);
        }
    }
}
