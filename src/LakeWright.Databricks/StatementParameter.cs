namespace LakeWright.Databricks;

/// <summary>
/// A bound parameter. Construct through the typed factories rather than the constructor, so the
/// Databricks type name is not a free string at the call site.
/// </summary>
/// <remarks>
/// The underlying client models the type as <see cref="string"/>, so nothing stops a caller
/// writing <c>"INTEGER"</c> and getting a runtime error. These factories are the whole reason
/// this type exists.
/// </remarks>
// CA1720 objects to String/Int/Double as identifiers because they collide with CLR type names.
// Here they name Databricks SQL types, which is what the caller is choosing, and renaming them to
// OfString/OfInt would obscure the mapping to the STRING/INT/DOUBLE the server actually sees.
#pragma warning disable CA1720
public readonly record struct StatementParameter(string Name, string? Value, string Type)
{
    public static StatementParameter String(string name, string? value) => new(name, value, "STRING");

    public static StatementParameter Int(string name, int value) =>
        new(name, value.ToString(System.Globalization.CultureInfo.InvariantCulture), "INT");

    public static StatementParameter BigInt(string name, long value) =>
        new(name, value.ToString(System.Globalization.CultureInfo.InvariantCulture), "BIGINT");

    public static StatementParameter Boolean(string name, bool value) =>
        new(name, value ? "true" : "false", "BOOLEAN");

    public static StatementParameter Double(string name, double value) =>
        new(name, value.ToString("R", System.Globalization.CultureInfo.InvariantCulture), "DOUBLE");

    public static StatementParameter Date(string name, DateOnly value) =>
        new(name, value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), "DATE");

    /// <summary>Timestamps are sent as UTC. A local time here is a bug that surfaces as an off-by-hours query.</summary>
    public static StatementParameter Timestamp(string name, DateTimeOffset value) =>
        new(name,
            value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture),
            "TIMESTAMP");

    public static StatementParameter Tenant(string name, Core.Tenancy.TenantId tenantId) =>
        String(name, tenantId.ToString());
}
#pragma warning restore CA1720
