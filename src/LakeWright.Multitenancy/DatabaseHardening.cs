using Microsoft.EntityFrameworkCore;

namespace LakeWright.Multitenancy;

/// <summary>
/// Database-level grants that make the append-only guarantee real.
/// </summary>
/// <remarks>
/// The change-tracker guard in <see cref="LakeWrightDbContext"/> cannot see
/// <c>ExecuteUpdate</c>, <c>ExecuteDelete</c> or raw SQL, because none of them go through
/// <c>SaveChanges</c>. Only the database can refuse those, so the append-only claim in
/// <c>docs/compliance/soc2-mapping.md</c> depends on this running.
///
/// It has to be applied to a role that does not own the table. A table's owner keeps implicit
/// privileges that <c>REVOKE</c> does not remove, so hardening a schema while connected as its
/// owner produces a configuration that looks locked down and is not. The application therefore
/// connects as a role distinct from the migration role, which is worth stating plainly because it
/// is the part most likely to be skipped in a hurry.
/// </remarks>
public static class DatabaseHardening
{
    /// <summary>
    /// Creates the application role if absent and grants it exactly what the application needs.
    /// </summary>
    /// <param name="db">Context connected as a role that owns the tables.</param>
    /// <param name="applicationRole">Role the application connects as. Must not own the tables.</param>
    /// <param name="password">Password for the role when it is being created.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task ApplyAsync(
        LakeWrightDbContext db,
        string applicationRole,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        RoleName.Validate(applicationRole);

        // `CREATE ROLE` has no `IF NOT EXISTS`, and wrapping it in a DO block puts PL/pgSQL's `$$`
        // into a C# string. Asking first is less clever and easier to read.
        var exists = await db.Database
            .SqlQueryRaw<int>("SELECT 1 AS \"Value\" FROM pg_roles WHERE rolname = {0}", applicationRole)
            .AnyAsync(cancellationToken);

        // EF1002/EF1003 warn about raw SQL, correctly. This is DDL: Postgres has no parameter form
        // for a role or table name, so `GRANT ... TO {role}` cannot be parameterised by anything.
        // The role name is validated against an identifier pattern above and the password is
        // quote-escaped. This is the only place in the codebase that builds SQL from a string, and
        // it runs at deployment rather than on a request.
#pragma warning disable EF1002, EF1003
        if (!exists)
        {
            // The role name is validated as an identifier because Postgres has no parameter form
            // for one. The password does have one nowhere it is used here, so it is escaped the
            // way quote_literal would.
            await db.Database.ExecuteSqlRawAsync(
                "CREATE ROLE " + applicationRole + " LOGIN PASSWORD " + QuoteLiteral(password),
                cancellationToken);
        }

        await db.Database.ExecuteSqlRawAsync(
            $"""
             GRANT USAGE ON SCHEMA public TO {applicationRole};

             -- DELETE only on organizations. Deleting a tenant removes that row and the foreign
             -- keys cascade to memberships and operations, so the application never needs to
             -- delete from those directly. Granting it anyway would leave a mis-scoped admin
             -- endpoint able to strip another tenant's memberships without ever touching the
             -- organizations row. Verified that the cascade fires without the extra grants.
             GRANT SELECT, INSERT, UPDATE, DELETE ON organizations TO {applicationRole};
             GRANT SELECT, INSERT, UPDATE ON memberships, operations TO {applicationRole};

             -- The whole point. Insert and read only; no route to amend history.
             REVOKE ALL ON audit_events FROM {applicationRole};
             GRANT SELECT, INSERT ON audit_events TO {applicationRole};
             """,
            cancellationToken);
#pragma warning restore EF1002, EF1003
    }

    /// <summary>
    /// Postgres has no parameter form for a literal in a DO block, so the password is escaped by
    /// doubling quotes, the same thing <c>quote_literal</c> does.
    /// </summary>
    private static string QuoteLiteral(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}

internal static partial class RoleName
{
    [System.Text.RegularExpressions.GeneratedRegex(@"^[a-z][a-z0-9_]{0,62}\z")]
    private static partial System.Text.RegularExpressions.Regex Pattern { get; }

    public static void Validate(string role)
    {
        if (!Pattern.IsMatch(role))
        {
            throw new ArgumentException(
                $"'{role}' is not a valid role name. It is interpolated into DDL because Postgres " +
                "has no parameter form for an identifier, so it is restricted to lowercase " +
                "letters, digits and underscores.",
                nameof(role));
        }
    }
}
