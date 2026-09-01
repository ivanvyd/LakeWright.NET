using System.Net;
using System.Net.Http.Json;
using LakeWright.Core.Cost;
using LakeWright.Multitenancy;
using LakeWright.Multitenancy.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using static LakeWright.TenantIsolation.Tests.TestApi;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// The /cost endpoint, exercised through the actual route, auth, and tenant middleware.
/// </summary>
/// <remarks>
/// The service-level <see cref="CostAttributionTests"/> cover the SQL and the math. These cover
/// the bits that only the HTTP path can break: that the route sits under the tenant middleware
/// (so a Globex caller can never see Acme's data), that the Viewer policy actually applies, and
/// that the 31-day window cap and inverted-window check are enforced before the implementation
/// is called. A maintainer who changes <c>MapLakeWrightCost</c> and forgets
/// <c>.RequireAuthorization</c> would pass the service tests and break these.
/// </remarks>
[Trait("Category", "TenantIsolation")]
[Collection(nameof(PostgresTests))]
public class CostEndpointTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_viewer_can_read_their_organizations_cost()
    {
        // Arrange — Vera is a Viewer at Acme; the endpoint requires Viewer.
        var ct = TestContext.Current.CancellationToken;
        var (host, client) = await StartAsync(postgres);
        using var _h = host;

        // Act
        var response = await client.SendAsync(
            As(Vera, HttpMethod.Get, $"/organizations/{AcmeId.Value}/cost"), ct);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var summary = await response.Content.ReadFromJsonAsync<TenantCostSummary>(ct);
        summary.ShouldNotBeNull();
        summary!.TenantId.ShouldBe(AcmeId);
        summary.Source.ShouldBe(CostSource.Proxy);
    }

    [Fact]
    public async Task A_cross_tenant_read_answers_404_not_403()
    {
        // Arrange — Bob (Globex Admin) asks for Acme's cost.
        var ct = TestContext.Current.CancellationToken;
        var (host, client) = await StartAsync(postgres);
        using var _h = host;

        // Act
        var response = await client.SendAsync(
            As(Bob, HttpMethod.Get, $"/organizations/{AcmeId.Value}/cost"), ct);

        // Assert — 404, not 403. A 403 would confirm that Acme has a cost endpoint at this id.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_unauthenticated_request_is_refused()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (host, client) = await StartAsync(postgres);
        using var _h = host;

        // Act
        var request = new HttpRequestMessage(HttpMethod.Get, $"/organizations/{AcmeId.Value}/cost");
        var response = await client.SendAsync(request, ct);

        // Assert — the fallback policy requires authentication, so the request is refused
        // before the tenant middleware or the endpoint runs.
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_window_longer_than_31_days_is_rejected_with_400()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (host, client) = await StartAsync(postgres);
        using var _h = host;

        // Act — a 60-day window. Use Uri.EscapeDataString so the '+' in the ISO 8601
        // timezone offset does not get decoded as a space on the server.
        var now = DateTimeOffset.UtcNow;
        var from = Uri.EscapeDataString(now.AddDays(-60).ToString("o"));
        var until = Uri.EscapeDataString(now.ToString("o"));
        var response = await client.SendAsync(
            As(Vera, HttpMethod.Get, $"/organizations/{AcmeId.Value}/cost?from={from}&until={until}"), ct);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_inverted_window_is_rejected_with_400()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (host, client) = await StartAsync(postgres);
        using var _h = host;

        // Act — from is after until. URL-encode the ISO 8601 strings.
        var now = DateTimeOffset.UtcNow;
        var from = Uri.EscapeDataString(now.ToString("o"));
        var until = Uri.EscapeDataString(now.AddDays(-1).ToString("o"));
        var response = await client.SendAsync(
            As(Vera, HttpMethod.Get, $"/organizations/{AcmeId.Value}/cost?from={from}&until={until}"), ct);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_window_ending_in_the_distant_future_is_rejected_with_400()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (host, client) = await StartAsync(postgres);
        using var _h = host;

        // Act — a 30-day window anchored in the year 3000. The width is within the cap;
        // the position is the issue. URL-encode the ISO 8601 strings.
        var from = DateTimeOffset.Parse("3000-01-01T00:00:00Z", null);
        var until = DateTimeOffset.Parse("3000-01-31T00:00:00Z", null);
        var fromEnc = Uri.EscapeDataString(from.ToString("o"));
        var untilEnc = Uri.EscapeDataString(until.ToString("o"));
        var response = await client.SendAsync(
            As(Vera, HttpMethod.Get, $"/organizations/{AcmeId.Value}/cost?from={fromEnc}&until={untilEnc}"), ct);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_billing_provider_failure_answers_502_without_the_provider_message()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var cost = Substitute.For<ICostAttribution>();
        cost.ResolveAsync(
                Arg.Any<LakeWright.Core.Tenancy.TenantContext>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<TenantCostSummary>>(_ => throw new BillingUsageException(
                "PERMISSION_DENIED",
                isTransient: false));
        var (host, client) = await StartAsync(
            postgres,
            services => services.AddScoped(_ => cost));
        using var _h = host;

        var response = await client.SendAsync(
            As(Vera, HttpMethod.Get, $"/organizations/{AcmeId.Value}/cost"),
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        body.ShouldContain("PERMISSION_DENIED");
        body.ShouldNotContain("system.billing.usage");
    }

    [Fact]
    public async Task An_oversized_billing_report_answers_422_with_the_enforced_run_limit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var cost = Substitute.For<ICostAttribution>();
        cost.ResolveAsync(
                Arg.Any<LakeWright.Core.Tenancy.TenantContext>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<TenantCostSummary>>(_ => throw new BillingUsageException(
                "REPORT_TOO_LARGE",
                isTransient: false));
        var (host, client) = await StartAsync(
            postgres,
            services => services.AddScoped(_ => cost));
        using var _h = host;

        var response = await client.SendAsync(
            As(Vera, HttpMethod.Get, $"/organizations/{AcmeId.Value}/cost"),
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        body.ShouldContain("REPORT_TOO_LARGE");
        body.ShouldContain(BillingUsageLimits.MaxJobRunsPerReport.ToString());
    }
}
