using System.Data.Common;

namespace LakeWright.DatabaseMaintenance;

internal static partial class DatabasePartitioning
{
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
        var rollbackKind = hasRollback
            ? await RelationKindAsync(
                connection, transaction, "audit_events_partitioned_rollback", cancellationToken)
            : null;
        var valid = state.Phase switch
        {
            AuditPartitionPhase.Migrated => canonicalKind == "p" && hasBackup && !hasRollback,
            AuditPartitionPhase.Finalized => canonicalKind == "p" && !hasBackup && !hasRollback,
            AuditPartitionPhase.RolledBack => canonicalKind == "r" && !hasBackup && rollbackKind == "p",
            _ => false
        };
        var triggerTable = state.Phase == AuditPartitionPhase.RolledBack
            ? "audit_events_partitioned_rollback"
            : "audit_events";
        valid = valid && await ScalarAsync<bool>(
            connection,
            transaction,
            """
            SELECT pg_catalog.to_regclass('public.lakewright_audit_partition_state') IS NOT NULL
               AND pg_catalog.to_regclass('public.audit_event_partitions') IS NOT NULL
               AND pg_catalog.to_regclass('public.audit_event_ids') IS NOT NULL
               AND pg_catalog.to_regprocedure('public.lakewright_create_audit_partition(timestamptz)') IS NOT NULL
               AND pg_catalog.to_regprocedure('public.lakewright_drop_audit_partition(text,timestamptz,timestamptz)') IS NOT NULL
               AND pg_catalog.to_regprocedure('public.lakewright_register_audit_event_id()') IS NOT NULL
               AND EXISTS (
                   SELECT 1
                   FROM pg_catalog.pg_index index_row
                   JOIN pg_catalog.pg_attribute attribute
                     ON attribute.attrelid = index_row.indrelid
                    AND attribute.attnum = index_row.indkey[0]
                   WHERE index_row.indrelid = pg_catalog.to_regclass('public.audit_event_ids')
                     AND index_row.indisprimary
                     AND index_row.indisunique
                     AND index_row.indisvalid
                     AND index_row.indnkeyatts = 1
                     AND attribute.attname = 'Id')
               AND EXISTS (
                   SELECT 1
                   FROM pg_catalog.pg_index index_row
                   JOIN pg_catalog.pg_attribute attribute
                     ON attribute.attrelid = index_row.indrelid
                    AND attribute.attnum = index_row.indkey[0]
                   WHERE index_row.indexrelid = pg_catalog.to_regclass('public.audit_event_ids_occurred_at')
                     AND index_row.indrelid = pg_catalog.to_regclass('public.audit_event_ids')
                     AND index_row.indisvalid
                     AND index_row.indnkeyatts = 1
                     AND attribute.attname = 'OccurredAt')
               AND EXISTS (
                   SELECT 1
                   FROM pg_catalog.pg_trigger trigger_row
                   JOIN pg_catalog.pg_class parent ON parent.oid = trigger_row.tgrelid
                   JOIN pg_catalog.pg_namespace namespace ON namespace.oid = parent.relnamespace
                   WHERE namespace.nspname = 'public'
                     AND parent.relname = @trigger_table
                     AND trigger_row.tgname = 'lakewright_register_audit_event_id'
                     AND trigger_row.tgfoid = pg_catalog.to_regprocedure('public.lakewright_register_audit_event_id()')
                     AND trigger_row.tgtype = 7
                     AND trigger_row.tgenabled IN ('O', 'A')
                     AND NOT trigger_row.tgisinternal)
               AND EXISTS (
                   SELECT 1
                   FROM pg_catalog.pg_proc function_row
                   WHERE function_row.oid = pg_catalog.to_regprocedure('public.lakewright_register_audit_event_id()')
                     AND function_row.prosecdef)
            """,
            cancellationToken,
            ("trigger_table", triggerTable));
        if (!valid)
        {
            throw new InvalidOperationException(
                $"Audit partition lifecycle '{state.Phase}' does not match the database topology.");
        }
    }

    private static Task CleanupRollbackAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
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

    private enum AuditPartitionPhase
    {
        Migrated,
        Finalized,
        RolledBack
    }

    private sealed record AuditPartitionState(AuditPartitionPhase Phase);
}
