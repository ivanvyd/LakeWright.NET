using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Azure.Core;
using LakeWright.Conversations;
using LakeWright.Core.Tenancy;
using LakeWright.Databricks;
using LakeWright.Embedding;
using LakeWright.Embedding.Ops;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using var handler = new FixtureWorkspace();
var services = new ServiceCollection();
var logs = new FixtureLogs();
services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
services.AddLakeWrightTenancy<FixtureMembership>();
services.AddSingleton<TokenCredential>(new FixtureCredential());
var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Genie:WorkspaceUrl"] = "https://workspace.invalid",
    [$"Genie:Spaces:{FixtureMembership.TenantId}"] = "fixture-space",
    ["Databricks:WorkspaceUrl"] = "https://workspace.invalid",
    ["Databricks:WarehouseId"] = "fixture-warehouse",
    ["DashboardEmbedding:WorkspaceUrl"] = "https://workspace.invalid",
    ["DashboardEmbedding:ClientId"] = "fixture-embed-client",
    ["DashboardEmbedding:ClientSecret"] = "fixture-embed-secret",
    ["DashboardOps:WorkspaceUrl"] = "https://workspace.invalid",
    ["DashboardOps:ClientId"] = "fixture-ops-client",
    ["DashboardOps:ClientSecret"] = "fixture-ops-secret",
}).Build();
var clientNames = new List<string>();
void CaptureClient(IHttpClientBuilder builder)
{
    clientNames.Add(builder.Name);
    builder.ConfigurePrimaryHttpMessageHandler(() => handler);
}
services.AddLakeWrightGenie(configuration);
CaptureClient(services.AddHttpClient<IGenieConversations, GenieConversations>());
services.AddLakeWrightDatabricks(configuration);
CaptureClient(services.AddHttpClient("LakeWright.Databricks.Credentials"));
CaptureClient(services.AddHttpClient("LakeWright.Databricks.Export"));
services.AddLakeWrightDashboardEmbedding(configuration, CaptureClient);
services.AddLakeWrightDashboardOps(configuration, CaptureClient);
services.AddLakeWrightDashboardRefresh(configuration, CaptureClient);
await using var provider = services.BuildServiceProvider();
provider.GetRequiredService<ILoggerFactory>().CreateLogger("fixture").Log(
    LogLevel.Information, new EventId(0), "Fixture logging is enabled.", null, static (message, _) => message);
await using var scope = provider.CreateAsyncScope();
var tenant = await scope.ServiceProvider.GetRequiredService<ITenantContextResolver>()
    .ResolveAsync(FixtureMembership.TenantId, "member", CancellationToken.None)
    ?? throw new InvalidOperationException("The fixture member must resolve.");
