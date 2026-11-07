using LakeWright.Core.Tenancy;
using LakeWright.Core.Tokens;

namespace LakeWright.Embedding;

/// <summary>
/// The default <see cref="IWorkspaceTokenCache"/>, using the shared token cache for expiration and
/// concurrent factory reuse.
/// </summary>
/// <remarks>
/// Concurrent calls for one key share the token request. Entries expire 30 seconds before the
/// returned token's <c>ExpiresAt</c>, so the cache does not guess the token lifetime.
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

    public void EvictTenant(TenantId tenantId) =>
        _inner.RemoveWhere(key => key.TenantId == tenantId);
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
