using System.Data;
using System.Data.Common;
using System.Globalization;
using LakeWright.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace LakeWright.DatabaseMaintenance;

/// <summary>Controls audit partition creation and retention.</summary>
internal sealed class AuditPartitionOptions
{
    /// <summary>Number of calendar years to retain. The documented default is seven years.</summary>
    public int RetentionYears { get; init; } = 7;

    /// <summary>Number of future calendar months to pre-create.</summary>
    public int FutureMonths { get; init; } = 2;

    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan StatementTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public long MaxMigrationRows { get; init; } = 1_000_000;

    public long MaxMigrationBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(RetentionYears, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(RetentionYears, 100);
        ArgumentOutOfRangeException.ThrowIfLessThan(FutureMonths, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(FutureMonths, 24);
        if (LockTimeout < TimeSpan.FromMilliseconds(100) || LockTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(LockTimeout));
        }
        if (StatementTimeout < TimeSpan.FromSeconds(1) || StatementTimeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(StatementTimeout));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxMigrationRows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxMigrationRows, 100_000_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxMigrationBytes, 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxMigrationBytes, 1024L * 1024 * 1024 * 1024);
    }
}

/// <summary>Result of one migration-role maintenance run.</summary>
/// <param name="CreatedPartitions">Partitions created by the run.</param>
/// <param name="DroppedPartitions">Expired partitions dropped by the run.</param>
internal sealed record AuditPartitionMaintenanceResult(int CreatedPartitions, int DroppedPartitions);

/// <summary>
/// Safely migrates and maintains the append-only <c>audit_events</c> table as monthly PostgreSQL
/// range partitions.
/// </summary>
/// <remarks>
/// Every method in this type performs DDL and must run as the table-owning migration role, never
/// as the application role. Values travel as database parameters. Dynamic identifiers exist only
/// inside the installed PostgreSQL helpers, where they are generated from a timestamp and quoted
/// with <c>format('%I', ...)</c>.
/// </remarks>
internal static class DatabasePartitioning
{
    private const long MaintenanceLockKey = 4_817_191_033_702_026_091L;
    private const int LifecycleSchemaVersion = 1;

