using LakeWright.Core.Jobs;
using LakeWright.Core.Tenancy;
using LakeWright.Databricks;
using LakeWright.Multitenancy;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// The tenancy tier must not depend on the Databricks integration.
/// </summary>
/// <remarks>
/// <c>AddLakeWright</c> tells adopters that tenancy, authorization and the operations API run
/// against PostgreSQL alone. That was true of the DI registrations and false of the build: a
/// project reference dragged the Databricks client in as a transitive dependency of anyone who
/// wanted only tenant resolution.
///
/// A reference is one line to add and nothing else notices, so the claim needs a test rather than a
/// convention.
/// </remarks>
public class DependencyDirectionTests
{
    [Fact]
    public void Multitenancy_does_not_reference_the_databricks_integration()
    {
        // Arrange
        var tenancy = typeof(OperationStore).Assembly;

        // Act
        var referenced = tenancy.GetReferencedAssemblies().Select(a => a.Name).ToArray();

        // Assert
        referenced.ShouldNotContain(typeof(DatabricksJobSubmitter).Assembly.GetName().Name);
    }

    [Fact]
    public void The_job_abstraction_lives_with_the_tenancy_contracts()
    {
        // Arrange — the reason the reference above can be dropped. If IJobSubmitter moved back
        // into the integration project, the worker would need the reference again.
        var expected = typeof(TenantId).Assembly;

        // Act
        var actual = typeof(IJobSubmitter).Assembly;

        // Assert
        actual.ShouldBe(expected);
    }
}
