using LakeWright.Core.Tenancy;
using LakeWright.Embedding;
using LakeWright.Embedding.Ops;
using Microsoft.Extensions.Options;

namespace LakeWright.TenantIsolation.Tests;

[Trait("Category", "TenantIsolation")]
public sealed class EmbedPreconditionTests
{
    [Fact]
    public async Task Broker_checks_an_opt_in_precondition_before_any_workspace_exchange()
    {
        var precondition = new RejectingPrecondition();
        var handler = new CountingHandler();
        var broker = new DashboardTokenBroker(
            new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") },
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
        handler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Strict_precondition_denies_an_unassigned_dashboard_before_any_token_exchange()
    {
        var handler = new CountingHandler();
        var verifier = new RecordingVerifier();
        var broker = new DashboardTokenBroker(
            new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") },
            Options.Create(new DashboardEmbeddingOptions
            {
                WorkspaceUrl = "https://localhost",
                ClientId = "client",
                ClientSecret = "secret",
            }),
            TimeProvider.System,
            precondition: new PublishedRevisionEmbedPrecondition(verifier, new Assignment(false)));

        await Should.ThrowAsync<PublishedDashboardNotVerifiedException>(() => broker.IssueAsync(
            TenantContextFactory.ForTenant(TenantId.New(), "analytics"),
            "dash-1",
            "viewer-1",
            TestContext.Current.CancellationToken));

        verifier.Calls.ShouldBe(0);
        handler.Calls.ShouldBe(0);
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

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        }
    }

    private sealed class Assignment(bool assigned) : ITenantDashboardAssignment
    {
        public Task<bool> IsAssignedAsync(TenantContext tenant, string dashboardId, CancellationToken cancellationToken = default) =>
            Task.FromResult(assigned);
    }

    private sealed class RecordingVerifier : IDashboardPublishVerifier
    {
        public int Calls { get; private set; }

        public Task<bool> HasUnpublishedChangesAsync(string dashboardId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<PublishedRevisionVerification> VerifyServedRevisionAsync(string dashboardId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new PublishedRevisionVerification(true, true, string.Empty, null));
        }
    }
}
