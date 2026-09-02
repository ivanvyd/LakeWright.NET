using System.Security.Cryptography;
using System.Text;
using LakeWright.Conversations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace LakeWright.Caching.Redis;

/// <summary>Configures Redis keys used for shared Genie conversation ownership.</summary>
public sealed class RedisConversationOwnershipOptions
{
    /// <summary>Namespace isolated to one application deployment.</summary>
    public string Prefix { get; set; } = "lakewright:conversation-ownership";

    /// <summary>Redis logical database used by this ownership store.</summary>
    public int Database { get; set; } = -1;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Prefix);
        if (Database < -1)
        {
            throw new ArgumentOutOfRangeException(nameof(Database));
        }
    }
}

/// <summary>Redis-backed ownership store that is safe to share across application replicas.</summary>
/// <remarks>
/// Ownership is claimed with Redis <c>SET NX</c>, so a second owner can never overwrite an
/// existing claim. Redis keys contain SHA-256 hashes; only the owner-specific Redis set stores a
/// conversation id, because <see cref="IConversationOwnership.ListAsync"/> must return it.
/// </remarks>
public sealed class RedisConversationOwnership : IConversationOwnership
{
    private readonly IRedisConversationOwnershipStore _store;
    private readonly RedisConversationOwnershipOptions _options;

    public RedisConversationOwnership(
        IConnectionMultiplexer connection,
        RedisConversationOwnershipOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _options = options ?? new RedisConversationOwnershipOptions();
        _options.Validate();
        _store = new RedisConversationOwnershipStore(connection.GetDatabase(_options.Database));
    }

    internal RedisConversationOwnership(IRedisConversationOwnershipStore store, RedisConversationOwnershipOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? new RedisConversationOwnershipOptions();
        _options.Validate();
    }

    public async ValueTask RecordAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default)
    {
        Validate(conversationId, ownerKey);
        cancellationToken.ThrowIfCancellationRequested();
        var ownerHash = Hash(ownerKey);
        var ownershipKey = OwnershipKey(conversationId);
        var claimed = await _store.ClaimAsync(ownershipKey, ownerHash).ConfigureAwait(false);
        if (!claimed)
        {
            var existing = await _store.GetOwnerAsync(ownershipKey).ConfigureAwait(false);
            if (!OwnerMatches(existing, ownerHash))
            {
                throw new ConversationOwnershipException(conversationId);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _store.AddToOwnerAsync(OwnerSetKey(ownerKey), conversationId).ConfigureAwait(false);
    }

    public async ValueTask<bool> IsOwnerAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default)
    {
        Validate(conversationId, ownerKey);
        cancellationToken.ThrowIfCancellationRequested();
        return OwnerMatches(await _store.GetOwnerAsync(OwnershipKey(conversationId)).ConfigureAwait(false), Hash(ownerKey));
    }

    public async ValueTask<IReadOnlyList<string>> ListAsync(string ownerKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerKey);
        cancellationToken.ThrowIfCancellationRequested();
        return (await _store.ListAsync(OwnerSetKey(ownerKey)).ConfigureAwait(false))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    public async ValueTask RemoveAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default)
    {
        Validate(conversationId, ownerKey);
        cancellationToken.ThrowIfCancellationRequested();
        var ownerHash = Hash(ownerKey);
        var ownershipKey = OwnershipKey(conversationId);
        if (!OwnerMatches(await _store.GetOwnerAsync(ownershipKey).ConfigureAwait(false), ownerHash))
        {
            return;
        }

        // SET NX makes an ownership claim immutable. Once the matching value above is observed,
        // no foreign writer can replace it between these two cleanup operations.
        await _store.DeleteClaimAsync(ownershipKey).ConfigureAwait(false);
        await _store.RemoveFromOwnerAsync(OwnerSetKey(ownerKey), conversationId).ConfigureAwait(false);
    }

    private string OwnershipKey(string conversationId) => $"{_options.Prefix}:claim:{Hash(conversationId)}";

    private string OwnerSetKey(string ownerKey) => $"{_options.Prefix}:owner:{Hash(ownerKey)}";

    private static bool OwnerMatches(string? stored, string expected) =>
        stored is not null && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(stored),
            Encoding.UTF8.GetBytes(expected));

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void Validate(string conversationId, string ownerKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerKey);
    }
}

internal interface IRedisConversationOwnershipStore
{
    Task<bool> ClaimAsync(string ownershipKey, string ownerHash);
    Task<string?> GetOwnerAsync(string ownershipKey);
    Task AddToOwnerAsync(string ownerSetKey, string conversationId);
    Task<IReadOnlyList<string>> ListAsync(string ownerSetKey);
    Task DeleteClaimAsync(string ownershipKey);
    Task RemoveFromOwnerAsync(string ownerSetKey, string conversationId);
}

internal sealed class RedisConversationOwnershipStore(IDatabase database) : IRedisConversationOwnershipStore
{
    public Task<bool> ClaimAsync(string ownershipKey, string ownerHash) =>
        database.StringSetAsync(ownershipKey, ownerHash, when: When.NotExists);

    public async Task<string?> GetOwnerAsync(string ownershipKey)
    {
        var owner = await database.StringGetAsync(ownershipKey).ConfigureAwait(false);
        return owner.HasValue ? owner.ToString() : null;
    }

    public async Task AddToOwnerAsync(string ownerSetKey, string conversationId) =>
        _ = await database.SetAddAsync(ownerSetKey, conversationId).ConfigureAwait(false);

    public async Task<IReadOnlyList<string>> ListAsync(string ownerSetKey) =>
        (await database.SetMembersAsync(ownerSetKey).ConfigureAwait(false)).Select(value => value.ToString()).ToArray();

    public async Task DeleteClaimAsync(string ownershipKey) =>
        _ = await database.KeyDeleteAsync(ownershipKey).ConfigureAwait(false);

    public async Task RemoveFromOwnerAsync(string ownerSetKey, string conversationId) =>
        _ = await database.SetRemoveAsync(ownerSetKey, conversationId).ConfigureAwait(false);
}

/// <summary>Registers Redis-backed ownership after the host has registered an <see cref="IConnectionMultiplexer"/>.</summary>
public static class RedisConversationOwnershipServiceCollectionExtensions
{
    public static IServiceCollection AddLakeWrightRedisConversationOwnership(
        this IServiceCollection services,
        Action<RedisConversationOwnershipOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new RedisConversationOwnershipOptions();
        configure?.Invoke(options);
        options.Validate();
        services.RemoveAll<IConversationOwnership>();
        services.AddSingleton<IConversationOwnership>(provider => new RedisConversationOwnership(
            provider.GetRequiredService<IConnectionMultiplexer>(), options));
        return services;
    }
}
