using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace LakeWright.Multitenancy;

/// <summary>Controls audit partition creation and retention.</summary>
public sealed class AuditPartitionOptions
{
    /// <summary>Number of calendar years to retain. The documented default is seven years.</summary>
    public int RetentionYears { get; init; } = 7;

    /// <summary>Number of future calendar months to pre-create.</summary>
    public int FutureMonths { get; init; } = 2;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(RetentionYears, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(RetentionYears, 100);
        ArgumentOutOfRangeException.ThrowIfLessThan(FutureMonths, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(FutureMonths, 24);
    }
}

/// <summary>Result of one migration-role maintenance run.</summary>
/// <param name="CreatedPartitions">Partitions created by the run.</param>
/// <param name="DroppedPartitions">Expired partitions dropped by the run.</param>
public sealed record AuditPartitionMaintenanceResult(int CreatedPartitions, int DroppedPartitions);

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
public static class DatabasePartitioning
{
    private const long MaintenanceLockKey = 4_817_191_033_702_026_091L;

    private const string InstallHelpersSql = """
        CREATE TABLE IF NOT EXISTS audit_event_partitions (
            "PartitionName" text PRIMARY KEY,
            "StartsAt" timestamptz NOT NULL UNIQUE,
            "EndsAt" timestamptz NOT NULL UNIQUE,
            CONSTRAINT "CK_audit_event_partitions_bounds" CHECK ("StartsAt" < "EndsAt")
        );

        CREATE OR REPLACE FUNCTION lakewright_create_audit_partition(p_start timestamptz)
        RETURNS boolean
        LANGUAGE plpgsql
        SECURITY INVOKER
        SET search_path = pg_catalog, public
        AS $function$
        DECLARE
            partition_start timestamptz := date_trunc('month', p_start);
            partition_end timestamptz := partition_start + interval '1 month';
            partition_name text := 'audit_events_' || to_char(partition_start, 'YYYY_MM');
            index_name text := partition_name || '_org_occurred';
            already_present boolean;
        BEGIN
            IF partition_start <> p_start THEN
                RAISE EXCEPTION 'partition start must be the first instant of a UTC month';
            END IF;

            SELECT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_inherits i
                JOIN pg_catalog.pg_class child ON child.oid = i.inhrelid
                JOIN pg_catalog.pg_class parent ON parent.oid = i.inhparent
                JOIN pg_catalog.pg_namespace namespace ON namespace.oid = parent.relnamespace
                WHERE namespace.nspname = 'public'
                  AND parent.relname = 'audit_events'
                  AND child.relname = partition_name
            ) INTO already_present;

            IF NOT already_present THEN
                EXECUTE format(
                    'CREATE TABLE public.%I PARTITION OF public.audit_events '
                    || 'FOR VALUES FROM (%L) TO (%L)',
                    partition_name,
                    partition_start,
                    partition_end);
            END IF;

            EXECUTE format(
                'CREATE INDEX IF NOT EXISTS %I ON public.%I ("OrganizationId", "OccurredAt")',
                index_name,
                partition_name);

            INSERT INTO public.audit_event_partitions
                ("PartitionName", "StartsAt", "EndsAt")
            VALUES (partition_name, partition_start, partition_end)
            ON CONFLICT ("PartitionName") DO UPDATE
            SET "StartsAt" = EXCLUDED."StartsAt", "EndsAt" = EXCLUDED."EndsAt";

            RETURN NOT already_present;
        END;
        $function$;

        CREATE OR REPLACE FUNCTION lakewright_drop_audit_partition(
            p_name text,
            p_start timestamptz,
            p_end timestamptz)
        RETURNS void
        LANGUAGE plpgsql
        SECURITY INVOKER
        SET search_path = pg_catalog, public
        AS $function$
        BEGIN
            IF p_name <> 'audit_events_' || to_char(p_start, 'YYYY_MM')
               OR p_start <> date_trunc('month', p_start)
               OR p_end <> p_start + interval '1 month' THEN
                RAISE EXCEPTION 'refusing non-canonical audit partition %', p_name;
            END IF;

            IF NOT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_inherits i
                JOIN pg_catalog.pg_class child ON child.oid = i.inhrelid
                JOIN pg_catalog.pg_class parent ON parent.oid = i.inhparent
                JOIN pg_catalog.pg_namespace namespace ON namespace.oid = parent.relnamespace
                WHERE namespace.nspname = 'public'
                  AND parent.relname = 'audit_events'
                  AND child.relname = p_name
            ) THEN
                RAISE EXCEPTION '% is not a partition of public.audit_events', p_name;
            END IF;

            DELETE FROM public.audit_event_ids
            WHERE "OccurredAt" >= p_start AND "OccurredAt" < p_end;
            EXECUTE format('DROP TABLE public.%I', p_name);
            DELETE FROM public.audit_event_partitions WHERE "PartitionName" = p_name;
        END;
        $function$;

        REVOKE ALL ON FUNCTION lakewright_create_audit_partition(timestamptz) FROM PUBLIC;
        REVOKE ALL ON FUNCTION lakewright_drop_audit_partition(text, timestamptz, timestamptz) FROM PUBLIC;
        """;

