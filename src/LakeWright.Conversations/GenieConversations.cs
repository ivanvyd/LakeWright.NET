using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
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
/// surface that ships no tenancy of its own.
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

    public GenieConversations(
        HttpClient http,
        TokenCredential credential,
        IOptions<GenieOptions> options,
        TimeProvider time)
    {
        _http = http;
        _credential = credential;
        _options = options.Value;
        _time = time;
    }

    public Task<GenieAnswer> AskAsync(
        TenantContext tenant,
        string question,
        CancellationToken cancellationToken = default)
    {
        var space = ResolveSpace(tenant);
        return SendAsync(
            space,
            $"api/2.0/genie/spaces/{space}/start-conversation",
            new { content = question },
            cancellationToken);
    }

    public Task<GenieAnswer> ContinueAsync(
        TenantContext tenant,
        string conversationId,
        string question,
        CancellationToken cancellationToken = default)
    {
        var space = ResolveSpace(tenant);
        return SendAsync(
            space,
            $"api/2.0/genie/spaces/{space}/conversations/{Uri.EscapeDataString(conversationId)}/messages",
            new { content = question },
            cancellationToken);
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

    private async Task<GenieAnswer> SendAsync(
        string space,
        string path,
        object body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        await AuthenticateAsync(request, cancellationToken).ConfigureAwait(false);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        using var started = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

        var conversationId = ReadString(started.RootElement, "conversation_id")
            ?? throw new InvalidOperationException("Genie returned no conversation_id.");
        var messageId = ReadString(started.RootElement, "message_id")
            ?? throw new InvalidOperationException("Genie returned no message_id.");

        return await PollAsync(space, conversationId, messageId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GenieAnswer> PollAsync(
        string space,
        string conversationId,
        string messageId,
        CancellationToken cancellationToken)
    {
        var deadline = _time.GetUtcNow().Add(_options.ResponseTimeout);
        var delay = FirstPollDelay;

        while (true)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"api/2.0/genie/spaces/{space}/conversations/{Uri.EscapeDataString(conversationId)}" +
                $"/messages/{Uri.EscapeDataString(messageId)}");
            await AuthenticateAsync(request, cancellationToken).ConfigureAwait(false);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

            using var message = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            var outcome = MapStatus(ReadString(message.RootElement, "status"));
            if (outcome != GenieOutcome.Unknown || IsTerminalUnknown(message.RootElement))
            {
                var (text, sql) = ReadAttachments(message.RootElement);
                return new GenieAnswer(conversationId, messageId, outcome, text, sql);
            }

            if (_time.GetUtcNow() >= deadline)
            {
                var (text, sql) = ReadAttachments(message.RootElement);
                return new GenieAnswer(conversationId, messageId, GenieOutcome.TimedOut, text, sql);
            }

            await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);

            // Backoff to a ceiling: a question answered in two seconds should not wait a minute,
            // and one still running after five should not be asked about every second.
            delay = delay < MaxPollDelay
                ? TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxPollDelay.Ticks))
                : MaxPollDelay;
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
