using System.Globalization;
using LakeWright.Core.Cost;
using LakeWright.Core.Tenancy;
using Microsoft.Azure.Databricks.Client;
using Microsoft.Azure.Databricks.Client.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LakeWright.Databricks;

/// <summary>
/// Reads job-run usage and effective list-price cost from Databricks billing system tables.
/// </summary>
/// <remarks>
/// The query is fixed text and every value is a Statement Execution parameter. Run identifiers
/// are chunked and passed as one bound comma-delimited value; <c>split</c> turns it into an array
/// in Databricks SQL. The system table is account-wide, so <c>workspace_id</c> is an additional
/// mandatory bound filter rather than an assumption that run ids are globally unique.
/// </remarks>
public sealed class DatabricksBillingUsageReader : IBillingUsageReader
{
    private const int RunIdsPerQuery = 500;
    private const NumberStyles DecimalStyles =
        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;

    private const string BillingSql =
        """
        SELECT CAST(u.usage_metadata.job_run_id AS STRING) AS JobRunId,
               COALESCE(SUM(CASE WHEN u.usage_unit = 'DBU' THEN u.usage_quantity ELSE 0 END), 0) AS DbusConsumed,
               p.currency_code AS CurrencyCode,
               COALESCE(SUM(u.usage_quantity * p.pricing.effective_list.default), 0) AS EstimatedListCost
        FROM system.billing.usage u
        JOIN system.billing.list_prices p
          ON p.account_id = u.account_id
         AND p.cloud = u.cloud
         AND p.sku_name = u.sku_name
         AND p.usage_unit = u.usage_unit
         AND u.usage_end_time >= p.price_start_time
         AND (p.price_end_time IS NULL OR u.usage_end_time < p.price_end_time)
        WHERE u.workspace_id = :workspace_id
          AND u.usage_metadata.job_run_id IS NOT NULL
          AND array_contains(split(:job_run_ids, ','), CAST(u.usage_metadata.job_run_id AS STRING))
          AND u.usage_start_time < :until
          AND u.usage_end_time > :from
          AND u.usage_date >= :from_date
          AND u.usage_date <= :until_date
        GROUP BY CAST(u.usage_metadata.job_run_id AS STRING), p.currency_code
        """;

    private readonly IDatabricksStatementSession _session;
    private readonly DatabricksOptions _databricks;
    private readonly BillingUsageOptions _billing;
    private readonly TimeProvider _timeProvider;

    public DatabricksBillingUsageReader(
        DatabricksClient client,
        IOptions<DatabricksOptions> databricks,
        IOptions<BillingUsageOptions> billing,
        ILogger<DatabricksBillingUsageReader> logger,
        TimeProvider timeProvider)
        : this(
            new DatabricksStatementSession(client, logger),
            databricks.Value,
            billing.Value,
            timeProvider)
    {
    }

