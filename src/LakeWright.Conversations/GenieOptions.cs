using System.ComponentModel.DataAnnotations;
using LakeWright.Core.Tenancy;

namespace LakeWright.Conversations;

/// <summary>
/// The workspace, and which Genie Agent each tenant is allowed to talk to.
/// </summary>
/// <remarks>
/// <para>
/// <b>One agent per tenant, and no default.</b> The Conversation API takes no filter, no row
/// predicate and no viewer identity — a question is answered against whatever tables the agent was
/// curated with. So the agent boundary is the only tenancy boundary available, and a shared agent
/// serving two tenants is a cross-tenant read waiting for someone to ask the right question.
/// </para>
/// <para>
/// There is deliberately no fallback agent. A tenant missing from <see cref="Spaces"/> is refused
/// rather than pointed at something reasonable-looking, because the failure mode of guessing here
/// is answering one tenant's question with another tenant's data.
/// </para>
/// </remarks>
public sealed class GenieOptions
{
    /// <summary>Workspace base URL, for example <c>https://adb-123.4.azuredatabricks.net</c>.</summary>
    public string WorkspaceUrl { get; set; } = string.Empty;

    /// <summary>Tenant id to Genie Agent (space) id. Keys are tenant GUIDs in string form.</summary>
    public IDictionary<string, string> Spaces { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long to keep polling one question before giving up.</summary>
    /// <remarks>
    /// Databricks recommends stopping at ten minutes: a question still running after that is not
    /// going to answer usefully, and a request held open longer is a connection the caller's
    /// server cannot reuse.
    /// </remarks>
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromMinutes(10);

    internal bool TryResolveSpace(TenantContext tenant, out string spaceId) =>
        Spaces.TryGetValue(tenant.TenantId.ToString(), out spaceId!) && !string.IsNullOrWhiteSpace(spaceId);
}
