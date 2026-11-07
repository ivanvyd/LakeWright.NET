using System.Net;
using System.Text;
using LakeWright.Core.Features;
using LakeWright.Embedding;
using LakeWright.Embedding.Ops;
using Microsoft.Extensions.Options;

namespace LakeWright.TenantIsolation.Tests;

public sealed class DashboardPublicationApiTests
{
    [Fact]
    public async Task A_missing_published_revision_leaves_a_known_draft_unpublished()
    {
        using var http = Client(request => request.RequestUri!.AbsolutePath.EndsWith("/published", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Json("""{"update_time":"2026-11-01T00:00:00Z","serialized_dashboard":"{}"}"""));
        var api = new DatabricksPublishVerificationApi(http, new Tokens());
        var verifier = new DashboardPublishVerifier(api, Options.Create(new DashboardPublishVerifierOptions()), TimeProvider.System, new AlwaysOnFeatureGate());

        (await api.GetPublishedRevisionAsync("dash-1", TestContext.Current.CancellationToken)).ShouldBeNull();
        (await verifier.HasUnpublishedChangesAsync("dash-1", TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_failed_revision_request_is_not_treated_as_missing_metadata(HttpStatusCode status)
    {
        using var http = Client(_ => new HttpResponseMessage(status));
        var api = new DatabricksPublishVerificationApi(http, new Tokens());

        var exception = await Should.ThrowAsync<DashboardVerificationApiException>(() =>
            api.GetPublishedRevisionAsync("dash-1", TestContext.Current.CancellationToken));

        exception.StatusCode.ShouldBe((int)status);
    }

    [Theory]
    [InlineData("2026-11-01T00:00:00Z", true)]
    [InlineData("unknown", false)]
    [InlineData(null, false)]
    public async Task The_editor_preserves_the_draft_timestamp_when_available(string? updatedAt, bool known)
    {
        using var http = Client(_ => Json(System.Text.Json.JsonSerializer.Serialize(new
        {
            dashboard_id = "dash-1",
            display_name = "Operations",
            warehouse_id = "warehouse-1",
            serialized_dashboard = "{}",
            update_time = updatedAt,
        })));
        var api = new DatabricksDashboardEditorApi(http, new Tokens());

        var draft = await api.GetDraftAsync("dash-1", TestContext.Current.CancellationToken);

        draft.UpdatedAt.HasValue.ShouldBe(known);
        if (known)
        {
            draft.UpdatedAt.ShouldBe(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        }
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> response) =>
        new(new Handler(response)) { BaseAddress = new Uri("https://workspace.example/") };

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    private sealed class Tokens : IOpsTokenBroker
    {
        public Task<EmbedToken> AcquireAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbedToken("synthetic", DateTimeOffset.MaxValue));
    }
}
