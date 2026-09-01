using LakeWright.AspNetCore;
using LakeWright.Core.Cost;
using LakeWright.Databricks;
using LakeWright.Multitenancy.Cost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// Registering tenancy must not require a Databricks workspace.
/// </summary>
/// <remarks>
/// The two registrations were once one, which made <c>WorkspaceUrl</c> required to start any
/// application at all — including the sample, which is meant to run on a Postgres container alone.
/// Nothing caught it, because the startup validator only fires when a host starts.
/// </remarks>
public sealed class OptionalDatabricksTests
{
    [Fact]
    public void Tenancy_starts_with_no_databricks_configuration()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLakeWright(Configuration(new()
        {
            ["ConnectionStrings:LakeWright"] = "Host=localhost;Database=unused",
            ["Multitenancy:Catalog"] = "lakewright_test"
        }));

        // Act
        var validate = Validator(services);

        // Assert
        validate.ShouldNotThrow();
    }

    [Fact]
    public void Databricks_rejects_a_half_configured_section()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLakeWrightDatabricks(Configuration(new()
        {
            ["Databricks:WorkspaceUrl"] = "https://adb-1.azuredatabricks.net"
        }));

        // Act
        var validate = Validator(services);

        // Assert
        validate.ShouldThrow<OptionsValidationException>()
            .Message.ShouldContain(nameof(DatabricksOptions.WarehouseId));
    }

    [Fact]
    public void Databricks_accepts_a_complete_section()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLakeWrightDatabricks(Configuration(new()
        {
            ["Databricks:WorkspaceUrl"] = "https://adb-1.azuredatabricks.net",
            ["Databricks:WarehouseId"] = "abc123"
        }));

        // Act
        var validate = Validator(services);

        // Assert
        validate.ShouldNotThrow();
    }

    [Fact]
    public void Billing_cost_rejects_a_missing_workspace_id()
    {
        var services = new ServiceCollection();
        services.AddLakeWrightBillingCostAttribution(Configuration([]));

        var validate = Validator(services);

        validate.ShouldThrow<OptionsValidationException>()
            .Message.ShouldContain(nameof(BillingUsageOptions.WorkspaceId));
    }

    [Fact]
    public void Billing_cost_registration_composes_the_reader_and_replaces_the_proxy()
    {
        var services = new ServiceCollection();
        services.AddLakeWrightCostAttribution();
        services.AddLakeWrightBillingCostAttribution(Configuration(new()
        {
            ["DatabricksBilling:WorkspaceId"] = "workspace-123"
        }));

        var cost = services.Last(descriptor => descriptor.ServiceType == typeof(ICostAttribution));
        var reader = services.Last(descriptor => descriptor.ServiceType == typeof(IBillingUsageReader));

        cost.ImplementationType.ShouldBe(typeof(BillingCostAttribution));
        reader.ImplementationType.ShouldBe(typeof(DatabricksBillingUsageReader));
        reader.Lifetime.ShouldBe(ServiceLifetime.Singleton);
        Validator(services).ShouldNotThrow();
    }

    [Fact]
    public void Billing_cost_rejects_invalid_statement_bounds()
    {
        var services = new ServiceCollection();
        services.AddLakeWrightBillingCostAttribution(Configuration(new()
        {
            ["DatabricksBilling:WorkspaceId"] = "workspace-123",
            ["DatabricksBilling:SubmissionWaitTimeoutSeconds"] = "0",
            ["DatabricksBilling:MaxConcurrentStatements"] = "0",
            ["DatabricksBilling:MaxOutstandingStatements"] = "0"
        }));

        var message = Validator(services).ShouldThrow<OptionsValidationException>().Message;
        message.ShouldContain(nameof(BillingUsageOptions.SubmissionWaitTimeoutSeconds));
        message.ShouldContain(nameof(BillingUsageOptions.MaxConcurrentStatements));
    }

    [Fact]
    public void Billing_cost_rejects_a_submission_timeout_beyond_the_overall_deadline()
    {
        var services = new ServiceCollection();
        services.AddLakeWrightBillingCostAttribution(Configuration(new()
        {
            ["DatabricksBilling:WorkspaceId"] = "workspace-123",
            ["DatabricksBilling:PollingTimeoutSeconds"] = "5",
            ["DatabricksBilling:SubmissionWaitTimeoutSeconds"] = "50"
        }));

        var message = Validator(services).ShouldThrow<OptionsValidationException>().Message;
        message.ShouldContain(nameof(BillingUsageOptions.SubmissionWaitTimeoutSeconds));
        message.ShouldContain(nameof(BillingUsageOptions.PollingTimeoutSeconds));
    }

    private static Action Validator(IServiceCollection services)
    {
        var provider = services.BuildServiceProvider();
        return () => provider.GetRequiredService<IStartupValidator>().Validate();
    }

    private static IConfiguration Configuration(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
}
