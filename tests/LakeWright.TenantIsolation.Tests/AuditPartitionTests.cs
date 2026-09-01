using LakeWright.Core.Tenancy;
using LakeWright.DatabaseMaintenance;
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

    [Fact]
    public async Task Partition_months_are_UTC_even_when_the_database_session_is_not()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        await db.Database.OpenConnectionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SET TIME ZONE 'America/Los_Angeles'", ct);
        db.AuditEvents.AddRange(
            NewEvent(new DateTimeOffset(2030, 3, 1, 0, 0, 0, TimeSpan.Zero), "dst-boundary"),
            NewEvent(new DateTimeOffset(2030, 6, 1, 0, 0, 0, TimeSpan.Zero), "utc-boundary"));
        await db.SaveChangesAsync(ct);

        await DatabasePartitioning.MigrateAsync(db, FixedNow, cancellationToken: ct);

        var partitions = await ListPartitionsAsync(db, ct);
        partitions.ShouldContain("audit_events_2030_06");
        var marchEnd = await db.Database.SqlQueryRaw<DateTimeOffset>(
            """
            SELECT "EndsAt" AS "Value"
            FROM audit_event_partitions
            WHERE "PartitionName" = 'audit_events_2030_03'
            """).SingleAsync(ct);
        marchEnd.ShouldBe(new DateTimeOffset(2030, 4, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Rollback_restores_application_acl_and_revokes_rollback_artifact_access()
    {
        const string role = "lakewright_partition_rollback_app";
        const string password = "partition-rollback-password";
        var ct = TestContext.Current.CancellationToken;
        await using var owner = await postgres.NewDatabaseAsync();
        await DatabaseHardening.ApplyAsync(owner, role, password, ct);
        owner.AuditEvents.Add(NewEvent(FixedNow, role));
        await owner.SaveChangesAsync(ct);
        await DatabasePartitioning.MigrateAsync(owner, FixedNow, cancellationToken: ct);

        await DatabasePartitioning.RollbackMigrationAsync(owner, ct);

        await using var app = PostgresFixture.AsApplicationRole(owner, role, password);
        (await app.AuditEvents.CountAsync(ct)).ShouldBe(1);
        app.AuditEvents.Add(NewEvent(FixedNow.AddDays(1), role));
        await app.SaveChangesAsync(ct);

        var retainedCopyRead = await Should.ThrowAsync<PostgresException>(
            async () => await app.Database.ExecuteSqlRawAsync(
                "SELECT 1 FROM audit_events_partitioned_rollback LIMIT 1", ct));
        retainedCopyRead.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        var registryRead = await Should.ThrowAsync<PostgresException>(
            async () => await app.Database.ExecuteSqlRawAsync(
                "SELECT 1 FROM audit_event_ids LIMIT 1", ct));
        registryRead.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Rollback_cleanup_allows_a_deterministic_remigration()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        db.AuditEvents.Add(NewEvent(FixedNow, "before"));
        await db.SaveChangesAsync(ct);

        await DatabasePartitioning.MigrateAsync(db, FixedNow, cancellationToken: ct);
        await DatabasePartitioning.RollbackMigrationAsync(db, ct);
        await DatabasePartitioning.FinalizeMigrationAsync(db, ct);
        await DatabasePartitioning.MigrateAsync(db, FixedNow.AddMonths(1), cancellationToken: ct);
        await DatabasePartitioning.ValidateAsync(db, ct);

        (await DatabasePartitioning.IsPartitionedAsync(db, ct)).ShouldBeTrue();
        (await RelationExistsAsync(db, "audit_events_partitioned_rollback", ct)).ShouldBeFalse();
        (await db.AuditEvents.Select(row => row.PrincipalId).ToListAsync(ct)).ShouldBe(["before"]);
    }

    [Fact]
    public async Task Maintenance_refuses_corrupt_lifecycle_state()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        await DatabasePartitioning.MigrateAsync(db, FixedNow, cancellationToken: ct);
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE lakewright_audit_partition_state SET \"SchemaVersion\" = 999", ct);

        var error = await Should.ThrowAsync<InvalidOperationException>(
            async () => await DatabasePartitioning.ValidateAsync(db, ct));

        error.Message.ShouldContain("Unsupported audit partition lifecycle state version=999");

        await db.Database.ExecuteSqlRawAsync(
            "UPDATE lakewright_audit_partition_state SET \"SchemaVersion\" = 1, \"Phase\" = 'Finalized'", ct);
        var topologyError = await Should.ThrowAsync<InvalidOperationException>(
            async () => await DatabasePartitioning.ValidateAsync(db, ct));
        topologyError.Message.ShouldContain("does not match the database topology");
    }

    [Fact]
    public async Task Identity_registry_has_the_retention_range_index()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        await DatabasePartitioning.MigrateAsync(db, FixedNow, cancellationToken: ct);

        var definition = await db.Database.SqlQueryRaw<string>(
            """
            SELECT indexdef AS "Value"
            FROM pg_catalog.pg_indexes
            WHERE schemaname = 'public' AND indexname = 'audit_event_ids_occurred_at'
            """).SingleAsync(ct);

        definition.ShouldContain("(\"OccurredAt\")");
    }

    [Fact]
    public async Task Migration_refuses_tables_above_the_configured_row_limit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        db.AuditEvents.AddRange(NewEvent(FixedNow, "one"), NewEvent(FixedNow, "two"));
        await db.SaveChangesAsync(ct);

        var error = await Should.ThrowAsync<InvalidOperationException>(
            async () => await DatabasePartitioning.MigrateAsync(
                db,
                FixedNow,
                new AuditPartitionOptions { MaxMigrationRows = 1 },
                ct));

        error.Message.ShouldContain("row count 2 exceeds");
        (await DatabasePartitioning.IsPartitionedAsync(db, ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Migration_lock_wait_is_bounded_by_the_configured_timeout()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        await using var blocker = new NpgsqlConnection(db.Database.GetConnectionString());
        await blocker.OpenAsync(ct);
        await using var blockerTransaction = await blocker.BeginTransactionAsync(ct);
        await using (var command = new NpgsqlCommand("LOCK TABLE audit_events IN ACCESS SHARE MODE", blocker, blockerTransaction))
        {
            await command.ExecuteNonQueryAsync(ct);
        }

        var error = await Should.ThrowAsync<PostgresException>(
            async () => await DatabasePartitioning.MigrateAsync(
                db,
                FixedNow,
                new AuditPartitionOptions { LockTimeout = TimeSpan.FromMilliseconds(100) },
                ct));

        error.SqlState.ShouldBe(PostgresErrorCodes.LockNotAvailable);
    }

    [Fact]
    public async Task Migration_rejects_mutable_application_grants()
    {
        const string role = "lakewright_partition_mutable_acl";
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        await db.Database.ExecuteSqlRawAsync(
            $"CREATE ROLE {role}; GRANT UPDATE ON audit_events TO {role};", ct);

        var error = await Should.ThrowAsync<InvalidOperationException>(
            async () => await DatabasePartitioning.MigrateAsync(db, FixedNow, cancellationToken: ct));

        error.Message.ShouldContain($"{role}:UPDATE");
        (await DatabasePartitioning.IsPartitionedAsync(db, ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Migration_rejects_sparse_history_that_requires_too_many_partitions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        db.AuditEvents.AddRange(
            NewEvent(new DateTimeOffset(1, 1, 1, 0, 0, 0, TimeSpan.Zero), "ancient"),
            NewEvent(FixedNow, "current"));
        await db.SaveChangesAsync(ct);

        var error = await Should.ThrowAsync<InvalidOperationException>(
            async () => await DatabasePartitioning.MigrateAsync(db, FixedNow, cancellationToken: ct));

        error.Message.ShouldContain("monthly partitions, exceeding the supported migration limit 120");
        (await DatabasePartitioning.IsPartitionedAsync(db, ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Lifecycle_validation_requires_the_registry_range_index()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        await DatabasePartitioning.MigrateAsync(db, FixedNow, cancellationToken: ct);
        await db.Database.ExecuteSqlRawAsync("DROP INDEX audit_event_ids_occurred_at", ct);

        var error = await Should.ThrowAsync<InvalidOperationException>(
            async () => await DatabasePartitioning.ValidateAsync(db, ct));

        error.Message.ShouldContain("does not match the database topology");
    }

    [Fact]
    public async Task Lifecycle_validation_requires_the_registry_primary_key()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        await DatabasePartitioning.MigrateAsync(db, FixedNow, cancellationToken: ct);
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE audit_event_ids DROP CONSTRAINT audit_event_ids_pkey", ct);

        var error = await Should.ThrowAsync<InvalidOperationException>(
            async () => await DatabasePartitioning.ValidateAsync(db, ct));

        error.Message.ShouldContain("does not match the database topology");
    }

    [Fact]
    public async Task Lifecycle_validation_requires_an_enabled_identity_trigger()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        await DatabasePartitioning.MigrateAsync(db, FixedNow, cancellationToken: ct);
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE audit_events DISABLE TRIGGER lakewright_register_audit_event_id", ct);

        var error = await Should.ThrowAsync<InvalidOperationException>(
            async () => await DatabasePartitioning.ValidateAsync(db, ct));

        error.Message.ShouldContain("does not match the database topology");
    }

    [Fact]
    public async Task Lifecycle_validation_rejects_a_replica_only_identity_trigger()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        await DatabasePartitioning.MigrateAsync(db, FixedNow, cancellationToken: ct);
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE audit_events ENABLE REPLICA TRIGGER lakewright_register_audit_event_id", ct);

        var error = await Should.ThrowAsync<InvalidOperationException>(
            async () => await DatabasePartitioning.ValidateAsync(db, ct));

        error.Message.ShouldContain("does not match the database topology");
    }

    [Fact]
    public async Task Lifecycle_validation_requires_a_security_definer_identity_function()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        await DatabasePartitioning.MigrateAsync(db, FixedNow, cancellationToken: ct);
        await db.Database.ExecuteSqlRawAsync(
            "ALTER FUNCTION lakewright_register_audit_event_id() SECURITY INVOKER", ct);

        var error = await Should.ThrowAsync<InvalidOperationException>(
            async () => await DatabasePartitioning.ValidateAsync(db, ct));

        error.Message.ShouldContain("does not match the database topology");
    }

    [Fact]
    public async Task Non_superuser_owner_preserves_hidden_rows_through_forced_rls_lifecycle()
    {
        const string role = "lakewright_partition_migrator";
        const string password = "partition-migrator-password";
        var ct = TestContext.Current.CancellationToken;
        await using var superuser = await postgres.NewDatabaseAsync();
        superuser.AuditEvents.AddRange(
            NewEvent(FixedNow, role),
            NewEvent(FixedNow.AddDays(1), "hidden-from-migrator"));
        await superuser.SaveChangesAsync(ct);
        await superuser.Database.ExecuteSqlRawAsync(
            $"""
            CREATE ROLE {role} LOGIN PASSWORD '{password}';
            GRANT USAGE, CREATE ON SCHEMA public TO {role};
            ALTER TABLE audit_events OWNER TO {role};
            ALTER TABLE audit_events ENABLE ROW LEVEL SECURITY;
            ALTER TABLE audit_events FORCE ROW LEVEL SECURITY;
            CREATE POLICY migrator_only ON audit_events
                USING ("PrincipalId" = current_user)
                WITH CHECK ("PrincipalId" = current_user);
            """,
            ct);
        await using var migrator = PostgresFixture.AsApplicationRole(superuser, role, password);

        await DatabasePartitioning.MigrateAsync(migrator, FixedNow, cancellationToken: ct);
        await DatabasePartitioning.ValidateAsync(migrator, ct);
        (await superuser.AuditEvents.CountAsync(ct)).ShouldBe(2);

        await DatabasePartitioning.RollbackMigrationAsync(migrator, ct);
        (await superuser.AuditEvents.CountAsync(ct)).ShouldBe(2);
        (await ForcedRlsRelationCountAsync(superuser, ct)).ShouldBe(2);

        await DatabasePartitioning.FinalizeMigrationAsync(migrator, ct);
        await DatabasePartitioning.MigrateAsync(migrator, FixedNow, cancellationToken: ct);
        await DatabasePartitioning.FinalizeMigrationAsync(migrator, ct);

        (await superuser.AuditEvents.CountAsync(ct)).ShouldBe(2);
        (await ForcedRlsRelationCountAsync(superuser, ct)).ShouldBe(1);
        (await migrator.AuditEvents.CountAsync(ct)).ShouldBe(1);
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

    private static Task<bool> RelationExistsAsync(
        LakeWrightDbContext db,
        string relation,
        CancellationToken cancellationToken) =>
        db.Database.SqlQueryRaw<bool>(
            "SELECT pg_catalog.to_regclass('public.' || {0}) IS NOT NULL AS \"Value\"",
            relation).SingleAsync(cancellationToken);

    private static Task<int> ForcedRlsRelationCountAsync(
        LakeWrightDbContext db,
        CancellationToken cancellationToken) =>
        db.Database.SqlQueryRaw<int>(
            """
            SELECT count(*)::integer AS "Value"
            FROM pg_catalog.pg_class relation
            JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
            WHERE namespace.nspname = 'public'
              AND relation.relname IN ('audit_events', 'audit_events_partitioned_rollback')
              AND relation.relrowsecurity
              AND relation.relforcerowsecurity
            """).SingleAsync(cancellationToken);
}
