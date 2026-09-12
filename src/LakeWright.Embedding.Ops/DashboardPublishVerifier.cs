using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
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
/// Reads an authoritative serialized <em>published</em> dashboard definition. Implementations may
/// read a deployment artifact or another supported system of record, but must not return the
/// mutable draft.
/// </summary>
public interface IPublishedDashboardDefinitionReader
{
    /// <summary>Returns the served definition, or null when the source cannot prove it.</summary>
    Task<string?> ReadAsync(string dashboardId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads a source-owned assertion that every tenant-owned relation in a served dashboard revision
/// is constrained by the viewer's external value.
/// </summary>
/// <remarks>
/// Arbitrary SQL cannot be proven tenant-safe by token matching. Implement this over a controlled
/// query-template compiler, an authoritative parsed-query/lineage check, or an equivalent
/// deployment-time verifier. The assertion is accepted only when its dashboard id, served revision,
/// and SHA-256 digest match the separately supplied served definition.
/// </remarks>
public interface IPublishedDashboardIsolationEvidenceReader
{
    /// <summary>Returns evidence for the supplied dashboard's served revision, or null if it cannot be proven.</summary>
    Task<PublishedDashboardIsolationEvidence?> ReadAsync(
        string dashboardId,
        CancellationToken cancellationToken = default);
}

/// <summary>Revision-bound evidence from a trusted, source-owned isolation verifier.</summary>
public sealed record PublishedDashboardIsolationEvidence(
    string DashboardId,
    DateTimeOffset PublishedRevisionCreatedAt,
    string SerializedDashboardSha256,
    bool TenantRelationsConstrained,
    string Reason);

/// <summary>Result of attempting to prove that a served dashboard remains tenant-safe.</summary>
public sealed record PublishedRevisionVerification(
    bool Verifiable,
    bool Verified,
    string Reason,
    DashboardPublishGateVerdict? PublishGate)
{
    /// <summary>Revision-bound source-owned evidence used for a successful strict verification.</summary>
    public PublishedDashboardIsolationEvidence? IsolationEvidence { get; init; }
}

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

/// <summary>Strict broker precondition that requires assignment plus a verified served revision.</summary>
public sealed class PublishedRevisionEmbedPrecondition : IEmbedPrecondition
{
    private readonly IDashboardPublishVerifier _verifier;
    private readonly ITenantDashboardAssignment? _assignments;

    /// <summary>
    /// Retained for source compatibility. It fails closed because strict embedding also requires a
    /// host-owned tenant-to-dashboard assignment resolver.
    /// </summary>
    public PublishedRevisionEmbedPrecondition(IDashboardPublishVerifier verifier)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
    }

    /// <summary>Creates the strict precondition with the host's assignment resolver.</summary>
    public PublishedRevisionEmbedPrecondition(
        IDashboardPublishVerifier verifier,
        ITenantDashboardAssignment assignments)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _assignments = assignments ?? throw new ArgumentNullException(nameof(assignments));
    }

    public async Task EnsureSatisfiedAsync(TenantContext tenant, string dashboardId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(dashboardId);
        if (_assignments is null)
        {
            throw new PublishedDashboardNotVerifiedException(
                "No tenant-to-dashboard assignment resolver is configured for strict embedding.");
        }
        if (!await _assignments.IsAssignedAsync(tenant, dashboardId, cancellationToken).ConfigureAwait(false))
        {
            throw new PublishedDashboardNotVerifiedException(
                "The requested dashboard is not assigned to the resolved tenant.");
        }

        var result = await _verifier.VerifyServedRevisionAsync(dashboardId, cancellationToken).ConfigureAwait(false);
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
    IPublishedDashboardDefinitionReader? servedDefinitionReader = null,
    IPublishedDashboardIsolationEvidenceReader? isolationEvidenceReader = null) : IDashboardPublishVerifier
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
        if (servedDefinitionReader is null || isolationEvidenceReader is null)
        {
            return new PublishedRevisionVerification(
                Verifiable: false,
                Verified: false,
                "A served-dashboard definition reader and source-owned isolation-evidence reader are both required. The Lakeview published endpoint exposes revision metadata but not serialized dashboard SQL.",
                null);
        }

        var serialized = await servedDefinitionReader.ReadAsync(dashboardId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(serialized))
        {
            return new PublishedRevisionVerification(false, false, "The published-dashboard definition reader could not prove a served definition.", null);
        }

        var lint = DashboardMarkerLint.InspectDashboard(serialized);
        if (!lint.Passed)
        {
            return new PublishedRevisionVerification(
                true,
                false,
                "The served dashboard has no executable external-value marker in every dataset.",
                lint);
        }

        var evidence = await isolationEvidenceReader.ReadAsync(dashboardId, cancellationToken).ConfigureAwait(false);
        if (evidence is null)
        {
            return new PublishedRevisionVerification(true, false, "The source-owned isolation verifier could not prove the served dashboard revision.", lint);
        }
        if (!string.Equals(evidence.DashboardId, dashboardId, StringComparison.Ordinal))
        {
            return new PublishedRevisionVerification(true, false, "The isolation evidence belongs to a different dashboard.", lint);
        }

        var publishedAt = await api.GetPublishedRevisionAsync(dashboardId, cancellationToken).ConfigureAwait(false);
        if (publishedAt is null || evidence.PublishedRevisionCreatedAt != publishedAt.Value)
        {
            return new PublishedRevisionVerification(true, false, "The isolation evidence is not bound to the current served revision.", lint);
        }
        if (!MatchesDigest(serialized, evidence.SerializedDashboardSha256))
        {
            return new PublishedRevisionVerification(true, false, "The isolation evidence does not match the served dashboard definition.", lint);
        }
        if (!evidence.TenantRelationsConstrained)
        {
            return new PublishedRevisionVerification(true, false,
                string.IsNullOrWhiteSpace(evidence.Reason)
                    ? "The source-owned verifier could not prove that every tenant-owned relation is constrained."
                    : evidence.Reason,
                lint)
            {
                IsolationEvidence = evidence,
            };
        }

        return new PublishedRevisionVerification(true, true, string.Empty, lint)
        {
            IsolationEvidence = evidence,
        };
    }

    private static bool MatchesDigest(string serialized, string expectedHex)
    {
        if (expectedHex.Length != 64)
        {
            return false;
        }

        try
        {
            var expected = Convert.FromHexString(expectedHex);
            var actual = SHA256.HashData(Encoding.UTF8.GetBytes(serialized));
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
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
