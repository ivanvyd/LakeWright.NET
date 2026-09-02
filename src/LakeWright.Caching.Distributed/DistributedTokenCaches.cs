using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LakeWright.Core.Tenancy;
using LakeWright.Embedding;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LakeWright.Caching.Distributed;

/// <summary>Configures opt-in distributed token caches.</summary>
/// <remarks>
/// A generic <c>IDistributedCache</c> has no atomic create operation, so this package collapses
/// concurrent misses within one process using bounded lock striping. It reduces repeated exchanges
/// across replicas after the first write, but a host that requires global cold-miss coalescing must
/// add a provider-specific distributed lease around token minting.
/// </remarks>
public sealed class DistributedTokenCacheOptions
{
    public string Prefix { get; set; } = "lakewright";

    public TimeSpan SafetyMargin { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaximumExpiryJitter { get; set; } = TimeSpan.FromSeconds(5);

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Prefix);
        if (SafetyMargin < TimeSpan.Zero || MaximumExpiryJitter < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(SafetyMargin));
        }
    }
}

/// <summary>Stores workspace OAuth tokens in an <see cref="IDistributedCache"/>.</summary>
public sealed class DistributedWorkspaceTokenCache : IWorkspaceTokenCache
{
    private readonly DistributedTokenStore _store;

    public DistributedWorkspaceTokenCache(IDistributedCache cache, TimeProvider time, DistributedTokenCacheOptions? options = null) =>
        _store = new DistributedTokenStore(cache, time, options);

    public Task<EmbedToken> GetOrAddAsync(string clientId, Func<CancellationToken, ValueTask<EmbedToken>> factory, CancellationToken cancellationToken) =>
        _store.GetOrAddAsync("workspace", clientId, factory, cancellationToken);
}

/// <summary>Stores viewer-scoped embed tokens in an <see cref="IDistributedCache"/>.</summary>
public sealed class DistributedEmbedTokenCache : IEmbedTokenCache
{
    private readonly IDistributedCache _cache;
    private readonly DistributedTokenStore _store;
    private readonly string _prefix;

    public DistributedEmbedTokenCache(IDistributedCache cache, TimeProvider time, DistributedTokenCacheOptions? options = null)
    {
        _cache = cache;
        _store = new DistributedTokenStore(cache, time, options);
        _prefix = (options ?? new DistributedTokenCacheOptions()).Prefix;
    }

    public async Task<EmbedToken> GetOrAddAsync(EmbedCacheKey key, Func<CancellationToken, ValueTask<EmbedToken>> factory, CancellationToken cancellationToken)
    {
        var tenantHash = Hash(key.TenantId.ToString());
        var generation = _cache.GetString($"{_prefix}:embed-generation:{tenantHash}") ?? "0";
        return await _store.GetOrAddAsync(
            "embed",
            string.Join('\n', key.TenantId, key.ScopeVersion, key.DashboardId, key.ViewerId, generation),
            factory,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Makes all previously cached tokens for a tenant unreachable on every replica.</summary>
    public void EvictTenant(TenantId tenantId) =>
        _cache.SetString($"{_prefix}:embed-generation:{Hash(tenantId.ToString())}", Guid.NewGuid().ToString("N"));

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

/// <summary>Registers distributed token caches after the host has registered <see cref="IDistributedCache"/>.</summary>
public static class DistributedTokenCacheServiceCollectionExtensions
{
    public static IServiceCollection AddLakeWrightDistributedTokenCaches(
        this IServiceCollection services,
        Action<DistributedTokenCacheOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new DistributedTokenCacheOptions();
        configure?.Invoke(options);
        options.Validate();
        services.TryAddSingleton(TimeProvider.System);
        services.RemoveAll<IWorkspaceTokenCache>();
        services.RemoveAll<IEmbedTokenCache>();
        services.AddSingleton<IWorkspaceTokenCache>(provider => new DistributedWorkspaceTokenCache(
            provider.GetRequiredService<IDistributedCache>(), provider.GetRequiredService<TimeProvider>(), options));
        services.AddSingleton<IEmbedTokenCache>(provider => new DistributedEmbedTokenCache(
            provider.GetRequiredService<IDistributedCache>(), provider.GetRequiredService<TimeProvider>(), options));
        return services;
    }
}

internal sealed class DistributedTokenStore
{
    private static readonly SemaphoreSlim[] Locks = Enumerable.Range(0, 257).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly IDistributedCache _cache;
    private readonly TimeProvider _time;
    private readonly DistributedTokenCacheOptions _options;

    public DistributedTokenStore(IDistributedCache cache, TimeProvider time, DistributedTokenCacheOptions? options)
    {
        _cache = cache;
        _time = time;
        _options = options ?? new DistributedTokenCacheOptions();
        _options.Validate();
    }

    public async Task<EmbedToken> GetOrAddAsync(string category, string sourceKey, Func<CancellationToken, ValueTask<EmbedToken>> factory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        ArgumentNullException.ThrowIfNull(factory);
        var key = $"{_options.Prefix}:{category}:{Hash(sourceKey)}";
        var cached = await TryGetAsync(key, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        var singleFlight = Locks[(int)(BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0) % (uint)Locks.Length)];
        await singleFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = await TryGetAsync(key, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                return cached;
            }

            var token = await factory(cancellationToken).ConfigureAwait(false);
            var expires = token.ExpiresAt - _options.SafetyMargin - Jitter();
            if (expires > _time.GetUtcNow())
            {
                await _cache.SetAsync(key, JsonSerializer.SerializeToUtf8Bytes(token), new DistributedCacheEntryOptions
                {
                    AbsoluteExpiration = expires,
                }, cancellationToken).ConfigureAwait(false);
            }

            return token;
        }
        finally
        {
            singleFlight.Release();
        }
    }

    private async Task<EmbedToken?> TryGetAsync(string key, CancellationToken cancellationToken)
    {
        var bytes = await _cache.GetAsync(key, cancellationToken).ConfigureAwait(false);
        var token = bytes is null ? null : JsonSerializer.Deserialize<EmbedToken>(bytes);
        return token is not null && token.ExpiresAt - _options.SafetyMargin > _time.GetUtcNow() ? token : null;
    }

    private TimeSpan Jitter() => _options.MaximumExpiryJitter <= TimeSpan.Zero
        ? TimeSpan.Zero
        : TimeSpan.FromTicks(Random.Shared.NextInt64(_options.MaximumExpiryJitter.Ticks + 1));

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
