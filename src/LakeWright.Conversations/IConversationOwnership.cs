using System.Collections.Concurrent;
using LakeWright.Core.Tenancy;

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
/// <para>
/// <see cref="GenieConversations"/> calls the tenant-aware members, which bind a conversation to
/// the tenant it was started in. A custom store must override both of them to get that binding.
/// A store that does not override them keeps the 3.0 behaviour: the default implementations
/// ignore the tenant, so the owner key alone grants access. The tenant-unaware members remain for
/// compatibility; the next major version makes the tenant-aware members the contract.
/// </para>
/// <para>
/// Conversations in the acknowledged shared space (<see cref="GenieOptions.SharedSpaceId"/>) arrive
/// bound to <c>TenantId(Guid.Empty)</c>. Treat it as an ordinary tenant. Never treat it as "no
/// tenant" or as a wildcard: that would let a tenant with its own agent reach shared-space
/// conversations.
/// </para>
/// </remarks>
public interface IConversationOwnership
{
    ValueTask RecordAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default);

    ValueTask<bool> IsOwnerAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<string>> ListAsync(string ownerKey, CancellationToken cancellationToken = default);

    ValueTask RemoveAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default);

    /// <summary>Records <paramref name="ownerKey"/> as the owner of a conversation started in <paramref name="tenantId"/>.</summary>
    /// <remarks>
    /// Throws <see cref="ConversationOwnershipException"/> when the conversation is already recorded
    /// for another owner or another tenant. A shared-space conversation arrives with
    /// <c>TenantId(Guid.Empty)</c>; store it as an ordinary tenant. The default implementation
    /// ignores the tenant.
    /// </remarks>
    ValueTask RecordAsync(
        TenantId tenantId, string conversationId, string ownerKey, CancellationToken cancellationToken = default) =>
        RecordAsync(conversationId, ownerKey, cancellationToken);

    /// <summary>Whether <paramref name="ownerKey"/> owns the conversation within <paramref name="tenantId"/>.</summary>
    /// <remarks>
    /// A record written without a tenant matches any tenant. <c>TenantId(Guid.Empty)</c> identifies
    /// the shared space and matches only itself, never as a wildcard. The default implementation
    /// ignores the tenant.
    /// </remarks>
    ValueTask<bool> IsOwnerAsync(
        TenantId tenantId, string conversationId, string ownerKey, CancellationToken cancellationToken = default) =>
        IsOwnerAsync(conversationId, ownerKey, cancellationToken);
}

internal sealed class MemoryConversationOwnership : IConversationOwnership
{
    private readonly ConcurrentDictionary<string, Claim> _owners = new(StringComparer.Ordinal);

    public ValueTask RecordAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default) =>
        Record(conversationId, new Claim(ownerKey, null));

    public ValueTask RecordAsync(
        TenantId tenantId, string conversationId, string ownerKey, CancellationToken cancellationToken = default) =>
        Record(conversationId, new Claim(ownerKey, tenantId));

    public ValueTask<bool> IsOwnerAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default) =>
        IsOwner(conversationId, ownerKey, null);

    public ValueTask<bool> IsOwnerAsync(
        TenantId tenantId, string conversationId, string ownerKey, CancellationToken cancellationToken = default) =>
        IsOwner(conversationId, ownerKey, tenantId);

    public ValueTask<IReadOnlyList<string>> ListAsync(string ownerKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerKey);
        return ValueTask.FromResult<IReadOnlyList<string>>(
            [.. _owners.Where(entry => string.Equals(entry.Value.Owner, ownerKey, StringComparison.Ordinal))
                .Select(entry => entry.Key)
                .OrderBy(id => id, StringComparer.Ordinal)]);
    }

    public ValueTask RemoveAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default)
    {
        Validate(conversationId, ownerKey);
        if (_owners.TryGetValue(conversationId, out var claim) && claim.Admits(ownerKey, null))
        {
            _owners.TryRemove(conversationId, out _);
        }

        return ValueTask.CompletedTask;
    }

    private ValueTask Record(string conversationId, Claim claim)
    {
        Validate(conversationId, claim.Owner);
        if (_owners.TryAdd(conversationId, claim))
        {
            return ValueTask.CompletedTask;
        }

        if (!_owners.TryGetValue(conversationId, out var existing))
        {
            throw new InvalidOperationException("Conversation ownership could not be recorded.");
        }
        if (!existing.Admits(claim.Owner, claim.Tenant))
        {
            throw new ConversationOwnershipException(conversationId);
        }

        return ValueTask.CompletedTask;
    }

    private ValueTask<bool> IsOwner(string conversationId, string ownerKey, TenantId? tenantId)
    {
        Validate(conversationId, ownerKey);
        return ValueTask.FromResult(_owners.TryGetValue(conversationId, out var claim) && claim.Admits(ownerKey, tenantId));
    }

    private static void Validate(string conversationId, string ownerKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerKey);
    }

    /// <summary>An owner, and the tenant the conversation was started in; no tenant for a 3.0 record.</summary>
    private readonly record struct Claim(string Owner, TenantId? Tenant)
    {
        /// <summary>A null <paramref name="tenant"/> checks the owner only.</summary>
        public bool Admits(string owner, TenantId? tenant) =>
            string.Equals(Owner, owner, StringComparison.Ordinal)
            && (tenant is null || Tenant is null || Tenant == tenant);
    }
}

/// <summary>A conversation is not owned by the current application principal.</summary>
public sealed class ConversationOwnershipException(string conversationId)
    : InvalidOperationException($"Conversation '{conversationId}' is not owned by this caller.");