    private const string CreateParentSql = """
        CREATE TABLE audit_events (
            "Id" uuid NOT NULL,
            "OrganizationId" uuid NULL,
            "PrincipalId" varchar(200) NOT NULL,
            "Action" varchar(100) NOT NULL,
            "ResourceType" varchar(100) NOT NULL,
            "ResourceId" varchar(200) NULL,
            "OccurredAt" timestamptz NOT NULL,
            "Detail" jsonb NULL
        ) PARTITION BY RANGE ("OccurredAt");

        CREATE TABLE audit_event_ids (
            "Id" uuid PRIMARY KEY,
            "OccurredAt" timestamptz NOT NULL
        );
        """;

    private const string CopyRowsSql = """
        INSERT INTO audit_events
            ("Id", "OrganizationId", "PrincipalId", "Action", "ResourceType", "ResourceId", "OccurredAt", "Detail")
        SELECT "Id", "OrganizationId", "PrincipalId", "Action", "ResourceType", "ResourceId", "OccurredAt", "Detail"
        FROM audit_events_unpartitioned_backup;

        INSERT INTO audit_event_ids ("Id", "OccurredAt")
        SELECT "Id", "OccurredAt" FROM audit_events_unpartitioned_backup;
        """;

    private const string InstallIdentityTriggerSql = """
        CREATE OR REPLACE FUNCTION lakewright_register_audit_event_id()
        RETURNS trigger
        LANGUAGE plpgsql
        SECURITY DEFINER
        SET search_path = pg_catalog, public
        AS $function$
        BEGIN
            INSERT INTO public.audit_event_ids ("Id", "OccurredAt")
            VALUES (NEW."Id", NEW."OccurredAt");
            RETURN NEW;
        END;
        $function$;

        REVOKE ALL ON FUNCTION lakewright_register_audit_event_id() FROM PUBLIC;
        DROP TRIGGER IF EXISTS lakewright_register_audit_event_id ON audit_events;
        CREATE TRIGGER lakewright_register_audit_event_id
            BEFORE INSERT ON audit_events
            FOR EACH ROW EXECUTE FUNCTION lakewright_register_audit_event_id();
        """;

