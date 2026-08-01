using Azure.Core;
using LakeWright.Core.Jobs;
using LakeWright.Core.Tenancy;
using LakeWright.Databricks;
using LakeWright.Multitenancy;
using LakeWright.Multitenancy.Model;
using LakeWright.Multitenancy.Operations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Azure.Databricks.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LakeWright.AspNetCore;

public static class LakeWrightServiceCollectionExtensions
{
    /// <summary>
    /// Registers tenancy, authorization and the operations API.
    /// </summary>
    /// <remarks>
    /// Deliberately does not register authentication. Which identity provider a product uses is
    /// the product's decision, and an accelerator that picks one has chosen for its adopter.
    /// Call <c>AddAuthentication().AddOpenIdConnect(...)</c> yourself; this only requires that a
    /// principal carries a stable subject claim.
    ///
    /// It also does not require Databricks. Tenancy, authorization and the operations API run
    /// against PostgreSQL alone, which is what lets a contributor work on them with no cloud
    /// account. Add <see cref="AddLakeWrightDatabricks"/> when you want queries and jobs.
    /// </remarks>
    public static IServiceCollection AddLakeWright(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<MultitenancyOptions>()
            .Bind(configuration.GetSection(MultitenancyOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddDbContext<LakeWrightDbContext>((provider, options) =>
            options.UseNpgsql(configuration.GetConnectionString("LakeWright")));

        services.AddScoped<ITenantContextResolver, EfTenantContextResolver>();
        services.AddScoped<IMembershipReader, EfMembershipReader>();
        services.TryAddSingletonTimeProvider();
        services.AddScoped<AuditLog>();
        services.AddScoped<OperationStore>();

        services.AddHttpContextAccessor();
        services.AddScoped<ITenantContextAccessor, HttpTenantContextAccessor>();
        services.AddScoped<IAuthorizationHandler, TenantRoleHandler>();

        services.AddAuthorizationBuilder()
            .AddPolicy(TenantPolicies.Viewer, p => p.AddRequirements(new TenantRoleRequirement(MembershipRole.Viewer)))
            .AddPolicy(TenantPolicies.Member, p => p.AddRequirements(new TenantRoleRequirement(MembershipRole.Member)))
            .AddPolicy(TenantPolicies.Admin, p => p.AddRequirements(new TenantRoleRequirement(MembershipRole.Admin)))

            // Unauthenticated requests are refused unless an endpoint opts out with
            // [AllowAnonymous]. Opt-out beats opt-in: a new endpoint added in a hurry is protected
            // by default rather than exposed by omission.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());

        return services;
    }

    /// <summary>
    /// Registers the Databricks clients.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="AddLakeWright"/> so the application starts without a workspace.
    /// Folding it in made <c>WorkspaceUrl</c> and <c>WarehouseId</c> required at startup, which
    /// broke the promise that a contributor needs no cloud account — found by running the sample
    /// rather than by reading it.
    ///
    /// Supply a <see cref="TokenCredential"/>. On Azure that is <c>DefaultAzureCredential</c>
    /// backed by a managed identity, which Databricks accepts with no stored secret (ADR 0006).
    /// </remarks>
    public static IServiceCollection AddLakeWrightDatabricks(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DatabricksOptions>()
            .Bind(configuration.GetSection(DatabricksOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<DatabricksOptions>>().Value;
            var credential = provider.GetRequiredService<TokenCredential>();
            return DatabricksClient.CreateClient(options.WorkspaceUrl, credential);
        });

        services.AddScoped<IStatementExecutor, DatabricksStatementExecutor>();
        services.AddScoped<IJobSubmitter, DatabricksJobSubmitter>();

        return services;
    }

    /// <summary>Runs the operation worker in this process.</summary>
    /// <remarks>
    /// Separate from <see cref="AddLakeWright"/> so a web front end and a worker can be deployed
    /// as different processes from the same image, which is what you want the moment a long
    /// operation should not compete with request handling.
    /// </remarks>
    public static IServiceCollection AddLakeWrightOperationWorker(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<OperationWorkerOptions>()
            .Bind(configuration.GetSection(OperationWorkerOptions.SectionName))
            .ValidateOnStart();

        services.TryAddSingletonTimeProvider();
        services.AddHostedService<OperationWorker>();
        return services;
    }

    private static void TryAddSingletonTimeProvider(this IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }

    /// <summary>Resolves the tenant for every request that carries one.</summary>
    public static IApplicationBuilder UseLakeWrightTenancy(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<TenantResolutionMiddleware>();
    }
}
