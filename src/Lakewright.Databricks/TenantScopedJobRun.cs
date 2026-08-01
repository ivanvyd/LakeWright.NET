using Lakewright.Core.Tenancy;

namespace Lakewright.Databricks;

/// <summary>
/// A Lakeflow job run requested for exactly one tenant.
/// </summary>
/// <remarks>
/// Same shape as <see cref="TenantScopedStatement"/> and for the same reason: it cannot be built
/// without a <see cref="TenantContext"/>, and <see cref="IJobSubmitter"/> accepts nothing else.
///
/// The tenant reaches the job as a parameter rather than through the connection, because a job runs
/// as the workspace identity and has no notion of the request that started it. Whatever the job does
/// with that parameter is the job's business; this type's job is to make it impossible to submit
/// without one.
/// </remarks>
public readonly struct TenantScopedJobRun
{
    private TenantScopedJobRun(
        TenantContext tenant,
        long jobId,
        string idempotencyKey,
        IReadOnlyDictionary<string, string> parameters)
    {
        Tenant = tenant;
        JobId = jobId;
        IdempotencyKey = idempotencyKey;
        Parameters = parameters;
    }

    public TenantContext Tenant { get; }

    public long JobId { get; }

    /// <summary>
    /// Sent as the Jobs API idempotency token. Capped at 64 characters by the API, and it has no
    /// documented deduplication window, which is why reconciliation exists rather than trusting it.
    /// </summary>
    public string IdempotencyKey { get; }

    public IReadOnlyDictionary<string, string> Parameters { get; }

    /// <summary>Tenant identity and catalog location, passed to the job as parameters.</summary>
    public const string TenantIdParameter = "lakewright_tenant_id";
    public const string CatalogParameter = "lakewright_catalog";
    public const string SchemaParameter = "lakewright_schema";

    public static TenantScopedJobRun Create(
        TenantContext tenant,
        long jobId,
        string idempotencyKey,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        if (idempotencyKey.Length > 64)
        {
            throw new ArgumentException(
                "The Jobs API caps idempotency_token at 64 characters.", nameof(idempotencyKey));
        }

        var all = new Dictionary<string, string>(parameters ?? new Dictionary<string, string>(), StringComparer.Ordinal)
        {
            [TenantIdParameter] = tenant.TenantId.ToString(),
            [CatalogParameter] = tenant.Catalog,
            [SchemaParameter] = tenant.Schema
        };

        return new TenantScopedJobRun(tenant, jobId, idempotencyKey, all);
    }
}
