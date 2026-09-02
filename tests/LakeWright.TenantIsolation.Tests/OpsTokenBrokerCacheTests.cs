using LakeWright.Embedding;
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

    public void Dispose()
    {
        _workspace.Stop();
        _workspace.Dispose();
        GC.SuppressFinalize(this);
    }
}
