using System.Net;
using System.Text;
using LakeWright.Core.Tenancy;
using LakeWright.Embedding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddLakeWrightTenancy<FloorResolver>();
services.AddLakeWrightDashboardEmbedding(new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["DashboardEmbedding:WorkspaceUrl"] = "https://floor.invalid",
        ["DashboardEmbedding:ClientId"] = "floor-client",
        ["DashboardEmbedding:ClientSecret"] = "floor-secret",
    })
    .Build());

var workspace = new FloorWorkspace();
services.AddHttpClient<IDashboardTokenBroker, DashboardTokenBroker>()
    .ConfigurePrimaryHttpMessageHandler(() => workspace);

await using var provider = services.BuildServiceProvider();
await using var scope = provider.CreateAsyncScope();
var resolver = scope.ServiceProvider.GetRequiredService<ITenantContextResolver>();
var tenant = await resolver.ResolveAsync(FloorResolver.Tenant, "member", CancellationToken.None);
if (tenant is null || await resolver.ResolveAsync(FloorResolver.Tenant, "outsider", CancellationToken.None) is not null)
{
    return 1;
}
if (scope.ServiceProvider.GetService<ITenantContextFactory>() is not null)
{
    return 2;
}

var token = await scope.ServiceProvider.GetRequiredService<IDashboardTokenBroker>()
    .IssueAsync(tenant, "dashboard", "viewer", CancellationToken.None);
return token.AccessToken == "floor-token" && workspace.ExternalValue == FloorResolver.Tenant.ToString() ? 0 : 3;

internal sealed class FloorResolver(ITenantContextFactory contexts) : ITenantContextResolver
{
    internal static readonly TenantId Tenant = TenantId.Parse("0198f000-0000-7000-8000-0000000000d1");

    public Task<TenantContext?> ResolveAsync(TenantId tenantId, string principalId, CancellationToken cancellationToken) =>
        Task.FromResult(principalId == "member" && tenantId == Tenant
            ? contexts.ForTenant(tenantId, "analytics")
            : null);
}

internal sealed class FloorWorkspace : HttpMessageHandler
{
    internal string? ExternalValue { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath == "/oidc/v1/token")
        {
            return Json("""{"access_token":"floor-token","expires_in":3600}""");
        }
        if (request.RequestUri.AbsolutePath.EndsWith("/published/tokeninfo", StringComparison.Ordinal))
        {
            ExternalValue = request.RequestUri.Query.Split('&')
                .Select(pair => pair.Split('=', 2))
                .Where(pair => pair.Length == 2 && pair[0].TrimStart('?') == "external_value")
                .Select(pair => Uri.UnescapeDataString(pair[1]))
                .SingleOrDefault();
            return Json("""{"scope":"dashboards:read","authorization_details":[{"type":"workspace_resource"}]}""");
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static Task<HttpResponseMessage> Json(string body) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    });
}
