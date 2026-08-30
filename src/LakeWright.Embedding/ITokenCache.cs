using LakeWright.Core.Tenancy;

namespace LakeWright.Embedding;

/// <summary>
/// Caches the OAuth workspace token returned by leg 1 of the exchange.
/// </summary>
/// <remarks>
/// The same service principal is used for every viewer and every dashboard, so its token is
/// the highest-value thing to cache: N board opens collapse to a single leg-1 roundtrip. The
/// cache key is the <c>ClientId</c> because rotating credentials (the only case that should
/// invalidate) means a different principal. See ADR 0018.
/// </remarks>
public interface IWorkspaceTokenCache
{
    /// <summary>
    /// Returns the cached workspace token if one is still valid, otherwise invokes
    /// <paramref name="factory"/> exactly once across concurrent callers and stores the
    /// result with absolute expiration set from the token's own <c>ExpiresAt</c>.
    /// </summary>
    /// <param name="clientId">The service principal id; the cache key.</param>
    /// <param name="factory">Mints a fresh token. Receives the request's cancellation token.</param>
    /// <param name="cancellationToken">Cancels the lookup, not the factory itself.</param>
    Task<EmbedToken> GetOrAddAsync(
        string clientId,
        Func<CancellationToken, ValueTask<EmbedToken>> factory,
        CancellationToken cancellationToken);
}

/// <summary>
/// Caches the downscoped viewer token returned by leg 3 of the exchange.
/// </summary>
/// <remarks>
/// The cache key is the full input that produced it: <see cref="TenantContext.TenantId"/>, the
/// optional <see cref="TenantContext.ScopeVersion"/>, the dashboard id, and the viewer id.
/// Changing any of them is a different row filter on the other side, so a hit is correct only
/// when the full set matches. <see cref="TenantContext.ScopeVersion"/> belongs in the key
/// because the whole point of the version (ADR 0017) is to bust the vendor's 24-hour result
/// cache on a scope change; serving a stale value here would defeat it.
/// </remarks>
public interface IEmbedTokenCache
{
    /// <summary>
    /// Returns the cached embed token if one is still valid, otherwise invokes
    /// <paramref name="factory"/> exactly once across concurrent callers and stores the
    /// result with absolute expiration set from the token's own <c>ExpiresAt</c>.
    /// </summary>
    /// <param name="key">The composite key derived from the request inputs.</param>
    /// <param name="factory">Performs the three-leg exchange. Receives the request's cancellation token.</param>
    /// <param name="cancellationToken">Cancels the lookup, not the factory itself.</param>
    Task<EmbedToken> GetOrAddAsync(
        EmbedCacheKey key,
        Func<CancellationToken, ValueTask<EmbedToken>> factory,
        CancellationToken cancellationToken);
}

/// <summary>
/// The composite key for <see cref="IEmbedTokenCache"/>: a hit means the request inputs are
/// byte-for-byte the same as the cached call. <see cref="ScopeVersion"/> is nullable because
/// most tenants do not need a version and a null one is a no-op on the broker's
/// <c>external_value</c> composition (ADR 0017).
/// </summary>
public readonly record struct EmbedCacheKey(
    TenantId TenantId,
    string? ScopeVersion,
    string DashboardId,
    string ViewerId);
