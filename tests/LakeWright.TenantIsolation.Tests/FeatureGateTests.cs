using Azure.Core;
using LakeWright.AspNetCore;
using LakeWright.Conversations;
using LakeWright.Core.Features;
using LakeWright.Core.Tenancy;
using LakeWright.Databricks;
using LakeWright.Embedding;
using Microsoft.Azure.Databricks.Client.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LakeWright.TenantIsolation.Tests;

public sealed class FeatureGateTests
{
    [Fact]
    public void The_options_monitor_gate_observes_a_configuration_reload()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LakeWright:Features:Enabled:embedding"] = "false",
        }).Build();
        var services = new ServiceCollection();
        services.AddLakeWrightFeatureGate(configuration);
        using var provider = services.BuildServiceProvider();
        var gate = provider.GetRequiredService<ILakeWrightFeatureGate>();

        gate.IsEnabled(LakeWrightFeatures.Embedding).ShouldBeFalse();
        configuration["LakeWright:Features:Enabled:embedding"] = "true";
        configuration.Reload();

        gate.IsEnabled(LakeWrightFeatures.Embedding).ShouldBeTrue();
    }

    [Fact]
    public async Task Disabled_embedding_and_operations_are_refused_before_HTTP()
    {
        var gate = new DisabledFeatureGate();
        var embedding = new DashboardTokenBroker(
            new HttpClient(),
            Options.Create(new DashboardEmbeddingOptions()),
            TimeProvider.System,
            features: gate);
        var ops = new OpsTokenBroker(
            new HttpClient(),
            Options.Create(new DashboardOpsOptions()),
            TimeProvider.System,
            features: gate);

        var embedError = await Should.ThrowAsync<FeatureDisabledException>(() => embedding.IssueAsync(
            Tenant(), "dashboard", "viewer", TestContext.Current.CancellationToken));
        var opsError = await Should.ThrowAsync<FeatureDisabledException>(() =>
            ops.AcquireAsync(TestContext.Current.CancellationToken));

        embedError.Feature.ShouldBe(LakeWrightFeatures.Embedding);
        opsError.Feature.ShouldBe(LakeWrightFeatures.Operations);
    }

    [Fact]
    public async Task Disabled_statements_and_conversations_are_refused_before_transport()
    {
        var gate = new DisabledFeatureGate();
        var session = new TrackingSession();
        var executor = new DatabricksStatementExecutor(
            session,
            new DatabricksOptions { WarehouseId = "warehouse" },
            features: gate);
        var conversations = new GenieConversations(
            new HttpClient(),
            new StubCredential(),
            Options.Create(new GenieOptions()),
            TimeProvider.System,
            features: gate);

        var statementError = await Should.ThrowAsync<FeatureDisabledException>(() => executor.ExecuteAsync(
            TenantScopedStatement.Create(Tenant(), "SELECT 1"),
            TestContext.Current.CancellationToken));
        var conversationError = await Should.ThrowAsync<FeatureDisabledException>(() => conversations.AskAsync(
            Tenant(), "owner", "question", TestContext.Current.CancellationToken));

        statementError.Feature.ShouldBe(LakeWrightFeatures.Statements);
        conversationError.Feature.ShouldBe(LakeWrightFeatures.Conversations);
        session.ExecuteCalls.ShouldBe(0);
    }

    private static TenantContext Tenant() =>
        TenantContextFactory.ForTenant(TenantId.New(), "analytics");

    private sealed class DisabledFeatureGate : ILakeWrightFeatureGate
    {
        public bool IsEnabled(string feature) => false;
    }

    private sealed class TrackingSession : IDatabricksStatementSession
    {
        public int ExecuteCalls { get; private set; }

        public Task<StatementOutcome> ExecuteAsync(SqlStatement request, TenantId tenantId, CancellationToken cancellationToken)
        {
            ExecuteCalls++;
            throw new InvalidOperationException("The feature gate should have stopped this call.");
        }

        public Task<StatementOutcome> GetAsync(TenantId tenantId, string statementId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The feature gate should have stopped this call.");

        public Task CancelAsync(string statementId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The feature gate should have stopped this call.");
    }

    private sealed class StubCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("unused", DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            new(GetToken(requestContext, cancellationToken));
    }
}
