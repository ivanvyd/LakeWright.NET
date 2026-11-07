using System.Net;
using Azure.Core;
using LakeWright.Core.Jobs;
using LakeWright.Databricks;
using Microsoft.Azure.Databricks.Client;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Request = WireMock.RequestBuilders.Request;

namespace LakeWright.TenantIsolation.Tests;

public sealed class DatabricksJobSubmitterTests : IDisposable
{
    private readonly WireMockServer _workspace = WireMockServer.Start();

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_rejected_cancellation_is_not_reported_as_success(HttpStatusCode status)
    {
        RejectRequests(status);

        var exception = await Should.ThrowAsync<HttpRequestException>(() => Submitter().CancelRunAsync(123, TestContext.Current.CancellationToken));

        exception.StatusCode.ShouldBe(status);
        exception.ToString().ShouldNotContain("synthetic-tenant-secret");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_failed_status_request_is_not_a_terminal_run_failure(HttpStatusCode status)
    {
        RejectRequests(status);

        var exception = await Should.ThrowAsync<HttpRequestException>(() => Submitter().GetRunAsync(123, TestContext.Current.CancellationToken));

        exception.StatusCode.ShouldBe(status);
        exception.ToString().ShouldNotContain("synthetic-tenant-secret");
    }

    [Fact]
    public async Task A_terminal_run_response_remains_authoritative()
    {
        _workspace.Given(Request.Create().UsingGet()).RespondWith(Response.Create()
            .WithHeader("Content-Type", "application/json")
            .WithBody("""{"run_id":123,"status":{"state":"TERMINATED","termination_details":{"code":"CANCELED"}}}"""));

        var outcome = await Submitter().GetRunAsync(123, TestContext.Current.CancellationToken);

        outcome.ShouldBe(new RunOutcome.Cancelled(123));
    }

    private void RejectRequests(HttpStatusCode status) =>
        _workspace.Given(Request.Create().UsingAnyMethod()).RespondWith(Response.Create()
            .WithStatusCode(status)
            .WithHeader("Content-Type", "application/json")
            .WithBody("""{"error_code":"REQUEST_REJECTED","message":"synthetic-tenant-secret"}"""));

    private DatabricksJobSubmitter Submitter() => new(
        DatabricksClient.CreateClient(_workspace.Urls[0], new Credential()),
        NullLogger<DatabricksJobSubmitter>.Instance);

    public void Dispose() => _workspace.Dispose();

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("synthetic", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
