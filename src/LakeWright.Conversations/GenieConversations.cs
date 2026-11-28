using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using LakeWright.Core.Features;
using LakeWright.Core.Tenancy;
using Microsoft.Extensions.Options;

namespace LakeWright.Conversations;

/// <summary>
/// The Genie Conversation API, scoped to one tenant's agent.
/// </summary>
/// <remarks>
/// The agent id is never a parameter. It is resolved from the <see cref="TenantContext"/>, which
/// only membership resolution can produce, so a caller cannot address another tenant's agent by
/// passing its id — the property ADR 0002 gives the query layer, applied to the one Databricks
/// surface whose conversation history is shared by the authenticated service principal.
/// </remarks>
public sealed class GenieConversations : IGenieConversations
{
    private const string DatabricksScope = "2ff814a6-3304-4ab8-85cb-cd0e6f879c1d/.default";

    private static readonly TimeSpan FirstPollDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxPollDelay = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The tenant that conversations in the acknowledged shared space are bound to. Every tenant
    /// there addresses the same space, and a conversation bound to a real tenant stays out of reach.
    /// <see cref="GenieSharedSpaceOptionsValidator"/> refuses a real tenant with this id in shared mode.
    /// </summary>
    internal static readonly TenantId SharedSpaceTenant = new(Guid.Empty);

    private readonly HttpClient _http;
    private readonly TokenCredential _credential;
    private readonly GenieOptions _options;
    private readonly TimeProvider _time;
    private readonly IConversationOwnership _ownership;
    private readonly ILakeWrightFeatureGate _features;

    public GenieConversations(
        HttpClient http,
        TokenCredential credential,
        IOptions<GenieOptions> options,
        TimeProvider time,
        IConversationOwnership? ownership = null,
        ILakeWrightFeatureGate? features = null)
    {
        _http = http;
        _credential = credential;
        _options = options.Value;
        _time = time;
        _ownership = ownership ?? new MemoryConversationOwnership();
        _features = features ?? new AlwaysOnFeatureGate();
    }