    /// <summary>
    /// Atomically replaces an existing ordinary audit table with a partitioned parent, preserving
    /// every row, table grants, row-security policies and the entity's globally unique Id.
    /// </summary>
    /// <remarks>
    /// The original table is retained as <c>audit_events_unpartitioned_backup</c>. Run
    /// <see cref="ValidateAsync"/>, then either <see cref="FinalizeMigrationAsync"/> to accept the
    /// migration or <see cref="RollbackMigrationAsync"/> to restore it. An advisory lock makes
    /// concurrent deploy jobs idempotent.
    /// </remarks>
    public static async Task MigrateAsync(
        LakeWrightDbContext db,
        DateTimeOffset now,
        AuditPartitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        options ??= new AuditPartitionOptions();
        options.Validate();
        EnsureUtc(now);

        await ExecuteInTransactionAsync(db, async (connection, transaction) =>
        {
            await AcquireLockAsync(connection, transaction, cancellationToken);
            await ExecuteAsync(connection, transaction, AuditPartitionSql.InstallHelpers, cancellationToken);
            var state = await ReadStateAsync(connection, transaction, cancellationToken);

            var kind = await ScalarAsync<string?>(
                connection,
                transaction,
                """
                SELECT c.relkind::text
                FROM pg_catalog.pg_class c
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public' AND c.relname = 'audit_events'
                """,
                cancellationToken);

            if (kind == "p")
            {
                RequirePhase(state, AuditPartitionPhase.Migrated, AuditPartitionPhase.Finalized);
                await AssertLifecycleTopologyAsync(connection, transaction, state!, cancellationToken);
                await RequireManagedParentAsync(connection, transaction, cancellationToken);
                await AssertIdentityRegistryAsync(connection, transaction, cancellationToken);
                await EnsureWindowAsync(connection, transaction, now, options.FutureMonths, cancellationToken);
                return;
            }

            if (kind is not "r")
            {
                throw new InvalidOperationException(
                    "public.audit_events must be an ordinary or partitioned table before migration.");
            }

            if (state is not null)
            {
                throw new InvalidOperationException(
                    $"Audit partition lifecycle is '{state.Phase}'; clean up that lifecycle before migrating again.");
            }

            var ownerIsCurrentRole = await ScalarAsync<bool>(
                connection,
                transaction,
                """
                SELECT tableowner = current_user
                FROM pg_catalog.pg_tables
                WHERE schemaname = 'public' AND tablename = 'audit_events'
                """,
                cancellationToken);
            if (!ownerIsCurrentRole)
            {
                throw new InvalidOperationException(
                    "The migration role must own public.audit_events; application-role DDL is refused.");
            }

            if (await RelationExistsAsync(
                    connection, transaction, "audit_events_unpartitioned_backup", cancellationToken))
            {
                throw new InvalidOperationException(
                    "audit_events_unpartitioned_backup already exists; complete or roll back the prior migration.");
            }

            if (await RelationExistsAsync(
                    connection, transaction, "audit_events_partitioned_rollback", cancellationToken)
                || await RelationExistsAsync(connection, transaction, "audit_event_ids", cancellationToken)
                || await ScalarAsync<long>(
                    connection,
                    transaction,
                    "SELECT count(*) FROM audit_event_partitions",
                    cancellationToken) != 0)
            {
                throw new InvalidOperationException(
                    "Audit partition artifacts exist without lifecycle state; manual inspection is required.");
            }

            await AssertAppendOnlyAclAsync(connection, transaction, cancellationToken);
            await AssertMigrationSizeAsync(connection, transaction, options, exactRows: false, cancellationToken);

            await ExecuteAsync(
                connection,
                transaction,
                "LOCK TABLE audit_events IN ACCESS EXCLUSIVE MODE; ALTER TABLE audit_events RENAME TO audit_events_unpartitioned_backup;",
                cancellationToken);
            await AssertMigrationSizeAsync(connection, transaction, options, exactRows: true, cancellationToken);
            await ExecuteAsync(connection, transaction, AuditPartitionSql.CreateParent, cancellationToken);

            var oldest = await ScalarAsync<DateTime?>(
                connection, transaction, "SELECT min(\"OccurredAt\") FROM audit_events_unpartitioned_backup", cancellationToken);
            var newest = await ScalarAsync<DateTime?>(
                connection, transaction, "SELECT max(\"OccurredAt\") FROM audit_events_unpartitioned_backup", cancellationToken);

            if (oldest.HasValue && newest.HasValue)
            {
                await EnsureRangeAsync(
                    connection,
                    transaction,
                    AsUtc(oldest.Value),
                    AsUtc(newest.Value),
                    cancellationToken);
            }
            await EnsureWindowAsync(connection, transaction, now, options.FutureMonths, cancellationToken);

            await ExecuteAsync(connection, transaction, AuditPartitionSql.CopyRows, cancellationToken);
            await AssertExactCopyAsync(connection, transaction, cancellationToken);
            await ExecuteAsync(connection, transaction, AuditPartitionSql.InstallIdentityTrigger, cancellationToken);
            await ExecuteAsync(connection, transaction, AuditPartitionSql.CopySecurity, cancellationToken);
            await WriteStateAsync(
                connection, transaction, AuditPartitionPhase.Migrated, cancellationToken);
        }, cancellationToken, options);
    }

