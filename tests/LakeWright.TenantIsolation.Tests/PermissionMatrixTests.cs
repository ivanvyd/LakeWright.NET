using System.Text;
using LakeWright.AspNetCore;
using LakeWright.Multitenancy.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// Generates the permission matrix from the routing table and fails if the committed copy is stale.
/// </summary>
/// <remarks>
/// The SOC 2 mapping cites `docs/compliance/permissions.md` as evidence for the authorization
/// control. A hand-written table would be evidence of what someone believed a year ago; this one is
/// read out of the endpoints themselves, so the document cannot drift from the code without a test
/// going red.
///
/// The mapping previously cited this file when it did not exist at all, which is why it is
/// generated rather than promised.
/// </remarks>
[Trait("Category", "TenantIsolation")]
public class PermissionMatrixTests
{
    private static readonly string MatrixPath =
        Path.Combine(RepositoryRoot(), "docs", "compliance", "permissions.md");

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LakeWright.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    [Fact]
    public async Task The_committed_permission_matrix_matches_the_routing_table()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s => { s.AddRouting(); s.AddAuthorization(); })
                .Configure(app => app.UseRouting().UseEndpoints(e => e.MapLakeWrightOperations())))
            .StartAsync(ct);

        // Act
        var endpoints = host.Services.GetRequiredService<EndpointDataSource>();
        var generated = Render(endpoints);

        // Assert — a route under the tenant prefix with no role policy is the footgun this suite
        // exists to catch. Rendering it as a documented row would make the gap look intentional.
        Unprotected(endpoints).ShouldBeEmpty(
            "every tenant-scoped endpoint needs a TenantPolicies value; the fallback policy asks "
            + "only for an authenticated user, which any member of any role satisfies");
        var committed = File.Exists(MatrixPath)
            ? await File.ReadAllTextAsync(MatrixPath, ct)
            : string.Empty;

        if (!string.Equals(Normalise(committed), Normalise(generated), StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(MatrixPath, generated, ct);
            Assert.Fail(
                $"{MatrixPath} was stale and has been regenerated. Review the diff and commit it.");
        }
    }

    private static IReadOnlyList<string> Unprotected(EndpointDataSource endpoints) =>
        [.. endpoints.Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.Contains(
                TenantResolutionMiddleware.RouteValue, StringComparison.Ordinal) == true)
            .Where(e => !e.Metadata
                .OfType<Microsoft.AspNetCore.Authorization.IAuthorizeData>()
                .Any(a => Rank.ContainsKey(a.Policy ?? string.Empty)))
            .Select(e => e.DisplayName ?? e.RoutePattern.RawText ?? "?")];

    private static string Normalise(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

    private static string Render(EndpointDataSource endpoints)
    {
        var rows = endpoints.Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => new
            {
                Route = "/" + e.RoutePattern.RawText?.TrimStart('/'),
                Methods = string.Join(", ",
                    e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["ANY"]),
                Policies = e.Metadata
                    .OfType<Microsoft.AspNetCore.Authorization.IAuthorizeData>()
                    .Select(a => a.Policy)
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToArray()
            })
            .OrderBy(r => r.Route, StringComparer.Ordinal)
            .ThenBy(r => r.Methods, StringComparer.Ordinal);

        var sb = new StringBuilder();
        sb.AppendLine("# Permission matrix");
        sb.AppendLine();
        sb.AppendLine("Generated from the routing table by `PermissionMatrixTests`. Do not edit by hand:");
        sb.AppendLine("the test rewrites this file and fails when it drifts from the code.");
        sb.AppendLine();
        sb.AppendLine("Roles are a floor, not an exact match. An Admin satisfies a Member policy, and a");
        sb.AppendLine("Member satisfies a Viewer policy. Every endpoint below also requires a resolved tenant,");
        sb.AppendLine("which means membership of that organization; a caller who is not a member gets 404");
        sb.AppendLine("before authorization is consulted.");
        sb.AppendLine();
        sb.AppendLine("| Method | Route | Minimum role |");
        sb.AppendLine("|---|---|---|");

        foreach (var row in rows)
        {
            sb.AppendLine($"| {row.Methods} | `{row.Route}` | {Describe(Floor(row.Policies))} |");
        }

        sb.AppendLine();
        sb.AppendLine("Every tenant-scoped endpoint carries an explicit role policy, and the route group");
        sb.AppendLine("carries Viewer as a floor so one added without a policy of its own still requires");
        sb.AppendLine("membership at a role rather than merely an authenticated caller.");
        return sb.ToString();
    }

    /// <summary>The strictest policy on an endpoint, since every one of them must be satisfied.</summary>
    private static string Floor(IReadOnlyCollection<string?> policies) =>
        Rank.Keys.Where(policies.Contains).OrderByDescending(p => Rank[p]).FirstOrDefault()
        ?? policies.FirstOrDefault()
        ?? NoPolicy;

    private static readonly Dictionary<string, int> Rank = new(StringComparer.Ordinal)
    {
        [TenantPolicies.Viewer] = 0,
        [TenantPolicies.Member] = 1,
        [TenantPolicies.Admin] = 2
    };

    private const string NoPolicy = "(none)";

    private static string Describe(string policy) => policy switch
    {
        TenantPolicies.Viewer => nameof(MembershipRole.Viewer),
        TenantPolicies.Member => nameof(MembershipRole.Member),
        TenantPolicies.Admin => nameof(MembershipRole.Admin),
        _ => policy
    };
}
