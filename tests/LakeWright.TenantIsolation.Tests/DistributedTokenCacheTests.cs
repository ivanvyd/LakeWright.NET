using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using LakeWright.Caching.Distributed;
using LakeWright.Core.Tenancy;
using LakeWright.Embedding;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace LakeWright.TenantIsolation.Tests;

public sealed class DistributedTokenCacheTests
{
    [Fact]
    public async Task A_workspace_token_cache_hit_collapses_concurrent_requests()
    {
        var cache = new TestDistributedCache();
        var tokens = new DistributedWorkspaceTokenCache(cache, TimeProvider.System, new EphemeralDataProtectionProvider());
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
        var tokens = new DistributedEmbedTokenCache(cache, TimeProvider.System, new EphemeralDataProtectionProvider());
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

    [Fact]
    public async Task The_cache_never_holds_a_usable_token()
    {
        var cache = new TestDistributedCache();
        var tokens = new DistributedWorkspaceTokenCache(cache, TimeProvider.System, new EphemeralDataProtectionProvider());

        await tokens.GetOrAddAsync("client-id", _ => Mint("bearer-secret-value"), TestContext.Current.CancellationToken);

        var stored = Encoding.UTF8.GetString(cache.Values.Single());
        stored.ShouldNotContain("bearer-secret-value");
    }

    [Fact]
    public async Task Replicas_that_share_a_key_ring_share_entries()
    {
        var cache = new TestDistributedCache();
        var keyRing = new EphemeralDataProtectionProvider();
        var calls = 0;

        await new DistributedWorkspaceTokenCache(cache, TimeProvider.System, keyRing).GetOrAddAsync(
            "client-id", _ => Mint($"token-{++calls}"), TestContext.Current.CancellationToken);
        var other = await new DistributedWorkspaceTokenCache(cache, TimeProvider.System, keyRing).GetOrAddAsync(
            "client-id", _ => Mint($"token-{++calls}"), TestContext.Current.CancellationToken);

        calls.ShouldBe(1);
        other.AccessToken.ShouldBe("token-1");
    }

    [Fact]
    public async Task An_entry_from_another_key_ring_is_a_miss_that_gets_replaced()
    {
        var cache = new TestDistributedCache();
        var calls = 0;

        await new DistributedWorkspaceTokenCache(cache, TimeProvider.System, new EphemeralDataProtectionProvider())
            .GetOrAddAsync("client-id", _ => Mint($"token-{++calls}"), TestContext.Current.CancellationToken);
        var foreign = await new DistributedWorkspaceTokenCache(cache, TimeProvider.System, new EphemeralDataProtectionProvider())
            .GetOrAddAsync("client-id", _ => Mint($"token-{++calls}"), TestContext.Current.CancellationToken);

        calls.ShouldBe(2);
        foreign.AccessToken.ShouldBe("token-2");
    }

    [Fact]
    public async Task A_plaintext_entry_written_before_encryption_is_not_trusted()
    {
        var cache = new TestDistributedCache();
        var tokens = new DistributedWorkspaceTokenCache(cache, TimeProvider.System, new EphemeralDataProtectionProvider());
        await tokens.GetOrAddAsync("client-id", _ => Mint("first"), TestContext.Current.CancellationToken);
        var key = cache.Keys.Single();
        cache.Set(key, JsonSerializer.SerializeToUtf8Bytes(new EmbedToken("planted", DateTimeOffset.UtcNow.AddMinutes(5))), new());

        var token = await tokens.GetOrAddAsync("client-id", _ => Mint("fresh"), TestContext.Current.CancellationToken);

        token.AccessToken.ShouldBe("fresh");
    }

    [Fact]
    public async Task An_entry_copied_from_another_key_is_not_trusted()
    {
        var cache = new TestDistributedCache();
        var keyRing = new EphemeralDataProtectionProvider();
        var workspace = new DistributedWorkspaceTokenCache(cache, TimeProvider.System, keyRing);
        var embed = new DistributedEmbedTokenCache(cache, TimeProvider.System, keyRing);
        var ct = TestContext.Current.CancellationToken;
        var tenantA = new EmbedCacheKey(TenantId.New(), "scope", "dashboard", "viewer-a");
        var tenantB = new EmbedCacheKey(TenantId.New(), "scope", "dashboard", "viewer-b");

        await workspace.GetOrAddAsync("client-id", _ => Mint("workspace-token"), ct);
        var workspaceKey = cache.Keys.Single();
        await embed.GetOrAddAsync(tenantA, _ => Mint("tenant-a-token"), ct);
        var tenantAKey = cache.Keys.Single(key => key != workspaceKey && key.Contains(":embed:", StringComparison.Ordinal));
        await embed.GetOrAddAsync(tenantB, _ => Mint("tenant-b-token"), ct);
        var tenantBKey = cache.Keys.Single(key => key != workspaceKey && key != tenantAKey && key.Contains(":embed:", StringComparison.Ordinal));

        cache.Set(tenantBKey, cache.Get(workspaceKey)!, new());
        (await embed.GetOrAddAsync(tenantB, _ => Mint("fresh-1"), ct)).AccessToken.ShouldBe("fresh-1");

        cache.Set(tenantBKey, cache.Get(tenantAKey)!, new());
        (await embed.GetOrAddAsync(tenantB, _ => Mint("fresh-2"), ct)).AccessToken.ShouldBe("fresh-2");
    }

    [Fact]
    public void Registration_works_without_the_host_configuring_data_protection()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDistributedCache, TestDistributedCache>();
        services.AddLakeWrightDistributedTokenCaches();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IWorkspaceTokenCache>().ShouldBeOfType<DistributedWorkspaceTokenCache>();
        provider.GetRequiredService<IEmbedTokenCache>().ShouldBeOfType<DistributedEmbedTokenCache>();
    }

    private static ValueTask<EmbedToken> Mint(string value) =>
        new(new EmbedToken(value, DateTimeOffset.UtcNow.AddMinutes(5)));

    private sealed class TestDistributedCache : IDistributedCache
    {
        private readonly ConcurrentDictionary<string, byte[]> _entries = new(StringComparer.Ordinal);

        public IReadOnlyCollection<string> Keys => _entries.Keys.ToArray();

        public IReadOnlyCollection<byte[]> Values => _entries.Values.ToArray();

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
