using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using LakeWright.Core;
using LakeWright.Core.Features;
using LakeWright.Core.Tenancy;
using Microsoft.Extensions.Options;

namespace LakeWright.Embedding.Ops;

/// <summary>Checks the relationship between a dashboard draft and the revision viewers receive.</summary>
public interface IDashboardPublishVerifier
{
    /// <summary>Returns whether the latest draft update is newer than the published revision.</summary>
    Task<bool> HasUnpublishedChangesAsync(string dashboardId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inspects the serialized definition that a supplied reader proves is served to viewers.
    /// The public Lakeview published endpoint exposes revision metadata, not serialized SQL, so
    /// this reports <see cref="PublishedRevisionVerification.Verifiable"/> false until an adopter
    /// registers a reader for its authoritative published artifact.
    /// </summary>
    Task<PublishedRevisionVerification> VerifyServedRevisionAsync(
        string dashboardId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads an authoritative serialized *published* dashboard definition. Implementations may read a
/// deployment artifact or another supported system of record, but must not return the mutable draft.
/// </summary>
public interface IPublishedDashboardDefinitionReader
{
    /// <summary>Returns the served definition, or null when the source cannot prove it.</summary>
    Task<string?> ReadAsync(string dashboardId, CancellationToken cancellationToken = default);
}

/// <summary>Result of attempting to prove that a served dashboard remains tenant-safe.</summary>
public sealed record PublishedRevisionVerification(
    bool Verifiable,
    bool Verified,
    string Reason,
    DashboardPublishGateVerdict? PublishGate);

/// <summary>Controls the bounded cache used for draft-versus-published comparisons.</summary>
public sealed class DashboardPublishVerifierOptions
{
    /// <summary>How long a comparison result may be reused.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(1);

    internal void Validate()
    {
        if (CacheDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(CacheDuration), "CacheDuration must be positive.");
        }
    }
}

/// <summary>Optional strict broker precondition that refuses a mint until served-revision verification passes.</summary>
public sealed class PublishedRevisionEmbedPrecondition(IDashboardPublishVerifier verifier) : IEmbedPrecondition
{
    public async Task EnsureSatisfiedAsync(TenantContext tenant, string dashboardId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        var result = await verifier.VerifyServedRevisionAsync(dashboardId, cancellationToken).ConfigureAwait(false);
        if (!result.Verified)
        {
            throw new PublishedDashboardNotVerifiedException(result.Reason);
        }
    }
}

/// <summary>Raised by the strict embed precondition when served revision verification is unavailable or fails.</summary>
public sealed class PublishedDashboardNotVerifiedException(string message) : LakeWrightException(message);

internal interface IPublishVerificationApi
{
    Task<(DateTimeOffset? UpdatedAt, string SerializedDashboard)> GetDraftAsync(string dashboardId, CancellationToken cancellationToken);
    Task<DateTimeOffset?> GetPublishedRevisionAsync(string dashboardId, CancellationToken cancellationToken);
}

internal sealed class DashboardPublishVerifier(
    IPublishVerificationApi api,
    IOptions<DashboardPublishVerifierOptions> options,
    TimeProvider timeProvider,
    ILakeWrightFeatureGate features,
    IPublishedDashboardDefinitionReader? servedDefinitionReader = null) : IDashboardPublishVerifier
{
    private readonly DashboardPublishVerifierOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, (bool HasChanges, DateTimeOffset ExpiresAt)> _changes = new(StringComparer.Ordinal);

    public async Task<bool> HasUnpublishedChangesAsync(string dashboardId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dashboardId);
        features.EnsureEnabled(LakeWrightFeatures.Operations);
        _options.Validate();
        if (_changes.TryGetValue(dashboardId, out var cached) && cached.ExpiresAt > timeProvider.GetUtcNow())
        {
            return cached.HasChanges;
        }

        var draft = await api.GetDraftAsync(dashboardId, cancellationToken).ConfigureAwait(false);
        var published = await api.GetPublishedRevisionAsync(dashboardId, cancellationToken).ConfigureAwait(false);
        var hasChanges = draft.UpdatedAt is { } updated && (published is null || updated > published.Value);
        _changes[dashboardId] = (hasChanges, timeProvider.GetUtcNow() + _options.CacheDuration);
        return hasChanges;
    }

    public async Task<PublishedRevisionVerification> VerifyServedRevisionAsync(
        string dashboardId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dashboardId);
        features.EnsureEnabled(LakeWrightFeatures.Operations);
        if (servedDefinitionReader is null)
        {
            return new PublishedRevisionVerification(
                Verifiable: false,
                Verified: false,
                "No published-dashboard definition reader is registered. The Lakeview published endpoint exposes revision metadata but not serialized dashboard SQL.",
                null);
        }

        var serialized = await servedDefinitionReader.ReadAsync(dashboardId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(serialized))
        {
            return new PublishedRevisionVerification(false, false, "The published-dashboard definition reader could not prove a served definition.", null);
        }

        var gate = DashboardPublishGate.InspectDashboard(serialized);
        return gate.Passed
            ? new PublishedRevisionVerification(true, true, string.Empty, gate)
            : new PublishedRevisionVerification(true, false, gate.Reason, gate);
    }
}

internal sealed class DatabricksPublishVerificationApi(HttpClient http, IOpsTokenBroker tokens) : IPublishVerificationApi
{
    public async Task<(DateTimeOffset? UpdatedAt, string SerializedDashboard)> GetDraftAsync(string dashboardId, CancellationToken cancellationToken)
    {
        using var payload = await SendAsync($"api/2.0/lakeview/dashboards/{Uri.EscapeDataString(dashboardId)}", cancellationToken).ConfigureAwait(false);
        var root = payload.RootElement;
        var serialized = ReadString(root, "serialized_dashboard")
            ?? throw new InvalidOperationException("The dashboard draft response omitted serialized_dashboard.");
        return (ParseTime(ReadString(root, "update_time")), serialized);
    }

    public async Task<DateTimeOffset?> GetPublishedRevisionAsync(string dashboardId, CancellationToken cancellationToken)
    {
        using var payload = await SendAsync($"api/2.0/lakeview/dashboards/{Uri.EscapeDataString(dashboardId)}/published", cancellationToken).ConfigureAwait(false);
        return ParseTime(ReadString(payload.RootElement, "revision_create_time"));
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

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static DateTimeOffset? ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
}

internal sealed class DashboardVerificationApiException(int statusCode)
    : LakeWrightException($"Databricks Dashboard API answered {statusCode}.");
