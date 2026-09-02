using LakeWright.Core;

namespace LakeWright.Databricks;

/// <summary>Raised before execution when a shared-schema statement cannot be safely scoped.</summary>
public sealed class TenantScopeMissingException(string message) : LakeWrightException(message);