    private const string CopySecuritySql = """
        DO $block$
        DECLARE
            source_table regclass := 'public.audit_events_unpartitioned_backup'::regclass;
            grant_row record;
            policy_row record;
            command_name text;
            role_list text;
        BEGIN
            FOR grant_row IN
                SELECT grantee, privilege_type, is_grantable
                FROM information_schema.role_table_grants
                WHERE table_schema = 'public'
                  AND table_name = 'audit_events_unpartitioned_backup'
                  AND grantee <> current_user
            LOOP
                IF grant_row.privilege_type NOT IN
                    ('SELECT', 'INSERT', 'UPDATE', 'DELETE', 'TRUNCATE', 'REFERENCES', 'TRIGGER') THEN
                    RAISE EXCEPTION 'unsupported audit_events privilege %', grant_row.privilege_type;
                END IF;

                EXECUTE format(
                    'GRANT %s ON TABLE public.audit_events TO %s%s',
                    grant_row.privilege_type,
                    CASE WHEN grant_row.grantee = 'PUBLIC' THEN 'PUBLIC'
                         ELSE format('%I', grant_row.grantee) END,
                    CASE WHEN grant_row.is_grantable = 'YES' THEN ' WITH GRANT OPTION' ELSE '' END);

                -- The rollback copy is evidence for the migration role, not a second append
                -- surface for the application. Move every explicit grant to the new parent.
                EXECUTE format(
                    'REVOKE ALL ON TABLE public.audit_events_unpartitioned_backup FROM %s',
                    CASE WHEN grant_row.grantee = 'PUBLIC' THEN 'PUBLIC'
                         ELSE format('%I', grant_row.grantee) END);
            END LOOP;

            FOR policy_row IN
                SELECT p.polname,
                       p.polpermissive,
                       p.polcmd,
                       pg_catalog.pg_get_expr(p.polqual, p.polrelid) AS using_expression,
                       pg_catalog.pg_get_expr(p.polwithcheck, p.polrelid) AS check_expression,
                       ARRAY(
                           SELECT CASE WHEN role_oid = 0 THEN 'PUBLIC'
                                       ELSE format('%I', role.rolname) END
                           FROM unnest(p.polroles) role_oid
                           LEFT JOIN pg_catalog.pg_roles role ON role.oid = role_oid
                       ) AS roles
                FROM pg_catalog.pg_policy p
                WHERE p.polrelid = source_table
            LOOP
                command_name := CASE policy_row.polcmd
                    WHEN 'r' THEN 'SELECT'
                    WHEN 'a' THEN 'INSERT'
                    WHEN 'w' THEN 'UPDATE'
                    WHEN 'd' THEN 'DELETE'
                    WHEN '*' THEN 'ALL'
                    ELSE NULL
                END;

                IF command_name IS NULL THEN
                    RAISE EXCEPTION 'unsupported row-security command %', policy_row.polcmd;
                END IF;

                SELECT string_agg(role_name, ', ') INTO role_list
                FROM unnest(policy_row.roles) role_name;

                EXECUTE format(
                    'CREATE POLICY %I ON public.audit_events AS %s FOR %s TO %s%s%s',
                    policy_row.polname,
                    CASE WHEN policy_row.polpermissive THEN 'PERMISSIVE' ELSE 'RESTRICTIVE' END,
                    command_name,
                    role_list,
                    CASE WHEN policy_row.using_expression IS NULL THEN ''
                         ELSE ' USING (' || policy_row.using_expression || ')' END,
                    CASE WHEN policy_row.check_expression IS NULL THEN ''
                         ELSE ' WITH CHECK (' || policy_row.check_expression || ')' END);
            END LOOP;

            IF (SELECT relrowsecurity FROM pg_catalog.pg_class WHERE oid = source_table) THEN
                ALTER TABLE audit_events ENABLE ROW LEVEL SECURITY;
            END IF;
            IF (SELECT relforcerowsecurity FROM pg_catalog.pg_class WHERE oid = source_table) THEN
                ALTER TABLE audit_events FORCE ROW LEVEL SECURITY;
            END IF;
        END;
        $block$;
        """;

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
            await ExecuteAsync(connection, transaction, InstallHelpersSql, cancellationToken);

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

            await ExecuteAsync(
                connection,
                transaction,
                "LOCK TABLE audit_events IN ACCESS EXCLUSIVE MODE; ALTER TABLE audit_events RENAME TO audit_events_unpartitioned_backup;",
                cancellationToken);
            await ExecuteAsync(connection, transaction, CreateParentSql, cancellationToken);

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

            await ExecuteAsync(connection, transaction, CopyRowsSql, cancellationToken);
            await AssertExactCopyAsync(connection, transaction, cancellationToken);
            await ExecuteAsync(connection, transaction, InstallIdentityTriggerSql, cancellationToken);
            await ExecuteAsync(connection, transaction, CopySecuritySql, cancellationToken);
        }, cancellationToken);
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
            await RequireManagedParentAsync(connection, transaction, cancellationToken);
            created = await EnsureWindowAsync(connection, transaction, now, options.FutureMonths, cancellationToken);

            var expired = await QueryPartitionsAsync(
                connection, transaction, now.AddYears(-options.RetentionYears), cancellationToken);
            if (expired.Count > 0 && await RelationExistsAsync(
                    connection, transaction, "audit_events_unpartitioned_backup", cancellationToken))
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
        }, cancellationToken);

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
            await RequireManagedParentAsync(connection, transaction, cancellationToken);
            if (await RelationExistsAsync(
                    connection, transaction, "audit_events_unpartitioned_backup", cancellationToken))
            {
                await AssertBackupContainedAsync(connection, transaction, cancellationToken);
            }
            await AssertIdentityRegistryAsync(connection, transaction, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>Deletes the validated rollback copy so retention maintenance can begin.</summary>
    public static async Task FinalizeMigrationAsync(
        LakeWrightDbContext db,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        await ExecuteInTransactionAsync(db, async (connection, transaction) =>
        {
            await AcquireLockAsync(connection, transaction, cancellationToken);
            await RequireManagedParentAsync(connection, transaction, cancellationToken);
            if (!await RelationExistsAsync(
                    connection, transaction, "audit_events_unpartitioned_backup", cancellationToken))
            {
                return;
            }
            await AssertBackupContainedAsync(connection, transaction, cancellationToken);
            await AssertIdentityRegistryAsync(connection, transaction, cancellationToken);
            await ExecuteAsync(connection, transaction, "DROP TABLE audit_events_unpartitioned_backup", cancellationToken);
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
        CancellationToken cancellationToken)
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

    private sealed record PartitionRange(string Name, DateTimeOffset Start, DateTimeOffset End);
}