    public async Task<GenieAnswer> AskAsync(
        TenantContext tenant,
        string ownerKey,
        string question,
        CancellationToken cancellationToken = default)
    {
        var (space, binding) = await AuthorizeAsync(tenant, ownerKey, null, null, cancellationToken).ConfigureAwait(false);
        using var deadline = new ResponseDeadline(_time, _options.ResponseTimeout, cancellationToken);
        var accepted = await StartConversationCoreAsync(space, binding, ownerKey, question, deadline).ConfigureAwait(false);
        return await PollAsync(space, accepted.ConversationId, accepted.MessageId, deadline, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<GenieAnswer> ContinueAsync(
        TenantContext tenant,
        string ownerKey,
        string conversationId,
        string question,
        CancellationToken cancellationToken = default)
    {
        var (space, _) = await AuthorizeAsync(tenant, ownerKey, conversationId, null, cancellationToken).ConfigureAwait(false);
        using var deadline = new ResponseDeadline(_time, _options.ResponseTimeout, cancellationToken);
        var accepted = await SubmitAsync(space, conversationId, question, deadline).ConfigureAwait(false);
        return await PollAsync(space, accepted.ConversationId, accepted.MessageId, deadline, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<GenieAcceptedMessage> StartConversationAsync(
        TenantContext tenant,
        string ownerKey,
        string question,
        CancellationToken cancellationToken = default)
    {
        var (space, binding) = await AuthorizeAsync(tenant, ownerKey, null, null, cancellationToken).ConfigureAwait(false);
        using var deadline = new ResponseDeadline(_time, _options.ResponseTimeout, cancellationToken);
        return await StartConversationCoreAsync(space, binding, ownerKey, question, deadline).ConfigureAwait(false);
    }

    public async Task<GenieAcceptedMessage> SendMessageAsync(
        TenantContext tenant,
        string ownerKey,
        string conversationId,
        string question,
        CancellationToken cancellationToken = default)
    {
        var (space, _) = await AuthorizeAsync(tenant, ownerKey, conversationId, null, cancellationToken).ConfigureAwait(false);
        using var deadline = new ResponseDeadline(_time, _options.ResponseTimeout, cancellationToken);
        return await SubmitAsync(space, conversationId, question, deadline).ConfigureAwait(false);
    }

    public async Task<GenieMessageSnapshot> GetMessageAsync(
        TenantContext tenant,
        string ownerKey,
        string conversationId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        var (space, _) = await AuthorizeAsync(tenant, ownerKey, conversationId, messageId, cancellationToken).ConfigureAwait(false);
        using var deadline = new ResponseDeadline(_time, _options.ResponseTimeout, cancellationToken);
        return await ReadMessageAsync(space, conversationId, messageId, deadline).ConfigureAwait(false);
    }

    public async Task<GenieAnswer> WaitForResponseAsync(
        TenantContext tenant,
        string ownerKey,
        string conversationId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        var (space, _) = await AuthorizeAsync(tenant, ownerKey, conversationId, messageId, cancellationToken).ConfigureAwait(false);
        using var deadline = new ResponseDeadline(_time, _options.ResponseTimeout, cancellationToken);
        return await PollAsync(space, conversationId, messageId, deadline, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<IReadOnlyList<string>> ListAsync(string ownerKey, CancellationToken cancellationToken = default)
    {
        _features.EnsureEnabled(LakeWrightFeatures.Conversations);
        return _ownership.ListAsync(ownerKey, cancellationToken);
    }

    public async Task DeleteAsync(
        TenantContext tenant,
        string ownerKey,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        var (space, _) = await AuthorizeAsync(tenant, ownerKey, conversationId, null, cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Delete, ConversationPath(space, conversationId));
        await AuthenticateAsync(request, cancellationToken).ConfigureAwait(false);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        await _ownership.RemoveAsync(conversationId, ownerKey, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The checks every operation makes, in the order the isolation suite pins: feature gate,
    /// identifiers, ownership, then the tenant's agent. All of them run before credentials or HTTP.
    /// A null <paramref name="conversationId"/> starts a new conversation, so there is no owner yet.
    /// Returns the agent and the tenant that ownership records are bound to.
    /// </summary>
    private async ValueTask<(string Space, TenantId Binding)> AuthorizeAsync(
        TenantContext tenant,
        string ownerKey,
        string? conversationId,
        string? messageId,
        CancellationToken cancellationToken)
    {
        _features.EnsureEnabled(LakeWrightFeatures.Conversations);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerKey);
        if (conversationId is not null)
        {
            ValidateId(conversationId, nameof(conversationId));
            if (messageId is not null)
            {
                ValidateId(messageId, nameof(messageId));
            }
        }

        // A configuration lookup only: an unmapped tenant is refused after the ownership check.
        var resolved = _options.TryResolveSpace(tenant, out var space, out var shared);
        // The acknowledged shared space has no tenant isolation and every tenant addresses it, so
        // its conversations are bound to one shared tenant rather than to the caller's.
        var binding = shared ? SharedSpaceTenant : tenant.TenantId;
        if (conversationId is not null
            && !await _ownership.IsOwnerAsync(binding, conversationId, ownerKey, cancellationToken).ConfigureAwait(false))
        {
            throw new ConversationOwnershipException(conversationId);
        }

        if (!resolved)
        {
            throw new InvalidOperationException(
                $"Tenant {tenant.TenantId} has no Genie Agent configured. Add it to GenieOptions.Spaces; " +
                "there is deliberately no default, because answering from another tenant's agent is worse " +
                "than not answering.");
        }

        return (space, binding);
    }

    private async Task<GenieAcceptedMessage> StartConversationCoreAsync(
        string space, TenantId binding, string ownerKey, string question, ResponseDeadline deadline)
    {
        var accepted = await SubmitAsync(space, null, question, deadline).ConfigureAwait(false);
        await RecordAcceptedConversationAsync(accepted, binding, ownerKey).ConfigureAwait(false);
        return accepted;
    }

    /// <summary>
    /// Posts one question: a new conversation when <paramref name="conversationId"/> is null,
    /// otherwise a follow-up that must come back under that same conversation.
    /// </summary>
    private async Task<GenieAcceptedMessage> SubmitAsync(
        string space, string? conversationId, string question, ResponseDeadline deadline)
    {
        var path = conversationId is null
            ? $"api/2.0/genie/spaces/{space}/start-conversation"
            : $"{ConversationPath(space, conversationId)}/messages";
        try
        {
            deadline.ThrowIfExpired();
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(new { content = question }),
            };
            await AuthenticateAsync(request, deadline.Token).ConfigureAwait(false);
            deadline.ThrowIfExpired();

            using var response = await _http.SendAsync(request, deadline.Token).ConfigureAwait(false);
            deadline.ThrowIfExpired();
            await ThrowIfFailedAsync(response, deadline.Token).ConfigureAwait(false);

            using var started = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false));
            deadline.ThrowIfExpired();

            var acceptedConversationId = ReadString(started.RootElement, "conversation_id");
            var messageId = ReadString(started.RootElement, "message_id");
            if (!IsValidId(acceptedConversationId) || !IsValidId(messageId))
            {
                throw new InvalidOperationException("Genie returned a missing or invalid message identity.");
            }

            ValidateResponseIdentity(started.RootElement, space, conversationId ?? acceptedConversationId, messageId);
            return new GenieAcceptedMessage(acceptedConversationId, messageId);
        }
        catch (Exception exception) when (deadline.IsTimeout(exception))
        {
            throw new GenieResponseTimeoutException();
        }
    }

    private async Task<GenieMessageSnapshot> ReadMessageAsync(
        string space, string conversationId, string messageId, ResponseDeadline deadline)
    {
        try
        {
            deadline.ThrowIfExpired();
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{ConversationPath(space, conversationId)}/messages/{Uri.EscapeDataString(messageId)}");
            await AuthenticateAsync(request, deadline.Token).ConfigureAwait(false);
            deadline.ThrowIfExpired();

            using var response = await _http.SendAsync(request, deadline.Token).ConfigureAwait(false);
            deadline.ThrowIfExpired();
            await ThrowIfFailedAsync(response, deadline.Token).ConfigureAwait(false);
            using var message = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false));
            deadline.ThrowIfExpired();
            ValidateResponseIdentity(message.RootElement, space, conversationId, messageId);

            var status = ReadString(message.RootElement, "status");
            var outcome = MapStatus(status);
            var (text, sql) = ReadAttachments(message.RootElement);
            return new GenieMessageSnapshot(conversationId, messageId, status,
                outcome != GenieOutcome.Unknown || IsTerminalUnknown(message.RootElement), outcome, text, sql);
        }
        catch (Exception exception) when (deadline.IsTimeout(exception))
        {
            throw new TimeoutException("The Genie message read timed out.");
        }
    }

    private async Task<GenieAnswer> PollAsync(
        string space,
        string conversationId,
        string messageId,
        ResponseDeadline deadline,
        CancellationToken cancellationToken)
    {
        var delay = FirstPollDelay;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deadline.IsExpired)
            {
                return TimedOut(conversationId, messageId);
            }

            try
            {
                var message = await ReadMessageAsync(space, conversationId, messageId, deadline).ConfigureAwait(false);
                if (message.IsTerminal)
                {
                    return new GenieAnswer(conversationId, messageId, message.Outcome, message.Text, message.GeneratedSql);
                }
            }
            catch (TimeoutException) when (deadline.IsExpired)
            {
                return TimedOut(conversationId, messageId);
            }
            catch (TimeoutException)
            {
                // One GET outlived the per-request HttpClient timeout while the response deadline
                // still has time left. A GET is safe to repeat, so poll again after the backoff.
            }

            var remaining = deadline.Remaining;
            if (remaining <= TimeSpan.Zero)
            {
                return TimedOut(conversationId, messageId);
            }

            try
            {
                await Task.Delay(delay <= remaining ? delay : remaining, _time, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsExpired)
            {
                return TimedOut(conversationId, messageId);
            }

            // Backoff to a ceiling: a question answered in two seconds should not wait a minute,
            // and one still running after five should not be asked about every second.
            delay = delay < MaxPollDelay
                ? TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxPollDelay.Ticks))
                : MaxPollDelay;
        }
    }

    private async Task RecordAcceptedConversationAsync(GenieAcceptedMessage accepted, TenantId binding, string ownerKey)
    {
        // The caller token is deliberately excluded: acceptance has happened and cancellation
        // must not make the id orphaned. The independent bounded deadline still prevents a failed
        // ownership backend from holding the request forever.
        using var persistenceDeadline = new ResponseDeadline(_time, _options.ResponseTimeout, CancellationToken.None);
        try
        {
            await _ownership.RecordAsync(binding, accepted.ConversationId, ownerKey, persistenceDeadline.Token).ConfigureAwait(false);
            persistenceDeadline.ThrowIfExpired();
        }
        catch (ConversationOwnershipException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ConversationOwnershipPersistenceException(accepted.ConversationId, accepted.MessageId, exception);
        }
    }

    private static GenieAnswer TimedOut(string conversationId, string messageId) =>
        new(conversationId, messageId, GenieOutcome.TimedOut, null, null);

    private static string ConversationPath(string space, string conversationId) =>
        $"api/2.0/genie/spaces/{space}/conversations/{Uri.EscapeDataString(conversationId)}";

    private static void ValidateId(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!IsValidId(value))
        {
            throw new ArgumentException("An identifier cannot be a relative path segment.", parameterName);
        }
    }

    private static bool IsValidId([NotNullWhen(true)] string? value) =>
        !string.IsNullOrWhiteSpace(value) && value is not ("." or "..");

    private static void ValidateResponseIdentity(JsonElement message, string space, string conversationId, string messageId)
    {
        foreach (var property in message.EnumerateObject())
        {
            var expected = property.Name switch
            {
                "space_id" => space,
                "conversation_id" => conversationId,
                "message_id" or "id" => messageId,
                _ => null,
            };
            if (expected is not null
                && (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() != expected))
            {
                throw new InvalidOperationException("Genie returned a conflicting message identity.");
            }
        }
    }

    private sealed class ResponseDeadline : IDisposable
    {
        private readonly TimeProvider _time;
        private readonly DateTimeOffset _expiresAt;
        private readonly CancellationToken _callerCancellation;
        private readonly CancellationTokenSource _cancellation;
        private readonly ITimer _timer;

        public ResponseDeadline(TimeProvider time, TimeSpan timeout, CancellationToken callerCancellation)
        {
            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout), "Genie response timeout must be positive.");
            }

            _time = time;
            _expiresAt = time.GetUtcNow().Add(timeout);
            _callerCancellation = callerCancellation;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(callerCancellation);
            _timer = time.CreateTimer(static state => ((CancellationTokenSource)state!).Cancel(), _cancellation, timeout, Timeout.InfiniteTimeSpan);
        }

        public CancellationToken Token => _cancellation.Token;

        public bool IsExpired => !_callerCancellation.IsCancellationRequested && _time.GetUtcNow() >= _expiresAt;

        public TimeSpan Remaining => _expiresAt - _time.GetUtcNow();

        public void ThrowIfExpired()
        {
            if (IsExpired)
            {
                throw new TimeoutException();
            }

            Token.ThrowIfCancellationRequested();
        }

        /// <summary>
        /// Whether a failure means time ran out rather than the caller cancelling: this deadline
        /// expired, or <see cref="HttpClient.Timeout"/> stopped one request, which surfaces as a
        /// cancellation wrapping a <see cref="TimeoutException"/>.
        /// </summary>
        public bool IsTimeout(Exception exception) => exception switch
        {
            TimeoutException => true,
            OperationCanceledException { InnerException: TimeoutException } => !_callerCancellation.IsCancellationRequested,
            OperationCanceledException => IsExpired,
            _ => false,
        };

        public void Dispose()
        {
            _timer.Dispose();
            _cancellation.Dispose();
        }
    }

    /// <summary>
    /// Platform states, mapped into the closed set this library reports. Anything unrecognised
    /// stays <see cref="GenieOutcome.Unknown"/> and keeps polling, because an unrecognised state
    /// is far more likely to be a new in-progress state than a new terminal one.
    /// </summary>
    private static GenieOutcome MapStatus(string? status) => status switch
    {
        "COMPLETED" => GenieOutcome.Completed,
        // Only the SQL result rows expired. The answer text and generated SQL, which are all this
        // library returns, remain; without this arm a resumed old message would poll until timeout.
        "QUERY_RESULT_EXPIRED" => GenieOutcome.Completed,
        "FAILED" => GenieOutcome.Failed,
        "CANCELLED" => GenieOutcome.Cancelled,
        _ => GenieOutcome.Unknown,
    };

    /// <summary>An error object means the platform has stopped, whatever it called the state.</summary>
    private static bool IsTerminalUnknown(JsonElement message) =>
        message.TryGetProperty("error", out var error) && error.ValueKind is not JsonValueKind.Null;

    private static (string? Text, string? Sql) ReadAttachments(JsonElement message)
    {
        if (!message.TryGetProperty("attachments", out var attachments)
            || attachments.ValueKind != JsonValueKind.Array)
        {
            return (null, null);
        }

        string? text = null;
        string? sql = null;

        foreach (var attachment in attachments.EnumerateArray())
        {
            if (text is null
                && attachment.TryGetProperty("text", out var textNode)
                && textNode.ValueKind == JsonValueKind.Object)
            {
                text = ReadString(textNode, "content");
            }

            if (sql is null
                && attachment.TryGetProperty("query", out var queryNode)
                && queryNode.ValueKind == JsonValueKind.Object)
            {
                sql = ReadString(queryNode, "query");
            }
        }

        return (text, sql);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private async Task AuthenticateAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _credential
            .GetTokenAsync(new TokenRequestContext([DatabricksScope]), cancellationToken)
            .ConfigureAwait(false);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }

    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Databricks answered {(int)response.StatusCode} {response.ReasonPhrase}: {body}"),
            inner: null,
            statusCode: response.StatusCode);
    }
}
