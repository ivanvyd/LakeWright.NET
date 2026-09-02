using LakeWright.Core.Tenancy;

namespace LakeWright.Embedding;

/// <summary>Validates an application-defined condition before a dashboard token is minted.</summary>
/// <remarks>
/// The broker owns the security-sensitive act of minting a viewer token, but deployment checks
/// such as a published-revision verification belong to optional modules. This small seam lets a
/// host or an operations package fail closed without making the embedding package depend on it.
/// Implementations must not trust a browser-supplied dashboard id without applying their own
/// assignment checks.
/// </remarks>
public interface IEmbedPrecondition
{
    /// <summary>Throws when the dashboard must not be embedded for this resolved tenant.</summary>
    Task EnsureSatisfiedAsync(
        TenantContext tenant,
        string dashboardId,
        CancellationToken cancellationToken = default);
}
