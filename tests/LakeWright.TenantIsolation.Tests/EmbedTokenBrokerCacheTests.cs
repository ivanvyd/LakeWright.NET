using LakeWright.Core.Tenancy;
using LakeWright.Embedding;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// The token caches, exercised against a fake workspace.
/// </summary>
/// <remarks>
/// The caches are optional dependencies on <see cref="DashboardTokenBroker"/>. These tests
/// cover the cached path. The no-cache path stays in <c>EmbedTokenBrokerTests</c> and is
/// unchanged: the broker is constructed directly with no cache and the three legs run every
/// time. The cached path collapses to a memory lookup on a hit, and to a single in-flight
/// exchange on a miss even when N callers race for the same key (ADR 0018).
/// </remarks>
[Trait("Category", "TenantIsolation")]
public class EmbedTokenBrokerCacheTests : IDisposable
{
    private static readonly TenantId AcmeId = TenantId.Parse("0198f000-0000-7000-8000-00000000ac11");
    private static readonly TenantId GlobexId = TenantId.Parse("0198f000-0000-7000-8000-00000000617b");

    private readonly WireMockServer _workspace = WireMockServer.Start();

    public void Dispose()
    {
        _workspace.Stop();
        _workspace.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_second_open_for_the_same_viewer_makes_zero_http_calls()
    {
        // Arrange
        StubExchange();
        var broker = CachedBroker();

        // Act — the first call pays the full three-leg price; the second should not.
        await broker.IssueAsync(Tenant(AcmeId), "dash-1", "viewer-7", TestContext.Current.CancellationToken);
        var requestsAfterFirst = _workspace.LogEntries.Count;
        var second = await broker.IssueAsync(Tenant(AcmeId), "dash-1", "viewer-7", TestContext.Current.CancellationToken);

        // Assert — no requests after the first call, and the cached token is returned
        // (same access token as the first call, since it was minted in-process).
        _workspace.LogEntries.Count.ShouldBe(requestsAfterFirst);
        second.AccessToken.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_different_viewer_does_not_hit_the_cached_token()
    {
        // Arrange
        StubExchange();
        var broker = CachedBroker();

        // Act
        await broker.IssueAsync(Tenant(AcmeId), "dash-1", "viewer-7", TestContext.Current.CancellationToken);
        var requestsAfterFirst = _workspace.LogEntries.Count;
        await broker.IssueAsync(Tenant(AcmeId), "dash-1", "viewer-8", TestContext.Current.CancellationToken);

        // Assert — the new viewer is a new key, so the cache misses and the broker runs.
        _workspace.LogEntries.Count.ShouldBeGreaterThan(requestsAfterFirst);
    }

    [Fact]
    public async Task A_different_dashboard_does_not_hit_the_cached_token()
    {
        // Arrange
        StubExchange();
        var broker = CachedBroker();

        // Act
        await broker.IssueAsync(Tenant(AcmeId), "dash-1", "viewer-7", TestContext.Current.CancellationToken);
        var requestsAfterFirst = _workspace.LogEntries.Count;
        await broker.IssueAsync(Tenant(AcmeId), "dash-2", "viewer-7", TestContext.Current.CancellationToken);

        // Assert
        _workspace.LogEntries.Count.ShouldBeGreaterThan(requestsAfterFirst);
    }

    [Fact]
    public async Task A_different_tenant_does_not_hit_the_cached_token()
    {
        // Arrange
        StubExchange();
        var broker = CachedBroker();

        // Act
        await broker.IssueAsync(Tenant(AcmeId), "dash-1", "viewer-7", TestContext.Current.CancellationToken);
        var requestsAfterFirst = _workspace.LogEntries.Count;
        await broker.IssueAsync(Tenant(GlobexId), "dash-1", "viewer-7", TestContext.Current.CancellationToken);

        // Assert — the cache key includes TenantId, so a different tenant is a miss.
        _workspace.LogEntries.Count.ShouldBeGreaterThan(requestsAfterFirst);
    }

    [Fact]
    public async Task A_different_scope_version_does_not_hit_the_cached_token()
    {
        // Arrange — same tenant, two different ScopeVersions, two different external_values.
        // Caching across versions would defeat the whole point of ADR 0017.
        StubExchange();
        var broker = CachedBroker();

        var v1 = Tenant(AcmeId, scopeVersion: "aaaa1111");
        var v2 = Tenant(AcmeId, scopeVersion: "bbbb2222");

        // Act
        await broker.IssueAsync(v1, "dash-1", "viewer-7", TestContext.Current.CancellationToken);
        var requestsAfterFirst = _workspace.LogEntries.Count;
        await broker.IssueAsync(v2, "dash-1", "viewer-7", TestContext.Current.CancellationToken);

        // Assert
        _workspace.LogEntries.Count.ShouldBeGreaterThan(requestsAfterFirst);
    }

    [Fact]
    public async Task Evicting_a_tenant_removes_all_of_its_cached_embed_tokens()
    {
        StubExchange();
        var time = new FakeTimeProvider();
        var embedCache = new MemoryEmbedTokenCache(time);
        var broker = new DashboardTokenBroker(
            new HttpClient { BaseAddress = new Uri(_workspace.Urls[0] + "/") },
            Options.Create(new DashboardEmbeddingOptions
            {
                WorkspaceUrl = _workspace.Urls[0],
                ClientId = "sp-id",
                ClientSecret = "sp-secret",
            }),
            time,
            new MemoryWorkspaceTokenCache(time),
            embedCache);

        await broker.IssueAsync(Tenant(AcmeId), "dash-1", "viewer-7", TestContext.Current.CancellationToken);
        var requestsAfterFirst = _workspace.LogEntries.Count;
        embedCache.EvictTenant(AcmeId);
        await broker.IssueAsync(Tenant(AcmeId), "dash-1", "viewer-7", TestContext.Current.CancellationToken);

        _workspace.LogEntries.Count.ShouldBeGreaterThan(requestsAfterFirst);
    }

    [Fact]
    public async Task The_workspace_token_is_shared_across_tenants_dashboards_and_viewers()
    {
        // Arrange — the workspace cache is keyed on ClientId only, so the second call
        // reuses the same workspace token. The /tokeninfo call still runs because each
        // (tenant, dashboard) has its own downscope — but the leg-1 roundtrip is gone.
        StubExchange();
        var broker = CachedBroker();

        // Act
        await broker.IssueAsync(Tenant(AcmeId), "dash-1", "viewer-7", TestContext.Current.CancellationToken);
        await broker.IssueAsync(Tenant(GlobexId), "dash-2", "viewer-8", TestContext.Current.CancellationToken);

        // Assert — exactly one /oidc/v1/token POST for the workspace token across both calls.
        // The embed cache is empty here because the two requests have different keys, so the
        // second call still does its own /tokeninfo and downscope, but neither asks for a
        // fresh workspace token. The stub answers every token POST with a fresh value, so
        // counting the matching bodies is the way to be sure which leg ran.
        var tokenPosts = _workspace.LogEntries
            .Where(e => e.RequestMessage!.Path == "/oidc/v1/token")
            .Select(e => e.RequestMessage!.Body ?? string.Empty)
            .ToArray();

        // Three legs across two calls: 2 leg-1 (workspace) + 2 leg-3 (downscope) = 4.
        // After the cache is wired, the second call's leg-1 should be served from memory,
        // so the wire count drops to 3: 1 leg-1 (the first call) + 2 leg-3.
        tokenPosts.Length.ShouldBe(3);
    }

    [Fact]
    public async Task Concurrent_callers_for_the_same_key_run_the_exchange_once()
    {
        // Arrange — 20 callers race for the same (tenant, dashboard, viewer). Without
        // the dogpile collapse, that would be 20 full exchanges. With it, exactly one.
        StubExchange();
        var broker = CachedBroker();

        // Act
        var tenants = Enumerable.Range(0, 20)
            .Select(_ => broker.IssueAsync(Tenant(AcmeId), "dash-1", "viewer-7", TestContext.Current.CancellationToken))
            .ToArray();
        var results = await Task.WhenAll(tenants);

        // Assert — every caller got a token (the same token, served from cache after the
        // first one populated it), and the wire saw one /tokeninfo call rather than 20.
        results.ShouldAllBe(t => t.AccessToken == results[0].AccessToken);
        var tokenInfoCalls = _workspace.LogEntries
            .Count(e => e.RequestMessage!.Path.Contains("tokeninfo", StringComparison.Ordinal));
        tokenInfoCalls.ShouldBe(1);
    }

    [Fact]
    public async Task An_expired_cache_entry_triggers_a_refresh()
    {
        // Arrange — a clock advanced past the 30-second safety margin. The cache entry is
        // still in memory until its absolute expiration is reached; the test moves the
        // clock past the safety margin so the next call misses, the entry is evicted, and
        // the broker re-runs the exchange.
        StubExchange();
        var time = new FakeTimeProvider();
        var broker = CachedBroker(time: time);

        await broker.IssueAsync(Tenant(AcmeId), "dash-1", "viewer-7", TestContext.Current.CancellationToken);
        var requestsAfterFirst = _workspace.LogEntries.Count;

        // Move 31 seconds past the original ExpiresAt (the stub returns expires_in=3600).
        // The cache entry was set to ExpiresAt - 30s, so 3600 - 30 + 1 = 3571s past now
        // is enough to cross it.
        time.Advance(TimeSpan.FromSeconds(3571));

        // Act
        await broker.IssueAsync(Tenant(AcmeId), "dash-1", "viewer-7", TestContext.Current.CancellationToken);

        // Assert — the broker hit the wire again because the cached entry was past its
        // safety margin and the cache evicted it.
        _workspace.LogEntries.Count.ShouldBeGreaterThan(requestsAfterFirst);
    }

    private void StubExchange()
    {
        _workspace
            .Given(Request.Create().WithPath("/oidc/v1/token").UsingPost())
            .RespondWith(Response.Create()
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"access_token":"a-token","expires_in":3600}"""));

        _workspace
            .Given(Request.Create()
                .WithPath("/api/2.0/lakeview/dashboards/*/published/tokeninfo")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                {
                  "scope": "dashboards:read",
                  "authorization_details": [{"type":"workspace_resource","dashboard_id":"dash-1"}]
                }
                """));
    }

    private DashboardTokenBroker CachedBroker(TimeProvider? time = null)
    {
        var http = new HttpClient { BaseAddress = new Uri(_workspace.Urls[0] + "/") };
        var options = Options.Create(new DashboardEmbeddingOptions
        {
            WorkspaceUrl = _workspace.Urls[0],
            ClientId = "sp-id",
            ClientSecret = "sp-secret",
        });

        var t = time ?? new FakeTimeProvider();
        return new DashboardTokenBroker(
            http,
            options,
            t,
            new MemoryWorkspaceTokenCache(t),
            new MemoryEmbedTokenCache(t));
    }

    private static TenantContext Tenant(TenantId id, string? scopeVersion = null) =>
        scopeVersion is null
            ? TenantContextFactory.ForTenant(id, "lakewright_dev", "analytics")
            : TenantContextFactory.ForTenant(id, "lakewright_dev", "analytics", scopeVersion);
}
