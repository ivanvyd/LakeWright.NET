using System.Net;
using System.Text.Json;
using LakeWright.Core.Tenancy;
using LakeWright.Embedding;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// The dashboard token exchange, against a fake workspace.
/// </summary>
/// <remarks>
/// These assert the shape of the three legs, which is the part the vendor's own samples show
/// without explaining. The live counterpart in <c>LiveEmbeddingTests</c> proves the same exchange
/// against a real workspace; this proves it without one, so CI covers the logic.
/// </remarks>
[Trait("Category", "TenantIsolation")]
public class EmbedTokenBrokerTests : IDisposable
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
    public async Task The_tenant_becomes_external_value_and_the_caller_never_chooses_it()
    {
        // Arrange
        StubExchange();
        var broker = Broker();

        // Act
        await broker.IssueAsync(Tenant(AcmeId), "dash-1", "viewer-7", TestContext.Current.CancellationToken);

        // Assert — external_value is the tenant, taken from the context rather than a parameter.
        var tokenInfo = _workspace.LogEntries.Single(e =>
            e.RequestMessage!.Path.Contains("tokeninfo", StringComparison.Ordinal));

        tokenInfo.RequestMessage!.Query!["external_value"].ShouldBe([AcmeId.ToString()]);
        tokenInfo.RequestMessage!.Query!["external_viewer_id"].ShouldBe(["viewer-7"]);
    }

    [Fact]
    public async Task Two_tenants_get_two_different_filters()
    {
        // Arrange
        StubExchange();
        var broker = Broker();

        // Act — same dashboard, same viewer handle, different tenant.
        await broker.IssueAsync(Tenant(AcmeId), "dash-1", "viewer-7", TestContext.Current.CancellationToken);
        await broker.IssueAsync(Tenant(GlobexId), "dash-1", "viewer-7", TestContext.Current.CancellationToken);

        // Assert
        var values = _workspace.LogEntries
            .Where(e => e.RequestMessage!.Path.Contains("tokeninfo", StringComparison.Ordinal))
            .Select(e => e.RequestMessage!.Query!["external_value"].Single())
            .ToArray();

        values.ShouldBe([AcmeId.ToString(), GlobexId.ToString()]);
    }

    [Fact]
    public async Task Authorization_details_travel_as_a_json_string_and_other_fields_verbatim()
    {
        // Arrange
        StubExchange();
        var broker = Broker();

        // Act
        await broker.IssueAsync(Tenant(AcmeId), "dash-1", "viewer-7", TestContext.Current.CancellationToken);

        // Assert — the second POST to the token endpoint is the scoped-token request.
        var form = ScopedTokenForm();

        form["grant_type"].ShouldBe("client_credentials");
        form["scope"].ShouldBe("dashboards:read");

        // Re-serialised, because it arrives as JSON and has to travel as a JSON string.
        var details = JsonDocument.Parse(form["authorization_details"]);
        details.RootElement[0].GetProperty("type").GetString().ShouldBe("workspace_resource");
    }

    [Fact]
    public async Task A_field_databricks_adds_later_is_forwarded_rather_than_dropped()
    {
        // Arrange — tokeninfo carries something this library has never heard of.
        StubExchange(extraTokenInfo: "\"a_future_claim\": \"keep-me\",");
        var broker = Broker();

        // Act
        await broker.IssueAsync(Tenant(AcmeId), "dash-1", "viewer-7", TestContext.Current.CancellationToken);

        // Assert — a dropped field would produce a token that is valid and wrongly scoped, which
        // is worse than a failure, so the response is copied blind rather than mapped.
        ScopedTokenForm()["a_future_claim"].ShouldBe("keep-me");
    }

    [Fact]
    public async Task The_viewer_identifiers_are_held_under_the_documented_ceiling()
    {
        // Arrange — Databricks caps external_viewer_id and external_value at 1 KB together, and
        // fails the exchange with a message naming neither of them.
        StubExchange();
        var broker = Broker();
        var oversized = new string('v', 1024);

        // Act
        var act = async () => await broker.IssueAsync(
            Tenant(AcmeId), "dash-1", oversized, TestContext.Current.CancellationToken);

        // Assert
        var error = await act.ShouldThrowAsync<ArgumentException>();
        error.Message.ShouldContain("1024");
    }

    [Fact]
    public async Task A_refusal_carries_the_reason_databricks_gave()
    {
        // Arrange — the status code alone does not distinguish "not published" from "no CAN RUN".
        _workspace
            .Given(Request.Create().WithPath("/oidc/v1/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.Unauthorized)
                .WithBody("""{"error":"invalid_client","error_description":"secret expired"}"""));

        var broker = Broker();

        // Act
        var act = async () => await broker.IssueAsync(
            Tenant(AcmeId), "dash-1", "viewer-7", TestContext.Current.CancellationToken);

        // Assert
        var error = await act.ShouldThrowAsync<HttpRequestException>();
        error.Message.ShouldContain("secret expired");
    }

    private Dictionary<string, string> ScopedTokenForm()
    {
        var body = _workspace.LogEntries
            .Where(e => e.RequestMessage!.Path == "/oidc/v1/token")
            .Select(e => e.RequestMessage!.Body ?? string.Empty)
            .Last(b => b.Contains("authorization_details", StringComparison.Ordinal));

        return body
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
    }

    private void StubExchange(string extraTokenInfo = "")
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
                .WithBody($$"""
                {
                  "scope": "dashboards:read",
                  {{extraTokenInfo}}
                  "authorization_details": [{"type":"workspace_resource","dashboard_id":"dash-1"}]
                }
                """));
    }

    private DashboardTokenBroker Broker()
    {
        var http = new HttpClient { BaseAddress = new Uri(_workspace.Urls[0] + "/") };
        var options = Options.Create(new DashboardEmbeddingOptions
        {
            WorkspaceUrl = _workspace.Urls[0],
            ClientId = "sp-id",
            ClientSecret = "sp-secret",
        });

        return new DashboardTokenBroker(http, options, new FakeTimeProvider());
    }

    private static TenantContext Tenant(TenantId id) =>
        TenantContextFactory.ForTenant(id, "lakewright_dev", "analytics");
}
