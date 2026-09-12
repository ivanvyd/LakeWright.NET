using Microsoft.Azure.Databricks.Client;
using Microsoft.Azure.Databricks.Client.Models;

namespace LakeWright.Databricks;

internal static class StatementResultReader
{
    // Count container/reference overhead as well as characters; empty cells must consume budget.
    private const long MaxInlineStorage = 25 * 1024 * 1024;
    internal const int MaxChunks = 100_000;

    public static async Task<StatementOutcome> CompleteInlineAsync(
        IDatabricksStatementSession session, StatementOutcome outcome, CancellationToken cancellationToken)
    {
        if (outcome is not StatementOutcome.Success { FirstChunk: { } first } success) { return outcome; }
        var rows = new List<IReadOnlyList<string?>>();
        long storage = 0;
        var chunk = first;
        var expectedIndex = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = chunk.DataArray ?? [];
                if (chunk.ChunkIndex != expectedIndex || chunk.RowOffset != rows.Count
                    || chunk.RowCount != values.LongLength
                    || expectedIndex >= MaxChunks)
                {
                    return Invalid(success.StatementId);
                }
                foreach (var row in values)
                {
                    if (row is null || row.Length != success.ColumnNames.Count) { return Invalid(success.StatementId); }
                    storage += 32L + row.Sum(value => 32L + (value?.Length ?? 0) * 2L);
                    if (storage > MaxInlineStorage)
                    {
                        return new StatementOutcome.Failure("RESULT_LIMIT_EXCEEDED",
                            "Inline results exceed the local materialization limit; use streaming export.", success.StatementId, false);
                    }
                    rows.Add(row);
                }
                var next = NextIndex(chunk);
                if (next is null)
                {
                    if (rows.Count != success.TotalRowCount
                        || (success.TotalChunkCount is { } total && total != expectedIndex + 1))
                    {
                        return Invalid(success.StatementId);
                    }
                    return new StatementOutcome.Success(success.ColumnNames, rows, success.TotalRowCount, success.StatementId);
                }
                if (next != expectedIndex + 1) { return Invalid(success.StatementId); }
                expectedIndex = next.Value;
                chunk = await session.GetChunkAsync(success.StatementId, expectedIndex, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ClientApiException exception)
        {
            return new StatementOutcome.Failure("RESULT_FETCH_FAILED", "The warehouse could not return the next result chunk.", success.StatementId, false)
            {
                StatusCode = exception.StatusCode,
            };
        }
        catch (InvalidDataException)
        {
            return Invalid(success.StatementId);
        }
    }

    public static IReadOnlyList<ExternalResultChunk> ExternalChunks(StatementExecutionResultChunk result)
    {
        var links = result.ExternalLinks?.ToArray() ?? [];
        return links.Select((link, index) => new ExternalResultChunk(
            link.ChunkIndex, link.RowOffset, link.RowCount, ExternalUri(link.ExternalLink),
            link.Expiration == default ? null : new DateTimeOffset(link.Expiration.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(link.Expiration, DateTimeKind.Utc) : link.Expiration.ToUniversalTime()),
            NextIndex(link) ?? (index == links.Length - 1 ? NextIndex(result) : null))).ToArray();
    }

    private static Uri ExternalUri(string link) => Uri.TryCreate(link, UriKind.Absolute, out var uri)
        ? uri : throw new InvalidDataException("The warehouse returned an invalid external result link.");

    private static int? NextIndex(StatementExecutionResult result)
    {
        if (result.NextChunkIndex > 0) { return result.NextChunkIndex; }
        if (!string.IsNullOrEmpty(result.NextChunkInternalLink))
        {
            // The API allows only the opaque link to be supplied. Chunks are sequential, so
            // resolve the next index with the SDK instead of following/logging its credential-bearing URL.
            if (result.ChunkIndex >= MaxChunks) { throw new InvalidDataException("Result chunk limit exceeded."); }
            return result.ChunkIndex + 1;
        }
        return null;
    }

    private static StatementOutcome.Failure Invalid(string statementId) =>
        new("RESULT_INCOMPLETE", "Result chunk metadata does not describe a complete, ordered result.", statementId, false);
}
