using System.Security.Claims;
using LakeWright.Core.Tenancy;
using LakeWright.Multitenancy;
using LakeWright.Multitenancy.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Signalboard;

/// <summary>
/// Two organizations and three people, so the isolation story is visible in one curl.
/// </summary>
/// <remarks>
/// Fixed identifiers on purpose: the README quotes them, and a sample whose credentials change on
/// every run cannot be documented.
/// </remarks>
public static class DemoTenants
{
    public static readonly TenantId Acme = TenantId.Parse("0198f000-0000-7000-8000-00000000ac11");
    public static readonly TenantId Globex = TenantId.Parse("0198f000-0000-7000-8000-00000000610b");

    public const string Alice = "demo|alice";
    public const string Vera = "demo|vera";
    public const string Bob = "demo|bob";

    /// <summary>Someone you can sign in as.</summary>
    public sealed record Person(string PrincipalId, string Name, MembershipRole Role, string Organization);

    /// <summary>
    /// The sign-in list, and the only subjects the cookie path will issue.
    /// </summary>
    /// <remarks>
    /// One list rather than a page that hardcodes the same three names next to a seeder that
    /// hardcodes them again. The seeding below reads it, so a fourth person is one entry.
    /// </remarks>
    public static readonly IReadOnlyList<Person> Everyone =
    [
        new(Alice, "Alice", MembershipRole.Admin, "Acme Logistics"),
        new(Vera, "Vera", MembershipRole.Viewer, "Acme Logistics"),
        new(Bob, "Bob", MembershipRole.Admin, "Globex Freight")
    ];

    private static readonly Dictionary<string, TenantId> TenantsByPrincipal = new(StringComparer.Ordinal)
    {
        [Alice] = Acme,
        [Vera] = Acme,
        [Bob] = Globex
    };

    public static bool IsKnown(string principalId) => TenantsByPrincipal.ContainsKey(principalId);

    /// <summary>The display name for a principal, or the principal itself if it is not seeded.</summary>
    /// <remarks>
    /// The header scheme accepts any subject, so the dashboard can show a row started by someone
    /// who is not on the list. Showing the raw identifier is honest; inventing a name is not.
    /// </remarks>
    public static string NameOf(string principalId) =>
        Everyone.FirstOrDefault(p => p.PrincipalId == principalId)?.Name ?? principalId;

    /// <summary>
    /// Builds the claims principal for a subject.
    /// </summary>
    /// <remarks>
    /// Carries the subject and a display name and nothing else. In particular it carries no
    /// organization or role claim: those are read from the database on every request, and a role
    /// in a cookie is a role the holder can keep after you revoke it.
    /// </remarks>
    public static ClaimsPrincipal PrincipalFor(string principalId)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, principalId),
                new Claim(ClaimTypes.Name, NameOf(principalId))
            ],
            authenticationType: "Demo",
            nameType: ClaimTypes.Name,
            roleType: ClaimTypes.Role);

        return new ClaimsPrincipal(identity);
    }

    public static async Task SeedDemoTenantsAsync(this IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LakeWrightDbContext>();

        await CreateSchemaAsync(db);

        if (await db.Organizations.AnyAsync()) { return; }

        var now = DateTimeOffset.UtcNow;

        db.Organizations.AddRange(
            new Organization
            {
                Id = Acme,
                Name = "Acme Logistics",
                Slug = "acme",
                CreatedAt = now,
                Schema = UnityCatalogIdentifier.SchemaForTenant(Acme),
                State = OrganizationState.Active
            },
            new Organization
            {
                Id = Globex,
                Name = "Globex Freight",
                Slug = "globex",
                CreatedAt = now,
                Schema = UnityCatalogIdentifier.SchemaForTenant(Globex),
                State = OrganizationState.Active
            });

        db.Memberships.AddRange(
            Member(Acme, Alice, MembershipRole.Admin, now),
            Member(Acme, Vera, MembershipRole.Viewer, now),
            Member(Globex, Bob, MembershipRole.Admin, now));

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Creates the tables, whether or not the database is already there.
    /// </summary>
    /// <remarks>
    /// <c>EnsureCreatedAsync</c> alone is not enough: it does nothing when the database exists, and
    /// compose creates <c>lakewright</c> through <c>POSTGRES_DB</c>. The result was an application
    /// that started and then answered 500 to everything, which is how this was found.
    ///
    /// A product uses migrations. A sample wants one command and no migration history to explain,
    /// so it asks EF for the tables directly.
    /// </remarks>
    private static async Task CreateSchemaAsync(LakeWrightDbContext db)
    {
        var creator = db.GetService<IRelationalDatabaseCreator>();

        if (!await creator.ExistsAsync())
        {
            await creator.CreateAsync();
        }

        if (!await creator.HasTablesAsync())
        {
            await creator.CreateTablesAsync();
        }
    }

    private static Membership Member(TenantId tenant, string principal, MembershipRole role, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = tenant,
            PrincipalId = principal,
            Role = role,
            CreatedAt = now
        };
}
