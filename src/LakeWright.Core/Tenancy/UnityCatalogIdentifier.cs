using System.Text.RegularExpressions;

namespace LakeWright.Core.Tenancy;

/// <summary>
/// Validates Unity Catalog object names.
/// </summary>
/// <remarks>
/// Catalog and schema names reach Databricks as identifiers rather than as bound parameters,
/// because the Statement Execution API takes them as separate fields on the request and there
/// is no parameter form for an identifier. They are therefore the one place in the query path
/// where a value is not parameterised, which makes them the one place worth validating.
/// </remarks>
public static partial class UnityCatalogIdentifier
{
    // `\z` rather than `$`: in .NET, `$` also matches immediately before a single trailing
    // newline, so "tenant_a\n" passes a `$`-anchored pattern. Verified.
    //
    // Partial property `Regex Pattern { get; }` (C# 13, .NET 9+) would also work, but the rest
    // of the library targets net8.0 alongside net10.0, so a method-level [GeneratedRegex] is
    // the lowest-common-denominator form: same generated source-shape, same compile-time regex,
    // compatible with every TFM in the multi-target.
    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool IsValid(string? value) => value is not null && Pattern().IsMatch(value);

    public static void Validate(string value, string paramName)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a valid Unity Catalog identifier. Expected lowercase letters, " +
                "digits and underscores, starting with a letter, at most 63 characters.",
                paramName);
        }
    }

    /// <summary>
    /// Derives the schema name for a tenant. Deterministic, so provisioning and querying agree
    /// without a lookup, and prefixed so a schema cannot collide with a reserved name.
    /// </summary>
    public static string SchemaForTenant(TenantId tenantId) =>
        $"tenant_{tenantId.Value:N}";
}
