using System.Collections.Concurrent;

namespace LakeWright.Core.Tokens;

/// <summary>
/// Collapses concurrent token requests and evicts entries shortly before their expiration.
/// </summary>
public sealed class MemoryTokenCache<TKey, TValue> where TKey : notnull
{
    private static readonly TimeSpan SafetyMargin = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<TKey, Entry> _entries = new();
    private readonly Func<TValue, DateTimeOffset> _expiresAt;
    private readonly TimeProvider _time;

    public MemoryTokenCache(TimeProvider time, Func<TValue, DateTimeOffset> expiresAt)
    {
        _time = time;
        _expiresAt = expiresAt;
    }

    public async Task<TValue> GetOrAddAsync(
        TKey key,
        Func<CancellationToken, ValueTask<TValue>> factory,
        CancellationToken cancellationToken)
    {
        var entry = GetOrCreate(key, factory, cancellationToken);
        if (entry.AbsoluteExpiration <= _time.GetUtcNow())
        {
            _entries.TryRemove(new KeyValuePair<TKey, Entry>(key, entry));
            entry = GetOrCreate(key, factory, cancellationToken);
        }

        try
        {
            var value = await entry.Lazy.Value.ConfigureAwait(false);
            entry.AbsoluteExpiration = _expiresAt(value) - SafetyMargin;
            return value;
        }
        catch
        {
            _entries.TryRemove(new KeyValuePair<TKey, Entry>(key, entry));
            throw;
        }
    }

    private Entry GetOrCreate(
        TKey key,
        Func<CancellationToken, ValueTask<TValue>> factory,
        CancellationToken cancellationToken) =>
        _entries.GetOrAdd(
            key,
            _ => new Entry(new Lazy<Task<TValue>>(() => factory(cancellationToken).AsTask())));

    private sealed class Entry(Lazy<Task<TValue>> lazy)
    {
        public Lazy<Task<TValue>> Lazy { get; } = lazy;
        public DateTimeOffset AbsoluteExpiration { get; set; } = DateTimeOffset.MaxValue;
    }
}
