using LakeWright.Embedding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace LakeWright.TenantIsolation.Tests;

[Trait("Category", "TenantIsolation")]
public sealed class OpsTokenBrokerCacheTests : IDisposable
{
    private readonly WireMockServer _workspace = WireMockServer.Start();

    [Fact]
    public async Task A_second_ops_request_reuses_the_workspace_token()
    {
        _workspace
            .Given(Request.Create().WithPath("/oidc/v1/token").UsingPost())
            .RespondWith(Response.Create()
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"access_token":"ops-token","expires_in":3600}"""));
        var time = new FakeTimeProvider();
        var broker = new OpsTokenBroker(
            new HttpClient { BaseAddress = new Uri(_workspace.Urls[0] + "/") },
            Options.Create(new DashboardOpsOptions
            {
                WorkspaceUrl = _workspace.Urls[0],
                ClientId = "ops-client-id",
                ClientSecret = "ops-client-secret",
            }),
            time,
            new MemoryOpsTokenCache(time));

        await broker.AcquireAsync(TestContext.Current.CancellationToken);
        await broker.AcquireAsync(TestContext.Current.CancellationToken);

        _workspace.LogEntries.Count(entry => entry.RequestMessage!.Path == "/oidc/v1/token").ShouldBe(1);
    }

    [Fact]
    public async Task A_second_catalog_page_makes_zero_token_calls()
    {
        StubTokenAndDashboardList();
        var services = new ServiceCollection();
        services.AddLakeWrightDashboardOps(Configuration());
        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<IDashboardCatalog>();

        await catalog.ListAsync(cancellationToken: TestContext.Current.CancellationToken);
        await catalog.ListAsync(pageToken: "page-2", cancellationToken: TestContext.Current.CancellationToken);

        _workspace.LogEntries.Count(entry => entry.RequestMessage!.Path == "/oidc/v1/token").ShouldBe(1);
    }

    [Fact]
    public async Task Dashboard_ops_uses_an_application_provided_time_provider_for_cache_expiry()
    {
        StubTokenAndDashboardList();
        var time = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddLakeWrightDashboardOps(Configuration());
        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<IDashboardCatalog>();

        await catalog.ListAsync(cancellationToken: TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(31));
        await catalog.ListAsync(cancellationToken: TestContext.Current.CancellationToken);

        provider.GetRequiredService<TimeProvider>().ShouldBeSameAs(time);
        _workspace.LogEntries.Count(entry => entry.RequestMessage!.Path == "/oidc/v1/token").ShouldBe(2);
    }

    private IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DashboardOps:WorkspaceUrl"] = _workspace.Urls[0],
            ["DashboardOps:ClientId"] = "ops-client-id",
            ["DashboardOps:ClientSecret"] = "ops-client-secret",
        })
        .Build();

    private void StubTokenAndDashboardList()
    {
        _workspace
            .Given(Request.Create().WithPath("/oidc/v1/token").UsingPost())
            .RespondWith(Response.Create()
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"access_token":"ops-token","expires_in":60}"""));
        _workspace
            .Given(Request.Create().WithPath("/api/2.0/lakeview/dashboards").UsingGet())
            .RespondWith(Response.Create()
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"dashboards":[],"next_page_token":null}"""));
    }

    public void Dispose()
    {
        _workspace.Stop();
        _workspace.Dispose();
        GC.SuppressFinalize(this);
    }
}
