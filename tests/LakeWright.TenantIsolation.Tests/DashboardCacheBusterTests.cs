using System.Net;
using LakeWright.Core.Features;
using LakeWright.Embedding.Ops;
using Microsoft.Extensions.Options;

namespace LakeWright.TenantIsolation.Tests;

[Trait("Category", "TenantIsolation")]
public sealed class DashboardCacheBusterTests
{
    [Fact]
    public async Task Stamps_every_dataset_then_publishes_without_embedding_credentials()
    {
        var editor = new FakeEditor(Draft());

        var result = await Buster(editor).BustOnceAsync("dash-1", 45, TestContext.Current.CancellationToken);

        result.AlreadyCurrent.ShouldBeFalse();
        editor.PatchCalls.ShouldBe(1);
        editor.PublishCalls.ShouldBe(1);
        editor.LastEmbedCredentials.ShouldBe(false);
        editor.Draft.SerializedDashboard.ShouldContain("-- refresh 45");
        (editor.Draft.SerializedDashboard.Split("-- refresh 45", StringSplitOptions.None).Length - 1).ShouldBe(2);
    }

    [Fact]
    public async Task Is_idempotent_after_the_marker_has_been_published()
    {
        var editor = new FakeEditor(Draft());
        var buster = Buster(editor);

        await buster.BustOnceAsync("dash-1", 45, TestContext.Current.CancellationToken);
        var result = await buster.BustOnceAsync("dash-1", 45, TestContext.Current.CancellationToken);

        result.AlreadyCurrent.ShouldBeTrue();
        editor.PatchCalls.ShouldBe(1);
        editor.PublishCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Retries_publication_after_a_successful_patch()
    {
        var editor = new FakeEditor(Draft()) { FailFirstPublish = true };
        var buster = Buster(editor);

        await Should.ThrowAsync<DashboardApiException>(() => buster.BustOnceAsync("dash-1", 45, TestContext.Current.CancellationToken));
        await buster.BustOnceAsync("dash-1", 45, TestContext.Current.CancellationToken);

        editor.PatchCalls.ShouldBe(1);
        editor.PublishCalls.ShouldBe(2);
    }

    [Fact]
    public async Task Publishes_a_matching_concurrent_draft_that_has_not_been_published()
    {
        var editor = new FakeEditor(Draft()) { ConflictOnFirstPatch = true };

        await Buster(editor).BustOnceAsync("dash-1", 45, TestContext.Current.CancellationToken);

        editor.PublishCalls.ShouldBe(1);
    }

    [Fact]
    public async Task A_concurrent_patch_is_treated_as_success_only_when_the_same_marker_appears()
    {
        var editor = new FakeEditor(Draft()) { ConflictOnFirstPatch = true, PublishOnConflict = true };

        var result = await Buster(editor).BustOnceAsync("dash-1", 45, TestContext.Current.CancellationToken);

        result.AlreadyCurrent.ShouldBeTrue();
        editor.PublishCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task Skips_publication_only_when_the_published_revision_covers_the_stamped_draft(int seconds, bool alreadyCurrent)
    {
        var draft = Draft(DashboardCacheBuster.AddMarker(Draft().SerializedDashboard, "-- refresh 45", out _));
        var editor = new FakeEditor(draft) { PublishedAt = draft.UpdatedAt!.Value.AddSeconds(seconds) };

        var result = await Buster(editor).BustOnceAsync("dash-1", 45, TestContext.Current.CancellationToken);

        result.AlreadyCurrent.ShouldBe(alreadyCurrent);
        editor.PatchCalls.ShouldBe(0);
        editor.PublishCalls.ShouldBe(alreadyCurrent ? 0 : 1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_revision_metadata_does_not_prove_publication(bool missingDraftTime)
    {
        var draft = Draft(DashboardCacheBuster.AddMarker(Draft().SerializedDashboard, "-- refresh 45", out _));
        var editor = new FakeEditor(missingDraftTime ? draft with { UpdatedAt = null } : draft)
        {
            PublishedAt = missingDraftTime ? draft.UpdatedAt : null,
        };

        var result = await Buster(editor).BustOnceAsync("dash-1", 45, TestContext.Current.CancellationToken);

        result.AlreadyCurrent.ShouldBeFalse();
        editor.PatchCalls.ShouldBe(0);
        editor.PublishCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Refuses_a_schedule_bucket_that_could_escape_the_sql_comment()
    {
        var editor = new FakeEditor(Draft());

        await Should.ThrowAsync<ArgumentException>(() => Buster(editor).ScheduledBustAsync(
            ["dash-1"], "daily\nselect", TestContext.Current.CancellationToken));

        editor.GetCalls.ShouldBe(0);
    }

    private static DashboardCacheBuster Buster(FakeEditor editor) => new(
        editor,
        editor,
        Options.Create(new DashboardCacheBustOptions()),
        new AlwaysOnFeatureGate());

    private static DashboardDraft Draft(string? serialized = null) => new(
        "dash-1",
        "Operations",
        "warehouse-1",
        "etag-1",
        "/Shared",
        serialized ?? """{"datasets":[{"name":"orders","queryLines":["SELECT 1"]},{"name":"sales","query":"SELECT 2"}]}""",
        DateTimeOffset.UnixEpoch.AddMinutes(1));

    private sealed class FakeEditor(DashboardDraft draft) : IDashboardEditorApi, IPublishVerificationApi
    {
        public DashboardDraft Draft { get; private set; } = draft;
        public int GetCalls { get; private set; }
        public int PatchCalls { get; private set; }
        public int PublishCalls { get; private set; }
        public bool? LastEmbedCredentials { get; private set; }
        public bool ConflictOnFirstPatch { get; init; }
        public bool FailFirstPublish { get; init; }
        public bool PublishOnConflict { get; init; }
        public DateTimeOffset? PublishedAt { get; set; }

        Task<(DateTimeOffset? UpdatedAt, string SerializedDashboard)> IPublishVerificationApi.GetDraftAsync(string dashboardId, CancellationToken cancellationToken) =>
            Task.FromResult((Draft.UpdatedAt, Draft.SerializedDashboard));

        public Task<DateTimeOffset?> GetPublishedRevisionAsync(string dashboardId, CancellationToken cancellationToken) =>
            Task.FromResult(PublishedAt);

        public Task<DashboardDraft> GetDraftAsync(string dashboardId, CancellationToken cancellationToken)
        {
            GetCalls++;
            return Task.FromResult(Draft);
        }

        public Task PatchDraftAsync(DashboardDraft draft, string serializedDashboard, CancellationToken cancellationToken)
        {
            PatchCalls++;
            if (ConflictOnFirstPatch && PatchCalls == 1)
            {
                var stamped = DashboardCacheBuster.AddMarker(Draft.SerializedDashboard, "-- refresh 45", out _);
                Draft = Draft with { SerializedDashboard = stamped, UpdatedAt = Draft.UpdatedAt?.AddSeconds(1) };
                if (PublishOnConflict)
                {
                    PublishedAt = Draft.UpdatedAt;
                }
                throw new DashboardApiException(HttpStatusCode.Conflict);
            }

            Draft = Draft with { SerializedDashboard = serializedDashboard, UpdatedAt = Draft.UpdatedAt?.AddSeconds(1) };
            return Task.CompletedTask;
        }

        public Task PublishAsync(string dashboardId, bool embedCredentials, CancellationToken cancellationToken)
        {
            PublishCalls++;
            if (FailFirstPublish && PublishCalls == 1)
            {
                throw new DashboardApiException(HttpStatusCode.ServiceUnavailable);
            }
            LastEmbedCredentials = embedCredentials;
            PublishedAt = Draft.UpdatedAt;
            return Task.CompletedTask;
        }
    }
}
