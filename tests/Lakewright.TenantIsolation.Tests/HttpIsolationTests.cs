using System.Net;
using System.Net.Http.Json;
using Lakewright.AspNetCore;
using Microsoft.Extensions.Hosting;
using static Lakewright.TenantIsolation.Tests.TestApi;

namespace Lakewright.TenantIsolation.Tests;

/// <summary>
/// Cross-tenant isolation over HTTP, which is where a customer actually meets it.
/// </summary>
/// <remarks>
/// The store-level tests prove a query cannot reach another tenant's rows. These prove the same
/// thing through routing, authentication and authorization, because an endpoint can leak by
/// answering the wrong status code just as easily as by returning the wrong rows.
/// </remarks>
[Trait("Category", "TenantIsolation")]
[Collection(nameof(PostgresTests))]
public class HttpIsolationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task An_operation_belonging_to_another_tenant_is_not_found()
    {
        // Arrange — Alice creates an operation at Acme; Bob knows its id.
        var ct = TestContext.Current.CancellationToken;
        var (host, client) = await StartAsync(postgres);
        using var _h = host;

        var start = As(Alice, HttpMethod.Post, $"/organizations/{AcmeId.Value}/operations");
        start.Content = JsonContent.Create(new StartOperationRequest("analysis"));
        var created = await client.SendAsync(start, ct);
        var operation = await created.Content.ReadFromJsonAsync<OperationResponse>(ct);

        // Act — Bob asks for it under his own organization, and under Acme's.
        var underGlobex = await client.SendAsync(
            As(Bob, HttpMethod.Get, $"/organizations/{GlobexId.Value}/operations/{operation!.Id}"), ct);
        var underAcme = await client.SendAsync(
            As(Bob, HttpMethod.Get, $"/organizations/{AcmeId.Value}/operations/{operation.Id}"), ct);

        // Assert — 404 both ways, never 403. A 403 would confirm the operation exists.
        created.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        underGlobex.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        underAcme.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_member_of_no_organization_cannot_reach_it_at_all()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (host, client) = await StartAsync(postgres);
        using var _h = host;

        // Act
        var response = await client.SendAsync(
            As("auth0|stranger", HttpMethod.Get, $"/organizations/{AcmeId.Value}/operations/{Guid.CreateVersion7()}"), ct);

        // Assert — the tenant middleware refuses before authorization is consulted.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_unauthenticated_request_is_refused_by_the_fallback_policy()
    {
        // Arrange — no principal header, so the stub returns NoResult.
        var ct = TestContext.Current.CancellationToken;
        var (host, client) = await StartAsync(postgres);
        using var _h = host;

        // Act
        var response = await client.GetAsync(
            new Uri($"/organizations/{AcmeId.Value}/operations/{Guid.CreateVersion7()}", UriKind.Relative), ct);

        // Assert — endpoints are protected by default rather than by remembering [Authorize].
        response.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_viewer_can_read_but_cannot_start_work()
    {
        // Arrange — Vera is a Viewer at Acme. Roles are a floor, so Viewer fails a Member policy.
        var ct = TestContext.Current.CancellationToken;
        var (host, client) = await StartAsync(postgres);
        using var _h = host;

        var start = As(Vera, HttpMethod.Post, $"/organizations/{AcmeId.Value}/operations");
        start.Content = JsonContent.Create(new StartOperationRequest("analysis"));

        // Act
        var starting = await client.SendAsync(start, ct);
        var reading = await client.SendAsync(
            As(Vera, HttpMethod.Get, $"/organizations/{AcmeId.Value}/operations/{Guid.CreateVersion7()}"), ct);

        // Assert — starting is forbidden; reading a missing operation is merely not found.
        starting.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        reading.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_malformed_organization_id_is_not_found_rather_than_a_server_error()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (host, client) = await StartAsync(postgres);
        using var _h = host;

        // Act
        var response = await client.SendAsync(
            As(Alice, HttpMethod.Get, $"/organizations/not-a-guid/operations/{Guid.CreateVersion7()}"), ct);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