    /// <summary>
    /// Pre-creates current and future partitions and drops only partitions whose complete range is
    /// older than the configured retention boundary.
    /// </summary>
    public static async Task<AuditPartitionMaintenanceResult> MaintainAsync(
        LakeWrightDbContext db,
        DateTimeOffset now,
        AuditPartitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        options ??= new AuditPartitionOptions();
        options.Validate();
        EnsureUtc(now);

        var created = 0;
        var dropped = 0;
        await ExecuteInTransactionAsync(db, async (connection, transaction) =>
        {
            await AcquireLockAsync(connection, transaction, cancellationToken);
            var state = await ReadRequiredStateAsync(connection, transaction, cancellationToken);
            RequirePhase(state, AuditPartitionPhase.Migrated, AuditPartitionPhase.Finalized);
            await RequireManagedParentAsync(connection, transaction, cancellationToken);
            created = await EnsureWindowAsync(connection, transaction, now, options.FutureMonths, cancellationToken);

            var expired = await QueryPartitionsAsync(
                connection, transaction, now.AddYears(-options.RetentionYears), cancellationToken);
            if (expired.Count > 0 && state.Phase != AuditPartitionPhase.Finalized)
            {
                throw new InvalidOperationException(
                    "Finalize or roll back the audit migration before retention drops old partitions.");
            }

            foreach (var partition in expired)
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    "SELECT lakewright_drop_audit_partition(@name, @start, @end)",
                    cancellationToken,
                    ("name", partition.Name),
                    ("start", partition.Start),
                    ("end", partition.End));
                dropped++;
            }
        }, cancellationToken, options);

        return new AuditPartitionMaintenanceResult(created, dropped);
    }

    /// <summary>Validates the migration's row-for-row copy and identity registry.</summary>
    public static async Task ValidateAsync(
        LakeWrightDbContext db,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        await ExecuteInTransactionAsync(db, async (connection, transaction) =>
        {
            await AcquireLockAsync(connection, transaction, cancellationToken);
            var state = await ReadRequiredStateAsync(connection, transaction, cancellationToken);
            RequirePhase(state, AuditPartitionPhase.Migrated, AuditPartitionPhase.Finalized);
            await RequireManagedParentAsync(connection, transaction, cancellationToken);
            if (await RelationExistsAsync(
                    connection, transaction, "audit_events_unpartitioned_backup", cancellationToken))
            {
                await AssertBackupContainedAsync(connection, transaction, cancellationToken);
            }
            await AssertIdentityRegistryAsync(connection, transaction, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>Accepts a migration, or removes retained artifacts after a rollback.</summary>
    public static async Task FinalizeMigrationAsync(
        LakeWrightDbContext db,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        await ExecuteInTransactionAsync(db, async (connection, transaction) =>
        {
            await AcquireLockAsync(connection, transaction, cancellationToken);
            var state = await ReadRequiredStateAsync(connection, transaction, cancellationToken);
            if (state.Phase == AuditPartitionPhase.Finalized)
            {
                return;
            }
            if (state.Phase == AuditPartitionPhase.RolledBack)
            {
                await CleanupRollbackAsync(connection, transaction, cancellationToken);
                return;
            }
            RequirePhase(state, AuditPartitionPhase.Migrated);
            await RequireManagedParentAsync(connection, transaction, cancellationToken);
            if (!await RelationExistsAsync(
                    connection, transaction, "audit_events_unpartitioned_backup", cancellationToken))
            {
                throw new InvalidOperationException("Migrated lifecycle state requires the rollback copy.");
            }
            await AssertBackupContainedAsync(connection, transaction, cancellationToken);
            await AssertIdentityRegistryAsync(connection, transaction, cancellationToken);
            await ExecuteAsync(connection, transaction, "DROP TABLE audit_events_unpartitioned_backup", cancellationToken);
            await WriteStateAsync(connection, transaction, AuditPartitionPhase.Finalized, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// Restores the original ordinary table. Rows appended after migration are copied back first;
    /// duplicate Ids or any row mismatch abort the entire transaction.
    /// </summary>
    public static async Task RollbackMigrationAsync(
        LakeWrightDbContext db,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        await ExecuteInTransactionAsync(db, async (connection, transaction) =>
        {
            await AcquireLockAsync(connection, transaction, cancellationToken);
            var state = await ReadRequiredStateAsync(connection, transaction, cancellationToken);
            RequirePhase(state, AuditPartitionPhase.Migrated);
            await RequireManagedParentAsync(connection, transaction, cancellationToken);
            if (!await RelationExistsAsync(
                    connection, transaction, "audit_events_unpartitioned_backup", cancellationToken))
            {
                throw new InvalidOperationException("The rollback copy has been finalized; rollback is no longer available.");
            }

            await ExecuteAsync(
                connection,
                transaction,
                """
                LOCK TABLE audit_events IN ACCESS EXCLUSIVE MODE;
                INSERT INTO audit_events_unpartitioned_backup
                    ("Id", "OrganizationId", "PrincipalId", "Action", "ResourceType", "ResourceId", "OccurredAt", "Detail")
                SELECT current."Id", current."OrganizationId", current."PrincipalId", current."Action",
                       current."ResourceType", current."ResourceId", current."OccurredAt", current."Detail"
                FROM audit_events current
                WHERE NOT EXISTS (
                    SELECT 1 FROM audit_events_unpartitioned_backup backup
                    WHERE backup."Id" = current."Id");
                ALTER TABLE audit_events RENAME TO audit_events_partitioned_rollback;
                ALTER TABLE audit_events_unpartitioned_backup RENAME TO audit_events;
                """,
                cancellationToken);
            await ExecuteAsync(
                connection, transaction, AuditPartitionSql.RestoreSecurityAfterRollback, cancellationToken);

            var mismatch = await ScalarAsync<bool>(
                connection,
                transaction,
                """
                SELECT EXISTS (
                    (SELECT "Id", "OrganizationId", "PrincipalId", "Action", "ResourceType", "ResourceId", "OccurredAt", "Detail"
                     FROM audit_events_partitioned_rollback
                     EXCEPT ALL
                     SELECT "Id", "OrganizationId", "PrincipalId", "Action", "ResourceType", "ResourceId", "OccurredAt", "Detail"
                     FROM audit_events)
                    UNION ALL
                    (SELECT "Id", "OrganizationId", "PrincipalId", "Action", "ResourceType", "ResourceId", "OccurredAt", "Detail"
                     FROM audit_events
                     EXCEPT ALL
                     SELECT "Id", "OrganizationId", "PrincipalId", "Action", "ResourceType", "ResourceId", "OccurredAt", "Detail"
                     FROM audit_events_partitioned_rollback)
                )
                """,
                cancellationToken);
            if (mismatch)
            {
                throw new InvalidOperationException("Rollback validation failed; no schema change was committed.");
            }
            await WriteStateAsync(connection, transaction, AuditPartitionPhase.RolledBack, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>Returns whether the public audit table is a range-partitioned parent.</summary>
    public static async Task<bool> IsPartitionedAsync(
        LakeWrightDbContext db,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }
        try
        {
            return await ScalarAsync<bool>(
                connection,
                null,
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_class c
                    JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = 'public' AND c.relname = 'audit_events' AND c.relkind = 'p'
                )
                """,
                cancellationToken);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task ExecuteInTransactionAsync(
        LakeWrightDbContext db,
        Func<DbConnection, DbTransaction, Task> action,
        CancellationToken cancellationToken,
        AuditPartitionOptions? options = null)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException("Partition maintenance owns its transaction and cannot be nested.");
        }

        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            options ??= new AuditPartitionOptions();
            await ConfigureTimeoutsAsync(connection, transaction, options, cancellationToken);
            await action(connection, transaction);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static Task AcquireLockAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            transaction,
            "SELECT pg_catalog.pg_advisory_xact_lock(@key)",
            cancellationToken,
            ("key", MaintenanceLockKey));

    private static Task ConfigureTimeoutsAsync(
        DbConnection connection,
        DbTransaction transaction,
        AuditPartitionOptions options,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            transaction,
            "SELECT set_config('lock_timeout', @lock_timeout, true), set_config('statement_timeout', @statement_timeout, true)",
            cancellationToken,
            ("lock_timeout", ToPostgresTimeout(options.LockTimeout)),
            ("statement_timeout", ToPostgresTimeout(options.StatementTimeout)));

    private static string ToPostgresTimeout(TimeSpan timeout) =>
        ((long)timeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + "ms";

    private static async Task AssertAppendOnlyAclAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var unsafeGrant = await ScalarAsync<string?>(
            connection,
            transaction,
            """
            SELECT grantee || ':' || privilege_type
            FROM information_schema.role_table_grants
            WHERE table_schema = 'public'
              AND table_name = 'audit_events'
              AND grantee <> current_user
              AND privilege_type IN ('UPDATE', 'DELETE', 'TRUNCATE')
            ORDER BY grantee, privilege_type
            LIMIT 1
            """,
            cancellationToken);
        if (unsafeGrant is not null)
        {
            throw new InvalidOperationException(
                $"Refusing to migrate mutable audit_events ACL '{unsafeGrant}'. Revoke UPDATE, DELETE and TRUNCATE first.");
        }
    }

    private static async Task AssertMigrationSizeAsync(
        DbConnection connection,
        DbTransaction transaction,
        AuditPartitionOptions options,
        bool exactRows,
        CancellationToken cancellationToken)
    {
        var relation = exactRows ? "audit_events_unpartitioned_backup" : "audit_events";
        var bytes = await ScalarAsync<long>(
            connection,
            transaction,
            "SELECT pg_catalog.pg_total_relation_size(pg_catalog.to_regclass('public.' || @relation))",
            cancellationToken,
            ("relation", relation));
        if (bytes > options.MaxMigrationBytes)
        {
            throw new InvalidOperationException(
                $"Audit table size {bytes} bytes exceeds the supported in-transaction migration limit {options.MaxMigrationBytes} bytes.");
        }

        var rows = exactRows
            ? await ScalarAsync<long>(
                connection,
                transaction,
                "SELECT count(*) FROM audit_events_unpartitioned_backup",
                cancellationToken)
            : await ScalarAsync<long>(
                connection,
                transaction,
                "SELECT greatest(coalesce(reltuples, 0), 0)::bigint FROM pg_catalog.pg_class WHERE oid = 'public.audit_events'::regclass",
                cancellationToken);
        if (rows > options.MaxMigrationRows)
        {
            throw new InvalidOperationException(
                $"Audit table row count {rows} exceeds the supported in-transaction migration limit {options.MaxMigrationRows}.");
        }
    }

    private static async Task<AuditPartitionState?> ReadStateAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            "SELECT \"SchemaVersion\", \"Phase\" FROM lakewright_audit_partition_state WHERE \"StateKey\" = true");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var version = reader.GetInt32(0);
        var phaseValue = reader.GetString(1);
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Audit partition lifecycle has more than one authoritative row.");
        }
        if (version != LifecycleSchemaVersion
            || !Enum.TryParse<AuditPartitionPhase>(phaseValue, ignoreCase: false, out var phase))
        {
            throw new InvalidOperationException(
                $"Unsupported audit partition lifecycle state version={version}, phase='{phaseValue}'.");
        }
        return new AuditPartitionState(phase);
    }

    private static async Task<AuditPartitionState> ReadRequiredStateAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var state = await ReadStateAsync(connection, transaction, cancellationToken)
            ?? throw new InvalidOperationException("Audit partition lifecycle state is missing.");
        await AssertLifecycleTopologyAsync(connection, transaction, state, cancellationToken);
        return state;
    }

    private static void RequirePhase(AuditPartitionState? state, params AuditPartitionPhase[] expected)
    {
        if (state is null || !expected.Contains(state.Phase))
        {
            var actual = state?.Phase.ToString() ?? "missing";
            throw new InvalidOperationException(
                $"Audit partition lifecycle is '{actual}', expected {string.Join(" or ", expected)}.");
        }
    }

    private static Task WriteStateAsync(
        DbConnection connection,
        DbTransaction transaction,
        AuditPartitionPhase phase,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO lakewright_audit_partition_state
                ("StateKey", "SchemaVersion", "Phase", "UpdatedAt")
            VALUES (true, @version, @phase, now())
            ON CONFLICT ("StateKey") DO UPDATE
            SET "SchemaVersion" = EXCLUDED."SchemaVersion",
                "Phase" = EXCLUDED."Phase",
                "UpdatedAt" = EXCLUDED."UpdatedAt"
            """,
            cancellationToken,
            ("version", LifecycleSchemaVersion),
            ("phase", phase.ToString()));

    private static async Task AssertLifecycleTopologyAsync(
        DbConnection connection,
        DbTransaction transaction,
        AuditPartitionState state,
        CancellationToken cancellationToken)
    {
        var canonicalKind = await RelationKindAsync(connection, transaction, "audit_events", cancellationToken);
        var hasBackup = await RelationExistsAsync(
            connection, transaction, "audit_events_unpartitioned_backup", cancellationToken);
        var hasRollback = await RelationExistsAsync(
            connection, transaction, "audit_events_partitioned_rollback", cancellationToken);
        var valid = state.Phase switch
        {
            AuditPartitionPhase.Migrated => canonicalKind == "p" && hasBackup && !hasRollback,
            AuditPartitionPhase.Finalized => canonicalKind == "p" && !hasBackup && !hasRollback,
            AuditPartitionPhase.RolledBack => canonicalKind == "r" && !hasBackup && hasRollback,
            _ => false
        };
        if (!valid)
        {
            throw new InvalidOperationException(
                $"Audit partition lifecycle '{state.Phase}' does not match the database topology.");
        }
    }

    private static async Task CleanupRollbackAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            transaction,
            """
            DROP TABLE audit_events_partitioned_rollback CASCADE;
            DROP TABLE audit_event_ids;
            DROP TABLE audit_event_partitions;
            DROP FUNCTION lakewright_register_audit_event_id();
            DROP FUNCTION lakewright_create_audit_partition(timestamptz);
            DROP FUNCTION lakewright_drop_audit_partition(text, timestamptz, timestamptz);
            DELETE FROM lakewright_audit_partition_state WHERE "StateKey" = true;
            """,
            cancellationToken);
    }

    private static async Task<int> EnsureWindowAsync(
        DbConnection connection,
        DbTransaction transaction,
        DateTimeOffset now,
        int futureMonths,
        CancellationToken cancellationToken)
    {
        var start = MonthStart(now);
        var created = 0;
        for (var offset = 0; offset <= futureMonths; offset++)
        {
            if (await EnsureMonthAsync(connection, transaction, start.AddMonths(offset), cancellationToken))
            {
                created++;
            }
        }
        return created;
    }

    private static async Task EnsureRangeAsync(
        DbConnection connection,
        DbTransaction transaction,
        DateTimeOffset oldest,
        DateTimeOffset newest,
        CancellationToken cancellationToken)
    {
        var month = MonthStart(oldest);
        var last = MonthStart(newest);
        while (month <= last)
        {
            await EnsureMonthAsync(connection, transaction, month, cancellationToken);
            month = month.AddMonths(1);
        }
    }

    private static Task<bool> EnsureMonthAsync(
        DbConnection connection,
        DbTransaction transaction,
        DateTimeOffset month,
        CancellationToken cancellationToken) =>
        ScalarAsync<bool>(
            connection,
            transaction,
            "SELECT lakewright_create_audit_partition(@start)",
            cancellationToken,
            ("start", month));

    private static async Task<List<PartitionRange>> QueryPartitionsAsync(
        DbConnection connection,
        DbTransaction transaction,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT "PartitionName", "StartsAt", "EndsAt"
            FROM audit_event_partitions
            WHERE "EndsAt" <= @cutoff
            ORDER BY "StartsAt"
            """,
            ("cutoff", cutoff));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var partitions = new List<PartitionRange>();
        while (await reader.ReadAsync(cancellationToken))
        {
            partitions.Add(new PartitionRange(
                reader.GetString(0),
                AsUtc(reader.GetDateTime(1)),
                AsUtc(reader.GetDateTime(2))));
        }
        return partitions;
    }

    private static async Task RequireManagedParentAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var partitioned = await ScalarAsync<bool>(
            connection,
            transaction,
            """
            SELECT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_class c
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public' AND c.relname = 'audit_events' AND c.relkind = 'p'
            )
            """,
            cancellationToken);
        if (!partitioned || !await RelationExistsAsync(connection, transaction, "audit_event_ids", cancellationToken))
        {
            throw new InvalidOperationException("Run MigrateAsync with the migration role first.");
        }
    }

    private static async Task AssertExactCopyAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var mismatch = await ScalarAsync<bool>(
            connection,
            transaction,
            """
            SELECT EXISTS (
                (SELECT "Id", "OrganizationId", "PrincipalId", "Action", "ResourceType", "ResourceId", "OccurredAt", "Detail"
                 FROM audit_events_unpartitioned_backup
                 EXCEPT ALL
                 SELECT "Id", "OrganizationId", "PrincipalId", "Action", "ResourceType", "ResourceId", "OccurredAt", "Detail"
                 FROM audit_events)
                UNION ALL
                (SELECT "Id", "OrganizationId", "PrincipalId", "Action", "ResourceType", "ResourceId", "OccurredAt", "Detail"
                 FROM audit_events
                 EXCEPT ALL
                 SELECT "Id", "OrganizationId", "PrincipalId", "Action", "ResourceType", "ResourceId", "OccurredAt", "Detail"
                 FROM audit_events_unpartitioned_backup)
            )
            """,
            cancellationToken);
        if (mismatch)
        {
            throw new InvalidOperationException("Audit migration validation failed; no schema change was committed.");
        }
    }

    private static async Task AssertBackupContainedAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var mismatch = await ScalarAsync<bool>(
            connection,
            transaction,
            """
            SELECT EXISTS (
                SELECT "Id", "OrganizationId", "PrincipalId", "Action", "ResourceType", "ResourceId", "OccurredAt", "Detail"
                FROM audit_events_unpartitioned_backup
                EXCEPT ALL
                SELECT "Id", "OrganizationId", "PrincipalId", "Action", "ResourceType", "ResourceId", "OccurredAt", "Detail"
                FROM audit_events
            )
            """,
            cancellationToken);
        if (mismatch)
        {
            throw new InvalidOperationException("The audit rollback copy does not match migrated rows.");
        }
    }

    private static async Task AssertIdentityRegistryAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var mismatch = await ScalarAsync<bool>(
            connection,
            transaction,
            """
            SELECT EXISTS (
                (SELECT "Id", "OccurredAt" FROM audit_events
                 EXCEPT SELECT "Id", "OccurredAt" FROM audit_event_ids)
                UNION ALL
                (SELECT "Id", "OccurredAt" FROM audit_event_ids
                 EXCEPT SELECT "Id", "OccurredAt" FROM audit_events)
            )
            """,
            cancellationToken);
        if (mismatch)
        {
            throw new InvalidOperationException("The audit identity registry does not match audit_events.");
        }
    }

    private static Task<bool> RelationExistsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string relation,
        CancellationToken cancellationToken) =>
        ScalarAsync<bool>(
            connection,
            transaction,
            "SELECT pg_catalog.to_regclass('public.' || @relation) IS NOT NULL",
            cancellationToken,
            ("relation", relation));

    private static Task<string?> RelationKindAsync(
        DbConnection connection,
        DbTransaction transaction,
        string relation,
        CancellationToken cancellationToken) =>
        ScalarAsync<string?>(
            connection,
            transaction,
            """
            SELECT c.relkind::text
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relname = @relation
            """,
            cancellationToken,
            ("relation", relation));

    private static async Task ExecuteAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = CreateCommand(connection, transaction, sql, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<T> ScalarAsync<T>(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = CreateCommand(connection, transaction, sql, parameters);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null or DBNull)
        {
            return default!;
        }
        return (T)result;
    }

    private static DbCommand CreateCommand(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        return command;
    }

    private static DateTimeOffset MonthStart(DateTimeOffset value) =>
        new(value.Year, value.Month, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static void EnsureUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Audit partition clocks must be UTC.", nameof(value));
        }
    }

    private enum AuditPartitionPhase
    {
        Migrated,
        Finalized,
        RolledBack
    }

    private sealed record AuditPartitionState(AuditPartitionPhase Phase);

    private sealed record PartitionRange(string Name, DateTimeOffset Start, DateTimeOffset End);
}
