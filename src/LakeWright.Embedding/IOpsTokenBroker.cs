namespace LakeWright.Embedding;

/// <summary>
/// Acquires a workspace token authenticated as the operations service principal.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="IDashboardTokenBroker"/>: the latter mints per-viewer tokens
/// (downscoped, scoped to a single dashboard) for the browser; this one acquires an
/// unscoped workspace token for the backend. They authenticate as different service
/// principals (ADR 0019) and have different blast radii: a viewer token only opens the
/// one dashboard it was minted for, while an ops token can list, refresh, and otherwise
/// interact with anything the ops SP can reach.
/// </para>
/// <para>
/// Implementations are expected to call the workspace's <c>/oidc/v1/token</c> endpoint with
/// the <c>client_credentials</c> grant. The returned token's lifetime is read from the
/// response rather than assumed, so a consumer that caches on it is reading the same field
/// the embed broker exposes (<see cref="EmbedToken.ExpiresAt"/>).
/// </para>
/// </remarks>
public interface IOpsTokenBroker
{
    /// <summary>
    /// Acquires a workspace token authenticated as the ops service principal, for use by
    /// backend operations against the workspace (catalog, refresh, etc.).
    /// </summary>
    /// <param name="cancellationToken">Cancels the token request.</param>
    Task<EmbedToken> AcquireAsync(CancellationToken cancellationToken = default);
}

/// <summary>Caches operations workspace tokens without exposing their storage mechanism.</summary>
public interface IOpsTokenCache
{
    /// <summary>Gets a cached token or acquires and caches a fresh one for this client ID.</summary>
    Task<EmbedToken> GetOrAddAsync(
        string clientId,
        Func<CancellationToken, ValueTask<EmbedToken>> factory,
        CancellationToken cancellationToken);
}
