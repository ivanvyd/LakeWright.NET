using Lakewright.AspNetCore;
using Lakewright.Databricks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Lakewright.TenantIsolation.Tests;

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
        services.AddLakewright(Configuration(new()
        {
            ["ConnectionStrings:Lakewright"] = "Host=localhost;Database=unused",
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
        services.AddLakewrightDatabricks(Configuration(new()
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
        services.AddLakewrightDatabricks(Configuration(new()
        {
            ["Databricks:WorkspaceUrl"] = "https://adb-1.azuredatabricks.net",
            ["Databricks:WarehouseId"] = "abc123"
        }));

        // Act
        var validate = Validator(services);

        // Assert
        validate.ShouldNotThrow();
    }

    private static Action Validator(IServiceCollection services)
    {
        var provider = services.BuildServiceProvider();
        return () => provider.GetRequiredService<IStartupValidator>().Validate();
    }

    private static IConfiguration Configuration(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
}
