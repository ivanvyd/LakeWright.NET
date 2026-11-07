using Azure.Core;
using LakeWright.Core.Tenancy;
using LakeWright.Databricks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

if (args.Length != 1 || args[0] != "--offline")
{
    Console.Error.WriteLine("Run with --offline. This example has no live workspace mode.");
    return 2;
}

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
var ct = deadline.Token;
using var workspace = new OfflineWorkspace(ct);
var services = new ServiceCollection();
services.AddLakeWrightTenancy<MembershipResolver>();
services.AddSingleton<TokenCredential>(new OfflineCredential());
services.AddLakeWrightDatabricks(new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Databricks:WorkspaceUrl"] = workspace.BaseAddress,
        ["Databricks:WarehouseId"] = "offline-warehouse",
    }).Build());

await using var provider = services.BuildServiceProvider();
await using var scope = provider.CreateAsyncScope();
var resolver = scope.ServiceProvider.GetRequiredService<ITenantContextResolver>();
var executor = scope.ServiceProvider.GetRequiredService<IStatementExecutor>();

// In an HTTP endpoint, principalId comes from the authenticated server identity.
const string PrincipalId = "member-alice";
var tenant = await resolver.ResolveAsync(MembershipResolver.Acme, PrincipalId, ct)
    ?? throw new InvalidOperationException("The fixture member was not resolved.");
var result = await executor.ExecuteAsync(
    TenantScopedStatement.Create(tenant, "SELECT :value AS value", StatementParameter.Int("value", 1)), ct);
await workspace.VerifyRequestAsync();
if (result is not StatementOutcome.Success success || success.Rows.Single().Single() != "1")
{
    throw new InvalidOperationException("The fixture query did not return the expected row.");
}
Console.WriteLine("ALLOW: member-alice -> Acme; tenant-scoped query returned 1.");

var before = workspace.Requests;
var wrongTenant = await resolver.ResolveAsync(MembershipResolver.Globex, PrincipalId, ct);
if (wrongTenant is not null)
{
    throw new InvalidOperationException("A non-member received a tenant context.");
}
// Return 404 here. No context means no statement, transport call, or application write.
if (workspace.Requests != before || before != 1 || success.Rows.Single().Single() != "1")
{
    throw new InvalidOperationException("Denial changed fixture state.");
}
Console.WriteLine("DENY: member-alice -> Globex; membership denied; no statement sent; transport requests remain 1.");
Console.WriteLine("OFFLINE_OK: published 2.1.2; no workspace credentials or external network calls.");
return 0;

internal sealed class MembershipResolver(ITenantContextFactory contexts) : ITenantContextResolver
{
    internal static readonly TenantId Acme = TenantId.Parse("0198f000-0000-7000-8000-0000000000e1");
    internal static readonly TenantId Globex = TenantId.Parse("0198f000-0000-7000-8000-0000000000e2");

    private static readonly Dictionary<(TenantId Tenant, string Principal), (string Catalog, string Schema)> Memberships = new()
    {
        [(Acme, "member-alice")] = ("analytics", "tenant_acme"),
        [(Globex, "member-bob")] = ("analytics", "tenant_globex"),
    };

    public Task<TenantContext?> ResolveAsync(TenantId tenantId, string principalId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Memberships.TryGetValue((tenantId, principalId), out var membership)
            ? contexts.ForTenant(tenantId, membership.Catalog, membership.Schema)
            : null);
    }
}

internal sealed class OfflineCredential : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new("offline-fixture-token", DateTimeOffset.MaxValue);

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        ValueTask.FromResult(GetToken(requestContext, cancellationToken));
}
