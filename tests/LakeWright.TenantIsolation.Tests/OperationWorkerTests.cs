using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using LakeWright.Core.Jobs;
using LakeWright.Core.Tenancy;
using LakeWright.Databricks;
using LakeWright.Multitenancy;
using LakeWright.Multitenancy.Model;
using LakeWright.Multitenancy.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// The worker, driven one iteration at a time against a real database and a fake Databricks.
/// </summary>
[Trait("Category", "TenantIsolation")]
[Collection(nameof(PostgresTests))]
public class OperationWorkerTests(PostgresFixture postgres)
{
    private static readonly TenantId AcmeId = TenantId.Parse("0198f000-0000-7000-8000-0000000000f1");
    private static readonly TenantId BetaId = TenantId.Parse("0198f000-0000-7000-8000-0000000000f2");
    private static readonly TenantId GammaId = TenantId.Parse("0198f000-0000-7000-8000-0000000000f3");
    private static readonly TenantId DeltaId = TenantId.Parse("0198f000-0000-7000-8000-0000000000f4");
    private const long JobId = 4242;

    private sealed class ClaimCounter : DbCommandInterceptor
    {
        public int Count;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE operations o", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref Count);
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// Stands in for Databricks. Records every idempotency key it is given, which is what lets the
    /// reconciliation test assert that a re-submission reuses the original key rather than
    /// starting a second run.
    /// </summary>
    private sealed class FakeSubmitter : IJobSubmitter
    {
        private readonly Dictionary<string, long> _runsByKey = new(StringComparer.Ordinal);
        private long _nextRunId = 1000;

        public ConcurrentQueue<string> SubmittedKeys { get; } = [];
        public ConcurrentQueue<TenantId> SubmittedTenants { get; } = [];
        public RunOutcome? SubmitOverride { get; set; }
        public RunOutcome RunState { get; set; } = new RunOutcome.Succeeded(0);
        public TaskCompletionSource? HoldFirstPoll { get; set; }
        public bool HoldAllPolls { get; set; }
        public TaskCompletionSource? FirstPollStarted { get; set; }
        public TaskCompletionSource? PollsReached { get; set; }
        public int PollsTarget { get; set; }
        public int PollCalls;

        public Task<RunOutcome> SubmitAsync(TenantScopedJobRun run, CancellationToken cancellationToken)
        {
            SubmittedKeys.Enqueue(run.IdempotencyKey);
            SubmittedTenants.Enqueue(run.Tenant.TenantId);

            if (SubmitOverride is { } forced) { return Task.FromResult(forced); }

            // What the real idempotency token does: the same key returns the same run.
            if (!_runsByKey.TryGetValue(run.IdempotencyKey, out var runId))
            {
                runId = _nextRunId++;
                _runsByKey[run.IdempotencyKey] = runId;
            }

            return Task.FromResult<RunOutcome>(new RunOutcome.Submitted(runId));
        }

        public List<long> CancelledRuns { get; } = [];

        public Task CancelRunAsync(long runId, CancellationToken cancellationToken)
        {
            CancelledRuns.Add(runId);
            return Task.CompletedTask;
        }

        public async Task<RunOutcome> GetRunAsync(long runId, CancellationToken cancellationToken)
        {
            var polls = Interlocked.Increment(ref PollCalls);
            if (PollsTarget > 0 && polls >= PollsTarget)
            {
                PollsReached?.TrySetResult();
            }
            if ((HoldAllPolls || runId == 1000) && HoldFirstPoll is { } hold)
            {
                FirstPollStarted?.TrySetResult();
                await hold.Task.WaitAsync(cancellationToken);
            }
            return RunState switch
            {
                RunOutcome.Succeeded => new RunOutcome.Succeeded(runId),
                RunOutcome.Cancelled => new RunOutcome.Cancelled(runId),
                RunOutcome.Failed f => new RunOutcome.Failed(runId, f.Reason, f.IsTransient),
                _ => (RunOutcome)new RunOutcome.Running(runId)
            };
        }
    }

