using Azure.Core;
using LakeWright.AspNetCore;
using Signalboard;
using Signalboard.Components;

var builder = WebApplication.CreateBuilder(args);

// LakeWright deliberately registers no identity provider. A real product calls
// AddAuthentication().AddOpenIdConnect(...) here. This sample uses a cookie for the dashboard and a
// header for curl, so it runs with nothing but Postgres — see DemoAuthenticationHandler for why
// that is safe to ship as a sample and unsafe to copy.
builder.Services.AddDemoAuthentication();

builder.Services.AddLakeWright(builder.Configuration);

// Databricks is optional here on purpose: tenancy, authorization and the operations API run
// against PostgreSQL alone. Without a workspace the sample still demonstrates isolation, and
// operations simply stay Pending because nothing submits them.
if (!string.IsNullOrWhiteSpace(builder.Configuration["Databricks:WorkspaceUrl"]))
{
    builder.Services.AddSingleton<TokenCredential, ConfiguredTokenCredential>();
    builder.Services.AddLakeWrightDatabricks(builder.Configuration);
    builder.Services.AddLakeWrightOperationWorker(builder.Configuration);
}

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<TenantWorkspace>();
builder.Services.AddOpenApi();

var app = builder.Build();

await app.Services.SeedDemoTenantsAsync();

app.UseStaticFiles();
app.UseAntiforgery();
app.UseAuthentication();
app.UseLakeWrightTenancy();
app.UseAuthorization();

// AddLakeWright sets a fallback policy requiring an authenticated user, so this needs the opt-out
// or the document is a 401. Publishing it anonymously suits a sample; a product would not.
app.MapOpenApi().AllowAnonymous();

app.MapLakeWrightOperations();
app.MapDemoAuthentication();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

await app.RunAsync();

/// <summary>Exposed so the sample can be driven by a test host.</summary>
public partial class Program;
