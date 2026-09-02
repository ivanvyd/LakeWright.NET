using LakeWright.Core.Features;
using LakeWright.Embedding;
using LakeWright.Embedding.Ops;
using Microsoft.Extensions.Time.Testing;

namespace LakeWright.TenantIsolation.Tests;

[Trait("Category", "TenantIsolation")]
public sealed class DashboardMetadataCatalogTests
{
    [Fact]
    public async Task Reuses_cached_draft_metadata_until_its_short_expiry()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var api = new FakeApi();
        var catalog = Catalog(api, new FakeCatalog(), clock);

        (await catalog.GetDraftAsync("dash-1", TestContext.Current.CancellationToken)).SerializedDashboard.ShouldContain("orders");
        await catalog.GetDraftAsync("dash-1", TestContext.Current.CancellationToken);

        api.DraftCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Walks_all_catalog_pages_then_caches_the_complete_list()
    {
        var fakeCatalog = new FakeCatalog();
        var catalog = Catalog(new FakeApi(), fakeCatalog, new FakeTimeProvider(DateTimeOffset.UnixEpoch));

        var all = await catalog.ListAllAsync(TestContext.Current.CancellationToken);
        var cached = await catalog.ListAllAsync(TestContext.Current.CancellationToken);

        all.Select(dashboard => dashboard.Id).ShouldBe(["dash-1", "dash-2"]);
        cached.Select(dashboard => dashboard.Id).ShouldBe(["dash-1", "dash-2"]);
        fakeCatalog.Calls.ShouldBe(2);
    }

    private static DashboardMetadataCatalog Catalog(FakeApi api, FakeCatalog dashboards, FakeTimeProvider clock) => new(
        api,
        dashboards,
        new MemoryDashboardMetadataCache(clock),
        new DashboardMetadataCacheOptions(),
        clock,
        new AlwaysOnFeatureGate());

    private sealed class FakeApi : IDashboardMetadataApi
    {
        public int DraftCalls { get; private set; }

        public Task<DashboardDraftMetadata> GetDraftAsync(string dashboardId, CancellationToken cancellationToken)
        {
            DraftCalls++;
            return Task.FromResult(new DashboardDraftMetadata("dash-1", "Orders", "warehouse", "etag", "/Shared", null, """{"datasets":[{"name":"orders","query":"SELECT 1"}]}"""));
        }

        public Task<DashboardPublishedMetadata> GetPublishedAsync(string dashboardId, CancellationToken cancellationToken) =>
            Task.FromResult(new DashboardPublishedMetadata(dashboardId, "Orders", "warehouse", false, null));
    }

    private sealed class FakeCatalog : IDashboardCatalog
    {
        public int Calls { get; private set; }

        public Task<DashboardCatalogPage> ListAsync(int? pageSize = null, string? pageToken = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(pageToken is null
                ? new DashboardCatalogPage([new DashboardSummary("dash-1", "One", null, null)], "next")
                : new DashboardCatalogPage([new DashboardSummary("dash-2", "Two", null, null)], null));
        }
    }
}
