using Lakewright.Core.Tenancy;
using Lakewright.Multitenancy;
using Lakewright.Multitenancy.Model;
using Microsoft.EntityFrameworkCore;

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

    public const string Alice = "demo|alice";   // Admin at Acme
    public const string Vera = "demo|vera";     // Viewer at Acme
    public const string Bob = "demo|bob";       // Admin at Globex

    public static async Task SeedDemoTenantsAsync(this IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LakewrightDbContext>();

        await db.Database.EnsureCreatedAsync();

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
