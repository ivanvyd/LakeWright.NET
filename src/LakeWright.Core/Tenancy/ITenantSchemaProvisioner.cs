namespace LakeWright.Core.Tenancy;

/// <summary>
/// Creates and drops the Unity Catalog schema that holds one tenant's data.
/// </summary>
/// <remarks>
/// The one place in this library that issues DDL, and the one place that runs without a
/// <see cref="TenantContext"/> — because at provisioning time there is no membership to resolve
/// and no member to resolve it for. That makes it the exception to the rule everything else
/// follows, so it is a separate, narrow interface rather than a method on the statement executor:
/// a caller has to reach for this deliberately.
///
/// Both names are Unity Catalog identifiers and must be validated with
/// <see cref="UnityCatalogIdentifier.Validate"/> before they reach an implementation. Identifiers
/// cannot be bound as parameters — there is no bind form for a schema name — so they are
/// interpolated, and the validation is the only thing standing between a caller and injected DDL.
///
/// Implementations are expected to be idempotent. Provisioning is retried after partial failure,
/// and a create that throws because the schema already exists turns a recoverable state into a
/// stuck one.
/// </remarks>
public interface ITenantSchemaProvisioner
{
    Task CreateAsync(string catalog, string schema, CancellationToken cancellationToken);

    /// <summary>
    /// Drops the schema and everything in it.
    /// </summary>
    /// <remarks>
    /// Irreversible, and deliberately step 3 of 5 in the documented deletion order: the tenant is
    /// already <c>PendingDeletion</c> and its operations already drained before anything calls
    /// this. See <c>docs/compliance/data-handling.md</c>.
    /// </remarks>
    Task DropAsync(string catalog, string schema, CancellationToken cancellationToken);
}
