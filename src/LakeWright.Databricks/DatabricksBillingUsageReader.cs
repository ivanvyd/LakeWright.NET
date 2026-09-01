using System.Globalization;
using System.Net;
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
/// are capped and passed as one bound comma-delimited value; <c>split</c> turns it into an array in
/// Databricks SQL. The system table is account-wide, so <c>workspace_id</c> is an additional
/// mandatory bound filter rather than an assumption that run ids are globally unique.
/// </remarks>
public sealed class DatabricksBillingUsageReader : IBillingUsageReader, IDisposable
{
    private const NumberStyles DecimalStyles =
        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;

    private const string BillingSql =
        """
        WITH PricedUsage AS (
            SELECT CAST(u.usage_metadata.job_run_id AS STRING) AS JobRunId,
                   u.usage_unit AS UsageUnit,
                   u.usage_quantity
                     * CAST(timestampdiff(
                           MICROSECOND,
                           greatest(u.usage_start_time, :from, p.price_start_time),
                           least(u.usage_end_time, :until, coalesce(p.price_end_time, u.usage_end_time)))
                         AS DECIMAL(38, 12))
                     / NULLIF(CAST(timestampdiff(
                           MICROSECOND, u.usage_start_time, u.usage_end_time)
                         AS DECIMAL(38, 12)), 0) AS WindowQuantity,
                   p.currency_code AS CurrencyCode,
                   p.pricing.effective_list.default AS EffectiveListPrice
            FROM system.billing.usage u
            JOIN system.billing.list_prices p
              ON p.account_id = u.account_id
             AND p.cloud = u.cloud
             AND p.sku_name = u.sku_name
             AND p.usage_unit = u.usage_unit
             AND u.usage_end_time > p.price_start_time
             AND (p.price_end_time IS NULL OR u.usage_start_time < p.price_end_time)
             AND :until > p.price_start_time
             AND (p.price_end_time IS NULL OR :from < p.price_end_time)
            WHERE u.workspace_id = :workspace_id
              AND u.usage_metadata.job_run_id IS NOT NULL
              AND array_contains(split(:job_run_ids, ','), CAST(u.usage_metadata.job_run_id AS STRING))
              AND u.usage_start_time < :until
              AND u.usage_end_time > :from
              AND u.usage_date >= :from_date
              AND u.usage_date <= :until_date
        )
        SELECT JobRunId,
               COALESCE(SUM(CASE WHEN UsageUnit = 'DBU' THEN WindowQuantity ELSE 0 END), 0) AS DbusConsumed,
               CurrencyCode,
               COALESCE(SUM(WindowQuantity * EffectiveListPrice), 0) AS EstimatedListCost
        FROM PricedUsage
        GROUP BY JobRunId, CurrencyCode
        """;

    private readonly IDatabricksStatementSession _session;
    private readonly DatabricksOptions _databricks;
    private readonly BillingUsageOptions _billing;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _statementSlots;
    private readonly int _maxOutstandingStatements;
    private int _outstandingStatements;

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
        _statementSlots = new SemaphoreSlim(billing.MaxConcurrentStatements);
        _maxOutstandingStatements = billing.MaxOutstandingStatements;
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
        BillingUsageLimits.ValidateReportWindow(from, until, _timeProvider.GetUtcNow());

        if (jobRunIds.Count == 0)
        {
            return [];
        }

        if (jobRunIds.Any(id => id <= 0))
        {
            throw new ArgumentException("Job run ids must be positive.", nameof(jobRunIds));
        }

        var uniqueRunIds = jobRunIds.Distinct().Order().ToArray();
        if (uniqueRunIds.Length > BillingUsageLimits.MaxJobRunsPerReport)
        {
            throw new BillingUsageException("REPORT_TOO_LARGE", isTransient: false);
        }

        var outstanding = Interlocked.Increment(ref _outstandingStatements);
        if (outstanding > _maxOutstandingStatements)
        {
            Interlocked.Decrement(ref _outstandingStatements);
            throw new BillingUsageException("BILLING_BUSY", isTransient: true);
        }

