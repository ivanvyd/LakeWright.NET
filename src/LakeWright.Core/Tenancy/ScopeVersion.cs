using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace LakeWright.Core.Tenancy;

/// <summary>Creates stable, claim-safe versions for a tenant's effective access scope.</summary>
public static class ScopeVersion
{
    /// <summary>Hashes member identifiers as an order-insensitive set and returns hex output.</summary>
    public static string FromMembers(IEnumerable<string> ids, int length = 12)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (length is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Scope-version length must be from 1 through 64.");
        }

        var canonical = ids.Select(id => id ?? throw new ArgumentException("Member identifiers cannot contain null.", nameof(ids)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(string.Join("\n", canonical));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..length];
    }
}

/// <summary>Supplies a tenant's current scope version from an adopter-owned membership source.</summary>
public interface IScopeVersionSource
{
    ValueTask<string> GetAsync(TenantId tenantId, CancellationToken cancellationToken = default);
}

/// <summary>Caches scope versions for a bounded interval to avoid repeating membership queries.</summary>
public sealed class CachedScopeVersionSource : IScopeVersionSource
{
    private readonly IScopeVersionSource _inner;
    private readonly TimeProvider _time;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<TenantId, Entry> _entries = new();

    public CachedScopeVersionSource(IScopeVersionSource inner, TimeProvider time, TimeSpan ttl)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(time);
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), "Scope-version cache TTL must be positive.");
        }

        _inner = inner;
        _time = time;
        _ttl = ttl;
    }

    public async ValueTask<string> GetAsync(TenantId tenantId, CancellationToken cancellationToken = default)
    {
        if (_entries.TryGetValue(tenantId, out var existing) && existing.ExpiresAt > _time.GetUtcNow())
        {
            return existing.Value;
        }

        var value = await _inner.GetAsync(tenantId, cancellationToken).ConfigureAwait(false);
        _entries[tenantId] = new Entry(value, _time.GetUtcNow().Add(_ttl));
        return value;
    }

    private sealed record Entry(string Value, DateTimeOffset ExpiresAt);
}
