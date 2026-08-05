using LakeWright.Core.Tenancy;
using LakeWright.Embedding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// Exercises the dashboard token exchange against a real workspace.
/// </summary>
/// <remarks>
/// <para>
/// Excluded from the default run and from CI: this needs a workspace, a service principal with an
/// OAuth secret, and a published dashboard the principal holds CAN RUN on.
/// </para>
/// <para>
/// The secret comes from <c>databricks service-principal-secrets-proxy create</c>, which issues one
/// at <b>workspace</b> level. Written down because the first attempt at this went to the account
/// API, got a 303, and concluded the account console was required — it is not, for a workspace
/// admin.
/// </para>
/// Run with:
///   DATABRICKS_HOST=https://... LAKEWRIGHT_DASHBOARD_ID=... \
///   LAKEWRIGHT_EMBED_CLIENT_ID=... LAKEWRIGHT_EMBED_CLIENT_SECRET=... \
///   dotnet test --filter Category=Live
/// </remarks>
[Trait("Category", "Live")]
public class LiveEmbeddingTests
{
    private static readonly TenantId AcmeId = TenantId.Parse("0198f000-0000-7000-8000-00000000ac11");
    private static readonly TenantId GlobexId = TenantId.Parse("0198f000-0000-7000-8000-00000000617b");

    private static string Require(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException(
            $"{name} is required for Category=Live tests. See the remarks on {nameof(LiveEmbeddingTests)}.");

    private static IDashboardTokenBroker Broker()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DashboardEmbedding:WorkspaceUrl"] = Require("DATABRICKS_HOST"),
                ["DashboardEmbedding:ClientId"] = Require("LAKEWRIGHT_EMBED_CLIENT_ID"),
                ["DashboardEmbedding:ClientSecret"] = Require("LAKEWRIGHT_EMBED_CLIENT_SECRET"),
            })
            .Build();

        services.AddLakeWrightDashboardEmbedding(configuration);
        return services.BuildServiceProvider().GetRequiredService<IDashboardTokenBroker>();
    }

    private static TenantContext Tenant(TenantId id) =>
        TenantContextFactory.ForTenant(id, Require("LAKEWRIGHT_CATALOG"), "analytics");

    [Fact]
    public async Task The_exchange_returns_a_token_that_expires()
    {
        // Arrange
        var broker = Broker();

        // Act
        var token = await broker.IssueAsync(
            Tenant(AcmeId),
            Require("LAKEWRIGHT_DASHBOARD_ID"),
            "viewer-live",
            TestContext.Current.CancellationToken);

        // Assert — a real scoped token, with a lifetime read from the response rather than assumed.
        token.AccessToken.ShouldNotBeNullOrWhiteSpace();
        token.ExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task The_token_carries_the_tenant_as_its_external_value()
    {
        // Arrange — the claim the whole module rests on: the filter a dashboard applies is the
        // tenant the caller resolved, not a value the caller chose.
        var broker = Broker();

        // Act
        var token = await broker.IssueAsync(
            Tenant(GlobexId),
            Require("LAKEWRIGHT_DASHBOARD_ID"),
            "viewer-live",
            TestContext.Current.CancellationToken);

        // Assert — decode the payload rather than trusting the request we sent.
        var payload = token.AccessToken.Split('.')[1];
        var json = System.Text.Encoding.UTF8.GetString(
            Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')
                .Replace('-', '+')
                .Replace('_', '/')));

        json.ShouldContain(GlobexId.ToString());
    }

    [Fact]
    public async Task A_dashboard_the_service_principal_cannot_run_is_refused()
    {
        // Arrange
        var broker = Broker();

        // Act
        var act = async () => await broker.IssueAsync(
            Tenant(AcmeId),
            "00000000-0000-0000-0000-000000000000",
            "viewer-live",
            TestContext.Current.CancellationToken);

        // Assert — and the reason travels, because the status code alone does not distinguish
        // "no such dashboard" from "no CAN RUN on it".
        var error = await act.ShouldThrowAsync<HttpRequestException>();
        error.Message.ShouldNotBeNullOrWhiteSpace();
    }
}