        try
        {
            await _statementSlots.WaitAsync(cancellationToken);
            try
            {
                var request = CreateRequest(from, until, uniqueRunIds);
                var startedAt = _timeProvider.GetUtcNow();
                var deadline = startedAt.AddSeconds(_billing.PollingTimeoutSeconds);
                var serverCancellationDeadline = startedAt.AddSeconds(
                    _billing.SubmissionWaitTimeoutSeconds);
                using var executeTimeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(_billing.PollingTimeoutSeconds),
                    _timeProvider);
                StatementOutcome outcome;
                try
                {
                    // Once creation starts, keep the local slot until Databricks returns a
                    // statement id. Caller cancellation can then cancel accepted remote work.
                    outcome = await _session.ExecuteAsync(
                        request,
                        tenant.TenantId,
                        executeTimeout.Token);
                }
                catch (OperationCanceledException) when (executeTimeout.IsCancellationRequested)
                {
                    await HoldAdmissionUntilAsync(serverCancellationDeadline);
                    throw new BillingUsageException("POLL_TIMEOUT", isTransient: true);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    await HoldAdmissionUntilAsync(serverCancellationDeadline);
                    throw new BillingUsageException("STATEMENT_CREATE_UNCERTAIN", isTransient: true);
                }

                if (outcome is StatementOutcome.Failure { StatementId: null } failure
                    && !IsDefinitiveRequestRejection(failure.StatusCode))
                {
                    await HoldAdmissionUntilAsync(serverCancellationDeadline);
                    throw new BillingUsageException("STATEMENT_CREATE_UNCERTAIN", isTransient: true);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    await CancelBestEffortAsync((outcome as StatementOutcome.Pending)?.StatementId);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                outcome = await WaitForCompletionAsync(
                    tenant,
                    outcome,
                    deadline,
                    cancellationToken);
                return Parse(outcome);
            }
            finally
            {
                _statementSlots.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _outstandingStatements);
        }
    }

    public void Dispose() => _statementSlots.Dispose();

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
            WaitTimeout = $"{_billing.SubmissionWaitTimeoutSeconds}s",
            OnWaitTimeout = SqlStatementOnWaitTimeout.CANCEL
        };

    private async Task HoldAdmissionUntilAsync(DateTimeOffset serverCancellationDeadline)
    {
        var remaining = serverCancellationDeadline - _timeProvider.GetUtcNow();
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, _timeProvider, CancellationToken.None);
        }
    }

    private static bool IsDefinitiveRequestRejection(HttpStatusCode? statusCode) =>
        statusCode is not null
        && (int)statusCode >= 400
        && (int)statusCode < 500
        && statusCode != HttpStatusCode.RequestTimeout;

    private async Task<StatementOutcome> WaitForCompletionAsync(
        TenantContext tenant,
        StatementOutcome outcome,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        string? activeStatementId = null;
        try
        {
            while (outcome is StatementOutcome.Pending pending)
            {
                activeStatementId = pending.StatementId;
                var remaining = deadline - _timeProvider.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                {
                    throw new BillingUsageException("POLL_TIMEOUT", isTransient: true);
                }

                await Task.Delay(
                    TimeSpan.FromMilliseconds(_billing.PollIntervalMilliseconds) < remaining
                        ? TimeSpan.FromMilliseconds(_billing.PollIntervalMilliseconds)
                        : remaining,
                    _timeProvider,
                    cancellationToken);
                using var pollTimeout = new CancellationTokenSource(remaining, _timeProvider);
                using var pollCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    pollTimeout.Token);
                try
                {
                    outcome = await _session.GetAsync(
                        tenant.TenantId,
                        pending.StatementId,
                        pollCancellation.Token);
                }
                catch (OperationCanceledException) when (
                    pollTimeout.IsCancellationRequested
                    && !cancellationToken.IsCancellationRequested)
                {
                    throw new BillingUsageException("POLL_TIMEOUT", isTransient: true);
                }
            }

            return outcome;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CancelBestEffortAsync(activeStatementId);
            throw;
        }
        catch
        {
            await CancelBestEffortAsync(activeStatementId);
            throw;
        }
    }

    private async Task CancelBestEffortAsync(string? statementId)
    {
        if (statementId is null)
        {
            return;
        }

        using var cancelTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _session.CancelAsync(statementId, cancelTimeout.Token);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Cancellation must never replace the original timeout, transport error, or caller cancellation.
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
