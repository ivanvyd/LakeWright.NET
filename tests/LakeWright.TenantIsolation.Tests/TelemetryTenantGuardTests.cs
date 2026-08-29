using System.Reflection;
using System.Text.RegularExpressions;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// The instruments an operator exports must not carry a tenant identifier.
/// </summary>
/// <remarks>
/// The library publishes plain <c>System.Diagnostics</c> instruments and the architecture is
/// explicit that none of them carry a tenant tag: a thousand tenants turns four instruments into
/// four thousand time series, and the bill for that arrives at the observability vendor. Per-tenant
/// totals are the application's job to derive from <c>operations</c> and <c>audit_events</c>.
///
/// This test pins the property against accidental regression. A future change that adds a tag
/// with a tenant identifier (whether as the key or the value, on the line that opens the call or
/// on a continuation line) fails the build, with the offending file and the matched call span
/// in the message.
///
/// <b>What the rule covers.</b> Every call to <c>LakeWrightTelemetry.&lt;X&gt;.Add(</c> or
/// <c>.Record(</c> in <c>src/</c> is walked as a paren-tracked span, and the span is checked for a
/// tenant token — as a tag key, as a tag value, as an identifier on a continuation line, or as a
/// string literal. Comments are stripped before the scan so a line that describes the rule does
/// not trigger it.
///
/// <b>What the rule does not cover.</b> A helper that lives in a different file and takes a
/// <c>Counter&lt;...&gt;</c> or <c>Histogram&lt;...&gt;</c> as a parameter and calls
/// <c>.Add(</c> or <c>.Record(</c> on it is the second bypass an earlier version let through.
/// A regex-based source scan cannot tell the difference between <c>counter.Add(...)</c> on a
/// metric instrument and <c>auditLog.Record(...)</c> on a <c>Microsoft.Extensions.Logging</c>
/// entry point, because the receiver is opaque. The right answer is an IL-level check that
/// resolves the call site to a <c>System.Diagnostics.Metrics</c> method; that is a follow-up
/// and not a blocker. A maintainer adding a tag today reads the docstring on
/// <see cref="LakeWright.Multitenancy.LakeWrightTelemetry"/> and the rule above.
/// </remarks>
[Trait("Category", "TenantIsolation")]
public class TelemetryTenantGuardTests
{
    // A tenant id on the right-hand side of a tag (the value position) or in the key position.
    // Catches: organization.OrganizationId, op.TenantId, operation.OrganizationId, an inline
    // string literal "tenant" or "tenant_id", and the snake_case variants.
    private static readonly Regex TenantToken = new(
        @"\b(tenant|tenantid|tenant_id|organizationid|organization_id)\b",
        RegexOptions.IgnoreCase);

    // A direct call to a metric instrument. The receiver must be a LakeWrightTelemetry field.
    private static readonly Regex DirectMetricCall = new(
        @"LakeWrightTelemetry\.\w+\s*\.\s*(?:Add|Record)\s*\(");

    [Fact]
    public void No_library_call_site_adds_a_tenant_tag_to_a_metric()
    {
        var libraryRoot = LocateLibraryRoot();
        var offenders = new List<string>();

        foreach (var path in Directory.EnumerateFiles(libraryRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) { continue; }
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) { continue; }
            if (path.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) { continue; }
            if (path.Contains($"{Path.DirectorySeparatorChar}docs{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) { continue; }

            var stripped = StripComments(File.ReadAllText(path));

            // Direct call sites: every LakeWrightTelemetry.<X>.Add( or .Record( opener is the
            // start of a call span. Walk the parens and check the whole span.
            foreach (Match match in DirectMetricCall.Matches(stripped))
            {
                var span = ExtractCallSpan(stripped, match.Index);
                if (TenantToken.IsMatch(span))
                {
                    offenders.Add($"{Path.GetFileName(path)} (line {LineOf(stripped, match.Index)}): {Truncate(span, 200)}");
                }
            }
        }

        offenders.ShouldBeEmpty(
            "No metric call in the library may reference a tenant identifier, whether as a tag " +
            "key, as a tag value, on the call line, or in a multi-line continuation. The " +
            "cardinality bomb is the whole reason no tag is the rule.");
    }

    private static string ExtractCallSpan(string source, int openParenIndex)
    {
        // The opening paren is the last character of the match. Walk forward to the matching close.
        var depth = 0;
        for (var i = openParenIndex; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '(') { depth++; }
            else if (c == ')')
            {
                depth--;
                if (depth == 0) { return source.Substring(openParenIndex, i - openParenIndex + 1); }
            }
        }
        // Unbalanced parens — return to the end of the source. The match will still find tokens.
        return source.Substring(openParenIndex);
    }

    private static int LineOf(string source, int index)
    {
        var line = 1;
        for (var i = 0; i < index; i++) { if (source[i] == '\n') { line++; } }
        return line;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "…");

    private static string LocateLibraryRoot()
    {
        // Walk up from the test assembly's location until we find the src/ folder. The test
        // assembly is built to .../bin/Release/net10.0/, and the library source lives in
        // .../src/.
        var dir = new DirectoryInfo(Assembly.GetExecutingAssembly().Location);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            throw new DirectoryNotFoundException(
                "Could not locate the Lakewright source root from the test assembly's location.");
        }
        return Path.Combine(dir.FullName, "src");
    }

    private static string StripComments(string content)
    {
        var withoutBlockComments = Regex.Replace(
            content, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(
            withoutBlockComments, @"//.*$", string.Empty, RegexOptions.Multiline);
    }
}
