using LakeWright.Core.Tenancy;
using LakeWright.Embedding;
using Microsoft.Extensions.Options;

namespace LakeWright.TenantIsolation.Tests;

[Trait("Category", "TenantIsolation")]
public sealed class EmbedPreconditionTests
{
    [Fact]
    public async Task Broker_checks_an_opt_in_precondition_before_any_workspace_exchange()
    {
        var precondition = new RejectingPrecondition();
        var broker = new DashboardTokenBroker(
            new HttpClient { BaseAddress = new Uri("https://localhost/") },
            Options.Create(new DashboardEmbeddingOptions
            {
                WorkspaceUrl = "https://localhost",
                ClientId = "client",
                ClientSecret = "secret",
            }),
            TimeProvider.System,
            precondition: precondition);

        await Should.ThrowAsync<InvalidOperationException>(() => broker.IssueAsync(
            TenantContextFactory.ForTenant(TenantId.New(), "analytics"),
            "dash-1",
            "viewer-1",
            TestContext.Current.CancellationToken));

        precondition.Calls.ShouldBe(1);
    }

    private sealed class RejectingPrecondition : IEmbedPrecondition
    {
        public int Calls { get; private set; }

        public Task EnsureSatisfiedAsync(TenantContext tenant, string dashboardId, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("not verified");
        }
    }
}
