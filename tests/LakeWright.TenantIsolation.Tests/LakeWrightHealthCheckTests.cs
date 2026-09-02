using LakeWright.Core.Tenancy;
using LakeWright.Databricks;
using LakeWright.Embedding;
using LakeWright.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LakeWright.TenantIsolation.Tests;

[Trait("Category", "TenantIsolation")]
public sealed class LakeWrightHealthCheckTests
{
    [Fact]
    public async Task Registers_non_billable_readiness_checks_without_the_statement_probe_by_default()
    {
        var services = Services();
        services.AddHealthChecks().AddLakeWrightHealthChecks();
        await using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestContext.Current.CancellationToken);

        result.Status.ShouldBe(HealthStatus.Healthy);
        result.Entries.Keys.OrderBy(name => name, StringComparer.Ordinal).ShouldBe(["lakewright.warehouse-state", "lakewright.workspace-token"]);
    }

    [Fact]
    public async Task Includes_the_billable_statement_probe_only_when_explicitly_enabled()
    {
        var services = Services();
        services.AddSingleton<IReadinessStatementProbe, ReadyStatementProbe>();
        services.AddHealthChecks().AddLakeWrightHealthChecks(options => options.EnableStatementProbe = true);
        await using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestContext.Current.CancellationToken);

        result.Status.ShouldBe(HealthStatus.Healthy);
        result.Entries.Keys.ShouldContain("lakewright.statement");
    }

    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWorkspaceTokenProbe, ReadyBroker>();
        services.AddSingleton<IWarehouseReadinessProbe, ReadyWarehouse>();
        return services;
    }

    private sealed class ReadyBroker : IWorkspaceTokenProbe
    {
        public Task ProbeWorkspaceTokenAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    }

    private sealed class ReadyWarehouse : IWarehouseReadinessProbe
    {
        public Task<string?> GetStateAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("RUNNING");
    }

    private sealed class ReadyStatementProbe : IReadinessStatementProbe
    {
        public Task ProbeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
