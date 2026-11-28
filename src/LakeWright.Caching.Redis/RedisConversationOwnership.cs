using System.Security.Cryptography;
using System.Text;
using LakeWright.Conversations;
using LakeWright.Core.Tenancy;
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
/// A claim recorded with a tenant binds the conversation to that tenant; a claim written by 3.0
/// carries no tenant and keeps working.
/// </remarks>
public sealed class RedisConversationOwnership : IConversationOwnership
{
    private const char TenantSeparator = ':';

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

    public ValueTask RecordAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default) =>
        RecordCoreAsync(conversationId, ownerKey, null, cancellationToken);

    public ValueTask RecordAsync(
        TenantId tenantId, string conversationId, string ownerKey, CancellationToken cancellationToken = default) =>
        RecordCoreAsync(conversationId, ownerKey, Hash(tenantId.ToString()), cancellationToken);

    public ValueTask<bool> IsOwnerAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default) =>
        IsOwnerCoreAsync(conversationId, ownerKey, null, cancellationToken);

    public ValueTask<bool> IsOwnerAsync(
        TenantId tenantId, string conversationId, string ownerKey, CancellationToken cancellationToken = default) =>
        IsOwnerCoreAsync(conversationId, ownerKey, Hash(tenantId.ToString()), cancellationToken);

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
        if (!Admits(await _store.GetOwnerAsync(ownershipKey).ConfigureAwait(false), ownerHash, null))
        {
            return;
        }

        // SET NX makes an ownership claim immutable. Once the matching value above is observed,
        // no foreign writer can replace it between these two cleanup operations.
        await _store.DeleteClaimAsync(ownershipKey).ConfigureAwait(false);
        await _store.RemoveFromOwnerAsync(OwnerSetKey(ownerKey), conversationId).ConfigureAwait(false);
    }

    /// <summary>
    /// Claims with <c>SET NX</c>; an existing claim must admit the same owner and tenant.
    /// A null <paramref name="tenantHash"/> writes a 3.0 claim and checks the owner only.
    /// </summary>
    private async ValueTask RecordCoreAsync(
        string conversationId, string ownerKey, string? tenantHash, CancellationToken cancellationToken)
    {
        Validate(conversationId, ownerKey);
        cancellationToken.ThrowIfCancellationRequested();
        var ownerHash = Hash(ownerKey);
        var ownershipKey = OwnershipKey(conversationId);
        var claim = tenantHash is null ? ownerHash : $"{ownerHash}{TenantSeparator}{tenantHash}";
        var claimed = await _store.ClaimAsync(ownershipKey, claim).ConfigureAwait(false);
        if (!claimed)
        {
            var existing = await _store.GetOwnerAsync(ownershipKey).ConfigureAwait(false);
            if (!Admits(existing, ownerHash, tenantHash))
            {
                throw new ConversationOwnershipException(conversationId);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _store.AddToOwnerAsync(OwnerSetKey(ownerKey), conversationId).ConfigureAwait(false);
    }

    private async ValueTask<bool> IsOwnerCoreAsync(
        string conversationId, string ownerKey, string? tenantHash, CancellationToken cancellationToken)
    {
        Validate(conversationId, ownerKey);
        cancellationToken.ThrowIfCancellationRequested();
        return Admits(await _store.GetOwnerAsync(OwnershipKey(conversationId)).ConfigureAwait(false), Hash(ownerKey), tenantHash);
    }

    private string OwnershipKey(string conversationId) => $"{_options.Prefix}:claim:{Hash(conversationId)}";

    private string OwnerSetKey(string ownerKey) => $"{_options.Prefix}:owner:{Hash(ownerKey)}";

    /// <summary>
    /// A claim is <c>ownerHash</c> (3.0, no tenant) or <c>ownerHash:tenantHash</c>. The owner part
    /// must match. A null <paramref name="tenantHash"/> checks the owner only; otherwise a claim
    /// without a tenant matches any tenant, and a claim with one must match it.
    /// </summary>
    private static bool Admits(string? stored, string ownerHash, string? tenantHash)
    {
        if (stored is null)
        {
            return false;
        }

        var separator = stored.IndexOf(TenantSeparator, StringComparison.Ordinal);
        var ownerMatches = FixedTimeEquals(separator < 0 ? stored : stored[..separator], ownerHash);
        var tenantMatches = tenantHash is null || separator < 0 || FixedTimeEquals(stored[(separator + 1)..], tenantHash);
        return ownerMatches && tenantMatches;
    }

    private static bool FixedTimeEquals(string stored, string expected) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(stored), Encoding.UTF8.GetBytes(expected));

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
