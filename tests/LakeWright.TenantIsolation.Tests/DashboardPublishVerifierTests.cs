using System.Security.Cryptography;
using System.Text;
using LakeWright.Core.Features;
using LakeWright.Core.Tenancy;
using LakeWright.Embedding;
using LakeWright.Embedding.Ops;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace LakeWright.TenantIsolation.Tests;

[Trait("Category", "TenantIsolation")]
public sealed class DashboardPublishVerifierTests
{
    private static readonly DateTimeOffset PublishedAt = DateTimeOffset.UnixEpoch.AddMinutes(1);

    [Fact]
    public async Task Detects_unpublished_changes_and_caches_the_comparison()
    {
        var api = new FakeApi { Draft = (DateTimeOffset.UnixEpoch.AddMinutes(2), Serialized()), PublishedAt = PublishedAt };
        var verifier = Verifier(api);

        (await verifier.HasUnpublishedChangesAsync("dash-1", TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await verifier.HasUnpublishedChangesAsync("dash-1", TestContext.Current.CancellationToken)).ShouldBeTrue();

        api.DraftCalls.ShouldBe(1);
        api.PublishedCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Does_not_claim_that_revision_metadata_or_marker_lint_proves_served_sql()
    {
        var result = await Verifier(new FakeApi()).VerifyServedRevisionAsync("dash-1", TestContext.Current.CancellationToken);

        result.Verifiable.ShouldBeFalse();
        result.Verified.ShouldBeFalse();
    }

    [Fact]
    public async Task Verifies_source_owned_evidence_bound_to_the_served_revision_and_definition()
    {
        var definition = Serialized();
        var verifier = Verifier(new FakeApi { PublishedAt = PublishedAt }, new Reader(definition), Evidence("dash-1", definition, constrained: true));

        var result = await verifier.VerifyServedRevisionAsync("dash-1", TestContext.Current.CancellationToken);

        result.Verifiable.ShouldBeTrue();
        result.Verified.ShouldBeTrue();
        result.IsolationEvidence.ShouldNotBeNull();
        result.PublishGate!.Datasets.Single().Name.ShouldBe("orders");
    }

    [Theory]
    [InlineData("SELECT __aibi_external_value AS claimed_tenant, * FROM sales")]
    [InlineData("SELECT * FROM sales WHERE __aibi_external_value IS NOT NULL")]
    [InlineData("SELECT * FROM sales WHERE tenant = __aibi_external_value OR TRUE")]
    [InlineData("WITH scoped AS (SELECT * FROM sales WHERE tenant = __aibi_external_value) SELECT * FROM sales")]
    [InlineData("SELECT * FROM sales WHERE tenant = __aibi_external_value UNION ALL SELECT * FROM sales")]
    [InlineData("SELECT * FROM sales JOIN regions ON sales.region = regions.id WHERE regions.tenant = __aibi_external_value")]
    public async Task Rejects_marker_only_or_unproven_sql_even_when_the_marker_lint_passes(string sql)
    {
        var definition = Serialized(sql);
        DashboardMarkerLint.InspectDashboard(definition).Passed.ShouldBeTrue();
        var verifier = Verifier(
            new FakeApi { PublishedAt = PublishedAt },
            new Reader(definition),
            Evidence("dash-1", definition, constrained: false, "The verifier cannot prove every tenant-owned relation is constrained."));

        var result = await verifier.VerifyServedRevisionAsync("dash-1", TestContext.Current.CancellationToken);

        result.Verifiable.ShouldBeTrue();
        result.Verified.ShouldBeFalse();
        result.Reason.ShouldContain("cannot prove");
    }

    [Fact]
    public async Task Rejects_evidence_for_a_different_served_definition()
    {
        var definition = Serialized();
        var otherDefinition = Serialized("SELECT * FROM orders WHERE tenant = __aibi_external_value AND active = true");
        var verifier = Verifier(new FakeApi { PublishedAt = PublishedAt }, new Reader(definition), Evidence("dash-1", otherDefinition, constrained: true));

        var result = await verifier.VerifyServedRevisionAsync("dash-1", TestContext.Current.CancellationToken);

        result.Verifiable.ShouldBeTrue();
        result.Verified.ShouldBeFalse();
        result.Reason.ShouldContain("does not match");
    }

    [Fact]
    public async Task Rejects_trusted_evidence_when_the_served_definition_has_no_executable_claim_marker()
    {
        var definition = Serialized("SELECT * FROM orders WHERE tenant = 'unscoped'");
        var verifier = Verifier(
            new FakeApi { PublishedAt = PublishedAt },
            new Reader(definition),
            Evidence("dash-1", definition, constrained: true));

        var result = await verifier.VerifyServedRevisionAsync("dash-1", TestContext.Current.CancellationToken);

        result.Verifiable.ShouldBeTrue();
        result.Verified.ShouldBeFalse();
        result.Reason.ShouldContain("no executable external-value marker");
    }

    [Fact]
    public async Task Rejects_evidence_bound_to_a_different_served_revision()
    {
        var definition = Serialized();
        var verifier = Verifier(
            new FakeApi { PublishedAt = PublishedAt },
            new Reader(definition),
            Evidence("dash-1", definition, constrained: true, revision: PublishedAt.AddMinutes(-1)));

        var result = await verifier.VerifyServedRevisionAsync("dash-1", TestContext.Current.CancellationToken);

        result.Verifiable.ShouldBeTrue();
        result.Verified.ShouldBeFalse();
        result.Reason.ShouldContain("current served revision");
    }

    [Fact]
    public async Task Strict_embed_precondition_rejects_an_unassigned_dashboard_before_verification()
    {
        var verifier = new RecordingVerifier();
        var precondition = new PublishedRevisionEmbedPrecondition(verifier, new Assignment(false));

        await Should.ThrowAsync<PublishedDashboardNotVerifiedException>(() => precondition.EnsureSatisfiedAsync(
            TenantContextFactory.ForTenant(TenantId.New(), "analytics"), "dash-1", TestContext.Current.CancellationToken));

        verifier.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Strict_embed_precondition_fails_closed_when_served_isolation_is_not_proven()
    {
        var definition = Serialized();
        var verifier = Verifier(new FakeApi { PublishedAt = PublishedAt }, new Reader(definition), Evidence("dash-1", definition, constrained: false, "No source-owned proof."));
        var precondition = new PublishedRevisionEmbedPrecondition(verifier, new Assignment(true));

        await Should.ThrowAsync<PublishedDashboardNotVerifiedException>(() => precondition.EnsureSatisfiedAsync(
            TenantContextFactory.ForTenant(TenantId.New(), "analytics"), "dash-1", TestContext.Current.CancellationToken));
    }

    private static DashboardPublishVerifier Verifier(FakeApi api, IPublishedDashboardDefinitionReader? reader = null, IPublishedDashboardIsolationEvidenceReader? evidence = null) => new(
        api, Options.Create(new DashboardPublishVerifierOptions()), new FakeTimeProvider(DateTimeOffset.UnixEpoch), new AlwaysOnFeatureGate(), reader, evidence);

    private static EvidenceReader Evidence(
        string dashboardId,
        string definition,
        bool constrained,
        string reason = "",
        DateTimeOffset? revision = null) => new(
        new PublishedDashboardIsolationEvidence(
            dashboardId,
            revision ?? PublishedAt,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(definition))).ToLowerInvariant(),
            constrained,
            reason));

    private static string Serialized(string? sql = null) =>
        $$"""{"datasets":[{"name":"orders","query":"{{sql ?? "SELECT * FROM orders WHERE tenant = __aibi_external_value"}}"}]}""";

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

    private sealed class EvidenceReader(PublishedDashboardIsolationEvidence evidence) : IPublishedDashboardIsolationEvidenceReader
    {
        public Task<PublishedDashboardIsolationEvidence?> ReadAsync(string dashboardId, CancellationToken cancellationToken = default) => Task.FromResult<PublishedDashboardIsolationEvidence?>(evidence);
    }

    private sealed class Assignment(bool assigned) : ITenantDashboardAssignment
    {
        public Task<bool> IsAssignedAsync(TenantContext tenant, string dashboardId, CancellationToken cancellationToken = default) => Task.FromResult(assigned);
    }

    private sealed class RecordingVerifier : IDashboardPublishVerifier
    {
        public int Calls { get; private set; }
        public Task<bool> HasUnpublishedChangesAsync(string dashboardId, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<PublishedRevisionVerification> VerifyServedRevisionAsync(string dashboardId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new PublishedRevisionVerification(true, true, string.Empty, null));
        }
    }
}
