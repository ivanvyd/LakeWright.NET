namespace Lakewright.Databricks;

/// <summary>
/// The result of running a statement. Success and failure are both values, so a caller cannot
/// reach the rows without having passed the failure case.
/// </summary>
/// <remarks>
/// The underlying client splits failure in two, and only one half throws. A bad warehouse id
/// raises <c>ClientApiException</c>; a statement that fails returns normally with
/// <c>Status.State = FAILED</c> and <c>Manifest</c> and <c>Result</c> both null. See
/// docs/planning/spike-01-statement-execution.md, where that was measured.
///
/// The consequence is that the obvious calling pattern, <c>(await Execute(s, ct)).Result.DataArray</c>,
/// throws <see cref="NullReferenceException"/> from a line unrelated to the cause; and a caller
/// who defends by null-checking returns an empty result set for a query that failed, which reads
/// downstream as "this tenant has no data". Both halves are unified here.
/// </remarks>
public abstract record StatementOutcome
{
    private StatementOutcome() { }

    public sealed record Success(
        IReadOnlyList<string> ColumnNames,
        IReadOnlyList<IReadOnlyList<string?>> Rows,
        long TotalRowCount,
        string StatementId) : StatementOutcome;

    /// <summary>
    /// The statement ran and failed, or the request was rejected. <paramref name="ErrorCode"/> is
    /// the Databricks code where one was returned.
    /// </summary>
    public sealed record Failure(
        string ErrorCode,
        string Message,
        string? StatementId,
        bool IsTransient) : StatementOutcome;

    /// <summary>
    /// The statement did not finish inside the wait timeout and is still running. The Statement
    /// Execution API caps <c>wait_timeout</c> at 50 seconds, so this is a normal outcome for real
    /// analytical queries rather than an error. Poll with <paramref name="StatementId"/>.
    /// </summary>
    public sealed record Pending(string StatementId) : StatementOutcome;
}
