using System.Collections.Concurrent;

namespace LakeWright.Conversations;

/// <summary>Records which application-level owner may access a Genie conversation.</summary>
/// <remarks>
/// <para>
/// Pass an opaque, stable application-principal key. Do not use an email address, display name, or
/// another value that could be exposed in diagnostics. The key is never sent to Databricks.
/// </para>
/// <para>
/// The built-in registration is in-memory and therefore intentionally forgets ownership after a
/// process restart. Multi-replica applications should replace this interface with durable shared
/// storage before enabling conversation continuation.
/// </para>
/// </remarks>
public interface IConversationOwnership
{
    ValueTask RecordAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default);

    ValueTask<bool> IsOwnerAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<string>> ListAsync(string ownerKey, CancellationToken cancellationToken = default);

    ValueTask RemoveAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default);
}

internal sealed class MemoryConversationOwnership : IConversationOwnership
{
    private readonly ConcurrentDictionary<string, string> _owners = new(StringComparer.Ordinal);

    public ValueTask RecordAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default)
    {
        Validate(conversationId, ownerKey);
        if (_owners.TryAdd(conversationId, ownerKey))
        {
            return ValueTask.CompletedTask;
        }

        if (!_owners.TryGetValue(conversationId, out var owner))
        {
            throw new InvalidOperationException("Conversation ownership could not be recorded.");
        }
        if (!string.Equals(owner, ownerKey, StringComparison.Ordinal))
        {
            throw new ConversationOwnershipException(conversationId);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> IsOwnerAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default)
    {
        Validate(conversationId, ownerKey);
        return ValueTask.FromResult(_owners.TryGetValue(conversationId, out var owner)
            && string.Equals(owner, ownerKey, StringComparison.Ordinal));
    }

    public ValueTask<IReadOnlyList<string>> ListAsync(string ownerKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerKey);
        return ValueTask.FromResult<IReadOnlyList<string>>(
            [.. _owners.Where(entry => string.Equals(entry.Value, ownerKey, StringComparison.Ordinal))
                .Select(entry => entry.Key)
                .OrderBy(id => id, StringComparer.Ordinal)]);
    }

    public ValueTask RemoveAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default)
    {
        Validate(conversationId, ownerKey);
        if (_owners.TryGetValue(conversationId, out var owner)
            && string.Equals(owner, ownerKey, StringComparison.Ordinal))
        {
            _owners.TryRemove(conversationId, out _);
        }

        return ValueTask.CompletedTask;
    }

    private static void Validate(string conversationId, string ownerKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerKey);
    }
}

/// <summary>A conversation is not owned by the current application principal.</summary>
public sealed class ConversationOwnershipException(string conversationId)
    : InvalidOperationException($"Conversation '{conversationId}' is not owned by this caller.");
