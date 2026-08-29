using System.Reflection;

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
/// named <c>tenant</c> or <c>tenant_id</c> to one of the four call sites fails the build, with
/// the offending line in the message. Reflection over the assembly's <c>IL</c> would be more
/// thorough than reading the source; reading the source is what the rule says and what the
/// maintainer will do next time a tag is being added, so the test is also a reminder of the rule
/// at the point someone is most likely to break it.
/// </remarks>
[Trait("Category", "TenantIsolation")]
public class TelemetryTenantGuardTests
{
    [Fact]
    public void No_library_call_site_adds_a_tenant_tag_to_a_metric()
    {
        var libraryRoot = LocateLibraryRoot();
        var tenantTagPattern = new System.Text.RegularExpressions.Regex(
            @"\b(tenant|tenantid|tenant_id|organizationid|organization_id)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var offenders = new List<string>();
        foreach (var path in Directory.EnumerateFiles(libraryRoot, "*.cs", SearchOption.AllDirectories))
        {
            // Skip generated code and tests; tests legitimately construct synthetic tenant ids.
            if (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) { continue; }
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) { continue; }
            if (path.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) { continue; }
            if (path.Contains($"{Path.DirectorySeparatorChar}docs{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) { continue; }

            // Lines that call into one of the four metric instruments and pass a tag that looks
            // like a tenant id. The pattern is conservative: it matches a recognised tenant-ish
            // word used as a tag key, not as a comment.
            var content = File.ReadAllText(path);
            if (!content.Contains("LakeWrightTelemetry.")) { continue; }

            foreach (var line in EnumerateCodeLines(content))
            {
                if (!line.Contains("LakeWrightTelemetry.")) { continue; }
                if (!line.Contains(".Add(") && !line.Contains(".Record(")) { continue; }
                if (tenantTagPattern.IsMatch(line))
                {
                    offenders.Add($"{Path.GetFileName(path)}: {line.Trim()}");
                }
            }
        }

        offenders.ShouldBeEmpty(
            "No instrument call site in the library may tag a metric with a tenant identifier. " +
            "The cardinality bomb is the whole reason no tag is the rule.");
    }

    private static string LocateLibraryRoot()
    {
        // Walk up from the test assembly's location until we find the src/ folder. The test assembly
        // is built to .../bin/Release/net10.0/, and the library source lives in .../src/.
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

    private static string[] EnumerateCodeLines(string content)
    {
        // Strip block comments so a comment that mentions "tenant" does not trigger the guard.
        var withoutBlockComments = System.Text.RegularExpressions.Regex.Replace(
            content, @"/\*.*?\*/", string.Empty,
            System.Text.RegularExpressions.RegexOptions.Singleline);
        // Strip line comments so single-line notes do not trigger the guard.
        var withoutLineComments = System.Text.RegularExpressions.Regex.Replace(
            withoutBlockComments, @"//.*$", string.Empty,
            System.Text.RegularExpressions.RegexOptions.Multiline);
        return withoutLineComments.Split('\n');
    }
}
