using Microsoft.EntityFrameworkCore;

namespace LakeWright.Multitenancy;

/// <summary>
/// Replaces the EF-created <c>audit_events</c> table with a Postgres-native partitioned table.
/// </summary>
/// <remarks>
/// <para>
/// The audit table grows without bound: a row per state transition, every claim, every external
/// statement id. The append-only guarantee in <see cref="LakeWrightDbContext"/> plus the
/// <c>REVOKE UPDATE, DELETE</c> in <see cref="DatabaseHardening"/> keeps the rows honest; they
/// do nothing about their size. A single unpartitioned table turns a 12-month retention sweep
/// into a <c>DELETE</c> the size of the table, locks everything, and the auditor is the person
/// who notices.
/// </para>
/// <para>
/// Postgres native range partitioning by <c>OccurredAt</c> keeps each month's rows in a
/// physically separate child table. Dropping a partition is a fast metadata operation; a
/// retention sweep becomes a <c>DROP TABLE</c> on a single child rather than a <c>DELETE</c>
/// on the whole.
/// </para>
/// <para>
/// The flow:
/// </para>
/// <list type="number">
/// <item><description>EF's <c>EnsureCreatedAsync</c> builds a non-partitioned <c>audit_events</c>.</description></item>
/// <item><description>This method drops the EF table and recreates it as a partitioned parent.</description></item>
/// <item><description>It creates the current month's child partition and indexes it.</description></item>
/// <item><description>Future partitions are created on demand by <see cref="EnsureCurrentAndNextMonthAsync"/>.</description></item>
/// </list>
/// <para>
/// The method is idempotent: a database that already has a partitioned <c>audit_events</c> is
/// left alone. That matters because the harness reuses a Postgres container, and the production
/// migration re-runs the same DDL on each deploy.
/// </para>
/// </remarks>
public static class DatabasePartitioning
{
    /// <summary>
    /// Ensures <c>audit_events</c> is a partitioned table with a child for the current month.
    /// </summary>
    /// <param name="db">Context connected as a role that owns the schema (the migration role).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task EnsurePartitionedAuditAsync(
        LakeWrightDbContext db,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (await IsPartitionedAsync(db, cancellationToken))
        {
            await EnsureCurrentAndNextMonthAsync(db, cancellationToken);
            return;
        }

        // EF created audit_events as a plain table on EnsureCreatedAsync. Drop it and rebuild
        // partitioned. The model only has one parent table; tests run against fresh databases,
        // so there is never data to copy. A production migration is a different problem and a
        // different change.
        await db.Database.ExecuteSqlRawAsync(
            """
            DROP TABLE IF EXISTS audit_events;
            CREATE TABLE audit_events (
                "Id"            uuid        NOT NULL,
                "OrganizationId" uuid        NULL,
                "PrincipalId"   varchar(200) NOT NULL,
                "Action"        varchar(100) NOT NULL,
                "ResourceType"  varchar(100) NOT NULL,
                "ResourceId"    varchar(200) NULL,
                "OccurredAt"    timestamptz NOT NULL,
                "Detail"        jsonb        NULL,
                PRIMARY KEY ("Id", "OccurredAt")
            ) PARTITION BY RANGE ("OccurredAt");
            """,
            cancellationToken);

        await EnsureCurrentAndNextMonthAsync(db, cancellationToken);
    }

    /// <summary>
    /// Creates the child partitions for the current and next month if they do not exist.
    /// </summary>
    /// <param name="db">Context connected as the schema owner.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task EnsureCurrentAndNextMonthAsync(
        LakeWrightDbContext db,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        // Two partitions ahead of the current month: this month's, next month's. The
        // application calls this on startup, so the latency between "month rolled over" and
        // "no partition for the new row" is the time between deploys. Keeping next month
        // around covers an unusual case where a row's OccurredAt lands just after a boundary
        // while this month's partition is being filled.
        var now = DateTimeOffset.UtcNow;
        await EnsureMonthAsync(db, now.Year, now.Month, cancellationToken);
        var next = now.AddMonths(1);
        await EnsureMonthAsync(db, next.Year, next.Month, cancellationToken);
    }

    /// <summary>
    /// Creates the child partition for one month if it does not exist, and indexes it.
    /// </summary>
    /// <param name="year">The partition's calendar year (UTC).</param>
    /// <param name="month">The partition's calendar month, 1..12 (UTC).</param>
    /// <param name="db">Context connected as the schema owner.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task EnsureMonthAsync(
        LakeWrightDbContext db,
        int year,
        int month,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentOutOfRangeException.ThrowIfLessThan(year, 2000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(year, 2100);
        ArgumentOutOfRangeException.ThrowIfLessThan(month, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(month, 12);

        var (start, end) = MonthBounds(year, month);
        var partitionName = $"audit_events_{year:D4}_{month:D2}";

        // CREATE TABLE IF NOT EXISTS is idempotent. The index creation below is not — it would
        // create a duplicate-index error — so the existence check is the gate.
#pragma warning disable EF1002, EF1003
        await db.Database.ExecuteSqlRawAsync(
            $"""
            CREATE TABLE IF NOT EXISTS {partitionName}
            PARTITION OF audit_events
            FOR VALUES FROM ('{start:o}') TO ('{end:o}');

            CREATE INDEX IF NOT EXISTS "{partitionName}_org_occurred"
            ON {partitionName} ("OrganizationId", "OccurredAt");
            """,
            cancellationToken);
#pragma warning restore EF1002, EF1003
    }

    private static (DateTimeOffset Start, DateTimeOffset End) MonthBounds(int year, int month)
    {
        // timestamptz expects ISO-8601; the `o` format spec produces one with offset. UTC is
        // the only sensible choice for a partition key that is read across timezones, and the
        // model's OccurredAt is DateTimeOffset in UTC.
        var start = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddMonths(1);
        return (start, end);
    }

    private static async Task<bool> IsPartitionedAsync(
        LakeWrightDbContext db,
        CancellationToken cancellationToken)
    {
        // pg_partitioned_table has a row for every partitioned table in the current database.
        // A non-partitioned audit_events has no row there. Checking by name keeps the call
        // independent of the partition key — the DDL above says RANGE (OccurredAt) but a future
        // change might re-key it.
#pragma warning disable EF1002
        var present = await db.Database
            .SqlQueryRaw<int>(
                """
                SELECT 1 AS "Value"
                FROM pg_class c
                JOIN pg_partitioned_table pt ON pt.partrelid = c.oid
                WHERE c.relname = 'audit_events'
                """)
            .AnyAsync(cancellationToken);
#pragma warning restore EF1002
        return present;
    }

    /// <summary>
    /// Test-visible version of the partition check. The private helper is private because
    /// callers should use <see cref="EnsurePartitionedAuditAsync"/>; tests that need to assert
    /// the property can use this public surface.
    /// </summary>
    public static Task<bool> IsPartitionedForTestAsync(
        LakeWrightDbContext db,
        CancellationToken cancellationToken = default) =>
        IsPartitionedAsync(db, cancellationToken);
}
