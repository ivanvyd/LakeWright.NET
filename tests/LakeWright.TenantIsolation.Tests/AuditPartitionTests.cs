using LakeWright.Core.Tenancy;
using LakeWright.Multitenancy;
using LakeWright.Multitenancy.Model;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static LakeWright.TenantIsolation.Tests.TestApi;

namespace LakeWright.TenantIsolation.Tests;

[Trait("Category", "TenantIsolation")]
[Collection(nameof(PostgresTests))]
public class AuditPartitionTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset FixedNow = new(2030, 6, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Populated_migration_is_lossless_and_preserves_global_id_identity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        var first = NewEvent(new DateTimeOffset(2029, 12, 12, 0, 0, 0, TimeSpan.Zero), "first");
        var second = NewEvent(new DateTimeOffset(2030, 6, 1, 0, 0, 0, TimeSpan.Zero), "second");
        db.AuditEvents.AddRange(first, second);
        await db.SaveChangesAsync(ct);

        await DatabasePartitioning.MigrateAsync(db, FixedNow, cancellationToken: ct);
        await DatabasePartitioning.ValidateAsync(db, ct);

        (await db.AuditEvents.OrderBy(row => row.OccurredAt).Select(row => row.Id).ToListAsync(ct))
            .ShouldBe([first.Id, second.Id]);
        (await DatabasePartitioning.IsPartitionedAsync(db, ct)).ShouldBeTrue();

        db.ChangeTracker.Clear();
        db.AuditEvents.Add(NewEvent(FixedNow.AddMonths(1), "duplicate", first.Id));
        var duplicate = await Should.ThrowAsync<DbUpdateException>(
            async () => await db.SaveChangesAsync(ct));
        duplicate.InnerException.ShouldBeOfType<PostgresException>().SqlState
            .ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Migration_preserves_application_acl_row_security_and_append_only_rules()
    {
        const string role = "lakewright_partition_app";
        const string password = "partition-probe-password";
        var ct = TestContext.Current.CancellationToken;
        await using var owner = await postgres.NewDatabaseAsync();
        await DatabaseHardening.ApplyAsync(owner, role, password, ct);
        await owner.Database.ExecuteSqlRawAsync(
            """
            ALTER TABLE audit_events ENABLE ROW LEVEL SECURITY;
            CREATE POLICY audit_principal_policy ON audit_events
                TO lakewright_partition_app
                USING ("PrincipalId" = current_user)
                WITH CHECK ("PrincipalId" = current_user);
            """,
            ct);

        owner.AuditEvents.AddRange(
            NewEvent(FixedNow, role),
            NewEvent(FixedNow, "hidden-principal"));
        await owner.SaveChangesAsync(ct);
        await DatabasePartitioning.MigrateAsync(owner, FixedNow, cancellationToken: ct);

        await using var app = PostgresFixture.AsApplicationRole(owner, role, password);
        (await app.AuditEvents.Select(row => row.PrincipalId).ToListAsync(ct)).ShouldBe([role]);
        app.AuditEvents.Add(NewEvent(FixedNow.AddDays(1), role));
        await app.SaveChangesAsync(ct);

        var backupWrite = await Should.ThrowAsync<PostgresException>(
            async () => await app.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO audit_events_unpartitioned_backup
                    ("Id", "PrincipalId", "Action", "ResourceType", "OccurredAt")
                VALUES ('00000000-0000-0000-0000-000000000002', 'lakewright_partition_app',
                        'test.audit', 'test', '2030-06-16T00:00:00Z')
                """,
                ct));
        backupWrite.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        await DatabasePartitioning.ValidateAsync(owner, ct);
        await DatabasePartitioning.FinalizeMigrationAsync(owner, ct);

        var update = await Should.ThrowAsync<PostgresException>(
            async () => await app.AuditEvents.ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.Action, "tampered"), ct));
        update.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        var registryWrite = await Should.ThrowAsync<PostgresException>(
            async () => await app.Database.ExecuteSqlRawAsync(
                "INSERT INTO audit_event_ids (\"Id\", \"OccurredAt\") VALUES ('00000000-0000-0000-0000-000000000001', now())",
                ct));
        registryWrite.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Fixed_clock_maintenance_is_idempotent_and_covers_future_months()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        await DatabasePartitioning.MigrateAsync(
            db,
            FixedNow,
            new AuditPartitionOptions { FutureMonths = 3 },
            ct);

        var first = await DatabasePartitioning.MaintainAsync(
            db,
            FixedNow,
            new AuditPartitionOptions { FutureMonths = 3 },
            ct);
        var second = await DatabasePartitioning.MaintainAsync(
            db,
            FixedNow.AddMonths(2),
            new AuditPartitionOptions { FutureMonths = 3 },
            ct);

        first.CreatedPartitions.ShouldBe(0);
        second.CreatedPartitions.ShouldBe(2);
        (await ListPartitionsAsync(db, ct)).ShouldContain("audit_events_2030_11");
    }

    [Fact]
    public async Task Retention_drops_only_partitions_wholly_before_the_cutoff()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        var expired = NewEvent(new DateTimeOffset(2023, 5, 31, 23, 0, 0, TimeSpan.Zero), "expired");
        var boundary = NewEvent(new DateTimeOffset(2023, 6, 1, 0, 0, 0, TimeSpan.Zero), "boundary");
        db.AuditEvents.AddRange(expired, boundary);
        await db.SaveChangesAsync(ct);
        await DatabasePartitioning.MigrateAsync(db, FixedNow, cancellationToken: ct);
        await DatabasePartitioning.FinalizeMigrationAsync(db, ct);

        var result = await DatabasePartitioning.MaintainAsync(db, FixedNow, cancellationToken: ct);

        result.DroppedPartitions.ShouldBe(1);
        (await db.AuditEvents.Select(row => row.Id).ToListAsync(ct)).ShouldBe([boundary.Id]);
        (await ListPartitionsAsync(db, ct)).ShouldNotContain("audit_events_2023_05");
        (await ListPartitionsAsync(db, ct)).ShouldContain("audit_events_2023_06");
    }

    [Fact]
    public async Task Rollback_copies_post_migration_rows_before_restoring_the_original_table()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        db.AuditEvents.Add(NewEvent(FixedNow, "before"));
        await db.SaveChangesAsync(ct);
        await DatabasePartitioning.MigrateAsync(db, FixedNow, cancellationToken: ct);

        db.ChangeTracker.Clear();
        db.AuditEvents.Add(NewEvent(FixedNow.AddDays(1), "after"));
        await db.SaveChangesAsync(ct);
        await DatabasePartitioning.RollbackMigrationAsync(db, ct);

        (await DatabasePartitioning.IsPartitionedAsync(db, ct)).ShouldBeFalse();
        (await db.AuditEvents.Select(row => row.PrincipalId).OrderBy(value => value).ToListAsync(ct))
            .ShouldBe(["after", "before"]);
    }

    [Fact]
    public async Task Application_role_cannot_run_partition_maintenance()
    {
        const string role = "lakewright_partition_ddl_probe";
        const string password = "partition-ddl-password";
        var ct = TestContext.Current.CancellationToken;
        await using var owner = await postgres.NewDatabaseAsync();
        await DatabasePartitioning.MigrateAsync(owner, FixedNow, cancellationToken: ct);
        await DatabasePartitioning.FinalizeMigrationAsync(owner, ct);
        await DatabaseHardening.ApplyAsync(owner, role, password, ct);
        await using var app = PostgresFixture.AsApplicationRole(owner, role, password);

        var refused = await Should.ThrowAsync<PostgresException>(
            async () => await DatabasePartitioning.MaintainAsync(app, FixedNow.AddMonths(1), cancellationToken: ct));

        refused.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    private static AuditEvent NewEvent(DateTimeOffset occurredAt, string principal, Guid? id = null) => new()
    {
        Id = id ?? Guid.CreateVersion7(),
        OrganizationId = AcmeId,
        PrincipalId = principal,
        Action = "test.audit",
        ResourceType = "test",
        ResourceId = principal,
        OccurredAt = occurredAt,
        Detail = null
    };

    private static Task<List<string>> ListPartitionsAsync(
        LakeWrightDbContext db,
        CancellationToken cancellationToken) =>
        db.Database.SqlQueryRaw<string>(
            """
            SELECT child.relname AS "Value"
            FROM pg_catalog.pg_inherits inheritance
            JOIN pg_catalog.pg_class parent ON parent.oid = inheritance.inhparent
            JOIN pg_catalog.pg_class child ON child.oid = inheritance.inhrelid
            WHERE parent.relname = 'audit_events'
            ORDER BY child.relname
            """).ToListAsync(cancellationToken);
}
