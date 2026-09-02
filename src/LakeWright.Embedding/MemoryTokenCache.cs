using System.Collections.Concurrent;
using LakeWright.Core.Tenancy;
using LakeWright.Core.Tokens;

namespace LakeWright.Embedding;

/// <summary>
/// The default <see cref="IWorkspaceTokenCache"/>, backed by a <see cref="ConcurrentDictionary{TKey, TValue}"/>
/// of <see cref="Lazy{T}"/> values so the factory body runs at most once per key under concurrent
/// access.
/// </summary>
/// <remarks>
/// <para>
/// The dogpile collapse is the whole point of caching: 20 concurrent opens for the same key
/// should run the exchange once, not 20 times. The standard in-memory cache helper
/// <c>GetOrCreateAsync</c> synchronises the lookup but runs the factory outside the lock, so
/// it does NOT collapse the dogpile on its own; the standard fix is to wrap the factory in
/// a <see cref="Lazy{T}"/>, and that is what this class does.
/// </para>
/// <para>
/// Absolute expiration is computed from the returned token's own <c>ExpiresAt</c>, with a
/// 30-second safety margin so a token that is about to expire is not served on what would
/// otherwise be the last legal call. The standard in-memory cache requires that expiration
/// be set before the entry is committed (i.e. before the factory body runs), which would
/// force a guess based on the lifetime documented today. Storing the entry as a
/// (lazy, computed-expiration) record sidesteps that, and lets the broker evict and re-add
/// the entry on the next lookup past expiration.
/// </para>
/// </remarks>
internal sealed class MemoryWorkspaceTokenCache : IWorkspaceTokenCache
{
    private readonly MemoryTokenCache<string, EmbedToken> _inner;

    public MemoryWorkspaceTokenCache(TimeProvider time)
    {
        _inner = new MemoryTokenCache<string, EmbedToken>(time, token => token.ExpiresAt);
    }

    public Task<EmbedToken> GetOrAddAsync(
        string clientId,
        Func<CancellationToken, ValueTask<EmbedToken>> factory,
        CancellationToken cancellationToken) =>
        _inner.GetOrAddAsync(clientId, factory, cancellationToken);
}

internal sealed class MemoryEmbedTokenCache : IEmbedTokenCache
{
    private readonly MemoryTokenCache<EmbedCacheKey, EmbedToken> _inner;

    public MemoryEmbedTokenCache(TimeProvider time)
    {
        _inner = new MemoryTokenCache<EmbedCacheKey, EmbedToken>(time, token => token.ExpiresAt);
    }

    public Task<EmbedToken> GetOrAddAsync(
        EmbedCacheKey key,
        Func<CancellationToken, ValueTask<EmbedToken>> factory,
        CancellationToken cancellationToken) =>
        _inner.GetOrAddAsync(key, factory, cancellationToken);
}

internal sealed class MemoryOpsTokenCache : IOpsTokenCache
{
    private readonly MemoryTokenCache<string, EmbedToken> _inner;

    public MemoryOpsTokenCache(TimeProvider time)
    {
        _inner = new MemoryTokenCache<string, EmbedToken>(time, token => token.ExpiresAt);
    }

    public Task<EmbedToken> GetOrAddAsync(
        string clientId,
        Func<CancellationToken, ValueTask<EmbedToken>> factory,
        CancellationToken cancellationToken) =>
        _inner.GetOrAddAsync(clientId, factory, cancellationToken);
}
