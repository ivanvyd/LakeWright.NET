using LakeWright.Core.Tenancy;
using LakeWright.Multitenancy.Model;

namespace LakeWright.Multitenancy;

/// <summary>
/// Writes audit rows into whatever transaction the caller is already committing.
/// </summary>
/// <remarks>
/// <see cref="Record"/> adds and does not save, on purpose. An audit trail that commits separately
/// from the thing it audits will eventually disagree with it: the action succeeds and the record is
/// lost, or the record survives an action that rolled back. Callers save once, and the row goes with
/// the action or not at all.
///
/// <see cref="AuditEvent"/> claimed this property in its own documentation before anything wrote
/// one. The claim is now true because this type exists and is called from the write paths, which is
/// a different thing from the schema being append-only — that part was always real.
/// </remarks>
public sealed class AuditLog(LakeWrightDbContext db, TimeProvider time)
{
    public void Record(
        TenantId? tenant,
        string principalId,
        string action,
        string resourceType,
        string? resourceId = null,
        string? detail = null)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = tenant,
            PrincipalId = principalId,
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId,
            OccurredAt = time.GetUtcNow(),
            Detail = detail
        });
    }
}

/// <summary>
/// The action names this library writes.
/// </summary>
/// <remarks>
/// Constants rather than literals at the call sites, because an auditor filters on these strings and
/// a typo produces a row that no query finds. Adopters are free to add their own.
/// </remarks>
public static class AuditActions
{
    public const string OperationStarted = "operation.started";
    public const string OperationCompleted = "operation.completed";

    public const string TenantProvisioned = "tenant.provisioned";

    /// <summary>
    /// Deletion was requested. The tenant stops being served here; nothing is destroyed yet.
    /// </summary>
    public const string TenantDeletionRequested = "tenant.deletion_requested";

    /// <summary>
    /// The tenant and its data are gone. This row outlives the organization it names, which is
    /// the point — it is the only remaining record that the tenant existed.
    /// </summary>
    public const string TenantDeleted = "tenant.deleted";

    /// <summary>
    /// A principal asked for a tenant it cannot reach. The response is a 404, so this row is the
    /// only place the attempt is visible.
    /// </summary>
    public const string TenantAccessDenied = "tenant.access_denied";
}

/// <summary>
/// The principal recorded for work with no request behind it.
/// </summary>
/// <remarks>
/// The worker acts for every tenant on its own schedule, so there is no user to name. Naming one
/// would be worse than admitting there isn't: an audit row attributing a background completion to
/// whoever started the operation reads as an action that person took hours later.
/// </remarks>
public static class SystemPrincipal
{
    public const string Worker = "system:operation-worker";
}
