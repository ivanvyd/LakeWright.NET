namespace LakeWright.Databricks;

/// <summary>Raised before execution when a shared-schema statement cannot prove tenant scope.</summary>
public sealed class TenantScopeMissingException(string message) : InvalidOperationException(message);
