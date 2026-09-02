using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using LakeWright.Core.Features;

namespace LakeWright.Embedding.Ops;

/// <summary>Reads cached draft and published dashboard metadata through the operations principal.</summary>
public interface IDashboardMetadataCatalog
{
    /// <summary>Gets the mutable draft definition and metadata.</summary>
    Task<DashboardDraftMetadata> GetDraftAsync(string dashboardId, CancellationToken cancellationToken = default);

    /// <summary>Gets published revision metadata.</summary>
    Task<DashboardPublishedMetadata> GetPublishedAsync(string dashboardId, CancellationToken cancellationToken = default);

    /// <summary>Lists all active workspace dashboards by walking every catalog page.</summary>
    Task<IReadOnlyList<DashboardSummary>> ListAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>Host-replaceable cache for dashboard metadata.</summary>
public interface IDashboardMetadataCache
{
    ValueTask<DashboardDraftMetadata?> GetDraftAsync(string dashboardId, CancellationToken cancellationToken = default);
    ValueTask SetDraftAsync(string dashboardId, DashboardDraftMetadata value, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);
    ValueTask<DashboardPublishedMetadata?> GetPublishedAsync(string dashboardId, CancellationToken cancellationToken = default);
    ValueTask SetPublishedAsync(string dashboardId, DashboardPublishedMetadata value, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<DashboardSummary>?> GetAllAsync(CancellationToken cancellationToken = default);
    ValueTask SetAllAsync(IReadOnlyList<DashboardSummary> value, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);
}

/// <summary>Draft dashboard metadata, including the serialized definition required by deployment checks.</summary>
public sealed record DashboardDraftMetadata(
    string Id,
    string DisplayName,
    string WarehouseId,
    string? Etag,
    string? ParentPath,
    DateTimeOffset? UpdatedAt,
    string SerializedDashboard);

/// <summary>Published dashboard metadata exposed by the Lakeview API.</summary>
public sealed record DashboardPublishedMetadata(
    string DashboardId,
    string DisplayName,
    string WarehouseId,
    bool EmbedCredentials,
    DateTimeOffset? RevisionCreatedAt);

/// <summary>Cache lifetime for dashboard metadata. Keep it short because drafts can change interactively.</summary>
public sealed class DashboardMetadataCacheOptions
{
    /// <summary>Maximum reuse time for a metadata response.</summary>
    public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(1);

    internal void Validate()
    {
        if (Duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Duration), "Duration must be positive.");
        }
    }
}

internal sealed class DashboardMetadataCatalog(
    IDashboardMetadataApi api,
    IDashboardCatalog catalog,
    IDashboardMetadataCache cache,
    DashboardMetadataCacheOptions options,
    TimeProvider timeProvider,
    ILakeWrightFeatureGate features) : IDashboardMetadataCatalog
{
    public async Task<DashboardDraftMetadata> GetDraftAsync(string dashboardId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dashboardId);
        features.EnsureEnabled(LakeWrightFeatures.Operations);
        var cached = await cache.GetDraftAsync(dashboardId, cancellationToken).ConfigureAwait(false);
        if (cached is not null) { return cached; }

        var draft = await api.GetDraftAsync(dashboardId, cancellationToken).ConfigureAwait(false);
        await cache.SetDraftAsync(dashboardId, draft, ExpiresAt(), cancellationToken).ConfigureAwait(false);
        return draft;
    }

    public async Task<DashboardPublishedMetadata> GetPublishedAsync(string dashboardId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dashboardId);
        features.EnsureEnabled(LakeWrightFeatures.Operations);
        var cached = await cache.GetPublishedAsync(dashboardId, cancellationToken).ConfigureAwait(false);
        if (cached is not null) { return cached; }

        var published = await api.GetPublishedAsync(dashboardId, cancellationToken).ConfigureAwait(false);
        await cache.SetPublishedAsync(dashboardId, published, ExpiresAt(), cancellationToken).ConfigureAwait(false);
        return published;
    }

    public async Task<IReadOnlyList<DashboardSummary>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        features.EnsureEnabled(LakeWrightFeatures.Operations);
        var cached = await cache.GetAllAsync(cancellationToken).ConfigureAwait(false);
        if (cached is not null) { return cached; }

        var all = new List<DashboardSummary>();
        string? pageToken = null;
        do
        {
            var page = await catalog.ListAsync(pageSize: 1000, pageToken, cancellationToken).ConfigureAwait(false);
            all.AddRange(page.Dashboards);
            pageToken = page.NextPageToken;
        }
        while (!string.IsNullOrWhiteSpace(pageToken));

        var frozen = Array.AsReadOnly(all.ToArray());
        await cache.SetAllAsync(frozen, ExpiresAt(), cancellationToken).ConfigureAwait(false);
        return frozen;
    }

    private DateTimeOffset ExpiresAt()
    {
        options.Validate();
        return timeProvider.GetUtcNow() + options.Duration;
    }
}

