using System.Net;
using System.Text;
using Azure.Core;
using LakeWright.AspNetCore;
using LakeWright.Databricks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace LakeWright.TenantIsolation.Tests;

[Trait("Category", "TenantIsolation")]
public sealed class DatabricksCredentialTests
{
    [Fact]
    public async Task Service_principal_credential_caches_a_workspace_token_by_client_id()
    {
        var handler = new TokenHandler();
        var clock = new FakeTimeProvider();
        var credential = new ServicePrincipalDatabricksCredential(
            new HttpClient(handler) { BaseAddress = new Uri("https://workspace.example/") },
            Microsoft.Extensions.Options.Options.Create(CreateOptions()),
            clock);

        var first = await credential.GetTokenAsync(TestContext.Current.CancellationToken);
        var second = await credential.GetTokenAsync(TestContext.Current.CancellationToken);

        first.ShouldBe("service-principal-token");
        second.ShouldBe(first);
        handler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Service_principal_credential_refreshes_before_the_vendor_expiration()
    {
        var handler = new TokenHandler();
        var clock = new FakeTimeProvider();
        var credential = new ServicePrincipalDatabricksCredential(
            new HttpClient(handler) { BaseAddress = new Uri("https://workspace.example/") },
            Microsoft.Extensions.Options.Options.Create(CreateOptions()),
            clock);

        await credential.GetTokenAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(31));
        await credential.GetTokenAsync(TestContext.Current.CancellationToken);

        handler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task A_rotated_service_principal_client_id_acquires_a_fresh_workspace_token()
    {
        var handler = new TokenHandler();
        var time = new FakeTimeProvider();
        var original = new ServicePrincipalDatabricksCredential(
            new HttpClient(handler) { BaseAddress = new Uri("https://workspace.example/") },
            Microsoft.Extensions.Options.Options.Create(CreateOptions("original-client-id")),
            time);
        var rotated = new ServicePrincipalDatabricksCredential(
            new HttpClient(handler) { BaseAddress = new Uri("https://workspace.example/") },
            Microsoft.Extensions.Options.Options.Create(CreateOptions("rotated-client-id")),
            time);

        await original.GetTokenAsync(TestContext.Current.CancellationToken);
        await rotated.GetTokenAsync(TestContext.Current.CancellationToken);

        handler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public void Databricks_registration_rejects_ambiguous_credential_configuration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TokenCredential>(new StubCredential());
        services.AddLakeWrightDatabricks(Configuration(new()
        {
            ["Databricks:WorkspaceUrl"] = "https://workspace.example",
            ["Databricks:WarehouseId"] = "warehouse-1",
            ["Databricks:ClientId"] = "service-principal-id",
            ["Databricks:ClientSecret"] = "service-principal-secret",
        }));

        var validate = () => services.BuildServiceProvider()
            .GetRequiredService<IStartupValidator>()
            .Validate();

        validate.ShouldThrow<OptionsValidationException>()
            .Message.ShouldContain("either a TokenCredential or a client ID and secret");
    }

    [Fact]
    public void Databricks_registration_rejects_ambiguous_credential_configuration_regardless_of_registration_order()
    {
        var services = new ServiceCollection();
        services.AddLakeWrightDatabricks(Configuration(new()
        {
            ["Databricks:WorkspaceUrl"] = "https://workspace.example",
            ["Databricks:WarehouseId"] = "warehouse-1",
            ["Databricks:ClientId"] = "service-principal-id",
            ["Databricks:ClientSecret"] = "service-principal-secret",
        }));
        services.AddSingleton<TokenCredential>(new StubCredential());

        var validate = () => services.BuildServiceProvider()
            .GetRequiredService<IStartupValidator>()
            .Validate();

        validate.ShouldThrow<OptionsValidationException>()
            .Message.ShouldContain("either a TokenCredential or a client ID and secret");
    }

    private static DatabricksOptions CreateOptions(string clientId = "service-principal-id") => new()
    {
        WorkspaceUrl = "https://workspace.example",
        WarehouseId = "warehouse-1",
        ClientId = clientId,
        ClientSecret = "service-principal-secret",
    };

    private static IConfiguration Configuration(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private sealed class StubCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("token", DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class TokenHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Method.ShouldBe(HttpMethod.Post);
            request.RequestUri!.AbsolutePath.ShouldBe("/oidc/v1/token");
            request.Headers.Authorization!.Scheme.ShouldBe("Basic");
            RequestCount++;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"access_token":"service-principal-token","expires_in":60}""", Encoding.UTF8, "application/json"),
            });
        }
    }
}
