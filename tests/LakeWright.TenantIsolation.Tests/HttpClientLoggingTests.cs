using System.Net;
using Azure.Core;
using LakeWright.Conversations;
using LakeWright.Databricks;
using LakeWright.Embedding;
using LakeWright.Embedding.Ops;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Logging;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// Every library-owned HTTP client carries a bearer or Basic credential, and at the .NET 8
/// dependency floor the default HttpClient loggers record those headers at Trace. Each
/// registration removes its loggers; this pins that for every client in one place, so a
/// registration that forgets <c>RemoveAllLoggers</c> fails here rather than in a host's logs.
/// </summary>
[Trait("Category", "TenantIsolation")]
public sealed class HttpClientLoggingTests
{
    [Fact]
    public async Task Library_clients_carry_no_http_logger_even_from_host_wide_defaults()
    {
        var services = new ServiceCollection();
        var recorder = new RecordingHttpLogger();
        var names = new List<string>();
        void Capture(IHttpClientBuilder builder) => names.Add(builder.Name);

        services.AddSingleton<TokenCredential>(new StaticCredential());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Genie:WorkspaceUrl"] = "https://workspace.invalid",
            ["Databricks:WorkspaceUrl"] = "https://workspace.invalid",
            ["Databricks:WarehouseId"] = "warehouse",
            ["DashboardEmbedding:WorkspaceUrl"] = "https://workspace.invalid",
            ["DashboardEmbedding:ClientId"] = "embed-client",
            ["DashboardEmbedding:ClientSecret"] = "embed-secret",
            ["DashboardOps:WorkspaceUrl"] = "https://workspace.invalid",
            ["DashboardOps:ClientId"] = "ops-client",
            ["DashboardOps:ClientSecret"] = "ops-secret",
        }).Build();

        services.AddLakeWrightGenie(configuration);
        Capture(services.AddHttpClient<IGenieConversations, GenieConversations>());
        services.AddLakeWrightDatabricks(configuration);
        Capture(services.AddHttpClient("LakeWright.Databricks.Credentials"));
        Capture(services.AddHttpClient("LakeWright.Databricks.Export"));
        services.AddLakeWrightDashboardEmbedding(configuration, Capture);
        services.AddLakeWrightDashboardOps(configuration, Capture);
        services.AddLakeWrightDashboardRefresh(configuration, Capture);

        // Registered after the library on purpose: host-wide defaults are applied before any
        // named configuration, so the library's removal wins whatever the registration order.
        services.ConfigureHttpClientDefaults(builder => builder
            .ConfigurePrimaryHttpMessageHandler(() => new OkHandler())
            .AddLogger(_ => recorder));
        services.AddHttpClient("host-control");

        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        names.Distinct().Count().ShouldBe(11);
        foreach (var name in names.Append("host-control"))
        {
            using var client = factory.CreateClient(name);
            using var response = await client.GetAsync(
                $"https://workspace.invalid/probe/{name}", TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
        }

        recorder.Requests.ShouldBe(["/probe/host-control"]);
    }

    private sealed class RecordingHttpLogger : IHttpClientLogger
    {
        public List<string> Requests { get; } = [];

        public object? LogRequestStart(HttpRequestMessage request)
        {
            Requests.Add(request.RequestUri!.AbsolutePath);
            return null;
        }

        public void LogRequestStop(object? context, HttpRequestMessage request, HttpResponseMessage response, TimeSpan elapsed)
        {
        }

        public void LogRequestFailed(object? context, HttpRequestMessage request, HttpResponseMessage? response, Exception exception, TimeSpan elapsed)
        {
        }
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private sealed class StaticCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("synthetic-token", DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