internal interface IDashboardMetadataApi
{
    Task<DashboardDraftMetadata> GetDraftAsync(string dashboardId, CancellationToken cancellationToken);
    Task<DashboardPublishedMetadata> GetPublishedAsync(string dashboardId, CancellationToken cancellationToken);
}

internal sealed class DatabricksDashboardMetadataApi(HttpClient http, IOpsTokenBroker tokens) : IDashboardMetadataApi
{
    public async Task<DashboardDraftMetadata> GetDraftAsync(string dashboardId, CancellationToken cancellationToken)
    {
        using var payload = await SendAsync($"api/2.0/lakeview/dashboards/{Uri.EscapeDataString(dashboardId)}", cancellationToken).ConfigureAwait(false);
        var root = payload.RootElement;
        return new DashboardDraftMetadata(
            Required(root, "dashboard_id"),
            Required(root, "display_name"),
            Required(root, "warehouse_id"),
            Optional(root, "etag"),
            Optional(root, "parent_path"),
            ParseTime(Optional(root, "update_time")),
            Required(root, "serialized_dashboard"));
    }

    public async Task<DashboardPublishedMetadata> GetPublishedAsync(string dashboardId, CancellationToken cancellationToken)
    {
        using var payload = await SendAsync($"api/2.0/lakeview/dashboards/{Uri.EscapeDataString(dashboardId)}/published", cancellationToken).ConfigureAwait(false);
        var root = payload.RootElement;
        return new DashboardPublishedMetadata(
            dashboardId,
            Required(root, "display_name"),
            Required(root, "warehouse_id"),
            root.TryGetProperty("embed_credentials", out var embedded) && embedded.ValueKind is JsonValueKind.True,
            ParseTime(Optional(root, "revision_create_time")));
    }

    private async Task<JsonDocument> SendAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        var token = await tokens.AcquireAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new DashboardVerificationApiException((int)response.StatusCode);
        }
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    private static string Required(JsonElement root, string name) => Optional(root, name)
        ?? throw new InvalidOperationException($"The dashboard response omitted {name}.");

    private static string? Optional(JsonElement root, string name) => root.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static DateTimeOffset? ParseTime(string? value) => DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
}

internal sealed class MemoryDashboardMetadataCache(TimeProvider timeProvider) : IDashboardMetadataCache
{
    private readonly ConcurrentDictionary<string, (DashboardDraftMetadata Value, DateTimeOffset ExpiresAt)> _drafts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DashboardPublishedMetadata Value, DateTimeOffset ExpiresAt)> _published = new(StringComparer.Ordinal);
    private (IReadOnlyList<DashboardSummary> Value, DateTimeOffset ExpiresAt)? _all;
    private readonly object _allLock = new();

    public ValueTask<DashboardDraftMetadata?> GetDraftAsync(string dashboardId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Get(_drafts, dashboardId));

    public ValueTask SetDraftAsync(string dashboardId, DashboardDraftMetadata value, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        _drafts[dashboardId] = (value, expiresAt);
        return ValueTask.CompletedTask;
    }

    public ValueTask<DashboardPublishedMetadata?> GetPublishedAsync(string dashboardId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Get(_published, dashboardId));

    public ValueTask SetPublishedAsync(string dashboardId, DashboardPublishedMetadata value, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        _published[dashboardId] = (value, expiresAt);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<DashboardSummary>?> GetAllAsync(CancellationToken cancellationToken = default)
    {
        lock (_allLock)
        {
            return ValueTask.FromResult(_all is { } cached && cached.ExpiresAt > timeProvider.GetUtcNow() ? cached.Value : null);
        }
    }

    public ValueTask SetAllAsync(IReadOnlyList<DashboardSummary> value, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        lock (_allLock)
        {
            _all = (Array.AsReadOnly(value.ToArray()), expiresAt);
        }
        return ValueTask.CompletedTask;
    }

    private T? Get<T>(ConcurrentDictionary<string, (T Value, DateTimeOffset ExpiresAt)> values, string key) where T : class =>
        values.TryGetValue(key, out var cached) && cached.ExpiresAt > timeProvider.GetUtcNow() ? cached.Value : null;
}
