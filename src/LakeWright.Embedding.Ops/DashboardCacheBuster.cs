using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using LakeWright.Core;
using LakeWright.Core.Features;
using Microsoft.Extensions.Options;

namespace LakeWright.Embedding.Ops;

/// <summary>Changes a published dashboard revision so viewers fetch results after a completed refresh.</summary>
public interface IDashboardCacheBuster
{
    /// <summary>
    /// Adds the stable <c>-- refresh {runId}</c> marker to a dashboard draft and publishes it.
    /// Repeating the call for the same run makes no workspace mutation.
    /// </summary>
    Task<DashboardCacheBustResult> BustOnceAsync(
        string dashboardId,
        long runId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Busts a group of dashboards once for a caller-defined schedule bucket. The bucket is a
    /// stable identifier, not wall-clock text, and is validated before it reaches dashboard SQL.
    /// </summary>
    Task<IReadOnlyList<DashboardCacheBustResult>> ScheduledBustAsync(
        IReadOnlyCollection<string> dashboardIds,
        string bucket,
        CancellationToken cancellationToken = default);
}

/// <summary>Explicit publishing behavior for cache-bust revisions.</summary>
public sealed class DashboardCacheBustOptions
{
    /// <summary>
    /// Whether a cache-bust publish may embed the ops principal's credentials in the dashboard.
    /// This defaults to false; opt in only when the dashboard is intentionally configured for it.
    /// </summary>
    public bool EmbedCredentials { get; set; }
}

/// <summary>One cache-bust result.</summary>
public sealed record DashboardCacheBustResult(string DashboardId, string Marker, bool AlreadyCurrent);

internal interface IDashboardEditorApi
{
    Task<DashboardDraft> GetDraftAsync(string dashboardId, CancellationToken cancellationToken);
    Task PatchDraftAsync(DashboardDraft draft, string serializedDashboard, CancellationToken cancellationToken);
    Task PublishAsync(string dashboardId, bool embedCredentials, CancellationToken cancellationToken);
}

internal sealed record DashboardDraft(
    string Id,
    string DisplayName,
    string WarehouseId,
    string? Etag,
    string? ParentPath,
    string SerializedDashboard);

internal sealed class DashboardCacheBuster(
    IDashboardEditorApi dashboards,
    IOptions<DashboardCacheBustOptions> options,
    ILakeWrightFeatureGate features) : IDashboardCacheBuster
{
    private readonly DashboardCacheBustOptions _options = options.Value;

    public Task<DashboardCacheBustResult> BustOnceAsync(
        string dashboardId,
        long runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dashboardId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runId);
        return BustAsync(dashboardId, $"-- refresh {runId}", cancellationToken);
    }

    public async Task<IReadOnlyList<DashboardCacheBustResult>> ScheduledBustAsync(
        IReadOnlyCollection<string> dashboardIds,
        string bucket,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dashboardIds);
        ValidateBucket(bucket);
        var results = new List<DashboardCacheBustResult>(dashboardIds.Count);
        foreach (var dashboardId in dashboardIds.Distinct(StringComparer.Ordinal))
        {
            results.Add(await BustAsync(dashboardId, $"-- refresh scheduled {bucket}", cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    private async Task<DashboardCacheBustResult> BustAsync(string dashboardId, string marker, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dashboardId);
        features.EnsureEnabled(LakeWrightFeatures.Operations);

        var draft = await dashboards.GetDraftAsync(dashboardId, cancellationToken).ConfigureAwait(false);
        var stamped = AddMarker(draft.SerializedDashboard, marker, out var alreadyCurrent);
        if (alreadyCurrent)
        {
            return new DashboardCacheBustResult(dashboardId, marker, AlreadyCurrent: true);
        }

        try
        {
            await dashboards.PatchDraftAsync(draft, stamped, cancellationToken).ConfigureAwait(false);
        }
        catch (DashboardApiException exception) when (exception.IsConcurrencyConflict)
        {
            // Another replica may have made the same idempotent change. Re-read exactly once:
            // retrying PATCH blindly could overwrite a concurrent authored dashboard change.
            var current = await dashboards.GetDraftAsync(dashboardId, cancellationToken).ConfigureAwait(false);
            _ = AddMarker(current.SerializedDashboard, marker, out var currentHasMarker);
            if (!currentHasMarker)
            {
                throw;
            }

            return new DashboardCacheBustResult(dashboardId, marker, AlreadyCurrent: true);
        }

        await dashboards.PublishAsync(dashboardId, _options.EmbedCredentials, cancellationToken).ConfigureAwait(false);
        return new DashboardCacheBustResult(dashboardId, marker, AlreadyCurrent: false);
    }

    internal static string AddMarker(string serializedDashboard, string marker, out bool alreadyCurrent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serializedDashboard);
        ArgumentException.ThrowIfNullOrWhiteSpace(marker);

        JsonObject root;
        try
        {
            root = JsonNode.Parse(serializedDashboard)?.AsObject()
                ?? throw new InvalidOperationException("The serialized dashboard is not a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The serialized dashboard is invalid JSON.", exception);
        }

        if (root["datasets"] is not JsonArray datasets || datasets.Count == 0)
        {
            throw new InvalidOperationException("The serialized dashboard has no datasets to stamp.");
        }

        var allCurrent = true;
        foreach (var datasetNode in datasets)
        {
            if (datasetNode is not JsonObject dataset)
            {
                throw new InvalidOperationException("The serialized dashboard contains a non-object dataset.");
            }

            if (dataset["queryLines"] is JsonArray lines)
            {
                if (!ContainsMarker(lines, marker))
                {
                    lines.Add(marker);
                    allCurrent = false;
                }
                continue;
            }

            if (dataset["query"]?.GetValue<string>() is { } query)
            {
                if (!ContainsMarker(query, marker))
                {
                    dataset["query"] = query.TrimEnd() + Environment.NewLine + marker;
                    allCurrent = false;
                }
                continue;
            }

            throw new InvalidOperationException("A dashboard dataset has neither queryLines nor query.");
        }

        alreadyCurrent = allCurrent;
        return root.ToJsonString();
    }

    private static bool ContainsMarker(JsonArray lines, string marker) =>
        lines.Any(line => line?.GetValue<string>()?.Trim() == marker);

    private static bool ContainsMarker(string query, string marker) =>
        query.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(line => line == marker);

    private static void ValidateBucket(string bucket)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        if (bucket.Length > 64 || bucket.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new ArgumentException("A schedule bucket must be 1-64 ASCII letters, digits, hyphens, or underscores.", nameof(bucket));
        }
    }
}

