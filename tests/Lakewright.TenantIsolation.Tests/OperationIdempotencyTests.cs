using System.Net;
using System.Net.Http.Json;
using Lakewright.AspNetCore;
using Lakewright.Core.Tenancy;
using Lakewright.Multitenancy;
using Lakewright.Multitenancy.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using static Lakewright.TenantIsolation.Tests.TestApi;

namespace Lakewright.TenantIsolation.Tests;

/// <summary>
/// A retried start must not become a second Databricks run.
/// </summary>
/// <remarks>
/// The cost of getting this wrong is not a duplicate row. It is a duplicate job, billed to the
/// tenant, for work it asked for once — and nothing downstream can detect it, because the second
/// operation is indistinguishable from a genuine second request.
/// </remarks>
[Collection(nameof(PostgresTests))]
public class OperationIdempotencyTests(PostgresFixture postgres)
{
    private static readonly TenantId AcmeId = TenantId.Parse("0198f000-0000-7000-8000-0000000000aa");

    private static TenantContext Ctx() => TenantContextFactory.ForTenant(AcmeId, "analytics");

    [Fact]
    public async Task A_retry_returns_the_original_operation()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        await Seed(db, ct);
        var store = new OperationStore(db);

        // Act — the same caller, the same key, twice.
        var first = await store.CreateAsync(Ctx(), Alice, "analysis", "retry-me", ct);
        var second = await store.CreateAsync(Ctx(), Alice, "analysis", "retry-me", ct);

        // Assert
        second.Id.ShouldBe(first.Id);
        (await db.Operations.CountAsync(ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Simultaneous_retries_produce_one_operation()
    {
        // Arrange — separate contexts, because one context serialises the writes and the test
        // would pass without the unique index doing anything.
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        await Seed(db, ct);
        var connectionString = db.Database.GetConnectionString()!;

        await using var a = PostgresFixture.ContextFor(connectionString);
        await using var b = PostgresFixture.ContextFor(connectionString);

        // Act
        var results = await Task.WhenAll(
            new OperationStore(a).CreateAsync(Ctx(), Alice, "analysis", "same-key", ct),
            new OperationStore(b).CreateAsync(Ctx(), Alice, "analysis", "same-key", ct));

        // Assert — one row, and both callers were told about the same one.
        results[0].Id.ShouldBe(results[1].Id);
        (await db.Operations.CountAsync(ct)).ShouldBe(1);
    }

    [Fact]
    public async Task One_members_key_does_not_collide_with_anothers()
    {
        // Arrange — Alice and Vera are both at Acme and pick the same unremarkable key.
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        await Seed(db, ct);
        var store = new OperationStore(db);

        // Act
        var alices = await store.CreateAsync(Ctx(), Alice, "analysis", "nightly", ct);
        var veras = await store.CreateAsync(Ctx(), Vera, "analysis", "nightly", ct);

        // Assert
        veras.Id.ShouldNotBe(alices.Id);
        (await db.Operations.CountAsync(ct)).ShouldBe(2);
    }

    [Fact]
    public async Task Callers_that_send_no_key_never_collide()
    {
        // Arrange — the filtered unique index must ignore nulls, or the second start fails.
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        await Seed(db, ct);
        var store = new OperationStore(db);

        // Act
        var first = await store.CreateAsync(Ctx(), Alice, "analysis", clientRequestId: null, ct);
        var second = await store.CreateAsync(Ctx(), Alice, "analysis", clientRequestId: null, ct);

        // Assert
        second.Id.ShouldNotBe(first.Id);
        (await db.Operations.CountAsync(ct)).ShouldBe(2);
    }

    [Fact]
    public async Task A_retried_post_returns_the_same_operation()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (host, client) = await StartAsync(postgres);
        using var _h = host;

        // Act
        var first = await client.SendAsync(Start("analysis", "job-42"), ct);
        var second = await client.SendAsync(Start("analysis", "job-42"), ct);

        // Assert — 202 both times, and the caller cannot tell which one it was.
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        second.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await Body(second, ct)).Id.ShouldBe((await Body(first, ct)).Id);
    }

    [Fact]
    public async Task Reusing_a_key_for_different_content_is_refused()
    {
        // Arrange — answering with the stored operation would answer a question nobody asked.
        var ct = TestContext.Current.CancellationToken;
        var (host, client) = await StartAsync(postgres);
        using var _h = host;

        // Act
        await client.SendAsync(Start("analysis", "job-43"), ct);
        var mismatched = await client.SendAsync(Start("export", "job-43"), ct);

        // Assert
        mismatched.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task An_oversized_key_is_rejected_rather_than_truncated()
    {
        // Arrange — a silently truncated key would dedupe requests that are not duplicates.
        var ct = TestContext.Current.CancellationToken;
        var (host, client) = await StartAsync(postgres);
        using var _h = host;

        var oversized = new string('k', OperationStore.MaxClientRequestIdLength + 1);

        // Act
        var response = await client.SendAsync(Start("analysis", oversized), ct);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static HttpRequestMessage Start(string kind, string key)
    {
        var request = As(Alice, HttpMethod.Post, $"/organizations/{AcmeId.Value}/operations");
        request.Headers.Add(OperationEndpoints.IdempotencyKeyHeader, key);
        request.Content = JsonContent.Create(new StartOperationRequest(kind));
        return request;
    }

    private static async Task<OperationResponse> Body(HttpResponseMessage response, CancellationToken ct) =>
        (await response.Content.ReadFromJsonAsync<OperationResponse>(ct))!;

    private static async Task Seed(LakewrightDbContext db, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        db.Organizations.Add(new Organization
        {
            Id = AcmeId,
            Name = "Acme",
            Slug = "acme",
            CreatedAt = now,
            Schema = UnityCatalogIdentifier.SchemaForTenant(AcmeId),
            State = OrganizationState.Active
        });

        await db.SaveChangesAsync(ct);
    }
}
