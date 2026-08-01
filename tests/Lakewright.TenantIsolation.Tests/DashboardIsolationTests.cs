using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Signalboard;

namespace Lakewright.TenantIsolation.Tests;

/// <summary>
/// The Blazor dashboard is a second way into tenant data, and it is held to the same rules.
/// </summary>
/// <remarks>
/// The API tests prove isolation through routing, authentication and authorization. None of that
/// runs for a Blazor page: it renders server-side and could query the tables directly. So the risk
/// is not that the guarded path breaks, it is that an unguarded one grows beside it — which is
/// exactly the shape of the bug this whole project exists to demonstrate.
///
/// These drive the sample itself rather than a stand-in, because the wiring is the thing under
/// test.
/// </remarks>
[Trait("Category", "TenantIsolation")]
[Collection(nameof(PostgresTests))]
public sealed class DashboardIsolationTests(PostgresFixture postgres) : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _app;

    public async ValueTask InitializeAsync()
    {
        await using var seed = await postgres.NewDatabaseAsync();
        var connectionString = seed.Database.GetConnectionString()!;

        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Lakewright", connectionString));
    }

    public ValueTask DisposeAsync()
    {
        _app?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>A client that keeps its cookie, so signing in sticks.</summary>
    private HttpClient Browser() => _app!.CreateClient(new WebApplicationFactoryClientOptions
    {
        HandleCookies = true,
        AllowAutoRedirect = false
    });

    private static async Task SignInAsync(HttpClient browser, string principalId, CancellationToken ct)
    {
        var response = await browser.PostAsync(
            new Uri("/signin", UriKind.Relative),
            new FormUrlEncodedContent([new KeyValuePair<string, string>("principal", principalId)]),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task The_dashboard_shows_only_the_signed_in_organization()
    {
        // Arrange — Alice starts work at Acme.
        var ct = TestContext.Current.CancellationToken;
        var alice = Browser();
        await SignInAsync(alice, DemoTenants.Alice, ct);

        var start = await alice.PostAsJsonAsync(
            new Uri($"/organizations/{DemoTenants.Acme.Value}/operations", UriKind.Relative),
            new { kind = "analysis" }, ct);
        start.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        // Act — Bob opens his own dashboard.
        var bob = Browser();
        await SignInAsync(bob, DemoTenants.Bob, ct);
        var bobsPage = await bob.GetStringAsync(new Uri("/operations", UriKind.Relative), ct);
        var alicesPage = await alice.GetStringAsync(new Uri("/operations", UriKind.Relative), ct);

        // Assert — Bob's dashboard is scoped to Globex and holds none of Acme's work.
        alicesPage.ShouldContain("Acme Logistics");
        bobsPage.ShouldContain("Globex Freight");
        bobsPage.ShouldNotContain("Acme Logistics");
        bobsPage.ShouldContain("Nothing yet");
    }

    [Fact]
    public async Task The_address_the_dashboard_prints_resolves_for_its_owner()
    {
        // Arrange — the dashboard prints an address and invites you to try it as someone from the
        // other organization. It printed the page route rather than the API route, which 404s for
        // everyone including the owner, so the demonstration proved nothing and looked like it
        // proved everything. A wrong address here is worse than a broken one.
        var ct = TestContext.Current.CancellationToken;
        var alice = Browser();
        await SignInAsync(alice, DemoTenants.Alice, ct);

        await alice.PostAsJsonAsync(
            new Uri($"/organizations/{DemoTenants.Acme.Value}/operations", UriKind.Relative),
            new { kind = "analysis" }, ct);

        var page = await alice.GetStringAsync(new Uri("/operations", UriKind.Relative), ct);
        var address = Regex.Match(page, @"/organizations/[0-9a-f-]+/operations/[0-9a-f-]+").Value;

        // Act
        var asOwner = await alice.GetAsync(new Uri(address, UriKind.Relative), ct);

        var bob = Browser();
        await SignInAsync(bob, DemoTenants.Bob, ct);
        var asOutsider = await bob.GetAsync(new Uri(address, UriKind.Relative), ct);

        // Assert — the 404 only means something because the same address answers 200 for its owner.
        address.ShouldNotBeEmpty();
        asOwner.StatusCode.ShouldBe(HttpStatusCode.OK);
        asOutsider.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_viewer_is_refused_the_start_control_and_the_action_behind_it()
    {
        // Arrange — hiding the button is not an authorization control, so the refusal must survive
        // a caller who ignores the UI entirely.
        var ct = TestContext.Current.CancellationToken;
        var vera = Browser();
        await SignInAsync(vera, DemoTenants.Vera, ct);

        // Act
        var page = await vera.GetStringAsync(new Uri("/operations", UriKind.Relative), ct);
        var posting = await vera.PostAsJsonAsync(
            new Uri($"/organizations/{DemoTenants.Acme.Value}/operations", UriKind.Relative),
            new { kind = "analysis" }, ct);

        // Assert
        page.ShouldContain("Starting work needs Member or above");
        posting.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_dashboard_is_not_served_to_an_anonymous_visitor()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var stranger = Browser();

        // Act
        var response = await stranger.GetAsync(new Uri("/operations", UriKind.Relative), ct);

        // Assert — redirected to sign in rather than rendered empty.
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location?.OriginalString.ShouldContain("/signin");
    }

    [Fact]
    public async Task The_sign_in_form_will_not_mint_an_arbitrary_subject()
    {
        // Arrange — the header scheme trusts any subject by design. The cookie path has no reason
        // to, and a sign-in page that does becomes an account-creation endpoint.
        var ct = TestContext.Current.CancellationToken;
        var browser = Browser();

        // Act
        var response = await browser.PostAsync(
            new Uri("/signin", UriKind.Relative),
            new FormUrlEncodedContent([new KeyValuePair<string, string>("principal", "demo|intruder")]),
            ct);

        var dashboard = await browser.GetAsync(new Uri("/operations", UriKind.Relative), ct);

        // Assert — sent back to the sign-in page, and still not signed in.
        response.Headers.Location?.OriginalString.ShouldContain("/signin");
        dashboard.StatusCode.ShouldBe(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task The_api_answers_401_rather_than_redirecting_a_client_to_a_sign_in_page()
    {
        // Arrange — cookie authentication redirects by default, which would hand an API client a
        // page of HTML and a 200 it cannot interpret.
        var ct = TestContext.Current.CancellationToken;
        var client = Browser();

        // Act
        var response = await client.GetAsync(
            new Uri($"/organizations/{DemoTenants.Acme.Value}/operations/{Guid.CreateVersion7()}",
                UriKind.Relative), ct);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
