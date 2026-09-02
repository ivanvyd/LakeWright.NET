using LakeWright.Core.Features;
using LakeWright.Core.Tenancy;
using LakeWright.Embedding.Ops;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace LakeWright.TenantIsolation.Tests;

[Trait("Category", "TenantIsolation")]
public sealed class DashboardPublishVerifierTests
{
    [Fact]
    public async Task Detects_unpublished_changes_and_caches_the_comparison()
    {
        var api = new FakeApi
        {
            Draft = (DateTimeOffset.UnixEpoch.AddMinutes(2), Serialized()),
            PublishedAt = DateTimeOffset.UnixEpoch.AddMinutes(1),
        };
        var verifier = Verifier(api);

        (await verifier.HasUnpublishedChangesAsync("dash-1", TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await verifier.HasUnpublishedChangesAsync("dash-1", TestContext.Current.CancellationToken)).ShouldBeTrue();

        api.DraftCalls.ShouldBe(1);
        api.PublishedCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Does_not_claim_that_revision_metadata_proves_served_sql()
    {
        var result = await Verifier(new FakeApi()).VerifyServedRevisionAsync("dash-1", TestContext.Current.CancellationToken);

        result.Verifiable.ShouldBeFalse();
        result.Verified.ShouldBeFalse();
    }

    [Fact]
    public async Task Verifies_an_authoritative_published_definition_with_the_publish_gate()
    {
        var verifier = Verifier(new FakeApi(), new Reader(Serialized()));

        var result = await verifier.VerifyServedRevisionAsync("dash-1", TestContext.Current.CancellationToken);

        result.Verifiable.ShouldBeTrue();
        result.Verified.ShouldBeTrue();
        result.PublishGate!.Datasets.Single().Name.ShouldBe("orders");
    }

    [Fact]
    public async Task Strict_embed_precondition_fails_closed_when_a_served_definition_cannot_be_proven()
    {
        var precondition = new PublishedRevisionEmbedPrecondition(Verifier(new FakeApi()));

        await Should.ThrowAsync<PublishedDashboardNotVerifiedException>(() => precondition.EnsureSatisfiedAsync(
            TenantContextFactory.ForTenant(TenantId.New(), "analytics"),
            "dash-1",
            TestContext.Current.CancellationToken));
    }

    private static DashboardPublishVerifier Verifier(FakeApi api, IPublishedDashboardDefinitionReader? reader = null) => new(
        api,
        Options.Create(new DashboardPublishVerifierOptions()),
        new FakeTimeProvider(DateTimeOffset.UnixEpoch),
        new AlwaysOnFeatureGate(),
        reader);

    private static string Serialized() => """{"datasets":[{"name":"orders","query":"SELECT * FROM data WHERE tenant = __aibi_external_value"}]}""";

    private sealed class FakeApi : IPublishVerificationApi
    {
        public (DateTimeOffset? UpdatedAt, string SerializedDashboard) Draft { get; init; } = (null, Serialized());
        public DateTimeOffset? PublishedAt { get; init; }
        public int DraftCalls { get; private set; }
        public int PublishedCalls { get; private set; }

        public Task<(DateTimeOffset? UpdatedAt, string SerializedDashboard)> GetDraftAsync(string dashboardId, CancellationToken cancellationToken)
        {
            DraftCalls++;
            return Task.FromResult(Draft);
        }

        public Task<DateTimeOffset?> GetPublishedRevisionAsync(string dashboardId, CancellationToken cancellationToken)
        {
            PublishedCalls++;
            return Task.FromResult(PublishedAt);
        }
    }

    private sealed class Reader(string definition) : IPublishedDashboardDefinitionReader
    {
        public Task<string?> ReadAsync(string dashboardId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(definition);
    }
}
