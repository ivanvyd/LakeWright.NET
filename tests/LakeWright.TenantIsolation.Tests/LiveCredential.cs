using Azure.Identity;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// The credential the <c>Category=Live</c> tests authenticate with.
/// </summary>
/// <remarks>
/// <c>DefaultAzureCredential</c> with managed identity excluded, deliberately.
///
/// A chained credential is supposed to move to the next source when one is unavailable. Off Azure
/// there is no IMDS endpoint at 169.254.169.254, and the probe surfaces as
/// <c>AuthenticationFailedException</c> rather than <c>CredentialUnavailableException</c> — so the
/// chain aborts instead of falling through to the Azure CLI, and every live test fails with a
/// managed-identity error on a machine that never had one.
///
/// It is intermittent, which is worse than broken: these tests passed on this machine earlier the
/// same day and then failed on the identical code, because the probe sometimes fails fast enough
/// to be classified differently. Excluding the source removes the flake rather than retrying it.
///
/// A deployed application does the opposite: it leaves managed identity in and wants it first.
/// This is a property of running the tests on a developer machine, not of the library, which takes
/// whatever <c>TokenCredential</c> is registered.
/// </remarks>
internal static class LiveCredential
{
    public static DefaultAzureCredential Create() =>
        new(new DefaultAzureCredentialOptions { ExcludeManagedIdentityCredential = true });
}
