using LakeWright.Core.Tenancy;
using LakeWright.Multitenancy;
using LakeWright.Multitenancy.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// Creating a tenant, and removing one in the order the compliance documentation states.
/// </summary>
/// <remarks>
/// Deletion is the operation with no undo, so the tests that matter here are the refusals. A
/// deletion that runs when it should not is not a bug you find later.
/// </remarks>
[Trait("Category", "TenantIsolation")]
[Collection(nameof(PostgresTests))]
public class TenantLifecycleTests(PostgresFixture postgres)
{
    /// <summary>Records what it was asked to do, so a test can assert the order and the arguments.</summary>
    private sealed class RecordingSchemas : ITenantSchemaProvisioner
    {
        public List<string> Calls { get; } = [];

        public Task CreateAsync(string catalog, string schema, CancellationToken cancellationToken)
        {
            Calls.Add($"create {catalog}.{schema}");
            return Task.CompletedTask;
        }

        public Task DropAsync(string catalog, string schema, CancellationToken cancellationToken)
        {
            Calls.Add($"drop {catalog}.{schema}");
            return Task.CompletedTask;
        }
    }

    private static TenantLifecycle Lifecycle(
        LakeWrightDbContext db,
        ITenantSchemaProvisioner? schemas = null) =>
        new(db,
            new AuditLog(db, TimeProvider.System),
            TimeProvider.System,
            Options.Create(new MultitenancyOptions { Catalog = "analytics" }),
            schemas);

    [Fact]
    public async Task Provisioning_creates_the_row_the_schema_and_an_audit_trail()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        var schemas = new RecordingSchemas();

        // Act
        var tenant = await Lifecycle(db, schemas).ProvisionAsync("Acme Logistics", "acme", "auth0|ops", ct);

