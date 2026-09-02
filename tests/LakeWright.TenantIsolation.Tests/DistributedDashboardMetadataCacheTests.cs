using LakeWright.Caching.Distributed;
using LakeWright.Embedding.Ops;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Time.Testing;

namespace LakeWright.TenantIsolation.Tests;

[Trait("Category", "TenantIsolation")]
public sealed class DistributedDashboardMetadataCacheTests
{
    [Fact]
    public async Task Shares_metadata_until_the_explicit_absolute_expiry()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var cache = new DistributedDashboardMetadataCache(new FakeCache(), clock);
        var draft = new DashboardDraftMetadata("dash-1", "One", "warehouse", null, null, null, "{}");

        await cache.SetDraftAsync("dash-1", draft, clock.GetUtcNow().AddMinutes(1), TestContext.Current.CancellationToken);
        (await cache.GetDraftAsync("dash-1", TestContext.Current.CancellationToken)).ShouldBe(draft);
        clock.Advance(TimeSpan.FromMinutes(1));

        (await cache.GetDraftAsync("dash-1", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    private sealed class FakeCache : IDistributedCache
    {
        private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);
        public byte[]? Get(string key) => _values.GetValueOrDefault(key);
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) => _values.Remove(key);
        public Task RemoveAsync(string key, CancellationToken token = default) { Remove(key); return Task.CompletedTask; }
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _values[key] = value;
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) { Set(key, value, options); return Task.CompletedTask; }
    }
}
