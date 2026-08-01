using Lakewright.Core.Tenancy;
using Lakewright.Multitenancy;
using Lakewright.Multitenancy.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Lakewright.TenantIsolation.Tests.TestApi;

namespace Lakewright.TenantIsolation.Tests;

/// <summary>
/// Real actions must leave audit rows.
/// </summary>
/// <remarks>
/// The append-only tests next door prove a row cannot be amended once written. They passed while
/// nothing wrote one, so the SOC 2 mapping claimed an audit trail the product did not produce. These
/// close that gap from the other side: they assert the rows exist after an action, and say nothing
/// about tamper-proofing.
/// </remarks>
[Collection(nameof(PostgresTests))]
public class AuditTrailTests(PostgresFixture postgres)
{
    private static TenantContext Ctx() => TenantContextFactory.ForTenant(AcmeId, "analytics");

    [Fact]
    public async Task Starting_an_operation_is_recorded()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SeededAsync();
        var store = new OperationStore(db, new AuditLog(db));

        // Act
        var operation = await store.CreateAsync(Ctx(), Alice, "analysis", clientRequestId: null, ct);

        // Assert
        var audit = await db.AuditEvents.SingleAsync(ct);
        audit.Action.ShouldBe(AuditActions.OperationStarted);
        audit.OrganizationId.ShouldBe(AcmeId);
        audit.PrincipalId.ShouldBe(Alice);
        audit.ResourceId.ShouldBe(operation.Id.ToString());
    }

    [Fact]
    public async Task A_replayed_start_is_recorded_once()
    {
        // Arrange — the second call starts nothing, so a second row would describe an event that
        // never happened.
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SeededAsync();
        var store = new OperationStore(db, new AuditLog(db));

        // Act
        await store.CreateAsync(Ctx(), Alice, "analysis", "once", ct);
        await store.CreateAsync(Ctx(), Alice, "analysis", "once", ct);

        // Assert
        (await db.AuditEvents.CountAsync(ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Completing_an_operation_is_recorded_against_the_worker()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SeededAsync();
        var store = new OperationStore(db, new AuditLog(db));
        var operation = await store.CreateAsync(Ctx(), Alice, "analysis", clientRequestId: null, ct);

        // Act
        await store.CompleteAsync(AcmeId, operation.Id, OperationState.Succeeded, null, ct);

        // Assert — attributed to the worker, not to whoever started it hours earlier.
        var audit = await db.AuditEvents
            .SingleAsync(a => a.Action == AuditActions.OperationCompleted, ct);
        audit.PrincipalId.ShouldBe(SystemPrincipal.Worker);
        audit.Detail.ShouldNotBeNull().ShouldContain(nameof(OperationState.Succeeded));
    }

    [Fact]
    public async Task A_second_completion_is_not_recorded_twice()
    {
        // Arrange — reconciliation and a slow worker can both reach CompleteAsync for one row.
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SeededAsync();
        var store = new OperationStore(db, new AuditLog(db));
        var operation = await store.CreateAsync(Ctx(), Alice, "analysis", clientRequestId: null, ct);

        // Act
        await store.CompleteAsync(AcmeId, operation.Id, OperationState.Succeeded, null, ct);
        await store.CompleteAsync(AcmeId, operation.Id, OperationState.Failed, "late", ct);

        // Assert
        (await db.AuditEvents.CountAsync(a => a.Action == AuditActions.OperationCompleted, ct))
            .ShouldBe(1);
    }

    [Fact]
    public async Task Reaching_for_another_tenant_is_recorded()
    {
        // Arrange — the response is a 404, so this row is the only trace of the attempt.
        var ct = TestContext.Current.CancellationToken;
        var (host, client) = await StartAsync(postgres);
        using var _h = host;

        // Act
        var response = await client.SendAsync(
            As(Bob, HttpMethod.Get, $"/organizations/{AcmeId.Value}/operations/{Guid.CreateVersion7()}"), ct);

        // Assert
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LakewrightDbContext>();
        var audit = await db.AuditEvents.SingleAsync(a => a.PrincipalId == Bob, ct);
        audit.Action.ShouldBe(AuditActions.TenantAccessDenied);
        audit.OrganizationId.ShouldBe(AcmeId);
    }

    private async Task<LakewrightDbContext> SeededAsync()
    {
        var db = await postgres.NewDatabaseAsync();

        db.Organizations.Add(new Organization
        {
            Id = AcmeId,
            Name = "Acme",
            Slug = "acme",
            CreatedAt = DateTimeOffset.UtcNow,
            Schema = UnityCatalogIdentifier.SchemaForTenant(AcmeId),
            State = OrganizationState.Active
        });

        await db.SaveChangesAsync();
        return db;
    }
}
