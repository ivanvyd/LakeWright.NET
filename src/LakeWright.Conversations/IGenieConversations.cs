using LakeWright.Core.Tenancy;

namespace LakeWright.Conversations;

/// <summary>
/// Asks a tenant's Genie Agent a question, and continues the conversation.
/// </summary>
public interface IGenieConversations
{
    /// <summary>Starts a conversation in the agent <paramref name="tenant"/> is mapped to.</summary>
    /// <remarks>
    /// The returned conversation is recorded for <paramref name="ownerKey"/> only after the
    /// workspace has accepted it. Use an opaque, stable application-principal key.
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