var firstClient = scope.ServiceProvider.GetRequiredService<IGenieConversations>();
var accepted = await firstClient.StartConversationAsync(tenant, "owner", "question", CancellationToken.None);
if (handler.Posts != 1 || handler.Gets != 0 || accepted != new GenieAcceptedMessage("conv", "msg"))
{
    throw new InvalidOperationException("Accepted IDs must arrive before polling.");
}
var resumedClient = scope.ServiceProvider.GetRequiredService<IGenieConversations>();
var snapshot = await resumedClient.GetMessageAsync(tenant, "owner", "conv", "msg", CancellationToken.None);
var answer = await resumedClient.WaitForResponseAsync(tenant, "owner", "conv", "msg", CancellationToken.None);
if (!snapshot.IsTerminal || answer.Outcome != GenieOutcome.Completed || handler.Posts != 1 || handler.Gets != 2)
{
    throw new InvalidOperationException("Resumption must read the accepted message without another POST.");
}
try
{
    await resumedClient.GetMessageAsync(tenant, "other-owner", "conv", "msg", CancellationToken.None);
    throw new InvalidOperationException("A foreign owner must be refused.");
}
catch (ConversationOwnershipException)
{
    if (handler.Gets != 2)
    {
        throw new InvalidOperationException("An unauthorized read reached HTTP.");
    }
}
IGenieConversations legacy = new LegacyAdapter();
if ((await legacy.ListAsync("owner", CancellationToken.None)).Count != 0)
{
    throw new InvalidOperationException("The legacy adapter changed.");
}
try
{
    await legacy.WaitForResponseAsync(tenant, "owner", "conv", "msg", CancellationToken.None);
    throw new InvalidOperationException("Legacy adapters must explicitly support the new primitives.");
}
catch (NotSupportedException) { }
var clientFactory = provider.GetRequiredService<IHttpClientFactory>();
var leakingClients = 0;
var basicCanary = Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture-client:fixture-secret"));
foreach (var name in clientNames)
{
    var previousLogs = logs.Entries.Count;
    using var client = clientFactory.CreateClient(name);
    foreach (var authorization in new[] { new AuthenticationHeaderValue("Bearer", "synthetic-token"), new AuthenticationHeaderValue("Basic", basicCanary) })
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://workspace.invalid/probe?signature=synthetic-signature");
        request.Headers.Authorization = authorization;
        using var response = await client.SendAsync(request, CancellationToken.None);
        response.EnsureSuccessStatusCode();
    }
    var leaked = logs.Entries.Skip(previousLogs).Any(entry => entry.Contains("synthetic-token", StringComparison.Ordinal)
        || entry.Contains(basicCanary, StringComparison.Ordinal) || entry.Contains("synthetic-signature", StringComparison.Ordinal));
    if (leaked)
    {
        leakingClients++;
    }
    Console.WriteLine($"{name}: credential data recorded: {leaked}.");
}
var capturedLogging = logs.Entries.Contains("Fixture logging is enabled.");
var tokenRecorded = logs.Entries.Any(entry => entry.Contains("synthetic-token", StringComparison.Ordinal));
Console.WriteLine($"Logging captured: {capturedLogging}; bearer token recorded: {tokenRecorded}.");
if (!capturedLogging || tokenRecorded || leakingClients != 0 || clientNames.Count != 11 || handler.PrivacyRequests != 22)
{
    throw new InvalidOperationException("The fixture must capture logs without recording the bearer token.");
}
Console.WriteLine("OK: accepted IDs, GET-only resume, owner denial, legacy adapter and 11 HTTP client logging checks.");

internal sealed class FixtureMembership(ITenantContextFactory contexts) : ITenantContextResolver
{
    internal static readonly TenantId TenantId = TenantId.Parse("0198f000-0000-7000-8000-0000000000d1");
    public Task<TenantContext?> ResolveAsync(TenantId tenantId, string principalId, CancellationToken cancellationToken) =>
        Task.FromResult(tenantId == TenantId && principalId == "member" ? contexts.ForTenant(tenantId, "analytics", "tenant_member") : null);
}

internal sealed class FixtureWorkspace : HttpMessageHandler
{
    internal int Posts { get; private set; }
    internal int Gets { get; private set; }
    internal int PrivacyRequests { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string body;
        if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/start-conversation", StringComparison.Ordinal))
        {
            Posts++;
            body = """{"conversation_id":"conv","message_id":"msg"}""";
        }
        else if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/conversations/conv/messages/msg", StringComparison.Ordinal))
        {
            Gets++;
            body = """{"status":"COMPLETED"}""";
        }
        else if (request.RequestUri!.AbsolutePath == "/probe")
        {
            PrivacyRequests++;
            body = "{}";
        }
        else
        {
            throw new InvalidOperationException("Unexpected fixture request.");
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}

internal sealed class FixtureCredential : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new("synthetic-token", DateTimeOffset.MaxValue);
    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        ValueTask.FromResult(GetToken(requestContext, cancellationToken));
}

internal sealed class LegacyAdapter : IGenieConversations
{
    public Task<GenieAnswer> AskAsync(TenantContext tenant, string ownerKey, string question, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<GenieAnswer> ContinueAsync(TenantContext tenant, string ownerKey, string conversationId, string question, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask<IReadOnlyList<string>> ListAsync(string ownerKey, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<string>>([]);
    public Task DeleteAsync(TenantContext tenant, string ownerKey, string conversationId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class FixtureLogs : ILoggerProvider
{
    internal System.Collections.Concurrent.ConcurrentQueue<string> Entries { get; } = new();
    public ILogger CreateLogger(string categoryName) => new FixtureLogger(Entries);
    public void Dispose() { }

    private sealed class FixtureLogger(System.Collections.Concurrent.ConcurrentQueue<string> entries) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            Record(state);
            return null;
        }
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            entries.Enqueue(formatter(state, exception));
            Record(state);
        }
        private void Record<TState>(TState state)
        {
            entries.Enqueue(state?.ToString() ?? string.Empty);
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var pair in values)
                {
                    entries.Enqueue(pair.Value?.ToString() ?? string.Empty);
                }
            }
        }
    }
}