internal sealed class DatabricksDashboardEditorApi(HttpClient http, IOpsTokenBroker tokens) : IDashboardEditorApi
{
    public async Task<DashboardDraft> GetDraftAsync(string dashboardId, CancellationToken cancellationToken)
    {
        using var payload = await SendJsonAsync(HttpMethod.Get, $"api/2.0/lakeview/dashboards/{Uri.EscapeDataString(dashboardId)}", null, cancellationToken).ConfigureAwait(false);
        var root = payload.RootElement;
        return new DashboardDraft(
            ReadRequiredString(root, "dashboard_id"),
            ReadRequiredString(root, "display_name"),
            ReadRequiredString(root, "warehouse_id"),
            ReadOptionalString(root, "etag"),
            ReadOptionalString(root, "parent_path"),
            ReadRequiredString(root, "serialized_dashboard"));
    }

    public async Task PatchDraftAsync(DashboardDraft draft, string serializedDashboard, CancellationToken cancellationToken)
    {
        using var payload = await SendJsonAsync(
            HttpMethod.Patch,
            $"api/2.0/lakeview/dashboards/{Uri.EscapeDataString(draft.Id)}",
            JsonSerializer.SerializeToElement(new
            {
                dashboard_id = draft.Id,
                display_name = draft.DisplayName,
                warehouse_id = draft.WarehouseId,
                etag = draft.Etag,
                parent_path = draft.ParentPath,
                serialized_dashboard = serializedDashboard,
            }),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task PublishAsync(string dashboardId, bool embedCredentials, CancellationToken cancellationToken)
    {
        using var payload = await SendJsonAsync(
            HttpMethod.Post,
            $"api/2.0/lakeview/dashboards/{Uri.EscapeDataString(dashboardId)}/published",
            JsonSerializer.SerializeToElement(new { embed_credentials = embedCredentials }),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendJsonAsync(
        HttpMethod method,
        string relativeUrl,
        JsonElement? body,
        CancellationToken cancellationToken)
    {
        var token = await tokens.AcquireAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(method, relativeUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        if (body is { } content)
        {
            request.Content = JsonContent.Create(content);
        }

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new DashboardApiException(response.StatusCode);
        }

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    private static string ReadRequiredString(JsonElement root, string name) =>
        ReadOptionalString(root, name) ?? throw new InvalidOperationException($"The dashboard response omitted {name}.");

    private static string? ReadOptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}

internal sealed class DashboardApiException(HttpStatusCode statusCode)
    : LakeWrightException($"Databricks Dashboard API answered {(int)statusCode}.")
{
    public bool IsConcurrencyConflict => StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed;

    public HttpStatusCode StatusCode { get; } = statusCode;
}