    internal DatabricksBillingUsageReader(
        IDatabricksStatementSession session,
        DatabricksOptions databricks,
        BillingUsageOptions billing,
        TimeProvider timeProvider)
    {
        _session = session;
        _databricks = databricks;
        _billing = billing;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<BillingRunUsage>> ReadAsync(
        TenantContext tenant,
        DateTimeOffset from,
        DateTimeOffset until,
        IReadOnlyCollection<long> jobRunIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(jobRunIds);
        if (from >= until)
        {
            throw new ArgumentException("from must be earlier than until.", nameof(from));
        }

        if (jobRunIds.Count == 0)
        {
            return [];
        }

        if (jobRunIds.Any(id => id <= 0))
        {
            throw new ArgumentException("Job run ids must be positive.", nameof(jobRunIds));
        }

        var uniqueRunIds = jobRunIds.Distinct().Order().ToArray();

        var rows = new List<BillingRunUsage>();
        foreach (var chunk in uniqueRunIds.Chunk(RunIdsPerQuery))
        {
            var request = CreateRequest(from, until, chunk);
            var outcome = await _session.ExecuteAsync(request, tenant.TenantId, cancellationToken);
            outcome = await WaitForCompletionAsync(tenant, outcome, cancellationToken);
            rows.AddRange(Parse(outcome));
        }

        return rows;
    }

    private SqlStatement CreateRequest(
        DateTimeOffset from,
        DateTimeOffset until,
        IReadOnlyCollection<long> jobRunIds) => new()
        {
            WarehouseId = _databricks.WarehouseId,
            Catalog = "system",
            Schema = "billing",
            Statement = BillingSql,
            Parameters =
            [
                Parameter(StatementParameter.String("workspace_id", _billing.WorkspaceId)),
                Parameter(StatementParameter.String(
                    "job_run_ids",
                    string.Join(',', jobRunIds.Select(id => id.ToString(CultureInfo.InvariantCulture))))),
                Parameter(StatementParameter.Timestamp("from", from)),
                Parameter(StatementParameter.Timestamp("until", until)),
                Parameter(StatementParameter.Date("from_date", DateOnly.FromDateTime(from.UtcDateTime))),
                Parameter(StatementParameter.Date("until_date", DateOnly.FromDateTime(until.UtcDateTime)))
            ],
            Disposition = SqlStatementDisposition.INLINE,
            Format = StatementFormat.JSON_ARRAY,
            RowLimit = 10_000,
            WaitTimeout = _databricks.WaitTimeout,
            OnWaitTimeout = SqlStatementOnWaitTimeout.CONTINUE
        };

    private async Task<StatementOutcome> WaitForCompletionAsync(
        TenantContext tenant,
        StatementOutcome outcome,
        CancellationToken cancellationToken)
    {
        string? activeStatementId = null;
        try
        {
            while (outcome is StatementOutcome.Pending pending)
            {
                activeStatementId = pending.StatementId;
                await Task.Delay(
                    TimeSpan.FromMilliseconds(_billing.PollIntervalMilliseconds),
                    _timeProvider,
                    cancellationToken);
                outcome = await _session.GetAsync(
                    tenant.TenantId,
                    pending.StatementId,
                    cancellationToken);
            }

            return outcome;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (activeStatementId is not null)
            {
                using var cancelTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await _session.CancelAsync(activeStatementId, cancelTimeout.Token);
                }
                catch (ClientApiException)
                {
                    // Cancellation is best effort. The caller's cancellation remains the result.
                }
                catch (OperationCanceledException) when (cancelTimeout.IsCancellationRequested)
                {
                    // The five-second best-effort cancel must not replace the caller's exception.
                }
            }

            throw;
        }
    }

    private static List<BillingRunUsage> Parse(StatementOutcome outcome)
    {
        if (outcome is StatementOutcome.Failure failure)
        {
            throw new BillingUsageException(failure.ErrorCode, failure.IsTransient);
        }

        if (outcome is not StatementOutcome.Success success)
        {
            throw new BillingUsageException("INLINE_RESULT_REQUIRED", isTransient: false);
        }

        var indexes = success.ColumnNames
            .Select((name, index) => (name, index))
            .ToDictionary(x => x.name, x => x.index, StringComparer.OrdinalIgnoreCase);
        var required = new[] { "JobRunId", "DbusConsumed", "CurrencyCode", "EstimatedListCost" };
        if (required.Any(column => !indexes.ContainsKey(column)))
        {
            throw new BillingUsageException("INVALID_SCHEMA", isTransient: false);
        }

        var rows = new List<BillingRunUsage>(success.Rows.Count);
        foreach (var row in success.Rows)
        {
            if (!TryRead(row, indexes["JobRunId"], out var runText)
                || !long.TryParse(runText, NumberStyles.None, CultureInfo.InvariantCulture, out var runId)
                || runId <= 0
                || !TryRead(row, indexes["DbusConsumed"], out var dbuText)
                || !decimal.TryParse(dbuText, DecimalStyles, CultureInfo.InvariantCulture, out var dbus)
                || !TryRead(row, indexes["CurrencyCode"], out var currency)
                || string.IsNullOrWhiteSpace(currency)
                || !TryRead(row, indexes["EstimatedListCost"], out var costText)
                || !decimal.TryParse(costText, DecimalStyles, CultureInfo.InvariantCulture, out var cost))
            {
                throw new BillingUsageException("INVALID_ROW", isTransient: false);
            }

            rows.Add(new BillingRunUsage(
                runId,
                dbus,
                new CurrencyAmount(currency.ToUpperInvariant(), cost)));
        }

        return rows;
    }

    private static bool TryRead(
        IReadOnlyList<string?> row,
        int index,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
    {
        value = index < row.Count ? row[index] : null;
        return value is not null;
    }

    private static SqlStatementParameter Parameter(StatementParameter parameter) => new()
    {
        Name = parameter.Name,
        Value = parameter.Value,
        Type = parameter.Type
    };
}
