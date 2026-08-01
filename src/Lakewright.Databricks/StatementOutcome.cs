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

    /// <summary>
    /// The statement succeeded and its rows are here.
    /// </summary>
    /// <remarks>
    /// Only produced for <c>INLINE</c> results. An earlier version defaulted to
    /// <c>EXTERNAL_LINKS</c> and still read <c>DataArray</c>, which is only populated for
    /// <c>INLINE</c> — so every successful query returned zero rows. Unit tests could not see it;
    /// the first live call did. See <see cref="LargeResult"/>.
    /// </remarks>
    public sealed record Success(
        IReadOnlyList<string> ColumnNames,
        IReadOnlyList<IReadOnlyList<string?>> Rows,
        long TotalRowCount,
        string StatementId) : StatementOutcome;

    /// <summary>
    /// The statement succeeded and its rows are in external storage, not here.
    /// </summary>
    /// <remarks>
    /// A distinct case rather than a <see cref="Success"/> with an empty row list, because those
    /// are not the same thing and a caller must not be able to confuse them.
    ///
    /// Fetch <paramref name="Links"/> with a plain HTTP client and **no Authorization header**:
    /// they are presigned, and Azure blob rejects a request carrying both a SAS and an
    /// Authorization header with HTTP 400. Chunk reads are destructive — the statement closes when
    /// the last chunk is read, and links expire an hour after success.
    /// </remarks>
    public sealed record LargeResult(
        IReadOnlyList<string> ColumnNames,
        IReadOnlyList<Uri> Links,
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
