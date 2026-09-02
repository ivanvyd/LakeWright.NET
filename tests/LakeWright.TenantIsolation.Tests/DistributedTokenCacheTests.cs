using System.Collections.Concurrent;
using LakeWright.Caching.Distributed;
using LakeWright.Core.Tenancy;
using LakeWright.Embedding;
using Microsoft.Extensions.Caching.Distributed;

namespace LakeWright.TenantIsolation.Tests;

public sealed class DistributedTokenCacheTests
{
    [Fact]
    public async Task A_workspace_token_cache_hit_collapses_concurrent_requests()
    {
        var cache = new TestDistributedCache();
        var tokens = new DistributedWorkspaceTokenCache(cache, TimeProvider.System);
        var calls = 0;

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => tokens.GetOrAddAsync(
            "client-id",
            _ => new ValueTask<EmbedToken>(new EmbedToken($"token-{Interlocked.Increment(ref calls)}", DateTimeOffset.UtcNow.AddMinutes(5))),
            TestContext.Current.CancellationToken)));

        calls.ShouldBe(1);
        results.Select(token => token.AccessToken).Distinct().ShouldBe(["token-1"]);
    }

    [Fact]
    public async Task Embed_eviction_bumps_a_distributed_generation_without_exposing_request_identifiers()
    {
        var cache = new TestDistributedCache();
        var tokens = new DistributedEmbedTokenCache(cache, TimeProvider.System);
        var tenant = TenantId.New();
        var key = new EmbedCacheKey(tenant, "scope", "dashboard-sensitive", "viewer-sensitive");
        var calls = 0;

        await tokens.GetOrAddAsync(key, _ => new ValueTask<EmbedToken>(
            new EmbedToken($"token-{Interlocked.Increment(ref calls)}", DateTimeOffset.UtcNow.AddMinutes(5))), TestContext.Current.CancellationToken);
        tokens.EvictTenant(tenant);
        var fresh = await tokens.GetOrAddAsync(key, _ => new ValueTask<EmbedToken>(
            new EmbedToken($"token-{Interlocked.Increment(ref calls)}", DateTimeOffset.UtcNow.AddMinutes(5))), TestContext.Current.CancellationToken);

        calls.ShouldBe(2);
        fresh.AccessToken.ShouldBe("token-2");
        cache.Keys.ShouldAllBe(keyName => !keyName.Contains("sensitive", StringComparison.Ordinal));
    }

    private sealed class TestDistributedCache : IDistributedCache
    {
        private readonly ConcurrentDictionary<string, byte[]> _entries = new(StringComparer.Ordinal);

        public IReadOnlyCollection<string> Keys => _entries.Keys.ToArray();

        public byte[]? Get(string key) => _entries.TryGetValue(key, out var value) ? value : null;

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));

        public void Refresh(string key) { }

        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

        public void Remove(string key) => _entries.TryRemove(key, out _);

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _entries[key] = value;

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }
    }
}
