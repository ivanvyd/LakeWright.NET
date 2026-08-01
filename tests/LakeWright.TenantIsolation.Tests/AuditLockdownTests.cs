using LakeWright.Core.Tenancy;
using LakeWright.Multitenancy;
using LakeWright.Multitenancy.Model;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// Proves the append-only guarantee holds at the connection, not only in the change tracker.
/// </summary>
/// <remarks>
/// The C# guard cannot see <c>ExecuteDelete</c>, <c>ExecuteUpdate</c> or raw SQL. A security
/// review demonstrated all three bypassing it. These tests run as the restricted application role
/// and assert Postgres refuses what C# cannot.
/// </remarks>
[Trait("Category", "TenantIsolation")]
[Collection(nameof(PostgresTests))]
public class AuditLockdownTests(PostgresFixture postgres)
{
    private const string AppRole = "lakewright_app";
    private const string AppPassword = "probe-password";

    private static readonly TenantId AcmeId = TenantId.Parse("0198f000-0000-7000-8000-0000000000c3");

    private static AuditEvent NewEvent() => new()
    {
        Id = Guid.CreateVersion7(),
        OrganizationId = AcmeId,
        PrincipalId = "auth0|alice",
        Action = "organization.provisioned",
        ResourceType = "organization",
        OccurredAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task The_application_role_cannot_delete_an_audit_event_even_via_ExecuteDelete()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var owner = await postgres.NewDatabaseAsync();
        await DatabaseHardening.ApplyAsync(owner, AppRole, AppPassword, ct);
        owner.AuditEvents.Add(NewEvent());
        await owner.SaveChangesAsync(ct);
        await using var app = PostgresFixture.AsApplicationRole(owner, AppRole, AppPassword);

        // Act — the exact call the C# guard cannot intercept: it never touches the change tracker
        // and never calls SaveChanges.
        var refused = await Should.ThrowAsync<PostgresException>(
            async () => await app.AuditEvents.ExecuteDeleteAsync(ct));

        // Assert
        refused.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await app.AuditEvents.CountAsync(ct)).ShouldBe(1);
    }

    [Fact]
    public async Task The_application_role_cannot_update_an_audit_event()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var owner = await postgres.NewDatabaseAsync();
        await DatabaseHardening.ApplyAsync(owner, AppRole, AppPassword, ct);
        owner.AuditEvents.Add(NewEvent());
        await owner.SaveChangesAsync(ct);
        await using var app = PostgresFixture.AsApplicationRole(owner, AppRole, AppPassword);

        // Act
        var refused = await Should.ThrowAsync<PostgresException>(
            async () => await app.AuditEvents.ExecuteUpdateAsync(
                s => s.SetProperty(e => e.Action, "tampered"), ct));

        // Assert
        refused.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task The_application_role_can_still_read_and_append()
    {
        // Arrange — a lockdown that also breaks the intended path is an outage, not a control.
        var ct = TestContext.Current.CancellationToken;
        await using var owner = await postgres.NewDatabaseAsync();
        await DatabaseHardening.ApplyAsync(owner, AppRole, AppPassword, ct);
        await using var app = PostgresFixture.AsApplicationRole(owner, AppRole, AppPassword);

        // Act
        app.AuditEvents.Add(NewEvent());
        await app.SaveChangesAsync(ct);

        // Assert
        (await app.AuditEvents.CountAsync(ct)).ShouldBe(1);
    }
}
