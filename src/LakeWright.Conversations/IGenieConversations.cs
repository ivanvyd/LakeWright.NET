using LakeWright.Core.Tenancy;

namespace LakeWright.Conversations;

/// <summary>
/// Asks a tenant's Genie Agent a question, and continues the conversation.
/// </summary>
public interface IGenieConversations
{
    /// <summary>Submits one question and returns its accepted identifiers without polling.</summary>
    /// <remarks>
    /// Ownership is recorded before returning. Persist these identifiers in the application before
    /// waiting. Cancellation after known acceptance does not undo the ownership write or cancel
    /// remote work. A failed submission may have been accepted remotely; do not retry it blindly.
    /// The default implementation throws <see cref="NotSupportedException"/> for older adapters.
    /// </remarks>
    Task<GenieAcceptedMessage> StartConversationAsync(
        TenantContext tenant,
        string ownerKey,
        string question,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This adapter does not support separate Genie message operations.");

    /// <summary>Submits one follow-up to an owned conversation without polling.</summary>
    /// <remarks>No automatic submission retry is performed. Older adapters throw <see cref="NotSupportedException"/>.</remarks>
    Task<GenieAcceptedMessage> SendMessageAsync(
        TenantContext tenant,
        string ownerKey,
        string conversationId,
        string question,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This adapter does not support separate Genie message operations.");

    /// <summary>Reads an owned message once with GET, without submitting a question.</summary>
    /// <remarks>Older adapters throw <see cref="NotSupportedException"/>.</remarks>
    Task<GenieMessageSnapshot> GetMessageAsync(
        TenantContext tenant,
        string ownerKey,
        string conversationId,
        string messageId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This adapter does not support separate Genie message operations.");

    /// <summary>Waits for an owned message using GET requests, without submitting a question.</summary>
    /// <remarks>
    /// Each call has its own <see cref="GenieOptions.ResponseTimeout"/>. Cancellation stops local
    /// waiting only. IDs do not grant access: ownership and the tenant's agent mapping are checked.
    /// Older adapters throw <see cref="NotSupportedException"/>.
    /// </remarks>
    Task<GenieAnswer> WaitForResponseAsync(
        TenantContext tenant,
        string ownerKey,
        string conversationId,
        string messageId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This adapter does not support separate Genie message operations.");

    /// <summary>Starts a conversation in the agent <paramref name="tenant"/> is mapped to.</summary>
    /// <remarks>
    /// Once the workspace accepts a conversation, its ownership is persisted for
    /// <paramref name="ownerKey"/> before polling. Use an opaque, stable application-principal key.
    /// </remarks>
    Task<GenieAnswer> AskAsync(
        TenantContext tenant,
        string ownerKey,
        string question,
        CancellationToken cancellationToken = default);

    /// <summary>Asks a follow-up in an existing conversation.</summary>
    /// <remarks>
    /// The conversation is addressed inside the tenant's own agent. A conversation belonging to
    /// another tenant is not reachable through this call, because the agent in the path comes from
    /// <paramref name="tenant"/> rather than from the caller. Conversations absent from the
    /// ownership store fail closed before a workspace call.
    /// </remarks>
    Task<GenieAnswer> ContinueAsync(
        TenantContext tenant,
        string ownerKey,
        string conversationId,
        string question,
        CancellationToken cancellationToken = default);

    /// <summary>Lists only conversation identifiers recorded for <paramref name="ownerKey"/>.</summary>
    ValueTask<IReadOnlyList<string>> ListAsync(string ownerKey, CancellationToken cancellationToken = default);

    /// <summary>Deletes an owned conversation and removes its local ownership record.</summary>
    /// <remarks>The local record is retained when the workspace delete fails.</remarks>
    Task DeleteAsync(
        TenantContext tenant,
        string ownerKey,
        string conversationId,
        CancellationToken cancellationToken = default);
}

/// <summary>An accepted message. Identifiers support checkpointing; they are not authorization.</summary>
public sealed record GenieAcceptedMessage(string ConversationId, string MessageId);

/// <summary>A single observation, including the platform status and whether waiting has ended.</summary>
/// <remarks>
/// An unfamiliar platform status maps to <see cref="GenieOutcome.Unknown"/>. An accompanying
/// error makes it terminal; use <see cref="IsTerminal"/> rather than treating Unknown as pending.
/// </remarks>
public sealed record GenieMessageSnapshot(
    string ConversationId,
    string MessageId,
    string? Status,
    bool IsTerminal,
    GenieOutcome Outcome,
    string? Text,
    string? GeneratedSql);

/// <summary>What a Genie Agent answered, and how to continue from it.</summary>
public sealed record GenieAnswer(
    string ConversationId,
    string MessageId,
    GenieOutcome Outcome,
    string? Text,
    string? GeneratedSql);

/// <summary>
/// The closed set of outcomes this library reports, mapped from the platform's own states.
/// </summary>
/// <remarks>
/// Databricks documents its message states as extensible, so they are mapped at the boundary
/// rather than switched over exhaustively — the same rule the operation worker follows for run
/// states. <see cref="Unknown"/> exists so a state added upstream surfaces as an unknown outcome
/// instead of crashing a request.
/// </remarks>
public enum GenieOutcome
{
    Unknown = 0,
    Completed = 1,
    Failed = 2,
    Cancelled = 3,
    /// <summary>Still running when the caller's patience, or <c>ResponseTimeout</c>, ran out.</summary>
    TimedOut = 4,
}

/// <summary>The local response deadline elapsed before a conversation identity was received.</summary>
/// <remarks>Remote acceptance is unknown: Databricks may still have created a conversation.</remarks>
public sealed class GenieResponseTimeoutException() : TimeoutException("The Genie response deadline elapsed before a conversation identity was received; remote acceptance is unknown.");

/// <summary>An accepted conversation could not be safely recorded for its owner.</summary>
/// <remarks>The identifiers support trusted recovery; they do not authorize continuation by themselves.</remarks>
public sealed class ConversationOwnershipPersistenceException(string conversationId, string messageId, Exception innerException)
    : InvalidOperationException("Genie accepted a conversation but ownership could not be persisted.", innerException)
{
    public string ConversationId { get; } = conversationId;
    public string MessageId { get; } = messageId;
}
