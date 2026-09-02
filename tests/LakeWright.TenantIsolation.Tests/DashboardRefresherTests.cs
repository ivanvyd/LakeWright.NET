using System.Net;
using System.Text.Json;
using LakeWright.Core.Features;
using LakeWright.Core.Tenancy;
using LakeWright.Embedding.Ops;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace LakeWright.TenantIsolation.Tests;

[Trait("Category", "TenantIsolation")]
public sealed class DashboardRefresherTests
{
    private static readonly TenantContext FirstTenant = TenantContextFactory.ForTenant(
        TenantId.Parse("0198f000-0000-7000-8000-00000000ac11"), "analytics");
    private static readonly TenantContext SecondTenant = TenantContextFactory.ForTenant(
        TenantId.Parse("0198f000-0000-7000-8000-00000000617b"), "analytics");

    [Fact]
    public async Task Joins_only_an_active_run_for_the_resolved_tenant()
    {
        var api = new FakeJobsApi
        {
            ActiveRuns =
            [
                Run(11, FirstTenant, RefreshRunState.Running),
                Run(12, SecondTenant, RefreshRunState.Running),
            ],
        };

        var start = await Refresher(api).StartOrJoinAsync(
            FirstTenant, RefreshJob.FromId(42), cancellationToken: TestContext.Current.CancellationToken);

        start.ShouldBe(new RefreshStart(11, Joined: true, api.Clock.GetUtcNow()));
        api.RunNowCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Does_not_expose_the_status_of_a_foreign_run()
    {
        var api = new FakeJobsApi { Runs = { [7] = Run(7, SecondTenant, RefreshRunState.Succeeded) } };
        var ownership = new MemoryRefreshRunOwnership();
        ownership.Record(SecondTenant, 7);

        await Should.ThrowAsync<UnauthorizedAccessException>(() => Refresher(api, ownership).StatusAsync(
            FirstTenant, [7], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Refuses_a_new_run_inside_the_success_minimum_interval()
    {
        var api = new FakeJobsApi
        {
            CompletedRuns = [Run(14, FirstTenant, RefreshRunState.Succeeded, endedAt: DateTimeOffset.UnixEpoch.AddMinutes(10))],
        };
        api.Clock.SetUtcNow(DateTimeOffset.UnixEpoch.AddMinutes(20));

        await Should.ThrowAsync<RefreshNotAdmittedException>(() => Refresher(api).StartOrJoinAsync(
            FirstTenant, RefreshJob.FromId(42), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Generates_an_opaque_cross_replica_idempotency_token()
    {
        var api = new FakeJobsApi();

        await Refresher(api).StartOrJoinAsync(
            FirstTenant, RefreshJob.FromId(42), cancellationToken: TestContext.Current.CancellationToken);

        api.LastIdempotencyToken.ShouldNotBeNull();
        api.LastIdempotencyToken!.ShouldNotContain(FirstTenant.TenantId.ToString());
        api.LastIdempotencyToken.ShouldMatch("^[0-9a-f]{64}$");
    }

    [Fact]
    public async Task Invalidates_a_named_job_lookup_after_the_workspace_reports_it_missing()
    {
        var api = new FakeJobsApi { FailFirstRunNowAsMissing = true };

        var result = await Refresher(api).StartOrJoinAsync(
            FirstTenant, RefreshJob.FromName("refresh-analytics"), cancellationToken: TestContext.Current.CancellationToken);

        result.RunId.ShouldBe(99);
        api.InvalidateCalls.ShouldBe(1);
        api.ResolveCalls.ShouldBe(2);
        api.RunNowCalls.ShouldBe(2);
    }

    [Fact]
    public void Reads_the_current_jobs_api_status_shape()
    {
        using var document = JsonDocument.Parse("""
        {"run_id":17,"job_id":42,"start_time":0,"end_time":1000,"job_parameters":{"lakewright_tenant_id":"0198f000-0000-7000-8000-00000000ac11"},"status":{"state":"TERMINATED","termination_details":{"code":"SUCCESS"}},"tasks":[{"task_key":"refresh","status":{"state":"TERMINATED","termination_details":{"code":"SUCCESS"}}}]}
        """);

        var run = DatabricksJobsApi.ParseRun(document.RootElement);

        run.State.ShouldBe(RefreshRunState.Succeeded);
        run.Tasks.Single().State.ShouldBe(RefreshRunState.Succeeded);
        run.TenantId.ShouldBe(FirstTenant.TenantId.ToString());
    }

    private static DashboardRefresher Refresher(FakeJobsApi api, IRefreshRunOwnership? ownership = null) => new(
        api,
        Options.Create(new DashboardRefreshOptions
        {
            Policy = new RefreshPolicy { MinimumInterval = TimeSpan.FromMinutes(30) },
        }),
        api.Clock,
        ownership ?? new MemoryRefreshRunOwnership(),
        new AlwaysOnFeatureGate());

    private static JobsRun Run(
        long id,
        TenantContext tenant,
        RefreshRunState state,
        DateTimeOffset? endedAt = null) => new(
        id,
        42,
        state,
        DateTimeOffset.UnixEpoch,
        endedAt,
        tenant.TenantId.ToString(),
        [],
        null);

    private sealed class FakeJobsApi : IJobsApi
    {
        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UnixEpoch);
        public IReadOnlyList<JobsRun> ActiveRuns { get; init; } = [];
        public IReadOnlyList<JobsRun> CompletedRuns { get; init; } = [];
        public Dictionary<long, JobsRun> Runs { get; } = [];
        public int RunNowCalls { get; private set; }
        public int ResolveCalls { get; private set; }
        public int InvalidateCalls { get; private set; }
        public bool FailFirstRunNowAsMissing { get; init; }
        public string? LastIdempotencyToken { get; private set; }

        public Task<long> ResolveJobIdAsync(RefreshJob job, CancellationToken cancellationToken)
        {
            ResolveCalls++;
            return Task.FromResult(job.Id ?? 42);
        }

        public Task<IReadOnlyList<JobsRun>> ListRunsAsync(long jobId, bool activeOnly, CancellationToken cancellationToken) =>
            Task.FromResult(activeOnly ? ActiveRuns : CompletedRuns);

        public Task<long> RunNowAsync(long jobId, TenantContext tenant, string idempotencyToken, CancellationToken cancellationToken)
        {
            RunNowCalls++;
            LastIdempotencyToken = idempotencyToken;
            if (FailFirstRunNowAsMissing && RunNowCalls == 1)
            {
                throw new JobsApiException(HttpStatusCode.BadRequest, "job does not exist");
            }
            return Task.FromResult(99L);
        }

        public Task<JobsRun> GetRunAsync(long runId, CancellationToken cancellationToken) =>
            Task.FromResult(Runs[runId]);

        public void Invalidate(RefreshJob job) => InvalidateCalls++;
    }
}
