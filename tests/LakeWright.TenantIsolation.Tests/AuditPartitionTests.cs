using LakeWright.Core.Tenancy;
using LakeWright.Multitenancy;
using LakeWright.Multitenancy.Model;
using Microsoft.EntityFrameworkCore;
using static LakeWright.TenantIsolation.Tests.TestApi;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// <c>audit_events</c> is partitioned by month so retention sweeps are cheap.
/// </summary>
/// <remarks>
/// The audit table grows without bound and the append-only guarantee keeps its rows honest; it
/// does nothing about their size. Postgres native range partitioning by <c>OccurredAt</c> keeps
/// each month's rows in a child table; dropping a partition is a metadata operation. The
/// <see cref="PostgresFixture"/> calls <see cref="DatabasePartitioning.EnsurePartitionedAuditAsync"/>
/// on every test database, so these tests verify the property the partition manager is meant
/// to maintain.
/// </remarks>
[Collection(nameof(PostgresTests))]
public class AuditPartitionTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Audit_events_is_a_partitioned_table()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();

        // Act
        var isPartitioned = await DatabasePartitioning.IsPartitionedForTestAsync(db, ct);

        // Assert
        isPartitioned.ShouldBeTrue(
            "audit_events must be partitioned by month so a retention sweep is a DROP TABLE on a " +
            "child rather than a DELETE on the parent.");
    }

    [Fact]
    public async Task The_current_month_has_a_partition()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();

        // Act
        var partitions = await ListAuditPartitionsAsync(db, ct);

        // Assert
        var expected = $"audit_events_{DateTimeOffset.UtcNow:yyyy_MM}";
        partitions.ShouldContain(expected,
            $"the current month ({expected}) must have a partition, otherwise an insert in this " +
            "month fails with 'no partition of relation \"audit_events\" found for row'.");
    }

    [Fact]
    public async Task The_next_month_has_a_partition_pre_created()
    {
        // Arrange — pre-creating next month's partition is a deploy-time nicety, not a runtime
        // correctness property. Skipping it lets a row whose OccurredAt lands just after the
        // boundary fail until something notices.
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();

        // Act
        var partitions = await ListAuditPartitionsAsync(db, ct);
        var next = DateTimeOffset.UtcNow.AddMonths(1);

        // Assert
        partitions.ShouldContain($"audit_events_{next:yyyy_MM}");
    }

    [Fact]
    public async Task Inserts_round_trip_through_the_partitioned_table()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();
        var now = DateTimeOffset.UtcNow;

        // Act — write through the EF model. The composite key is (Id, OccurredAt), so the same
        // Id in two different months would be a key collision, not a data collision. Use
        // distinct Ids here.
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            OrganizationId = AcmeId,
            PrincipalId = Alice,
            Action = "test.action",
            ResourceType = "Test",
            ResourceId = "1",
            OccurredAt = now,
            Detail = null,
        });
        await db.SaveChangesAsync(ct);

        // Assert
        var stored = await db.AuditEvents.SingleAsync(e => e.PrincipalId == Alice, ct);
        stored.Action.ShouldBe("test.action");
        stored.OrganizationId.ShouldBe(AcmeId);
    }

    private static async Task<List<string>> ListAuditPartitionsAsync(
        LakeWrightDbContext db,
        CancellationToken cancellationToken)
    {
        var names = await db.Database
            .SqlQueryRaw<string>(
                """
                SELECT child.relname AS "Value"
                FROM pg_inherits i
                JOIN pg_class parent ON parent.oid = i.inhparent
                JOIN pg_class child ON child.oid = i.inhrelid
                WHERE parent.relname = 'audit_events'
                ORDER BY child.relname
                """)
            .ToListAsync(cancellationToken);
        return names;
    }
}
