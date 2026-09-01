using System.Data.Common;

namespace LakeWright.DatabaseMaintenance;

internal static partial class DatabasePartitioning
{
    private static async Task<AuditRlsSettings> ReadRlsSettingsAsync(
        DbConnection connection,
        DbTransaction transaction,
        AuditRelation relation,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT c.relrowsecurity, c.relforcerowsecurity
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relname = @relation
            """,
            ("relation", RelationName(relation)));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException($"Audit relation '{RelationName(relation)}' is missing.");
        }
        return new AuditRlsSettings(reader.GetBoolean(0), reader.GetBoolean(1));
    }

    private static Task DisableForceRlsAsync(
        DbConnection connection,
        DbTransaction transaction,
        AuditRelation relation,
        AuditRlsSettings settings,
        CancellationToken cancellationToken) =>
        settings.Forced
            ? ExecuteAsync(connection, transaction, RlsSql(relation, settings.Enabled, forced: false), cancellationToken)
            : Task.CompletedTask;

    private static Task ApplyRlsSettingsAsync(
        DbConnection connection,
        DbTransaction transaction,
        AuditRelation relation,
        AuditRlsSettings settings,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, RlsSql(relation, settings.Enabled, settings.Forced), cancellationToken);

    private static string RelationName(AuditRelation relation) => relation switch
    {
        AuditRelation.Canonical => "audit_events",
        AuditRelation.Backup => "audit_events_unpartitioned_backup",
        AuditRelation.Rollback => "audit_events_partitioned_rollback",
        _ => throw new ArgumentOutOfRangeException(nameof(relation))
    };

    private static string RlsSql(AuditRelation relation, bool enabled, bool forced) =>
        (relation, enabled, forced) switch
        {
            (AuditRelation.Canonical, true, true) =>
                "ALTER TABLE audit_events ENABLE ROW LEVEL SECURITY; ALTER TABLE audit_events FORCE ROW LEVEL SECURITY;",
            (AuditRelation.Canonical, true, false) =>
                "ALTER TABLE audit_events ENABLE ROW LEVEL SECURITY; ALTER TABLE audit_events NO FORCE ROW LEVEL SECURITY;",
            (AuditRelation.Canonical, false, true) =>
                "ALTER TABLE audit_events FORCE ROW LEVEL SECURITY; ALTER TABLE audit_events DISABLE ROW LEVEL SECURITY;",
            (AuditRelation.Canonical, false, false) =>
                "ALTER TABLE audit_events NO FORCE ROW LEVEL SECURITY; ALTER TABLE audit_events DISABLE ROW LEVEL SECURITY;",
            (AuditRelation.Backup, true, true) =>
                "ALTER TABLE audit_events_unpartitioned_backup ENABLE ROW LEVEL SECURITY; ALTER TABLE audit_events_unpartitioned_backup FORCE ROW LEVEL SECURITY;",
            (AuditRelation.Backup, true, false) =>
                "ALTER TABLE audit_events_unpartitioned_backup ENABLE ROW LEVEL SECURITY; ALTER TABLE audit_events_unpartitioned_backup NO FORCE ROW LEVEL SECURITY;",
            (AuditRelation.Backup, false, true) =>
                "ALTER TABLE audit_events_unpartitioned_backup FORCE ROW LEVEL SECURITY; ALTER TABLE audit_events_unpartitioned_backup DISABLE ROW LEVEL SECURITY;",
            (AuditRelation.Backup, false, false) =>
                "ALTER TABLE audit_events_unpartitioned_backup NO FORCE ROW LEVEL SECURITY; ALTER TABLE audit_events_unpartitioned_backup DISABLE ROW LEVEL SECURITY;",
            (AuditRelation.Rollback, true, true) =>
                "ALTER TABLE audit_events_partitioned_rollback ENABLE ROW LEVEL SECURITY; ALTER TABLE audit_events_partitioned_rollback FORCE ROW LEVEL SECURITY;",
            (AuditRelation.Rollback, true, false) =>
                "ALTER TABLE audit_events_partitioned_rollback ENABLE ROW LEVEL SECURITY; ALTER TABLE audit_events_partitioned_rollback NO FORCE ROW LEVEL SECURITY;",
            (AuditRelation.Rollback, false, true) =>
                "ALTER TABLE audit_events_partitioned_rollback FORCE ROW LEVEL SECURITY; ALTER TABLE audit_events_partitioned_rollback DISABLE ROW LEVEL SECURITY;",
            (AuditRelation.Rollback, false, false) =>
                "ALTER TABLE audit_events_partitioned_rollback NO FORCE ROW LEVEL SECURITY; ALTER TABLE audit_events_partitioned_rollback DISABLE ROW LEVEL SECURITY;",
            _ => throw new ArgumentOutOfRangeException(nameof(relation))
        };

    private enum AuditRelation
    {
        Canonical,
        Backup,
        Rollback
    }

    private sealed record AuditRlsSettings(bool Enabled, bool Forced);
}
