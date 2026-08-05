namespace LakeWright.Embedding;

/// <summary>
/// The service principal an application embeds dashboards as, and the workspace it lives in.
/// </summary>
/// <remarks>
/// This is the one place in LakeWright.NET that needs a client secret. External embedding
/// authenticates the token exchange with HTTP Basic over the service principal's OAuth secret, and
/// Databricks documents no other credential for it — the managed identity path of
/// <see href="https://learn.microsoft.com/azure/databricks/dev-tools/authentication-oauth">ADR 0006</see>
/// mints workspace tokens, not the downscoped viewer tokens this exchange produces.
/// </remarks>
public sealed class DashboardEmbeddingOptions
{
    /// <summary>Workspace base URL, for example <c>https://adb-123.4.azuredatabricks.net</c>.</summary>
    public string WorkspaceUrl { get; set; } = string.Empty;

    /// <summary>Application id of the service principal holding CAN RUN on the dashboard.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>The service principal's OAuth secret.</summary>
    public string ClientSecret { get; set; } = string.Empty;
}
