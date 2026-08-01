using LakeWright.Core.Jobs;
using LakeWright.Core.Tenancy;
using LakeWright.Databricks;
using Microsoft.Azure.Databricks.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// Exercises the Databricks clients against a real workspace.
/// </summary>
/// <remarks>
/// Excluded from the default run and from CI. These need a workspace, they cost money, and they are
/// the only tests here that can fail because of something outside this repository.
///
/// They exist because the rest of the suite proves the *shapes* that prevent cross-tenant reads and
/// injection, and proves the worker's crash recovery against a fake. Neither proves that a statement
/// or a job actually round-trips. ADR 0005's acceptance criterion says "against the live workspace",
/// and a fake cannot satisfy that wording.
///
/// Run with:
///   DATABRICKS_HOST=https://... DATABRICKS_TOKEN=$(az account get-access-token \
///     --resource 2ff814a6-3304-4ab8-85cb-cd0e6f879c1d --query accessToken -o tsv) \
///   LAKEWRIGHT_WAREHOUSE_ID=... LAKEWRIGHT_JOB_ID=... LAKEWRIGHT_CATALOG=... \
///   dotnet test --filter Category=Live
/// </remarks>
[Trait("Category", "Live")]
public class LiveDatabricksTests
{
    private static string Require(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException(
            $"{name} is required for Category=Live tests. See the remarks on {nameof(LiveDatabricksTests)}.");

    private static DatabricksClient CreateClient() =>
        DatabricksClient.CreateClient(Require("DATABRICKS_HOST"), Require("DATABRICKS_TOKEN"));

    /// <summary>The schema the bundle creates, so these tests need no setup beyond a deployed bundle.</summary>
    private const string ReferenceSchema = "reference";

    private static readonly TenantId LiveTenantId =
        TenantId.Parse("0198f000-0000-7000-8000-0000000011fe");

    private static TenantContext Tenant() =>
        TenantContextFactory.ForTenant(LiveTenantId, Require("LAKEWRIGHT_CATALOG"), ReferenceSchema);

    private static DatabricksStatementExecutor Executor(DatabricksClient client) =>
        new(client,
            Options.Create(new DatabricksOptions
            {
                WorkspaceUrl = Require("DATABRICKS_HOST"),
                WarehouseId = Require("LAKEWRIGHT_WAREHOUSE_ID")
            }),
            NullLogger<DatabricksStatementExecutor>.Instance);

    [Fact]
    public async Task A_parameterised_statement_binds_values_rather_than_interpolating_them()
    {
        // Arrange — the payload is the point: if it were interpolated it would change the statement.
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient();
        var statement = TenantScopedStatement.Create(
            Tenant(),
            "SELECT :tenant AS tenant, :n + 1 AS bumped",
            StatementParameter.String("tenant", "acme'; DROP TABLE x; --"),
            StatementParameter.Int("n", 41));

        // Act
        var outcome = await Executor(client).ExecuteAsync(statement, ct);

        // Assert
        var success = outcome.ShouldBeOfType<StatementOutcome.Success>();
        success.Rows.Count.ShouldBe(1);
        success.Rows[0][0].ShouldBe("acme'; DROP TABLE x; --");
        success.Rows[0][1].ShouldBe("42");
    }

    [Fact]
    public async Task A_failing_statement_surfaces_as_a_failure_rather_than_an_empty_result()
    {
        // Arrange — the client returns rather than throws for this, so an unwrapped caller would
        // read a failed query as "no data". That translation is what StatementOutcome exists for.
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient();
        var statement = TenantScopedStatement.Create(
            Tenant(), "SELECT * FROM definitely_not_a_table_lakewright");

        // Act
        var outcome = await Executor(client).ExecuteAsync(statement, ct);

        // Assert
        var failure = outcome.ShouldBeOfType<StatementOutcome.Failure>();
        failure.ErrorCode.ShouldBe("BAD_REQUEST");
        failure.IsTransient.ShouldBeFalse();
    }

    [Fact]
    public async Task A_job_run_reaches_a_terminal_state_and_a_repeated_key_returns_the_same_run()
    {
        // Arrange — the second half is ADR 0005's reconciliation mechanism. Everything else in the
        // suite proves it against a fake that was written to behave this way; this proves Databricks
        // actually does.
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient();
        var submitter = new DatabricksJobSubmitter(client, NullLogger<DatabricksJobSubmitter>.Instance);
        var jobId = long.Parse(Require("LAKEWRIGHT_JOB_ID"), System.Globalization.CultureInfo.InvariantCulture);
        var run = TenantScopedJobRun.Create(Tenant(), jobId, Guid.CreateVersion7().ToString("N"));

        // Act
        var first = await submitter.SubmitAsync(run, ct);
        var resubmitted = await submitter.SubmitAsync(run, ct);

        var submitted = first.ShouldBeOfType<RunOutcome.Submitted>();
        RunOutcome terminal;
        var deadline = DateTimeOffset.UtcNow.AddMinutes(15);
        do
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            terminal = await submitter.GetRunAsync(submitted.RunId, ct);
        }
        while (terminal is RunOutcome.Running && DateTimeOffset.UtcNow < deadline);

        // Assert
        resubmitted.ShouldBeOfType<RunOutcome.Submitted>().RunId.ShouldBe(
            submitted.RunId,
            "re-submitting the same idempotency key must return the original run, not start a second");
        terminal.ShouldBeOfType<RunOutcome.Succeeded>();
    }
}
