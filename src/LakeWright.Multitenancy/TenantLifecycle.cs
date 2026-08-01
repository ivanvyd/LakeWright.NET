using System.Text.Json;
using LakeWright.Core.Tenancy;
using LakeWright.Multitenancy.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace LakeWright.Multitenancy;

/// <summary>
/// Creates tenants and removes them, in the order the compliance documentation states.
/// </summary>
/// <remarks>
/// Before this existed there was no way to create an organization outside the sample seeder and
/// the test suite: an adopter installed the library and then wrote rows into <c>organizations</c>
/// by hand. Deletion was worse — <see cref="OrganizationState.PendingDeletion"/> existed and
/// stopped reads, and nothing ever removed anything, so the SOC 2 mapping carried it as
/// *Design only*.
///
/// <see cref="ITenantSchemaProvisioner"/> is optional. Without it a tenant gets its row and no
/// Unity Catalog schema, which is the same bargain <c>AddLakeWright</c> makes everywhere else:
/// the tenancy tier works on PostgreSQL alone, and Databricks is something you add.
/// </remarks>
public sealed class TenantLifecycle(
    LakeWrightDbContext db,
    AuditLog audit,
    TimeProvider time,
    IOptions<MultitenancyOptions> tenancy,
    ITenantSchemaProvisioner? schemas = null)
{
    /// <summary>
    /// Creates a tenant, or returns the existing one with that slug.
    /// </summary>
    /// <remarks>
    /// Idempotent on the slug, because provisioning is the operation most likely to be retried
    /// after a partial failure: the row commits, the schema creation times out, and the caller
    /// tries again. A second call must finish the job rather than collide on a unique index or
    /// mint a second tenant with the same name.
    ///
    /// The unique index on the slug is what enforces that, not the lookup. Two requests arriving
    /// together both find nothing and both insert; one loses on the constraint and is answered
    /// with the winner. The first version of this had the lookup and no catch, so it was
    /// idempotent for a retry and threw for a race — the same defect this project had already
    /// fixed once in <see cref="OperationStore.CreateAsync"/> and did not carry across.
    ///
    /// The row is written first and the schema second, then the state moves to
    /// <see cref="OrganizationState.Active"/>. That order is what makes the retry safe. Creating
    /// the schema first would leave an orphan schema behind on failure, owned by nothing, with no
    /// row to find it from — and Unity Catalog schema names are globally unique, so that orphan
    /// blocks the next attempt.
    /// </remarks>
    public async Task<Organization> ProvisionAsync(
        string name,
        string slug,
        string principalId,
        CancellationToken cancellationToken)
    {
        UnityCatalogIdentifier.Validate(slug, nameof(slug));

        var organization = await db.Organizations
            .SingleOrDefaultAsync(o => o.Slug == slug, cancellationToken);

        if (organization is null)
        {
            var id = new TenantId(Guid.CreateVersion7());

            organization = new Organization
            {
                Id = id,
                Name = name,
                Slug = slug,
                Schema = UnityCatalogIdentifier.SchemaForTenant(id),
                CreatedAt = time.GetUtcNow(),
                State = OrganizationState.Provisioning
            };

            db.Organizations.Add(organization);
            audit.Record(
                id, principalId, AuditActions.TenantProvisioned,
                resourceType: nameof(Organization), resourceId: id.Value.ToString());

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException e) when (IsUniqueViolation(e))
            {
                // Clearing the tracker rather than detaching the organization alone: its audit row
                // is queued too, and leaving that behind would record a provisioning that lost.
                db.ChangeTracker.Clear();

                organization = await db.Organizations
                    .SingleOrDefaultAsync(o => o.Slug == slug, cancellationToken);

                // A unique violation with no row behind it means some other constraint failed, and
                // reporting that as a race would hide it.
                if (organization is null) { throw; }
            }
        }
        else if (organization.State != OrganizationState.Provisioning)
        {
            // Already finished, or on its way out. Re-provisioning a suspended or deleting tenant
            // by calling this again would quietly reactivate it.
            return organization;
        }

        if (schemas is not null)
        {
            await schemas.CreateAsync(tenancy.Value.Catalog, organization.Schema, cancellationToken);
        }

        organization.State = OrganizationState.Active;
        await db.SaveChangesAsync(cancellationToken);

        return organization;
    }

    private static bool IsUniqueViolation(DbUpdateException e) =>
        e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>
    /// Step 1 of deletion: stop serving the tenant, destroy nothing.
    /// </summary>
    /// <remarks>
    /// Separate call from <see cref="PurgeAsync"/> on purpose. Resolution and the claim loop both
    /// refuse a tenant in this state, so the tenant goes dark immediately while everything is
    /// still recoverable — which is the property that makes a deletion request survivable when it
    /// turns out to have been a mistake.
    /// </remarks>
    public async Task<bool> BeginDeletionAsync(
        TenantId tenantId,
        string principalId,
        CancellationToken cancellationToken)
    {
        var organization = await db.Organizations
            .SingleOrDefaultAsync(o => o.Id == tenantId, cancellationToken);

        if (organization is null) { return false; }

        organization.State = OrganizationState.PendingDeletion;
        audit.Record(
            tenantId, principalId, AuditActions.TenantDeletionRequested,
            resourceType: nameof(Organization), resourceId: tenantId.Value.ToString());

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Steps 2 to 5: drain, drop the schema, delete the rows, record it.
    /// </summary>
    /// <remarks>
    /// Refuses a tenant that is not already <see cref="OrganizationState.PendingDeletion"/>, so
    /// there is no single call that takes a live tenant to gone. Refuses one with work still in
    /// flight, because dropping a schema under a running query fails the query in a way that looks
    /// like a platform fault rather than a deletion.
    ///
    /// The audit row is written before the organization row is deleted and committed in the same
    /// transaction as the delete. Writing it after would leave a window where the tenant is gone
    /// and nothing records that anyone asked for it; the audit table survives the cascade because
    /// it holds no foreign key to <c>organizations</c>, which is why it is safe to keep a row
    /// naming a tenant that no longer exists.
    /// </remarks>
    public async Task<TenantPurgeResult> PurgeAsync(
        TenantId tenantId,
        string principalId,
        CancellationToken cancellationToken)
    {
        var organization = await db.Organizations
            .SingleOrDefaultAsync(o => o.Id == tenantId, cancellationToken);

        if (organization is null) { return TenantPurgeResult.NotFound; }

        if (organization.State != OrganizationState.PendingDeletion)
        {
            return TenantPurgeResult.NotPendingDeletion;
        }

        var inFlight = await db.Operations
            .CountAsync(o => o.OrganizationId == tenantId && o.CompletedAt == null, cancellationToken);

        if (inFlight > 0) { return TenantPurgeResult.OperationsInFlight; }

        if (schemas is not null)
        {
            await schemas.DropAsync(tenancy.Value.Catalog, organization.Schema, cancellationToken);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        audit.Record(
            tenantId, principalId, AuditActions.TenantDeleted,
            resourceType: nameof(Organization), resourceId: tenantId.Value.ToString(),
            detail: JsonSerializer.Serialize(new { slug = organization.Slug, schema = organization.Schema }));

        db.Organizations.Remove(organization);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return TenantPurgeResult.Deleted;
    }
}

/// <summary>Why a purge did or did not happen.</summary>
/// <remarks>
/// An enum rather than an exception for the refusals, because "not yet" and "not pending deletion"
/// are ordinary answers a caller acts on, not faults.
/// </remarks>
public enum TenantPurgeResult
{
    Deleted,
    NotFound,
    NotPendingDeletion,
    OperationsInFlight
}
