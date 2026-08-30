using System.Collections.Concurrent;
using LakeWright.Core.Tenancy;

namespace LakeWright.Embedding;

/// <summary>
/// The default <see cref="IWorkspaceTokenCache"/>, backed by a <see cref="ConcurrentDictionary{TKey, TValue}"/>
/// holding <see cref="Lazy{T}"/> values so the factory body runs at most once per key
/// under concurrent access.
/// </summary>
/// <remarks>
/// <para>
/// The dogpile collapse is the whole point of caching: 20 concurrent opens for the same key
/// should run the exchange once, not 20 times. The standard in-memory cache helper
/// <c>GetOrCreateAsync</c> synchronises the lookup but runs the factory outside the lock, so
/// it does NOT collapse the dogpile on its own; the standard fix is to wrap the factory in
/// a <see cref="Lazy{T}"/>, and that is what this class does. The first caller to arrive
/// builds the <see cref="Lazy{T}"/>; every concurrent caller sees the same instance, and the
/// inner <see cref="Task{T}"/> is awaited by all of them. The factory body therefore runs
/// exactly once per key per cache lifetime.
/// </para>
/// <para>
/// Absolute expiration is computed from the returned token's own <c>ExpiresAt</c>, with a
/// 30-second safety margin so a token that is about to expire is not served on what would
/// otherwise be the last legal call. The standard in-memory cache requires that expiration
/// be set before the entry is committed (i.e. before the factory body runs), which would
/// force a guess based on the lifetime documented today. A
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> of <see cref="Entry"/> records sidesteps
/// the problem: the entry is a record of (lazy, computed-expiration) that the broker fills
/// in after the task completes. On the next lookup past that computed expiration, the
/// broker evicts and re-adds, and the new factory runs once for that next dogpile.
/// </para>
/// <para>
/// The dictionary grows without bound under churn, which is fine for the workload (one
/// entry per client id, one entry per (tenant, dashboard, viewer, version) tuple). A
/// consumer that needs eviction pressure can replace the registration with their own
/// implementation of <see cref="IWorkspaceTokenCache"/> / <see cref="IEmbedTokenCache"/>.
/// </para>
/// </remarks>
internal sealed class MemoryWorkspaceTokenCache : IWorkspaceTokenCache
{
    /// <summary>
    /// Tokens are evicted a little before the vendor-stated lifetime so the broker never
    /// serves a token that the vendor has already rejected. 30 seconds is small relative to
    /// the one-hour lifetime and large relative to clock skew between this process and the
    /// workspace.
    /// </summary>
    private static readonly TimeSpan SafetyMargin = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public MemoryWorkspaceTokenCache(TimeProvider time)
    {
        _time = time;
    }

    public async Task<EmbedToken> GetOrAddAsync(
        string clientId,
        Func<CancellationToken, ValueTask<EmbedToken>> factory,
        CancellationToken cancellationToken)
    {
        var key = Key(clientId);
        var entry = _entries.AddOrUpdate(
            key,
            _ => new Entry(new Lazy<Task<EmbedToken>>(() => factory(cancellationToken).AsTask())),
            (_, existing) => existing);

        // If the entry is past its absolute expiration (computed from the previous call's
        // ExpiresAt - safety margin), it is no longer trustworthy. Remove it; the next
        // AddOrUpdate installs a fresh entry. Concurrent callers racing here will see
        // either the stale or the fresh entry; whichever they get, awaiting lazy.Value on
        // the fresh entry kicks the factory exactly once.
        if (entry.AbsoluteExpiration <= _time.GetUtcNow())
        {
            _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
            entry = _entries.AddOrUpdate(
                key,
                _ => new Entry(new Lazy<Task<EmbedToken>>(() => factory(cancellationToken).AsTask())),
                (_, existing) => existing);
        }

        var token = await entry.Lazy.Value.ConfigureAwait(false);
        entry.AbsoluteExpiration = token.ExpiresAt - SafetyMargin;
        return token;
    }

    private static string Key(string clientId) => clientId;

    private sealed class Entry
    {
        public Entry(Lazy<Task<EmbedToken>> lazy)
        {
            Lazy = lazy;
        }

        public Lazy<Task<EmbedToken>> Lazy { get; }
        public DateTimeOffset AbsoluteExpiration { get; set; } = DateTimeOffset.MaxValue;
    }
}

/// <summary>
/// The default <see cref="IEmbedTokenCache"/>, mirroring <see cref="MemoryWorkspaceTokenCache"/>.
/// </summary>
/// <remarks>
/// Same shape as <see cref="MemoryWorkspaceTokenCache"/>: a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> of <see cref="Entry"/> records, each
/// holding a <see cref="Lazy{T}"/> whose <see cref="Task{T}"/> is awaited by every
/// concurrent caller. The factory body therefore runs once per cache lifetime per key, and
/// the absolute expiration is computed from the returned token's own <c>ExpiresAt</c>.
/// </remarks>
internal sealed class MemoryEmbedTokenCache : IEmbedTokenCache
{
    private static readonly TimeSpan SafetyMargin = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<EmbedCacheKey, Entry> _entries = new();
    private readonly TimeProvider _time;

    public MemoryEmbedTokenCache(TimeProvider time)
    {
        _time = time;
    }

    public async Task<EmbedToken> GetOrAddAsync(
        EmbedCacheKey key,
        Func<CancellationToken, ValueTask<EmbedToken>> factory,
        CancellationToken cancellationToken)
    {
        var entry = _entries.AddOrUpdate(
            key,
            _ => new Entry(new Lazy<Task<EmbedToken>>(() => factory(cancellationToken).AsTask())),
            (_, existing) => existing);

        // See MemoryWorkspaceTokenCache for why we evict and re-add rather than mutate in
        // place: the Lazy.Task is built once and cached on the Lazy, so the only way to
        // force a re-run is to install a fresh entry.
        if (entry.AbsoluteExpiration <= _time.GetUtcNow())
        {
            _entries.TryRemove(new KeyValuePair<EmbedCacheKey, Entry>(key, entry));
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
        public Entry(Lazy<Task<EmbedToken>> lazy)
        {
            Lazy = lazy;
        }

        public Lazy<Task<EmbedToken>> Lazy { get; }
        public DateTimeOffset AbsoluteExpiration { get; set; } = DateTimeOffset.MaxValue;
    }
}
