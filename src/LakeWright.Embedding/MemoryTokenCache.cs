using System.Collections.Concurrent;
using LakeWright.Core.Tenancy;

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
    private readonly MemoryTokenCache<string> _inner;

    public MemoryWorkspaceTokenCache(TimeProvider time)
    {
        _inner = new MemoryTokenCache<string>(time);
    }

    public Task<EmbedToken> GetOrAddAsync(
        string clientId,
        Func<CancellationToken, ValueTask<EmbedToken>> factory,
        CancellationToken cancellationToken) =>
        _inner.GetOrAddAsync(clientId, factory, cancellationToken);
}

internal sealed class MemoryEmbedTokenCache : IEmbedTokenCache
{
    private readonly MemoryTokenCache<EmbedCacheKey> _inner;

    public MemoryEmbedTokenCache(TimeProvider time)
    {
        _inner = new MemoryTokenCache<EmbedCacheKey>(time);
    }

    public Task<EmbedToken> GetOrAddAsync(
        EmbedCacheKey key,
        Func<CancellationToken, ValueTask<EmbedToken>> factory,
        CancellationToken cancellationToken) =>
        _inner.GetOrAddAsync(key, factory, cancellationToken);
}

internal sealed class MemoryTokenCache<TKey> where TKey : notnull
{
    /// <summary>
    /// Tokens are evicted a little before the vendor-stated lifetime so the broker never
    /// serves a token that the vendor has already rejected. 30 seconds is small relative to
    /// the one-hour lifetime and large relative to clock skew between this process and the
    /// workspace.
    /// </summary>
    private static readonly TimeSpan SafetyMargin = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<TKey, Entry> _entries = new();
    private readonly TimeProvider _time;

    public MemoryTokenCache(TimeProvider time)
    {
        _time = time;
    }

    public async Task<EmbedToken> GetOrAddAsync(
        TKey key,
        Func<CancellationToken, ValueTask<EmbedToken>> factory,
        CancellationToken cancellationToken)
    {
        var entry = _entries.AddOrUpdate(
            key,
            _ => new Entry(new Lazy<Task<EmbedToken>>(() => factory(cancellationToken).AsTask())),
            (_, existing) => existing);

        // If the entry is past its absolute expiration, the Lazy.Task is already done and
        // would serve a stale token. Remove it; the next AddOrUpdate installs a fresh
        // entry. Concurrent callers racing here see either the stale or the fresh entry;
        // whichever they get, awaiting lazy.Value on the fresh entry kicks the factory
        // exactly once.
        if (entry.AbsoluteExpiration <= _time.GetUtcNow())
        {
            _entries.TryRemove(new KeyValuePair<TKey, Entry>(key, entry));
            entry = _entries.AddOrUpdate(
                key,
                _ => new Entry(new Lazy<Task<EmbedToken>>(() => factory(cancellationToken).AsTask())),
                (_, existing) => existing);
        }

        var token = await entry.Lazy.Value.ConfigureAwait(false);
        entry.AbsoluteExpiration = token.ExpiresAt - SafetyMargin;
        return token;
    }

    private sealed class Entry
    {
        public Entry(Lazy<Task<EmbedToken>> lazy) => Lazy = lazy;

        public Lazy<Task<EmbedToken>> Lazy { get; }
        public DateTimeOffset AbsoluteExpiration { get; set; } = DateTimeOffset.MaxValue;
    }
}
