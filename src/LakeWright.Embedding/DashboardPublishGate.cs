using System.Text.Json;
using LakeWright.Core.Sql;

namespace LakeWright.Embedding;

/// <summary>
/// A small, well-tested lint that reports executable references to
/// <c>__aibi_external_value</c> in a dashboard definition.
/// </summary>
/// <remarks>
/// <para>
/// A board that mentions the column inside a string literal —
/// <c>WHERE col = '__aibi_external_value'</c> — must not pass a simple substring check.
/// </para>
/// <para>
/// The tokenizer tracks string and comment state and reports the marker only when it appears in
/// code. It intentionally does not prove row ownership: a projection, tautology, unused CTE, or
/// one branch of a union can contain the marker while still returning another tenant's rows.
/// Use a source-owned, revision-bound isolation verifier before minting a browser token. This
/// lint remains useful for author feedback and deployment diagnostics.
/// </para>
/// </remarks>
public static class DashboardMarkerLint
{
    /// <summary>
    /// The claim column the embed broker sets. The linter accepts this exact identifier,
    /// case-insensitively, with no leading or trailing characters other than SQL
    /// identifier delimiters.
    /// </summary>
    public const string ExternalValueColumn = "__aibi_external_value";

    /// <summary>
    /// Inspect one dataset and report whether it contains an executable
    /// <c>__aibi_external_value</c> marker.
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

    /// <summary>
    /// Inspects every dataset in a serialized Lakeview dashboard definition for the marker.
    /// </summary>
    /// <remarks>
    /// Invalid JSON, a missing dataset array, and datasets with empty SQL fail this lint. A pass
    /// only proves marker presence; it is not a publish or tenant-isolation decision.
    /// </remarks>
    public static DashboardPublishGateVerdict InspectDashboard(string? serializedDashboard)
    {
        if (string.IsNullOrWhiteSpace(serializedDashboard))
        {
            return DashboardPublishGateVerdict.Fail("Serialized dashboard JSON is empty.");
        }

        try
        {
            using var document = JsonDocument.Parse(serializedDashboard);
            if (!document.RootElement.TryGetProperty("datasets", out var datasets) ||
                datasets.ValueKind != JsonValueKind.Array ||
                datasets.GetArrayLength() == 0)
            {
                return DashboardPublishGateVerdict.Fail("Dashboard has no datasets.");
            }

            var results = new List<DatasetPublishGateVerdict>();
            for (var index = 0; index < datasets.GetArrayLength(); index++)
            {
                var dataset = datasets[index];
                if (dataset.ValueKind != JsonValueKind.Object)
                {
                    results.Add(new DatasetPublishGateVerdict(
                        index,
                        "(unnamed)",
                        PublishGateVerdict.Fail("Dataset is not an object.")));
                    continue;
                }
                var name = dataset.TryGetProperty("name", out var nameElement) &&
                    nameElement.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(nameElement.GetString())
                    ? nameElement.GetString()!
                    : "(unnamed)";
                var verdict = InspectDataset(ReadDatasetSql(dataset), index);
                results.Add(new DatasetPublishGateVerdict(index, name, verdict));
            }

            var failed = results.FirstOrDefault(result => !result.Verdict.Passed);
            return failed is null
                ? DashboardPublishGateVerdict.Pass(results)
                : DashboardPublishGateVerdict.Fail(
                    $"Dataset #{failed.DatasetIndex + 1} ({failed.Name}): {failed.Verdict.Reason}",
                    results);
        }
        catch (JsonException)
        {
            return DashboardPublishGateVerdict.Fail("Serialized dashboard JSON is invalid.");
        }
    }

    private static string? ReadDatasetSql(JsonElement dataset)
    {
        if (dataset.TryGetProperty("queryLines", out var lines) && lines.ValueKind == JsonValueKind.Array)
        {
            return string.Join(
                Environment.NewLine,
                lines.EnumerateArray()
                    .Where(line => line.ValueKind == JsonValueKind.String)
                    .Select(line => line.GetString()));
        }

        return dataset.TryGetProperty("query", out var query) && query.ValueKind == JsonValueKind.String
            ? query.GetString()
            : null;
    }
}

/// <summary>
/// Legacy name for <see cref="DashboardMarkerLint"/>.
/// </summary>
/// <remarks>
/// This API is retained for source compatibility. Its verdict is marker linting only and must not
/// be used to assert that a dashboard filters tenant-owned rows or to authorize token minting.
/// </remarks>
public static class DashboardPublishGate
{
    /// <inheritdoc cref="DashboardMarkerLint.ExternalValueColumn"/>
    public const string ExternalValueColumn = DashboardMarkerLint.ExternalValueColumn;

    /// <inheritdoc cref="DashboardMarkerLint.Inspect"/>
    public static PublishGateVerdict Inspect(string? datasetSql) => DashboardMarkerLint.Inspect(datasetSql);

    /// <inheritdoc cref="DashboardMarkerLint.InspectAll"/>
    public static PublishGateVerdict InspectAll(IReadOnlyList<string> datasetSqls) => DashboardMarkerLint.InspectAll(datasetSqls);

    /// <inheritdoc cref="DashboardMarkerLint.InspectDashboard"/>
    public static DashboardPublishGateVerdict InspectDashboard(string? serializedDashboard) => DashboardMarkerLint.InspectDashboard(serializedDashboard);
}

/// <summary>
/// The result of a <see cref="DashboardMarkerLint.Inspect"/> call.
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

/// <summary>A publish-gate verdict for a complete serialized dashboard definition.</summary>
public sealed record DashboardPublishGateVerdict(
    bool Passed,
    string Reason,
    IReadOnlyList<DatasetPublishGateVerdict> Datasets)
{
    internal static DashboardPublishGateVerdict Pass(IReadOnlyList<DatasetPublishGateVerdict> datasets) =>
        new(true, string.Empty, datasets);

    internal static DashboardPublishGateVerdict Fail(
        string reason,
        IReadOnlyList<DatasetPublishGateVerdict>? datasets = null) =>
        new(false, reason, datasets ?? Array.Empty<DatasetPublishGateVerdict>());
}

/// <summary>The name, index, and safety verdict for one serialized dashboard dataset.</summary>
public sealed record DatasetPublishGateVerdict(int DatasetIndex, string Name, PublishGateVerdict Verdict);
