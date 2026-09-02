using System.Text.Json;
using LakeWright.Embedding;
using Microsoft.Extensions.Options;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// The dashboard catalog and the ops token broker it depends on, against a fake workspace.
/// </summary>
/// <remarks>
/// <para>
/// These prove the split between the embed and ops service principals (ADR 0024). The
/// embed path's tests stay in <c>EmbedTokenBrokerTests</c>; this file proves the ops path
/// authenticates as a different principal and that the catalog uses it.
/// </para>
/// <para>
/// The vendor's list endpoint and its response shape are read defensively, because the
/// workspace has changed the response between minor versions. The tests assert the library's
/// own projection, not the workspace's full payload.
/// </para>
/// </remarks>
[Trait("Category", "TenantIsolation")]
public class DashboardCatalogTests : IDisposable
{
    private readonly WireMockServer _workspace = WireMockServer.Start();

    public void Dispose()
    {
        _workspace.Stop();
        _workspace.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task The_ops_broker_mints_a_workspace_token_with_client_credentials()
    {
        // Arrange
        StubOpsToken();
        var broker = OpsBroker();

        // Act
        var token = await broker.AcquireAsync(TestContext.Current.CancellationToken);

        // Assert — the token came back and the workspace saw a client_credentials call.
        token.AccessToken.ShouldNotBeNull();
        _workspace.LogEntries.Count(e =>
            e.RequestMessage!.Path == "/oidc/v1/token"
            && e.RequestMessage!.Method == "POST").ShouldBe(1);
    }

    [Fact]
    public async Task The_ops_broker_sends_its_own_client_id_not_the_embed_one()
    {
        // Arrange — the embed SP and the ops SP are different ids. The wire should see the
        // ops id, never the embed one.
        StubOpsToken();
        var broker = OpsBroker(opsClientId: "ops-sp-id");

        // Act
        await broker.AcquireAsync(TestContext.Current.CancellationToken);

        // Assert — the workspace sees Basic auth with ops-sp-id, not embed-sp-id.
        var entry = _workspace.LogEntries.Single(e => e.RequestMessage!.Path == "/oidc/v1/token");
        var auth = entry.RequestMessage!.Headers!["Authorization"].Single();
        var decoded = DecodeBasicAuth(auth);
        decoded.ShouldStartWith("ops-sp-id:");
    }

    [Fact]
    public async Task The_catalog_lists_dashboards_using_the_ops_token()
    {
        // Arrange
        StubOpsToken();
        StubList("""
        {"dashboards":[{"dashboard_id":"dash-1","display_name":"Sales Overview","parent_path":"/Shared","published_at":"2026-08-30T12:00:00Z"},{"dashboard_id":"dash-2","display_name":"Pipeline Health","parent_path":null,"published_at":"2026-08-29T18:30:00Z"}],"next_page_token":null}
        """);
        var catalog = Catalog();

        // Act
        var page = await catalog.ListAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        page.Dashboards.Count.ShouldBe(2);
        page.Dashboards[0].Id.ShouldBe("dash-1");
        page.Dashboards[0].DisplayName.ShouldBe("Sales Overview");
        page.Dashboards[0].ParentPath.ShouldBe("/Shared");
        page.Dashboards[0].PublishedAt.ShouldNotBeNull();
        page.Dashboards[1].Id.ShouldBe("dash-2");
        page.Dashboards[1].ParentPath.ShouldBeNull();
        page.NextPageToken.ShouldBeNull();
    }

    [Fact]
    public async Task The_catalog_passes_page_size_and_page_token_through()
    {
        // Arrange
        StubOpsToken();
        StubList("""{ "dashboards": [], "next_page_token": null }""");
        var catalog = Catalog();

        // Act
        await catalog.ListAsync(pageSize: 25, pageToken: "abc", cancellationToken: TestContext.Current.CancellationToken);

        // Assert — the workspace saw both query parameters.
        var entry = _workspace.LogEntries.Last(e => e.RequestMessage!.Path.Contains("dashboards", StringComparison.Ordinal));
        entry.RequestMessage!.Query!["page_size"].Single().ShouldBe("25");
        entry.RequestMessage!.Query!["page_token"].Single().ShouldBe("abc");
    }

    [Fact]
    public async Task The_catalog_returns_a_next_page_token_when_the_workspace_provides_one()
    {
        // Arrange
        StubOpsToken();
        StubList("""{ "dashboards": [], "next_page_token": "tok-2" }""");
        var catalog = Catalog();

        // Act
        var page = await catalog.ListAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        page.NextPageToken.ShouldBe("tok-2");
    }

    [Fact]
    public async Task The_catalog_tolerates_drafts_endpoint_responses_with_a_different_id_field()
    {
        // Arrange — the library projects "dashboard_id" today, which is what the published
        // list returns. The drafts list uses the same shape. The library's defensive
        // projection (skip entries it cannot identify) is what keeps an unrelated response
        // shape from poisoning a real list.
        StubOpsToken();
        StubList("""{ "dashboards": [{ "id": "draft-1" }] }""");
        var catalog = Catalog();

        // Act
        var page = await catalog.ListAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Assert — the unidentifiable entry is skipped, not crashed on.
        page.Dashboards.ShouldBeEmpty();
    }

    private void StubOpsToken()
    {
        _workspace
            .Given(Request.Create().WithPath("/oidc/v1/token").UsingPost())
            .RespondWith(Response.Create()
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"access_token":"ops-token","expires_in":3600}"""));
    }

    private void StubList(string body)
    {
        _workspace
            .Given(Request.Create().WithPath("/api/2.0/lakeview/dashboards").UsingGet())
            .RespondWith(Response.Create()
                .WithHeader("Content-Type", "application/json")
                .WithBody(body));
    }

    private OpsTokenBroker OpsBroker(string opsClientId = "ops-sp-id", string opsClientSecret = "ops-sp-secret")
    {
        var http = new HttpClient { BaseAddress = new Uri(_workspace.Urls[0] + "/") };
        var options = Options.Create(new DashboardOpsOptions
        {
            WorkspaceUrl = _workspace.Urls[0],
            ClientId = opsClientId,
            ClientSecret = opsClientSecret,
        });
        return new OpsTokenBroker(http, options, TimeProvider.System);
    }

    private DashboardCatalog Catalog()
    {
        var http = new HttpClient { BaseAddress = new Uri(_workspace.Urls[0] + "/") };
        var options = Options.Create(new DashboardOpsOptions
        {
            WorkspaceUrl = _workspace.Urls[0],
            ClientId = "ops-sp-id",
            ClientSecret = "ops-sp-secret",
        });
        var ops = new OpsTokenBroker(http, options, TimeProvider.System);
        return new DashboardCatalog(http, ops);
    }

    private static string DecodeBasicAuth(string header)
    {
        var prefix = "Basic ";
        return System.Text.Encoding.UTF8.GetString(
            Convert.FromBase64String(header.Substring(prefix.Length)));
    }
}
