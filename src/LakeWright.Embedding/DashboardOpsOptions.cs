namespace LakeWright.Embedding;

/// <summary>
/// The service principal an application uses for backend dashboard operations, and the workspace
/// it lives in.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="DashboardEmbeddingOptions"/> on purpose (ADR 0019). The embedding
/// service principal mints per-viewer tokens and is bound to CAN RUN; the operations principal
/// runs catalog, refresh, and any other backend read against the workspace, with a different
/// (broader) permission set. Splitting them is the security baseline: a leaked embed secret
/// grants a viewer-forgery primitive only, not access to the dashboard set or to refresh jobs.
/// </para>
/// <para>
/// <see cref="WorkspaceUrl"/> defaults to the same value as
/// <see cref="DashboardEmbeddingOptions.WorkspaceUrl"/> when bound to configuration, because a
/// product that does not need a separate workspace for ops almost never has one. The two URLs
/// may legitimately differ when an embed workspace is a downstream consumer of an upstream
/// authoring workspace.
/// </para>
/// </remarks>
public sealed class DashboardOpsOptions
{
    /// <summary>Workspace base URL, for example <c>https://adb-123.4.azuredatabricks.net</c>.</summary>
    public string WorkspaceUrl { get; set; } = string.Empty;

    /// <summary>Application id of the service principal holding the ops permissions.</summary>
    /// <remarks>
    /// This is the principal the catalog and refresh paths authenticate as, not the one that
    /// mints viewer tokens. The two ids are deliberately different; the embed path is locked
    /// down to a token-minting role on a per-dashboard basis, while the ops path needs
    /// read-list and refresh on the workspace.
    /// </remarks>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>The service principal's OAuth secret.</summary>
    public string ClientSecret { get; set; } = string.Empty;
}
