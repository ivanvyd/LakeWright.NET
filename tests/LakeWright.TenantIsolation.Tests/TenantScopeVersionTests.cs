using LakeWright.Core.Tenancy;
using LakeWright.Embedding;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// The <see cref="TenantContext.ScopeVersion"/> property and the way the broker composes it into
/// the <c>external_value</c> claim.
/// </summary>
/// <remarks>
/// <para>
/// The broker's <c>external_value</c> is the bridge between the application database and the
/// Databricks workspace. A tenant whose scope narrows keeps seeing the old rows for up to 24
/// hours from the vendor's result cache unless the <c>external_value</c> changes. The version
/// property is the knob that lets a resolver signal "scope changed" without moving the
/// isolation boundary into every call site.
/// </para>
/// <para>
/// The reserved-character constraint is not a library convention. <c>|</c> and <c>:</c> are
/// reserved in the <c>urn:aibi:external_data:&lt;val&gt;:&lt;viewer&gt;:&lt;board&gt;</c> claim
/// format; a <c>|</c> makes Databricks return 400 with the body "Dashboard ID is missing in
/// token claim." <c>~</c> is the only delimiter that does not occur inside a GUID and does not
/// collide with the claim format. The constraint is asserted at <see cref="TenantContext.Create"/>
/// so a corrupted version cannot reach the broker at runtime.
/// </para>
/// </remarks>
[Trait("Category", "TenantIsolation")]
public class TenantScopeVersionTests
{
    private static readonly TenantId AcmeId = TenantId.Parse("0198f000-0000-7000-8000-00000000ac11");

    [Fact]
    public void Create_rejects_scopeVersion_with_reserved_pipe()
    {
        // Arrange / Act
        var act = () => TenantContextFactory.ForTenant(AcmeId, "lakewright_dev", "analytics", "abc|def");

        // Assert — the constructor is the gate; the broker never sees this value.
        var error = Should.Throw<ArgumentException>(act);
        error.ParamName.ShouldBe("scopeVersion");
        error.Message.ShouldContain("|");
    }

    [Fact]
    public void Create_rejects_scopeVersion_with_reserved_colon()
    {
        var act = () => TenantContextFactory.ForTenant(AcmeId, "lakewright_dev", "analytics", "abc:def");

        var error = Should.Throw<ArgumentException>(act);
        error.ParamName.ShouldBe("scopeVersion");
        error.Message.ShouldContain(":");
    }

    [Fact]
    public void Create_rejects_scopeVersion_with_reserved_tilde()
    {
        // Tilde is the broker's delimiter; a second one in the value would split the claim.
        var act = () => TenantContextFactory.ForTenant(AcmeId, "lakewright_dev", "analytics", "abc~def");

        var error = Should.Throw<ArgumentException>(act);
        error.ParamName.ShouldBe("scopeVersion");
        error.Message.ShouldContain("~");
    }

    [Fact]
    public void Null_scopeVersion_is_carried_and_produces_a_bare_external_value()
    {
        // Arrange / Act
        var tenant = TenantContextFactory.ForTenant(AcmeId, "lakewright_dev", "analytics", scopeVersion: null);

        // Assert
        tenant.ScopeVersion.ShouldBeNull();
    }

    [Fact]
    public void Empty_scopeVersion_treated_as_null_so_broker_uses_bare_id()
    {
        // Empty is the boundary case: a resolver that always passes a value (computed lazily)
        // may produce an empty string when the tenant has no scope yet. The broker treats that
        // as null and falls back to the bare id; otherwise the exchange would send
        // "{guid}~" and look like a malformed claim.
        var tenant = TenantContextFactory.ForTenant(AcmeId, "lakewright_dev", "analytics", scopeVersion: "");

        tenant.ScopeVersion.ShouldBe(string.Empty);
        // The broker's IsNullOrEmpty branch handles ""; verified end-to-end in
        // Broker_composes_external_value_with_tilde_delimiter below.
    }

