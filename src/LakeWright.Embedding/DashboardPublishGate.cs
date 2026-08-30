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
/// close the bypass that bit VRM in production. It is <em>not</em> an AST walk: a board that
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
    public static PublishGateVerdict Inspect(string? datasetSql)
    {
        if (string.IsNullOrWhiteSpace(datasetSql))
        {
            return PublishGateVerdict.Fail("Dataset SQL is empty.");
        }

        var marker = new MarkerScanner(datasetSql);
        var hits = marker.Scan();

        return hits.Count == 0
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
            var result = Inspect(datasetSqls[i]);
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

/// <summary>
/// Tokenizer that walks the SQL byte by byte, tracking single-quoted strings, line
/// comments, and block comments, and reports the marker only when it appears in code.
/// </summary>
/// <remarks>
/// Single-quoted strings use the SQL-standard doubled-quote escape (<c>''</c>). Backslash
/// escapes are not honored because Databricks SQL does not honor them either. Line
/// comments begin with <c>--</c> and run to the next newline. Block comments nest only
/// one level (no <c>/* /* */ */</c>); that is the SQL standard.
/// </remarks>
internal sealed class MarkerScanner
{
    private readonly string _sql;

    internal MarkerScanner(string sql) => _sql = sql;

    internal IReadOnlyList<MarkerHit> Scan()
    {
        var hits = new List<MarkerHit>();
        var i = 0;
        var len = _sql.Length;
        while (i < len)
        {
            var c = _sql[i];
            var next = i + 1 < len ? _sql[i + 1] : '\0';

            // String literal — skip to the matching single quote, honoring '' as an
            // escaped quote. The marker inside a string is data, not code.
            if (c == '\'')
            {
                i++;
                while (i < len)
                {
                    if (_sql[i] == '\'')
                    {
                        if (i + 1 < len && _sql[i + 1] == '\'')
                        {
                            i += 2; // doubled quote — keep going
                        }
                        else
                        {
                            i++; // closing quote
                            break;
                        }
                    }
                    else
                    {
                        i++;
                    }
                }
                continue;
            }

            // Line comment.
            if (c == '-' && next == '-')
            {
                while (i < len && _sql[i] != '\n')
                {
                    i++;
                }
                continue;
            }

            // Block comment — non-nesting.
            if (c == '/' && next == '*')
            {
                i += 2;
                while (i + 1 < len && !(_sql[i] == '*' && _sql[i + 1] == '/'))
                {
                    i++;
                }
                if (i + 1 < len)
                {
                    i += 2; // consume closing */
                }
                else
                {
                    i = len; // unterminated — be conservative and stop
                }
                continue;
            }

            // Bare double quote is not a SQL string delimiter, but a backtick-quoted
            // identifier can contain anything. Treat the contents of a backtick-quoted
            // identifier as not a reference.
            if (c == '`')
            {
                i++;
                while (i < len && _sql[i] != '`')
                {
                    i++;
                }
                if (i < len)
                {
                    i++; // closing `
                }
                continue;
            }

            // The marker is a bare identifier. Match only when the surrounding bytes
            // are not part of an identifier — `x__aibi_external_value` should not match.
            if (IsMarkerStart(c) && StartsMarkerAt(i))
            {
                if (i + DashboardPublishGate.ExternalValueColumn.Length <= len
                    && string.Equals(
                        _sql.Substring(i, DashboardPublishGate.ExternalValueColumn.Length),
                        DashboardPublishGate.ExternalValueColumn,
                        StringComparison.OrdinalIgnoreCase)
                    && EndsMarkerAt(i + DashboardPublishGate.ExternalValueColumn.Length - 1))
                {
                    hits.Add(new MarkerHit(0, i));
                }
            }
            i++;
        }
        return hits;
    }

    private static bool IsMarkerStart(char c) => c == '_' || char.IsLetter(c);

    private bool StartsMarkerAt(int i) =>
        i == 0 || !IsIdentifierPart(_sql[i - 1]);

    private bool EndsMarkerAt(int i) =>
        i + 1 == _sql.Length || !IsIdentifierPart(_sql[i + 1]);

    private static bool IsIdentifierPart(char c) => c == '_' || char.IsLetterOrDigit(c);
}
