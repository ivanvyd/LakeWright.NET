using LakeWright.Core.Sql;

namespace LakeWright.Embedding;

/// <summary>
/// A small, well-tested check that a dashboard's datasets reference <c>__aibi_external_value</c>
/// in a way that actually filters rows, before a tenant is allowed to embed it.
/// </summary>
/// <remarks>
/// <para>
/// The vendor's <c>__aibi_external_value</c> pattern works only when the column flows from a
/// claim through a SQL filter that actually constrains the dataset. A board that mentions the
/// column inside a string literal — <c>WHERE col = '__aibi_external_value'</c> — passes a
/// substring search but ships unscoped, and any tenant that opens it sees every row. The gap
/// analysis calls this out as the highest-value safety feature the library lacks (gap §3.4).
/// </para>
/// <para>
/// The check is a tokenizer that tracks three string states — single-quoted, line comment,
/// block comment — and reports the marker only when it appears in code. That is enough to
/// close the reproduced string-literal bypass. It is <em>not</em> an AST walk: a board that
/// reconstructs the marker by concatenation (<c>'__aibi_' || 'external_value'</c>) is
/// genuinely unscoped and the gate will refuse it. Closing that case is the warehouse's
/// <c>parsed_query</c> job, not this one's; see ADR 0017.
/// </para>
/// </remarks>
public static class DashboardPublishGate
{
    /// <summary>
    /// The claim column the embed broker sets. The gate accepts this exact identifier,
    /// case-insensitively, with no leading or trailing characters other than SQL
    /// identifier delimiters.
    /// </summary>
    public const string ExternalValueColumn = "__aibi_external_value";

    /// <summary>
    /// Inspect one dataset and report whether it filters on <c>__aibi_external_value</c>.
    /// </summary>
    /// <param name="datasetSql">The dataset's SQL text. May be multi-line.</param>
    /// <returns>
    /// A verdict carrying the count of out-of-string-literal references and the byte offsets
    /// at which they were found. An empty <paramref name="datasetSql"/> fails closed.
    /// </returns>
    public static PublishGateVerdict Inspect(string? datasetSql) =>
        InspectDataset(datasetSql, datasetIndex: 0);

    private static PublishGateVerdict InspectDataset(string? datasetSql, int datasetIndex)
    {
        if (string.IsNullOrWhiteSpace(datasetSql))
        {
            return PublishGateVerdict.Fail("Dataset SQL is empty.");
        }

        var hits = SqlTokenScanner.Find(datasetSql, ExternalValueColumn)
            .Select(offset => new MarkerHit(datasetIndex, offset))
            .ToArray();

        return hits.Length == 0
            ? PublishGateVerdict.Fail("No reference to __aibi_external_value outside of a string literal or comment.")
            : PublishGateVerdict.Pass(hits);
    }

    /// <summary>
    /// Inspect every dataset on a dashboard. A dashboard passes if every dataset passes.
    /// </summary>
    public static PublishGateVerdict InspectAll(IReadOnlyList<string> datasetSqls)
    {
        ArgumentNullException.ThrowIfNull(datasetSqls);
        if (datasetSqls.Count == 0)
        {
            return PublishGateVerdict.Fail("Dashboard has no datasets.");
        }

        var allHits = new List<MarkerHit>();
        for (var i = 0; i < datasetSqls.Count; i++)
        {
            var result = InspectDataset(datasetSqls[i], datasetIndex: i);
            if (!result.Passed)
            {
                return PublishGateVerdict.Fail(
                    $"Dataset #{i + 1}: {result.Reason}");
            }
            allHits.AddRange(result.Hits);
        }
        return PublishGateVerdict.Pass(allHits);
    }
}

/// <summary>
/// The result of a <see cref="DashboardPublishGate.Inspect"/> call.
/// </summary>
/// <param name="Passed">True when at least one out-of-string reference was found.</param>
/// <param name="Reason">
/// When <paramref name="Passed"/> is false, a human-readable reason. When true, empty.
/// </param>
/// <param name="Hits">
/// The byte offsets at which <c>__aibi_external_value</c> appeared as a real SQL token,
/// across all datasets inspected.
/// </param>
public sealed record PublishGateVerdict(
    bool Passed,
    string Reason,
    IReadOnlyList<MarkerHit> Hits)
{
    internal static PublishGateVerdict Pass(IReadOnlyList<MarkerHit> hits) =>
        new(true, string.Empty, hits);

    internal static PublishGateVerdict Fail(string reason) =>
        new(false, reason, Array.Empty<MarkerHit>());
}

/// <summary>One location where the marker was found as a real SQL token.</summary>
/// <param name="DatasetIndex">Zero-based index of the dataset the hit belongs to.</param>
/// <param name="Offset">Zero-based byte offset in the dataset SQL.</param>
public sealed record MarkerHit(int DatasetIndex, int Offset);
