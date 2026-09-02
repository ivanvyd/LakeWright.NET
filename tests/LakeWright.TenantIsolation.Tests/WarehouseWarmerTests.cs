using LakeWright.Core.Features;
using LakeWright.Embedding.Ops;
using Microsoft.Extensions.Time.Testing;

namespace LakeWright.TenantIsolation.Tests;

[Trait("Category", "TenantIsolation")]
public sealed class WarehouseWarmerTests
{
    [Fact]
    public async Task Is_disabled_by_default_without_waking_compute()
    {
        var api = new FakeApi();
        var warmer = Create(api, new WarehouseWarmOptions());

        var result = await warmer.WarmAsync("warehouse-1", TestContext.Current.CancellationToken);

        result.ShouldBe(WarehouseWarmResult.Disabled);
        api.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Requests_at_most_once_per_warehouse_during_the_interval()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var api = new FakeApi();
        var warmer = Create(api, new WarehouseWarmOptions { Enabled = true, MinimumInterval = TimeSpan.FromMinutes(5) }, clock);

        (await warmer.WarmAsync("warehouse-1", TestContext.Current.CancellationToken)).ShouldBe(WarehouseWarmResult.Requested);
        (await warmer.WarmAsync("warehouse-1", TestContext.Current.CancellationToken)).ShouldBe(WarehouseWarmResult.RateLimited);
        clock.Advance(TimeSpan.FromMinutes(5));
        (await warmer.WarmAsync("warehouse-1", TestContext.Current.CancellationToken)).ShouldBe(WarehouseWarmResult.Requested);

        api.Calls.ShouldBe(2);
    }

    private static WarehouseWarmer Create(FakeApi api, WarehouseWarmOptions options, FakeTimeProvider? clock = null) => new(
        api,
        new MemoryWarehouseWarmLimiter(),
        options,
        clock ?? new FakeTimeProvider(DateTimeOffset.UnixEpoch),
        new AlwaysOnFeatureGate());

    private sealed class FakeApi : IWarehouseWarmApi
    {
        public int Calls { get; private set; }

        public Task RequestStartAsync(string warehouseId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }
}
