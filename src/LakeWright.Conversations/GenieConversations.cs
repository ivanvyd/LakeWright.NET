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
        _features.EnsureEnabled(LakeWrightFeatures.Conversations);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerKey);
        var space = ResolveSpace(tenant);
        using var deadline = new ResponseDeadline(_time, _options.ResponseTimeout, cancellationToken);
        var accepted = await StartConversationCoreAsync(space, ownerKey, question, deadline).ConfigureAwait(false);
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
        _features.EnsureEnabled(LakeWrightFeatures.Conversations);
        ValidateId(conversationId, nameof(conversationId));
        await EnsureOwnerAsync(conversationId, ownerKey, cancellationToken).ConfigureAwait(false);
        var space = ResolveSpace(tenant);
        using var deadline = new ResponseDeadline(_time, _options.ResponseTimeout, cancellationToken);
        var accepted = await SubmitAsync(
            space,
            $"api/2.0/genie/spaces/{space}/conversations/{Uri.EscapeDataString(conversationId)}/messages",
            new { content = question },
            deadline,
            conversationId).ConfigureAwait(false);
        return await PollAsync(space, accepted.ConversationId, accepted.MessageId, deadline, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<GenieAcceptedMessage> StartConversationAsync(
        TenantContext tenant,
        string ownerKey,
        string question,
        CancellationToken cancellationToken = default)
    {
        _features.EnsureEnabled(LakeWrightFeatures.Conversations);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerKey);
        var space = ResolveSpace(tenant);
        using var deadline = new ResponseDeadline(_time, _options.ResponseTimeout, cancellationToken);
        return await StartConversationCoreAsync(space, ownerKey, question, deadline).ConfigureAwait(false);
    }

    public async Task<GenieAcceptedMessage> SendMessageAsync(
        TenantContext tenant,
        string ownerKey,
        string conversationId,
        string question,
        CancellationToken cancellationToken = default)
    {
        _features.EnsureEnabled(LakeWrightFeatures.Conversations);
        ValidateId(conversationId, nameof(conversationId));
        await EnsureOwnerAsync(conversationId, ownerKey, cancellationToken).ConfigureAwait(false);
        var space = ResolveSpace(tenant);
        using var deadline = new ResponseDeadline(_time, _options.ResponseTimeout, cancellationToken);
        return await SubmitAsync(
            space,
            $"api/2.0/genie/spaces/{space}/conversations/{Uri.EscapeDataString(conversationId)}/messages",
            new { content = question },
            deadline,
            conversationId).ConfigureAwait(false);
    }

    public async Task<GenieMessageSnapshot> GetMessageAsync(
        TenantContext tenant,
        string ownerKey,
        string conversationId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        _features.EnsureEnabled(LakeWrightFeatures.Conversations);
        ValidateId(conversationId, nameof(conversationId));
        ValidateId(messageId, nameof(messageId));
        await EnsureOwnerAsync(conversationId, ownerKey, cancellationToken).ConfigureAwait(false);
        var space = ResolveSpace(tenant);
        using var deadline = new ResponseDeadline(_time, _options.ResponseTimeout, cancellationToken);
        try
        {
            return await ReadMessageAsync(space, conversationId, messageId, deadline).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsExpired)
        {
            throw new TimeoutException("The Genie message read deadline elapsed.");
        }
        catch (GenieResponseTimeoutException)
        {
            throw new TimeoutException("The Genie message read deadline elapsed.");
        }
    }

    public async Task<GenieAnswer> WaitForResponseAsync(
        TenantContext tenant,
        string ownerKey,
        string conversationId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        _features.EnsureEnabled(LakeWrightFeatures.Conversations);
        ValidateId(conversationId, nameof(conversationId));
        ValidateId(messageId, nameof(messageId));
        await EnsureOwnerAsync(conversationId, ownerKey, cancellationToken).ConfigureAwait(false);
        var space = ResolveSpace(tenant);
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
        _features.EnsureEnabled(LakeWrightFeatures.Conversations);
        await EnsureOwnerAsync(conversationId, ownerKey, cancellationToken).ConfigureAwait(false);
        var space = ResolveSpace(tenant);
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"api/2.0/genie/spaces/{space}/conversations/{Uri.EscapeDataString(conversationId)}");
        await AuthenticateAsync(request, cancellationToken).ConfigureAwait(false);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        await _ownership.RemoveAsync(conversationId, ownerKey, cancellationToken).ConfigureAwait(false);
    }

    private string ResolveSpace(TenantContext tenant)
    {
        if (!_options.TryResolveSpace(tenant, out var space))
        {
            throw new InvalidOperationException(
                $"Tenant {tenant.TenantId} has no Genie Agent configured. Add it to GenieOptions.Spaces; " +
                "there is deliberately no default, because answering from another tenant's agent is worse " +
                "than not answering.");
        }

        return space;
    }

    private async ValueTask EnsureOwnerAsync(string conversationId, string ownerKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerKey);
        if (!await _ownership.IsOwnerAsync(conversationId, ownerKey, cancellationToken).ConfigureAwait(false))
        {
            throw new ConversationOwnershipException(conversationId);
        }
    }

    private async Task<GenieAcceptedMessage> StartConversationCoreAsync(
        string space, string ownerKey, string question, ResponseDeadline deadline)
    {
        var accepted = await SubmitAsync(
            space,
            $"api/2.0/genie/spaces/{space}/start-conversation",
            new { content = question },
            deadline).ConfigureAwait(false);
        // Once accepted, caller cancellation must not orphan the ownership record.
        await RecordAcceptedConversationAsync(accepted, ownerKey).ConfigureAwait(false);
        return accepted;
    }

    private async Task<GenieAcceptedMessage> SubmitAsync(
        string space,
        string path,
        object body,
        ResponseDeadline deadline,
        string? expectedConversationId = null)
    {
        try
        {
            deadline.ThrowIfExpired();
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(body),
            };
            await AuthenticateAsync(request, deadline.Token).ConfigureAwait(false);
            deadline.ThrowIfExpired();

            using var response = await _http.SendAsync(request, deadline.Token).ConfigureAwait(false);
            deadline.ThrowIfExpired();
            await ThrowIfFailedAsync(response, deadline.Token).ConfigureAwait(false);

            using var started = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false));
            deadline.ThrowIfExpired();

            var conversationId = ReadString(started.RootElement, "conversation_id")
                ?? throw new InvalidOperationException("Genie returned no conversation_id.");
            var messageId = ReadString(started.RootElement, "message_id")
                ?? throw new InvalidOperationException("Genie returned no message_id.");
            ValidateId(conversationId, nameof(conversationId));
            ValidateId(messageId, nameof(messageId));
            ValidateResponseIdentity(started.RootElement, space, expectedConversationId ?? conversationId, messageId);
            return new GenieAcceptedMessage(conversationId, messageId);
        }
        catch (OperationCanceledException) when (deadline.IsExpired)
        {
            throw new GenieResponseTimeoutException();
        }
    }

    private async Task<GenieMessageSnapshot> ReadMessageAsync(
        string space, string conversationId, string messageId, ResponseDeadline deadline)
    {
        deadline.ThrowIfExpired();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"api/2.0/genie/spaces/{space}/conversations/{Uri.EscapeDataString(conversationId)}" +
            $"/messages/{Uri.EscapeDataString(messageId)}");
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
                cancellationToken.ThrowIfCancellationRequested();
                if (deadline.IsExpired)
                {
                    return TimedOut(conversationId, messageId);
                }

                if (message.IsTerminal)
                {
                    return new GenieAnswer(conversationId, messageId, message.Outcome, message.Text, message.GeneratedSql);
                }
            }
            catch (OperationCanceledException) when (deadline.IsExpired)
            {
                return TimedOut(conversationId, messageId);
            }
            catch (GenieResponseTimeoutException)
            {
                return TimedOut(conversationId, messageId);
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

    private async Task RecordAcceptedConversationAsync(GenieAcceptedMessage accepted, string ownerKey)
    {
        // The caller token is deliberately excluded: acceptance has happened and cancellation
        // must not make the id orphaned. The independent bounded deadline still prevents a failed
        // ownership backend from holding the request forever.
        using var persistenceDeadline = new ResponseDeadline(_time, _options.ResponseTimeout, CancellationToken.None);
        try
        {
            await _ownership.RecordAsync(accepted.ConversationId, ownerKey, persistenceDeadline.Token).ConfigureAwait(false);
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

    private static void ValidateId(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value is "." or "..")
        {
            throw new ArgumentException("An identifier cannot be a relative path segment.", parameterName);
        }
    }

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
                throw new GenieResponseTimeoutException();
            }

            Token.ThrowIfCancellationRequested();
        }

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
