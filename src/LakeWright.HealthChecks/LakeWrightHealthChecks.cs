using LakeWright.Databricks;
using LakeWright.Embedding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LakeWright.HealthChecks;

/// <summary>Optional billable statement probe supplied by a host that can resolve a safe tenant context.</summary>
public interface IReadinessStatementProbe
{
    /// <summary>Runs the host-approved readiness statement. This may wake a warehouse.</summary>
    Task ProbeAsync(CancellationToken cancellationToken = default);
}

/// <summary>Controls the opt-in health checks. SQL execution remains off by default.</summary>
public sealed class LakeWrightHealthCheckOptions
{
    /// <summary>Whether to add the host-provided, billable statement probe.</summary>
    public bool EnableStatementProbe { get; set; }
}

/// <summary>Registers LakeWright readiness checks after embedding and Databricks have been registered.</summary>
public static class LakeWrightHealthCheckServiceCollectionExtensions
{
    /// <summary>Adds cached OAuth-leg, non-billable warehouse-state, and optional SQL readiness checks.</summary>
    public static IHealthChecksBuilder AddLakeWrightHealthChecks(
        this IHealthChecksBuilder builder,
        Action<LakeWrightHealthCheckOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new LakeWrightHealthCheckOptions();
        configure?.Invoke(options);
        builder.Services.AddLogging();
        builder.AddCheck<WorkspaceTokenHealthCheck>("lakewright.workspace-token", tags: ["ready"]);
        builder.AddCheck<WarehouseStateHealthCheck>("lakewright.warehouse-state", tags: ["ready"]);
        if (options.EnableStatementProbe)
        {
            builder.AddCheck<StatementHealthCheck>("lakewright.statement", tags: ["ready", "billable"]);
        }
        return builder;
    }
}

internal sealed class WorkspaceTokenHealthCheck(IWorkspaceTokenProbe broker) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await broker.ProbeWorkspaceTokenAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy("Workspace OAuth token is available.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Workspace OAuth token could not be acquired.", exception);
        }
    }
}

internal sealed class WarehouseStateHealthCheck(IWarehouseReadinessProbe warehouse) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var state = await warehouse.GetStateAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy("Warehouse state is readable.", data: new Dictionary<string, object> { ["state"] = state ?? "unknown" });
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Warehouse state could not be read.", exception);
        }
    }
}

internal sealed class StatementHealthCheck(IReadinessStatementProbe probe) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await probe.ProbeAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy("Configured readiness statement succeeded.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Configured readiness statement failed.", exception);
        }
    }
}
