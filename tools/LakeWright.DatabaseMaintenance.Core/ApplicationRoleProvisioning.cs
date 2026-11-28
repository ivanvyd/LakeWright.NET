using LakeWright.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace LakeWright.DatabaseMaintenance;

/// <summary>
/// Prepares a database for an application that connects as a role which does not own the tables.
/// </summary>
/// <remarks>
/// <see cref="DatabaseHardening"/> is what makes <c>audit_events</c> append-only, and it only works
/// when the application is not the table owner. Run this as the owner (the migration identity)
/// before the application first starts. A database that already has the LakeWright tables, because
/// the adopter's own migrations created them, only gets the role and its grants; an empty one gets
/// the schema too, the way the reference deployment needs it.
/// </remarks>
public static class ApplicationRoleProvisioning
{
    public static async Task ApplyAsync(
        LakeWrightDbContext db,
        string applicationRole,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var creator = db.GetService<IRelationalDatabaseCreator>();
        if (!await creator.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            await creator.CreateAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!await creator.HasTablesAsync(cancellationToken).ConfigureAwait(false))
        {
            await creator.CreateTablesAsync(cancellationToken).ConfigureAwait(false);
        }

        await DatabaseHardening.ApplyAsync(db, applicationRole, password, cancellationToken).ConfigureAwait(false);
    }
}
