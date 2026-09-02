using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Azure.Core;
using LakeWright.Core.Tenancy;
using LakeWright.Databricks;
using LakeWright.Embedding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddLakeWrightTenancy<InMemoryResolver>();
services.AddSingleton<TokenCredential>(new FloorCredential());
var workspace = new FloorWorkspace();
using var sqlServer = FloorSqlServer.Start();
services.AddLakeWrightDatabricks(new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Databricks:WorkspaceUrl"] = sqlServer.BaseAddress,
        ["Databricks:WarehouseId"] = "floor-warehouse",
    })
    .Build());
services.AddLakeWrightDashboardEmbedding(new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["DashboardEmbedding:WorkspaceUrl"] = "https://floor.invalid",
        ["DashboardEmbedding:ClientId"] = "floor-client",
        ["DashboardEmbedding:ClientSecret"] = "floor-secret",
    })
    .Build());

services.AddHttpClient<IDashboardTokenBroker, DashboardTokenBroker>()
    .ConfigurePrimaryHttpMessageHandler(() => workspace);

await using var provider = services.BuildServiceProvider();
await using var scope = provider.CreateAsyncScope();
var resolver = scope.ServiceProvider.GetRequiredService<ITenantContextResolver>();
var tenant = await resolver.ResolveAsync(InMemoryResolver.Tenant, "member", CancellationToken.None);
if (tenant is null || await resolver.ResolveAsync(InMemoryResolver.Tenant, "outsider", CancellationToken.None) is not null)
{
    return 1;
}
if (scope.ServiceProvider.GetService<ITenantContextFactory>() is not null)
{
    return 2;
}

var token = await scope.ServiceProvider.GetRequiredService<IDashboardTokenBroker>()
    .IssueAsync(tenant, "dashboard", "viewer", CancellationToken.None);
var statement = TenantScopedStatement.Create(
    tenant,
    "SELECT id, tenant_id FROM widgets WHERE tenant_id = :tenant_id");
var outcome = await scope.ServiceProvider.GetRequiredService<IStatementExecutor>()
    .ExecuteAsync(statement, CancellationToken.None);

var passed = token.AccessToken == "floor-token" &&
    workspace.ExternalValue == InMemoryResolver.Tenant.ToString() &&
    outcome is StatementOutcome.Success &&
    await sqlServer.VerifyAsync();

if (!passed)
{
    return 3;
}

Console.WriteLine("OK");
return 0;

internal sealed class InMemoryResolver(ITenantContextFactory contexts) : ITenantContextResolver
{
    internal static readonly TenantId Tenant = TenantId.Parse("0198f000-0000-7000-8000-0000000000d1");

    private static readonly Dictionary<(TenantId TenantId, string PrincipalId), TenantContextRequest> Memberships = new()
    {
        [(Tenant, "member")] = new("analytics", "shared"),
    };

    public Task<TenantContext?> ResolveAsync(TenantId tenantId, string principalId, CancellationToken cancellationToken)
    {
        return Task.FromResult(Memberships.TryGetValue((tenantId, principalId), out var request)
            ? contexts.ForSharedTenant(tenantId, request.Catalog, request.Schema)
            : null);
    }

    private sealed record TenantContextRequest(string Catalog, string Schema);
}

internal sealed class FloorCredential : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new("floor-token", DateTimeOffset.MaxValue);

    public override ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(GetToken(requestContext, cancellationToken));
}

internal sealed class FloorWorkspace : HttpMessageHandler
{
    internal string? ExternalValue { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath == "/oidc/v1/token")
        {
            return Task.FromResult(Json("""{"access_token":"floor-token","expires_in":3600}"""));
        }
        if (request.RequestUri.AbsolutePath.EndsWith("/published/tokeninfo", StringComparison.Ordinal))
        {
            ExternalValue = request.RequestUri.Query.Split('&')
                .Select(pair => pair.Split('=', 2))
                .Where(pair => pair.Length == 2 && pair[0].TrimStart('?') == "external_value")
                .Select(pair => Uri.UnescapeDataString(pair[1]))
                .SingleOrDefault();
            return Task.FromResult(Json("""{"scope":"dashboards:read","authorization_details":[{"type":"workspace_resource"}]}"""));
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}

internal sealed class FloorSqlServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Task<bool> _request;

    private FloorSqlServer(HttpListener listener)
    {
        _listener = listener;
        _request = HandleAsync();
    }

    internal string BaseAddress => _listener.Prefixes.Single().TrimEnd('/');

    internal static FloorSqlServer Start()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        return new FloorSqlServer(listener);
    }

    internal Task<bool> VerifyAsync() => _request;

    private async Task<bool> HandleAsync()
    {
        var context = await _listener.GetContextAsync();
        using var payload = JsonDocument.Parse(context.Request.InputStream);
        var root = payload.RootElement;
        var parameters = root.GetProperty("parameters");
        var valid = context.Request.HttpMethod == "POST" &&
            context.Request.Url!.AbsolutePath == "/api/2.0/sql/statements" &&
            root.GetProperty("catalog").GetString() == "analytics" &&
            root.GetProperty("schema").GetString() == "shared" &&
            root.GetProperty("statement").GetString() ==
                "SELECT * FROM (SELECT id, tenant_id FROM widgets WHERE tenant_id = :tenant_id) AS lakewright_tenant_scope WHERE lakewright_tenant_scope.tenant_id = :tenant_id" &&
            parameters.GetArrayLength() == 1 &&
            parameters[0].GetProperty("name").GetString() == "tenant_id" &&
            parameters[0].GetProperty("value").GetString() == InMemoryResolver.Tenant.ToString();

        var body = Encoding.UTF8.GetBytes("""{"statement_id":"floor-statement","status":{"state":"SUCCEEDED"},"manifest":{"schema":{"columns":[{"name":"id"}]},"total_row_count":1},"result":{"data_array":[["one"]]}}""");
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body);
        context.Response.Close();
        return valid;
    }

    public void Dispose() => _listener.Close();
}