    private static async Task<(ServiceProvider Provider, FakeSubmitter Submitter)>
        BuildAsync(PostgresFixture postgres, ClaimCounter? counter = null)
    {
        await using var seed = await postgres.NewDatabaseAsync();
        seed.Organizations.Add(new Organization
        {
            Id = AcmeId,
            Name = "Acme",
            Slug = "acme",
            CreatedAt = DateTimeOffset.UtcNow,
            Schema = UnityCatalogIdentifier.SchemaForTenant(AcmeId),
            State = OrganizationState.Active
        });
        await seed.SaveChangesAsync();
        var connectionString = seed.Database.GetConnectionString()!;

        var submitter = new FakeSubmitter();
        var services = new ServiceCollection();
        services.AddDbContext<LakeWrightDbContext>(o =>
        {
            o.UseNpgsql(connectionString);
            if (counter is not null) { o.AddInterceptors(counter); }
        });
        services.Configure<MultitenancyOptions>(options => options.Catalog = "analytics");
        services.AddLakeWrightTenancy<EfTenantContextResolver>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<AuditLog>();
        services.AddScoped<OperationStore>();
        services.AddSingleton<IJobSubmitter>(submitter);

        return (services.BuildServiceProvider(), submitter);
    }

    private static OperationWorker WorkerFor(ServiceProvider provider, int concurrency = 4, TimeSpan? grace = null, TimeSpan? idleDelay = null) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new OperationWorkerOptions
            {
                Jobs = { ["analysis"] = JobId },
                InitialPollInterval = TimeSpan.FromMilliseconds(1),
                MaxPollInterval = TimeSpan.FromMilliseconds(2),
                ReconciliationGracePeriod = grace ?? TimeSpan.FromMinutes(-5),
                IdleDelay = idleDelay ?? TimeSpan.FromSeconds(5),
                MaxConcurrentOperations = concurrency
            }),
            Options.Create(new MultitenancyOptions { Catalog = "analytics" }),
            NullLogger<OperationWorker>.Instance,
            TimeProvider.System);

    private static TenantContext Ctx() => TenantContextFactory.ForTenant(AcmeId, "analytics");

    [Fact]
    public async Task An_operation_is_claimed_submitted_recorded_and_completed()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (provider, submitter) = await BuildAsync(postgres);
        await using var _p = provider;

        await using (var scope = provider.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<OperationStore>();
            await store.CreateAsync(Ctx(), "auth0|alice", "analysis", clientRequestId: null, ct);
        }

        // Act
        var didWork = await WorkerFor(provider).RunOnceAsync(ct);

        // Assert
        await using var check = provider.CreateAsyncScope();
        var final = await check.ServiceProvider.GetRequiredService<LakeWrightDbContext>()
            .Operations.SingleAsync(ct);

        didWork.ShouldBeTrue();
        final.State.ShouldBe(OperationState.Succeeded);
        final.ExternalId.ShouldNotBeNull();
        final.CompletedAt.ShouldNotBeNull();
        submitter.SubmittedKeys.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_worker_that_died_between_submit_and_record_does_not_cause_a_second_run()
    {
        // Arrange — the case ADR 0005 exists for, and the one no happy-path test can reach. Claim
        // the operation, submit by hand, and stop: exactly what a worker killed one line before
        // RecordExternalIdAsync leaves behind. A run exists at Databricks; nothing local knows its id.
        var ct = TestContext.Current.CancellationToken;
        var (provider, submitter) = await BuildAsync(postgres);
        await using var _p = provider;

        string idempotencyKey;
        await using (var scope = provider.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<OperationStore>();
            var created = await store.CreateAsync(Ctx(), "auth0|alice", "analysis", clientRequestId: null, ct);
            idempotencyKey = created.IdempotencyKey;

            var claimed = await store.ClaimNextAsync(maxInFlightPerTenant: 100, ct);
            await submitter.SubmitAsync(
                TenantScopedJobRun.Create(Ctx(), JobId, claimed!.IdempotencyKey), ct);
        }

        submitter.SubmittedKeys.Count.ShouldBe(1, "arrange should leave exactly one submission");

        // Act
        var didWork = await WorkerFor(provider).RunOnceAsync(ct);

        // Assert — re-submitted with the ORIGINAL key, so Databricks returns the existing run
        // rather than starting a second one. That is the whole guarantee.
        await using var check = provider.CreateAsyncScope();
        var final = await check.ServiceProvider.GetRequiredService<LakeWrightDbContext>()
            .Operations.SingleAsync(ct);

        didWork.ShouldBeTrue();
        submitter.SubmittedKeys.Count.ShouldBe(2);
        submitter.SubmittedKeys.Distinct().Count().ShouldBe(1);
        submitter.SubmittedKeys.ElementAt(1).ShouldBe(idempotencyKey);
        final.ExternalId.ShouldBe("1000", "the reconciled run is the one already started, not a new one");
        final.State.ShouldBe(OperationState.Succeeded);
    }

    [Fact]
    public async Task A_worker_that_stopped_polling_resumes_rather_than_resubmitting()
    {
        // Arrange — an ordinary rolling deploy, not a crash. The operation was submitted and
        // recorded, so it is Running with a known run id, and then PollAsync exited on the
        // shutdown token. Reconciliation used to require ExternalId IS NULL and passed straight
        // over rows like this, leaving them Running forever.
        var ct = TestContext.Current.CancellationToken;
        var (provider, submitter) = await BuildAsync(postgres);
        await using var _p = provider;

        submitter.RunState = new RunOutcome.Running(0);

        await using (var scope = provider.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<OperationStore>();
            await store.CreateAsync(Ctx(), "auth0|alice", "analysis", clientRequestId: null, ct);

            var claimed = await store.ClaimNextAsync(maxInFlightPerTenant: 100, ct);
            var submitted = (RunOutcome.Submitted)await submitter.SubmitAsync(
                TenantScopedJobRun.Create(Ctx(), JobId, claimed!.IdempotencyKey), ct);

            await store.RecordExternalIdAsync(
                Ctx(), claimed.Id, submitted.RunId.ToString(CultureInfo.InvariantCulture), ct);
        }

        submitter.RunState = new RunOutcome.Succeeded(0);

        // Act
        var didWork = await WorkerFor(provider).RunOnceAsync(ct);

        // Assert — polling resumed on the existing run and finished it. One submission, not two:
        // re-submitting a run already in flight is the mistake this branch avoids.
        await using var check = provider.CreateAsyncScope();
        var final = await check.ServiceProvider.GetRequiredService<LakeWrightDbContext>()
            .Operations.SingleAsync(ct);

        didWork.ShouldBeTrue();
        submitter.SubmittedKeys.Count.ShouldBe(1);
        final.ExternalId.ShouldBe("1000");
        final.State.ShouldBe(OperationState.Succeeded);
        final.CompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task An_operation_kind_with_no_configured_job_fails_with_that_reason()
    {
        // Arrange — the worker submitted one hardcoded job for every kind, so a product with more
        // than one kind of work silently ran the wrong one. Failing is the honest answer; running
        // some other job because it happens to be configured is not.
        var ct = TestContext.Current.CancellationToken;
        var (provider, submitter) = await BuildAsync(postgres);
        await using var _p = provider;

        await using (var scope = provider.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<OperationStore>();
            await store.CreateAsync(Ctx(), "auth0|alice", "export", clientRequestId: null, ct);
        }

        // Act
        var didWork = await WorkerFor(provider).RunOnceAsync(ct);

        // Assert
        await using var check = provider.CreateAsyncScope();
        var final = await check.ServiceProvider.GetRequiredService<LakeWrightDbContext>()
            .Operations.SingleAsync(ct);

        didWork.ShouldBeTrue();
        submitter.SubmittedKeys.ShouldBeEmpty("nothing should be submitted for an unmapped kind");
        final.State.ShouldBe(OperationState.Failed);
        final.Error.ShouldNotBeNull().ShouldContain("export");
    }

    [Fact]
    public async Task A_run_that_exceeds_the_timeout_is_cancelled_rather_than_abandoned()
    {
        // Arrange — a run that never finishes. The timeout used to mark the operation failed and
        // return, which stopped the polling and left the job executing: still spending the compute
        // the timeout exists to bound, and still holding the tenant's schema, which tenant
        // deletion would then drop underneath it having counted the operation as finished.
        var ct = TestContext.Current.CancellationToken;
        var (provider, submitter) = await BuildAsync(postgres);
        await using var _p = provider;

        submitter.RunState = new RunOutcome.Running(0);

        await using (var scope = provider.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<OperationStore>();
            await store.CreateAsync(Ctx(), "auth0|alice", "analysis", clientRequestId: null, ct);
        }

        // A timeout already in the past, so the deadline is passed on the first poll.
        var worker = new OperationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new OperationWorkerOptions
            {
                Jobs = { ["analysis"] = JobId },
                InitialPollInterval = TimeSpan.FromMilliseconds(1),
                MaxPollInterval = TimeSpan.FromMilliseconds(2),
                RunTimeout = TimeSpan.FromMinutes(-1)
            }),
            Options.Create(new MultitenancyOptions { Catalog = "analytics" }),
            NullLogger<OperationWorker>.Instance,
            TimeProvider.System);

        // Act
        await worker.RunOnceAsync(ct);

        // Assert — the run was stopped, not merely forgotten.
        await using var check = provider.CreateAsyncScope();
        var final = await check.ServiceProvider.GetRequiredService<LakeWrightDbContext>()
            .Operations.SingleAsync(ct);

        submitter.CancelledRuns.ShouldBe([1000]);
        final.State.ShouldBe(OperationState.Failed);
        final.Error.ShouldNotBeNull().ShouldContain("cancelled");
    }

    [Fact]
    public async Task A_rejected_submission_fails_the_operation_rather_than_leaving_it_pending()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (provider, submitter) = await BuildAsync(postgres);
        await using var _p = provider;
        submitter.SubmitOverride = new RunOutcome.Failed(null, "warehouse not found", IsTransient: false);

        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<OperationStore>()
                .CreateAsync(Ctx(), "auth0|alice", "analysis", clientRequestId: null, ct);
        }

        // Act
        await WorkerFor(provider).RunOnceAsync(ct);

        // Assert — a rejected submission must not leave the row Pending, where it would be
        // reclaimed forever.
        await using var check = provider.CreateAsyncScope();
        var final = await check.ServiceProvider.GetRequiredService<LakeWrightDbContext>()
            .Operations.SingleAsync(ct);

        final.State.ShouldBe(OperationState.Failed);
        final.Error.ShouldBe("warehouse not found");
        final.ExternalId.ShouldBeNull();
    }

    [Fact]
    public async Task A_failed_run_records_the_platform_reason()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (provider, submitter) = await BuildAsync(postgres);
        await using var _p = provider;
        submitter.RunState = new RunOutcome.Failed(0, "DRIVER_ERROR", IsTransient: true);

        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<OperationStore>()
                .CreateAsync(Ctx(), "auth0|alice", "analysis", clientRequestId: null, ct);
        }

        // Act
        await WorkerFor(provider).RunOnceAsync(ct);

        // Assert — the platform's own wording, not a verdict of ours.
        await using var check = provider.CreateAsyncScope();
        var final = await check.ServiceProvider.GetRequiredService<LakeWrightDbContext>()
            .Operations.SingleAsync(ct);

        final.State.ShouldBe(OperationState.Failed);
        final.Error.ShouldBe("DRIVER_ERROR");
    }

    [Fact]
    public async Task An_idle_worker_reports_that_it_did_nothing()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (provider, _) = await BuildAsync(postgres);
        await using var _p = provider;

        // Act
        var didWork = await WorkerFor(provider).RunOnceAsync(ct);

        // Assert — the caller uses this to decide whether to idle, so an empty queue must not
        // report work.
        didWork.ShouldBeFalse();
    }

    [Fact]
    public async Task A_reconciliation_preferred_dispatch_does_not_starve_an_old_run_behind_new_work()
    {
        var ct = TestContext.Current.CancellationToken;
        var (provider, submitter) = await BuildAsync(postgres);
        await using var _p = provider;
        submitter.RunState = new RunOutcome.Succeeded(0);
        Guid orphanId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<OperationStore>();
            var orphan = await store.CreateAsync(Ctx(), "auth0|alice", "analysis", null, ct);
            var claimed = await store.ClaimNextAsync(100, ct);
            var run = (RunOutcome.Submitted)await submitter.SubmitAsync(TenantScopedJobRun.Create(Ctx(), JobId, claimed!.IdempotencyKey), ct);
            await store.RecordExternalIdAsync(Ctx(), claimed.Id, run.RunId.ToString(CultureInfo.InvariantCulture), ct);
            orphanId = orphan.Id;
            await store.CreateAsync(Ctx(), "auth0|alice", "analysis", null, ct);
        }

        await WorkerFor(provider).RunOnceAsync(ct, preferReconciliation: true);

        await using var check = provider.CreateAsyncScope();
        var completed = await check.ServiceProvider.GetRequiredService<LakeWrightDbContext>().Operations.SingleAsync(x => x.Id == orphanId, ct);
        completed.CompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Execute_pool_starts_second_tenant_while_first_poll_is_held()
    {
        var ct = TestContext.Current.CancellationToken;
        var (provider, submitter) = await BuildAsync(postgres);
        await using var _p = provider;
        submitter.RunState = new RunOutcome.Succeeded(0);
        submitter.HoldFirstPoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        submitter.HoldAllPolls = true;
        submitter.FirstPollStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LakeWrightDbContext>();
            db.Organizations.Add(new Organization { Id = BetaId, Name = "Beta", Slug = "beta", CreatedAt = DateTimeOffset.UtcNow, Schema = UnityCatalogIdentifier.SchemaForTenant(BetaId), State = OrganizationState.Active });
            await db.SaveChangesAsync(ct);
            var store = scope.ServiceProvider.GetRequiredService<OperationStore>();
            await store.CreateAsync(Ctx(), "auth0|alice", "analysis", null, ct);
            await store.CreateAsync(TenantContextFactory.ForTenant(BetaId, "analytics"), "auth0|bob", "analysis", null, ct);
        }
        var worker = WorkerFor(provider, 2, TimeSpan.FromMinutes(5));
        await worker.StartAsync(ct);
        await submitter.FirstPollStarted.Task.WaitAsync(ct);
        await Task.Delay(100, ct);
        submitter.SubmittedTenants.ShouldContain(AcmeId);
        submitter.SubmittedTenants.ShouldContain(BetaId);
        submitter.HoldFirstPoll.SetResult();
        await worker.StopAsync(ct);
    }

    [Fact]
    public async Task Controlled_dispatch_measurement_starts_one_or_four_held_polls_at_the_configured_bound()
    {
        var one = await MeasureHeldPollsAsync(1);
        var four = await MeasureHeldPollsAsync(4);

        one.ShouldBe(1);
        four.ShouldBe(4);
        Console.WriteLine($"controlled worker dispatch: concurrency=1 started {one} held poll; concurrency=4 started {four} held polls");
    }

    [Fact]
    public async Task Idle_dispatch_pool_backs_off_instead_of_spinning_empty_claims()
    {
        var ct = TestContext.Current.CancellationToken;
        var counter = new ClaimCounter();
        var (provider, _) = await BuildAsync(postgres, counter);
        await using var _p = provider;
        var worker = WorkerFor(provider, concurrency: 4, idleDelay: TimeSpan.FromMilliseconds(100));

        await worker.StartAsync(ct);
        await Task.Delay(250, ct);
        await worker.StopAsync(ct);

        // Initial slots plus three 100 ms backoff windows; a spinning scheduler reached 71 here.
        Volatile.Read(ref counter.Count).ShouldBeLessThanOrEqualTo(24);
    }

    private async Task<int> MeasureHeldPollsAsync(int concurrency)
    {
        var ct = TestContext.Current.CancellationToken;
        var (provider, submitter) = await BuildAsync(postgres);
        await using var _p = provider;
        submitter.RunState = new RunOutcome.Succeeded(0);
        submitter.HoldFirstPoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        submitter.HoldAllPolls = true;
        submitter.PollsReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        submitter.PollsTarget = concurrency;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LakeWrightDbContext>();
            foreach (var (id, name) in new[] { (BetaId, "Beta"), (GammaId, "Gamma"), (DeltaId, "Delta") })
            {
                db.Organizations.Add(new Organization { Id = id, Name = name, Slug = name.ToLowerInvariant(), CreatedAt = DateTimeOffset.UtcNow, Schema = UnityCatalogIdentifier.SchemaForTenant(id), State = OrganizationState.Active });
            }
            await db.SaveChangesAsync(ct);
            var store = scope.ServiceProvider.GetRequiredService<OperationStore>();
            foreach (var id in new[] { AcmeId, BetaId, GammaId, DeltaId })
            {
                await store.CreateAsync(TenantContextFactory.ForTenant(id, "analytics"), "auth0|worker", "analysis", null, ct);
            }
        }

        var worker = WorkerFor(provider, concurrency, TimeSpan.FromMinutes(5));
        await worker.StartAsync(ct);
        await submitter.PollsReached.Task.WaitAsync(ct);
        var observed = Volatile.Read(ref submitter.PollCalls);
        submitter.HoldFirstPoll.SetResult();
        await worker.StopAsync(ct);
        return observed;
    }
}