        // Assert
        tenant.State.ShouldBe(OrganizationState.Active);
        tenant.Schema.ShouldBe(UnityCatalogIdentifier.SchemaForTenant(tenant.Id));
        schemas.Calls.ShouldBe([$"create analytics.{tenant.Schema}"]);
        (await db.AuditEvents.SingleAsync(ct)).Action.ShouldBe(AuditActions.TenantProvisioned);
    }

    [Fact]
    public async Task Provisioning_twice_finishes_the_first_attempt_rather_than_making_a_second_tenant()
    {
        // Arrange — the retry case: the row commits, the schema call fails, the caller tries again.
        // A second tenant with the same slug, or a unique-index collision, would both be worse than
        // the partial state being retried.
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        var schemas = new RecordingSchemas();
        var lifecycle = Lifecycle(db, schemas);

        // Act
        var first = await lifecycle.ProvisionAsync("Acme", "acme", "auth0|ops", ct);
        var second = await lifecycle.ProvisionAsync("Acme", "acme", "auth0|ops", ct);

        // Assert
        second.Id.ShouldBe(first.Id);
        (await db.Organizations.CountAsync(ct)).ShouldBe(1);
        (await db.AuditEvents.CountAsync(a => a.Action == AuditActions.TenantProvisioned, ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_tenant_works_with_no_schema_provisioner_at_all()
    {
        // Arrange — the same bargain AddLakeWright makes everywhere else: PostgreSQL alone works,
        // and Databricks is something you add.
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();

        // Act
        var tenant = await Lifecycle(db).ProvisionAsync("Globex", "globex", "auth0|ops", ct);

        // Assert
        tenant.State.ShouldBe(OrganizationState.Active);
    }

    [Fact]
    public async Task Requesting_deletion_stops_service_and_destroys_nothing()
    {
        // Arrange — step 1 of the documented order, and the property that makes a mistaken
        // deletion request survivable.
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        var schemas = new RecordingSchemas();
        var lifecycle = Lifecycle(db, schemas);
        var tenant = await lifecycle.ProvisionAsync("Acme", "acme", "auth0|ops", ct);
        schemas.Calls.Clear();

        // Act
        var requested = await lifecycle.BeginDeletionAsync(tenant.Id, "auth0|ops", ct);

        // Assert — the resolver already refuses a PendingDeletion tenant, and nothing was dropped.
        requested.ShouldBeTrue();
        (await db.Organizations.SingleAsync(ct)).State.ShouldBe(OrganizationState.PendingDeletion);
        schemas.Calls.ShouldBeEmpty();
        (await new EfTenantContextResolver(db, Options.Create(new MultitenancyOptions { Catalog = "analytics" }))
            .ResolveAsync(tenant.Id, "auth0|ops", ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Purging_a_live_tenant_is_refused()
    {
        // Arrange — there must be no single call that takes a serving tenant to gone.
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        var schemas = new RecordingSchemas();
        var lifecycle = Lifecycle(db, schemas);
        var tenant = await lifecycle.ProvisionAsync("Acme", "acme", "auth0|ops", ct);
        schemas.Calls.Clear();

        // Act
        var result = await lifecycle.PurgeAsync(tenant.Id, "auth0|ops", ct);

        // Assert
        result.ShouldBe(TenantPurgeResult.NotPendingDeletion);
        (await db.Organizations.CountAsync(ct)).ShouldBe(1);
        schemas.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Purging_waits_for_work_still_in_flight()
    {
        // Arrange — step 2. Dropping a schema under a running query fails the query in a way that
        // looks like a platform fault rather than like a deletion someone asked for.
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        var schemas = new RecordingSchemas();
        var lifecycle = Lifecycle(db, schemas);
        var tenant = await lifecycle.ProvisionAsync("Acme", "acme", "auth0|ops", ct);

        var store = new OperationStore(db, new AuditLog(db, TimeProvider.System), TimeProvider.System);
        var operation = await store.CreateAsync(
            TenantContextFactory.ForTenant(tenant.Id, "analytics"), "auth0|alice", "analysis", null, ct);

        await lifecycle.BeginDeletionAsync(tenant.Id, "auth0|ops", ct);
        schemas.Calls.Clear();

        // Act
        var whileRunning = await lifecycle.PurgeAsync(tenant.Id, "auth0|ops", ct);
        await store.CompleteAsync(tenant.Id, operation.Id, OperationState.Succeeded, null, ct);
        var afterDraining = await lifecycle.PurgeAsync(tenant.Id, "auth0|ops", ct);

        // Assert
        whileRunning.ShouldBe(TenantPurgeResult.OperationsInFlight);
        afterDraining.ShouldBe(TenantPurgeResult.Deleted);
        schemas.Calls.ShouldBe([$"drop analytics.{tenant.Schema}"]);
    }

    [Fact]
    public async Task Purging_removes_the_tenant_and_leaves_the_audit_row_behind()
    {
        // Arrange — steps 3 to 5. The audit row has no foreign key to organizations precisely so
        // it can outlive the cascade: it is the only remaining record the tenant ever existed.
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        var lifecycle = Lifecycle(db, new RecordingSchemas());
        var tenant = await lifecycle.ProvisionAsync("Acme", "acme", "auth0|ops", ct);

        db.Memberships.Add(new Membership
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = tenant.Id,
            PrincipalId = "auth0|alice",
            Role = MembershipRole.Admin,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
        await lifecycle.BeginDeletionAsync(tenant.Id, "auth0|ops", ct);

        // Act
        var result = await lifecycle.PurgeAsync(tenant.Id, "auth0|ops", ct);

        // Assert
        result.ShouldBe(TenantPurgeResult.Deleted);
        (await db.Organizations.CountAsync(ct)).ShouldBe(0);
        (await db.Memberships.CountAsync(ct)).ShouldBe(0, "membership cascades with the organization");

        var deleted = await db.AuditEvents.SingleAsync(a => a.Action == AuditActions.TenantDeleted, ct);
        deleted.OrganizationId.ShouldBe(tenant.Id);
        deleted.Detail.ShouldNotBeNull().ShouldContain("acme");
    }

    [Fact]
    public async Task A_slug_that_is_not_a_valid_schema_name_is_refused_before_anything_is_written()
    {
        // Arrange — the slug reaches Unity Catalog DDL as an identifier, and identifiers cannot be
        // bound as parameters. Validation is the only thing between a caller and injected DDL.
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();

        // Act
        var provision = async () =>
            await Lifecycle(db).ProvisionAsync("Bad", "acme; DROP SCHEMA other", "auth0|ops", ct);

        // Assert
        await provision.ShouldThrowAsync<ArgumentException>();
        (await db.Organizations.CountAsync(ct)).ShouldBe(0);
    }
}
