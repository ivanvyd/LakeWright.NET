using System.Text.Json;
using LakeWright.Embedding;
using LakeWright.Embedding.Ops;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LakeWright.Caching.Distributed;

/// <summary>Options for sharing short-lived dashboard metadata across application replicas.</summary>
public sealed class DistributedDashboardMetadataCacheOptions
{
    /// <summary>Key namespace shared only by replicas connected to the same operations workspace.</summary>
    public string Prefix { get; set; } = "lakewright:dashboard-metadata";

    internal void Validate() => ArgumentException.ThrowIfNullOrWhiteSpace(Prefix);
}

/// <summary>Distributed implementation of the operations-only dashboard metadata cache.</summary>
public sealed class DistributedDashboardMetadataCache(
    IDistributedCache cache,
    TimeProvider timeProvider,
    DistributedDashboardMetadataCacheOptions? options = null) : IDashboardMetadataCache
{
    private readonly DistributedDashboardMetadataCacheOptions _options = options ?? new();

    public ValueTask<DashboardDraftMetadata?> GetDraftAsync(string dashboardId, CancellationToken cancellationToken = default) =>
        GetAsync<DashboardDraftMetadata>($"draft:{dashboardId}", cancellationToken);

    public ValueTask SetDraftAsync(string dashboardId, DashboardDraftMetadata value, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
        SetAsync($"draft:{dashboardId}", value, expiresAt, cancellationToken);

    public ValueTask<DashboardPublishedMetadata?> GetPublishedAsync(string dashboardId, CancellationToken cancellationToken = default) =>
        GetAsync<DashboardPublishedMetadata>($"published:{dashboardId}", cancellationToken);

    public ValueTask SetPublishedAsync(string dashboardId, DashboardPublishedMetadata value, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
        SetAsync($"published:{dashboardId}", value, expiresAt, cancellationToken);

    public ValueTask<IReadOnlyList<DashboardSummary>?> GetAllAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<DashboardSummary>>("all", cancellationToken);

    public ValueTask SetAllAsync(IReadOnlyList<DashboardSummary> value, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
        SetAsync("all", value, expiresAt, cancellationToken);

    private async ValueTask<T?> GetAsync<T>(string suffix, CancellationToken cancellationToken) where T : class
    {
        _options.Validate();
        var bytes = await cache.GetAsync(Key(suffix), cancellationToken).ConfigureAwait(false);
        var entry = bytes is null ? null : JsonSerializer.Deserialize<Entry<T>>(bytes);
        return entry is not null && entry.ExpiresAt > timeProvider.GetUtcNow() ? entry.Value : null;
    }

    private async ValueTask SetAsync<T>(string suffix, T value, DateTimeOffset expiresAt, CancellationToken cancellationToken) where T : class
    {
        _options.Validate();
        await cache.SetAsync(Key(suffix), JsonSerializer.SerializeToUtf8Bytes(new Entry<T>(value, expiresAt)), new DistributedCacheEntryOptions
        {
            AbsoluteExpiration = expiresAt,
        }, cancellationToken).ConfigureAwait(false);
    }

    private string Key(string suffix) => $"{_options.Prefix}:{Hash(suffix)}";

    private static string Hash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record Entry<T>(T Value, DateTimeOffset ExpiresAt);
}

/// <summary>Registers the shared metadata cache after the host has registered <see cref="IDistributedCache"/>.</summary>
public static class DistributedDashboardMetadataCacheServiceCollectionExtensions
{
    public static IServiceCollection AddLakeWrightDistributedDashboardMetadataCache(
        this IServiceCollection services,
        Action<DistributedDashboardMetadataCacheOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new DistributedDashboardMetadataCacheOptions();
        configure?.Invoke(options);
        options.Validate();
        services.TryAddSingleton(TimeProvider.System);
        services.RemoveAll<IDashboardMetadataCache>();
        services.AddSingleton<IDashboardMetadataCache>(provider => new DistributedDashboardMetadataCache(
            provider.GetRequiredService<IDistributedCache>(), provider.GetRequiredService<TimeProvider>(), options));
        return services;
    }
}
