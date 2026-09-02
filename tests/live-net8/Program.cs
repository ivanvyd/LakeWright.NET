using Azure.Core;
using Azure.Identity;
using LakeWright.Conversations;
using LakeWright.Core.Tenancy;
using LakeWright.Databricks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

var configuration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Databricks:WorkspaceUrl"] = Require("DATABRICKS_HOST"),
        ["Databricks:WarehouseId"] = Require("LAKEWRIGHT_WAREHOUSE_ID"),
        ["Genie:WorkspaceUrl"] = Require("DATABRICKS_HOST"),
        ["Genie:Spaces:" + Require("LAKEWRIGHT_LIVE_TENANT_ID")] = Environment.GetEnvironmentVariable("LAKEWRIGHT_GENIE_SPACE_ID"),
    })
    .Build();

var services = new ServiceCollection();
services.AddSingleton<TokenCredential>(new DefaultAzureCredential(new DefaultAzureCredentialOptions
{
    ExcludeManagedIdentityCredential = true,
    TenantId = Environment.GetEnvironmentVariable("AZURE_TENANT_ID"),
}));
services.AddLakeWrightTenancy<LiveResolver>();
services.AddLakeWrightDatabricks(configuration);

if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LAKEWRIGHT_GENIE_SPACE_ID")))
{
    services.AddLakeWrightGenie(configuration);
}

await using var provider = services.BuildServiceProvider();
provider.GetRequiredService<IStartupValidator>().Validate();
await using var scope = provider.CreateAsyncScope();
var tenantId = TenantId.Parse(Require("LAKEWRIGHT_LIVE_TENANT_ID"));
var tenant = await scope.ServiceProvider.GetRequiredService<ITenantContextResolver>()
    .ResolveAsync(tenantId, "live-validator", CancellationToken.None)
    ?? throw new InvalidOperationException("The live validator did not resolve its configured tenant.");

var statement = TenantScopedStatement.Create(tenant, "SELECT :value AS value", StatementParameter.Int("value", 1));
var outcome = await scope.ServiceProvider.GetRequiredService<IStatementExecutor>()
    .ExecuteAsync(statement, CancellationToken.None);
if (outcome is not StatementOutcome.Success success || success.Rows.SingleOrDefault()?.SingleOrDefault() != "1")
{
    throw new InvalidOperationException("The live net8 statement result was not the expected value.");
}

if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LAKEWRIGHT_GENIE_SPACE_ID")))
{
    var answer = await scope.ServiceProvider.GetRequiredService<IGenieConversations>()
        .AskAsync(
            tenant,
            "live-validator",
            Environment.GetEnvironmentVariable("LAKEWRIGHT_GENIE_PROMPT") ?? "Return the number one.");
    if (answer.Outcome != GenieOutcome.Completed || string.IsNullOrWhiteSpace(answer.ConversationId))
    {
        throw new InvalidOperationException("The live net8 Genie call did not complete.");
    }
}

Console.WriteLine("NET8_LIVE_OK");

static string Require(string name) => Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"{name} is required for the net8 live validation.");

internal sealed class LiveResolver(ITenantContextFactory contexts) : ITenantContextResolver
{
    public Task<TenantContext?> ResolveAsync(TenantId tenantId, string principalId, CancellationToken cancellationToken) =>
        Task.FromResult(principalId == "live-validator"
            ? contexts.ForTenant(
                tenantId,
                Environment.GetEnvironmentVariable("LAKEWRIGHT_CATALOG")
                    ?? throw new InvalidOperationException("LAKEWRIGHT_CATALOG is required for the net8 live validation."),
                Environment.GetEnvironmentVariable("LAKEWRIGHT_SCHEMA") ?? "reference")
            : null);
}
