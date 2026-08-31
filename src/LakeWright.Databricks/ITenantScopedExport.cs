using LakeWright.Core.Tenancy;

namespace LakeWright.Databricks;

/// <summary>
/// Streams the rows of a tenant-scoped statement, so a caller can write them to disk or to
/// a network target without holding the whole result set in memory.
/// </summary>
/// <remarks>
/// <para>
/// The executor's <see cref="IStatementExecutor.ExecuteAsync"/> returns the whole result in
/// one <see cref="StatementOutcome"/> value, which is fine for an interactive query that
/// returns a few hundred rows. A background export that walks ten thousand rows, or a board
/// whose result is bigger than the warehouse's <c>INLINE</c> cap, needs a streaming
/// surface so the rows can be written out as they arrive.
/// </para>
/// <para>
/// The streaming surface takes the same <see cref="TenantScopedStatement"/> as the executor
/// and yields a <see cref="ExportRow"/> per row, in the warehouse's order, with the column
/// names returned by the first call. Cancellation is honored at every yield. The library
/// does not project columns or filter rows: which columns and which filters belong in the
/// caller. The library's job is to deliver every row, in order, without buffering the world.
/// </para>
/// <para>
/// The export is generic across both <c>INLINE</c> and <c>EXTERNAL_LINKS</c> outcomes.
/// The latter is the case a real export hits once the result is bigger than the warehouse's
/// inline cap; the export walks the presigned chunk URLs and yields each chunk's rows. The
/// links are presigned and require no <c>Authorization</c> header (the executor's
/// <see cref="StatementOutcome.LargeResult"/> doc comment is explicit about this), so the
/// fetch uses a plain <see cref="HttpClient"/>.
/// </para>
/// </remarks>
public interface ITenantScopedExport
{
    /// <summary>
    /// Runs the statement and yields its rows, one <see cref="ExportRow"/> at a time.
    /// </summary>
    /// <param name="statement">
    /// A statement already scoped to one tenant via the <c>TenantScopedStatement.Create</c> factory.
    /// </param>
    /// <param name="cancellationToken">Cancels the export between rows.</param>
    /// <returns>
    /// An async sequence of rows. The first item of the returned
    /// <see cref="ExportColumn.Columns"/> collection is the column-name header; every
    /// subsequent <see cref="ExportRow"/> carries a row whose values are positioned to match
    /// the column names.
    /// </returns>
    IAsyncEnumerable<ExportRow> StreamAsync(
        TenantScopedStatement statement,
        CancellationToken cancellationToken);
}

/// <summary>
/// The header line of an export, returned before any data row.
/// </summary>
/// <param name="Columns">The warehouse's column names, in order.</param>
public sealed record ExportColumn(IReadOnlyList<string> Columns);

/// <summary>
/// One row of an export, with a <see cref="Column"/> header on the first item and rows
/// on every subsequent item.
/// </summary>
/// <param name="Column">
/// The header line, present only on the first item of the stream.
/// </param>
/// <param name="Values">
/// One row's values, positioned to match <see cref="Column"/>. Missing values are null.
/// Length always equals <c>Column.Columns.Count</c>.
/// </param>
public sealed record ExportRow(ExportColumn? Column, IReadOnlyList<string?> Values);