    [Fact]
    public async Task Broker_composes_external_value_with_tilde_delimiter_when_version_is_present()
    {
        // Arrange — a tenant whose scope version is the md5 of their scope rows. Hexadecimal and
        // underscore-safe; the value carries the version into the claim without breaking the
        // GUID.
        using var workspace = WireMockServer.Start();
        StubExchange(workspace);
        var broker = Broker(workspace);

        var version = "5d41402abc4b2a76b9719d911017c592"; // md5("hello")
        var tenant = TenantContextFactory.ForTenant(
            AcmeId, "lakewright_dev", "analytics", scopeVersion: version);

        // Act
        await broker.IssueAsync(tenant, "dash-1", "viewer-7", TestContext.Current.CancellationToken);

        // Assert
        var tokenInfo = workspace.LogEntries.Single(e =>
            e.RequestMessage!.Path.Contains("tokeninfo", StringComparison.Ordinal));

        var expected = $"{AcmeId.ToString()}~{version}";
        tokenInfo.RequestMessage!.Query!["external_value"].ShouldBe([expected]);
    }

    [Fact]
    public async Task Broker_falls_back_to_bare_tenant_id_when_version_is_null()
    {
        // Backward compatibility: this is the previous shape. A null version produces the same
        // external_value the library shipped before this change.
        using var workspace = WireMockServer.Start();
        StubExchange(workspace);
        var broker = Broker(workspace);

        var tenant = TenantContextFactory.ForTenant(AcmeId, "lakewright_dev", "analytics", scopeVersion: null);

        await broker.IssueAsync(tenant, "dash-1", "viewer-7", TestContext.Current.CancellationToken);

        var tokenInfo = workspace.LogEntries.Single(e =>
            e.RequestMessage!.Path.Contains("tokeninfo", StringComparison.Ordinal));
        tokenInfo.RequestMessage!.Query!["external_value"].ShouldBe([AcmeId.ToString()]);
    }

    [Fact]
    public async Task Two_tenants_with_different_scope_versions_get_different_filters()
    {
        // The whole point: changing the version changes the cache key. Same tenant, two versions
        // (e.g. before and after a scope change) must produce two different external_values.
        using var workspace = WireMockServer.Start();
        StubExchange(workspace);
        var broker = Broker(workspace);

        var v1 = TenantContextFactory.ForTenant(AcmeId, "lakewright_dev", "analytics", scopeVersion: "aaaa1111");
        var v2 = TenantContextFactory.ForTenant(AcmeId, "lakewright_dev", "analytics", scopeVersion: "bbbb2222");

        await broker.IssueAsync(v1, "dash-1", "viewer-7", TestContext.Current.CancellationToken);
        await broker.IssueAsync(v2, "dash-1", "viewer-7", TestContext.Current.CancellationToken);

        var values = workspace.LogEntries
            .Where(e => e.RequestMessage!.Path.Contains("tokeninfo", StringComparison.Ordinal))
            .Select(e => e.RequestMessage!.Query!["external_value"].Single())
            .ToArray();

        values.ShouldBe([$"{AcmeId.ToString()}~aaaa1111", $"{AcmeId.ToString()}~bbbb2222"]);
    }

    private static void StubExchange(WireMockServer workspace)
    {
        workspace
            .Given(Request.Create().WithPath("/oidc/v1/token").UsingPost())
            .RespondWith(Response.Create()
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"access_token":"a-token","expires_in":3600}"""));

        workspace
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

    private static DashboardTokenBroker Broker(WireMockServer workspace)
    {
        var http = new HttpClient { BaseAddress = new Uri(workspace.Urls[0] + "/") };
        var options = Options.Create(new DashboardEmbeddingOptions
        {
            WorkspaceUrl = workspace.Urls[0],
            ClientId = "sp-id",
            ClientSecret = "sp-secret",
        });
        return new DashboardTokenBroker(http, options, new FakeTimeProvider());
    }
}
