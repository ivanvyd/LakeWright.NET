using Microsoft.Azure.Databricks.Client;
using Microsoft.Extensions.Options;

namespace LakeWright.Databricks;

/// <summary>Reads the configured warehouse's state without starting it or submitting SQL.</summary>
public interface IWarehouseReadinessProbe
{
    /// <summary>Reads the configured warehouse metadata to prove the credential and warehouse are reachable.</summary>
    Task<string?> GetStateAsync(CancellationToken cancellationToken = default);
}

internal sealed class DatabricksWarehouseReadinessProbe(
    DatabricksClient client,
    IOptions<DatabricksOptions> options) : IWarehouseReadinessProbe
{
    public async Task<string?> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var warehouse = await client.SQL.Warehouse.Get(options.Value.WarehouseId, cancellationToken).ConfigureAwait(false);
        return warehouse.State.ToString();
    }
}
