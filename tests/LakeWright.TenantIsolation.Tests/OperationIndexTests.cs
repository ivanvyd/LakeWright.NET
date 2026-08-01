using LakeWright.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// Guards the index the claim query depends on.
/// </summary>
/// <remarks>
/// A performance review measured the claim without it against Postgres 17: 25.6ms at a
/// 50,000-row backlog, and 141.7ms at 300,000 with the sort spilling to disk. Every worker pays
/// that on every claim, and the backlog is largest exactly when recovering from an incident.
/// With the partial index the same plan reads straight from the index at 0.58ms.
///
/// A test rather than a comment because an index is easy to drop in a later migration and
/// nothing else here would notice.
/// </remarks>
[Trait("Category", "TenantIsolation")]
[Collection(nameof(PostgresTests))]
public class OperationIndexTests(PostgresFixture postgres)
{
    [Fact]
    public async Task The_claim_query_ordering_is_backed_by_a_partial_index()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.NewDatabaseAsync();

        // Act
        var indexes = await db.Database
            .SqlQuery<string>($"SELECT indexdef AS \"Value\" FROM pg_indexes WHERE tablename = 'operations'")
            .ToListAsync(ct);

        // Assert - partial, so it stays small however large the table grows.
        indexes.ShouldContain(
            i => i.Contains("IX_operations_claimable", StringComparison.Ordinal)
              && i.Contains("CreatedAt", StringComparison.Ordinal)
              && i.Contains("WHERE", StringComparison.Ordinal),
            "the claim query orders by CreatedAt and sorts every pending row without this");
    }
}
