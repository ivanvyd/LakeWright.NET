using System.Text;
using Lakewright.AspNetCore;
using Lakewright.Multitenancy.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Lakewright.TenantIsolation.Tests;

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
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lakewright.slnx")))
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
                .Configure(app => app.UseRouting().UseEndpoints(e => e.MapLakewrightOperations())))
            .StartAsync(ct);

        // Act
        var generated = Render(host.Services.GetRequiredService<EndpointDataSource>());

        // Assert
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
                Policy = e.Metadata
                    .OfType<Microsoft.AspNetCore.Authorization.IAuthorizeData>()
                    .Select(a => a.Policy)
                    .FirstOrDefault(p => !string.IsNullOrEmpty(p)) ?? "(fallback: authenticated)"
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
            sb.AppendLine($"| {row.Methods} | `{row.Route}` | {Describe(row.Policy)} |");
        }

        sb.AppendLine();
        sb.AppendLine("Endpoints with no explicit policy fall back to requiring an authenticated user, so a");
        sb.AppendLine("new endpoint is protected by omission rather than exposed by it.");
        return sb.ToString();
    }

    private static string Describe(string policy) => policy switch
    {
        TenantPolicies.Viewer => nameof(MembershipRole.Viewer),
        TenantPolicies.Member => nameof(MembershipRole.Member),
        TenantPolicies.Admin => nameof(MembershipRole.Admin),
        _ => policy
    };
}
