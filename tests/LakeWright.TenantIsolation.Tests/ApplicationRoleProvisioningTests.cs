using LakeWright.Core.Tenancy;
using LakeWright.DatabaseMaintenance;
using LakeWright.Multitenancy.Model;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// The deployment step that lets the application connect as a role that does not own the tables,
/// which is the precondition for the database to refuse audit tampering.
/// </summary>
[Trait("Category", "TenantIsolation")]
[Collection(nameof(PostgresTests))]
public sealed class ApplicationRoleProvisioningTests(PostgresFixture postgres)
{
    private const string AppRole = "lakewright_app";
    private const string AppPassword = "probe-password";

    [Fact]
    public async Task An_empty_database_gets_the_schema_and_a_role_that_can_append_but_not_amend_audit_events()
    {
        var ct = TestContext.Current.CancellationToken;
        string connectionString;
        await using (var scratch = await postgres.NewDatabaseAsync())
        {
            connectionString = scratch.Database.GetConnectionString()!;
            await scratch.Database.EnsureDeletedAsync(ct);
        }

        await using var owner = PostgresFixture.ContextFor(connectionString);
        await ApplicationRoleProvisioning.ApplyAsync(owner, AppRole, AppPassword, ct);
        await ApplicationRoleProvisioning.ApplyAsync(owner, AppRole, AppPassword, ct);

        await using var app = PostgresFixture.AsApplicationRole(owner, AppRole, AppPassword);
        app.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = TenantId.Parse("0198f000-0000-7000-8000-0000000000d7"),
            PrincipalId = "auth0|alice",
            Action = "organization.provisioned",
            ResourceType = "organization",
            OccurredAt = DateTimeOffset.UtcNow,
        });
        await app.SaveChangesAsync(ct);

        var refused = await Should.ThrowAsync<PostgresException>(
            async () => await app.AuditEvents.ExecuteDeleteAsync(ct));
        refused.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await app.AuditEvents.CountAsync(ct)).ShouldBe(1);
    }
}
