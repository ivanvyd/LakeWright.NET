using Lakewright.Core.Tenancy;
using Lakewright.Databricks;
using Lakewright.Multitenancy;
using Lakewright.Multitenancy.Model;
using Lakewright.Multitenancy.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lakewright.TenantIsolation.Tests;

/// <summary>
/// The worker, driven one iteration at a time against a real database and a fake Databricks.
/// </summary>
[Trait("Category", "TenantIsolation")]
[Collection(nameof(PostgresTests))]
public class OperationWorkerTests(PostgresFixture postgres)
{
    private static readonly TenantId AcmeId = TenantId.Parse("0198f000-0000-7000-8000-0000000000f1");
    private const long JobId = 4242;

    /// <summary>
    /// Stands in for Databricks. Records every idempotency key it is given, which is what lets the
    /// reconciliation test assert that a re-submission reuses the original key rather than
    /// starting a second run.
    /// </summary>
    private sealed class FakeSubmitter : IJobSubmitter
    {
        private readonly Dictionary<string, long> _runsByKey = new(StringComparer.Ordinal);
        private long _nextRunId = 1000;

        public List<string> SubmittedKeys { get; } = [];
        public RunOutcome? SubmitOverride { get; set; }
        public RunOutcome RunState { get; set; } = new RunOutcome.Succeeded(0);

        public Task<RunOutcome> SubmitAsync(TenantScopedJobRun run, CancellationToken cancellationToken)
        {
            SubmittedKeys.Add(run.IdempotencyKey);

            if (SubmitOverride is { } forced) { return Task.FromResult(forced); }

            // What the real idempotency token does: the same key returns the same run.
            if (!_runsByKey.TryGetValue(run.IdempotencyKey, out var runId))
            {
                runId = _nextRunId++;
                _runsByKey[run.IdempotencyKey] = runId;
            }

            return Task.FromResult<RunOutcome>(new RunOutcome.Submitted(runId));
        }

        public Task<RunOutcome> GetRunAsync(long runId, CancellationToken cancellationToken) =>
            Task.FromResult(RunState switch
            {
                RunOutcome.Succeeded => new RunOutcome.Succeeded(runId),
                RunOutcome.Cancelled => new RunOutcome.Cancelled(runId),
                RunOutcome.Failed f => new RunOutcome.Failed(runId, f.Reason, f.IsTransient),
                _ => (RunOutcome)new RunOutcome.Running(runId)
            });
    }

    private static async Task<(ServiceProvider Provider, FakeSubmitter Submitter)>
        BuildAsync(PostgresFixture postgres)
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
        services.AddDbContext<LakewrightDbContext>(o => o.UseNpgsql(connectionString));
        services.AddScoped<OperationStore>();
        services.AddSingleton<IJobSubmitter>(submitter);

        return (services.BuildServiceProvider(), submitter);
    }

    private static OperationWorker WorkerFor(ServiceProvider provider) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new OperationWorkerOptions
            {
                JobId = JobId,
                InitialPollInterval = TimeSpan.FromMilliseconds(1),
                MaxPollInterval = TimeSpan.FromMilliseconds(2),
                ReconciliationGracePeriod = TimeSpan.FromMinutes(-5)
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
            await store.CreateAsync(Ctx(), "auth0|alice", "analysis", ct);
        }

        // Act
        var didWork = await WorkerFor(provider).RunOnceAsync(ct);

        // Assert
        await using var check = provider.CreateAsyncScope();
        var final = await check.ServiceProvider.GetRequiredService<LakewrightDbContext>()
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
            var created = await store.CreateAsync(Ctx(), "auth0|alice", "analysis", ct);
            idempotencyKey = created.IdempotencyKey;

            var claimed = await store.ClaimNextAsync(ct);
            await submitter.SubmitAsync(
                TenantScopedJobRun.Create(Ctx(), JobId, claimed!.IdempotencyKey), ct);
        }

        submitter.SubmittedKeys.Count.ShouldBe(1, "arrange should leave exactly one submission");

        // Act
        var didWork = await WorkerFor(provider).RunOnceAsync(ct);

        // Assert — re-submitted with the ORIGINAL key, so Databricks returns the existing run
        // rather than starting a second one. That is the whole guarantee.
        await using var check = provider.CreateAsyncScope();
        var final = await check.ServiceProvider.GetRequiredService<LakewrightDbContext>()
            .Operations.SingleAsync(ct);

        didWork.ShouldBeTrue();
        submitter.SubmittedKeys.Count.ShouldBe(2);
        submitter.SubmittedKeys.Distinct().Count().ShouldBe(1);
        submitter.SubmittedKeys[1].ShouldBe(idempotencyKey);
        final.ExternalId.ShouldBe("1000", "the reconciled run is the one already started, not a new one");
        final.State.ShouldBe(OperationState.Succeeded);
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
                .CreateAsync(Ctx(), "auth0|alice", "analysis", ct);
        }

        // Act
        await WorkerFor(provider).RunOnceAsync(ct);

        // Assert — a rejected submission must not leave the row Pending, where it would be
        // reclaimed forever.
        await using var check = provider.CreateAsyncScope();
        var final = await check.ServiceProvider.GetRequiredService<LakewrightDbContext>()
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
                .CreateAsync(Ctx(), "auth0|alice", "analysis", ct);
        }

        // Act
        await WorkerFor(provider).RunOnceAsync(ct);

        // Assert — the platform's own wording, not a verdict of ours.
        await using var check = provider.CreateAsyncScope();
        var final = await check.ServiceProvider.GetRequiredService<LakewrightDbContext>()
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
}
