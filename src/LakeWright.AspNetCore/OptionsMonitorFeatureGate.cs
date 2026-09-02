using LakeWright.Core.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace LakeWright.AspNetCore;

/// <summary>Configuration shape for the opt-in runtime feature gate.</summary>
public sealed class FeatureGateOptions
{
    public const string SectionName = "LakeWright:Features";

    /// <summary>Explicit feature overrides. Missing names remain enabled.</summary>
    public Dictionary<string, bool> Enabled { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Reads runtime feature state from the current options snapshot.</summary>
public sealed class OptionsMonitorFeatureGate(IOptionsMonitor<FeatureGateOptions> options) : ILakeWrightFeatureGate
{
    public bool IsEnabled(string feature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feature);
        return !options.CurrentValue.Enabled.TryGetValue(feature, out var enabled) || enabled;
    }
}

/// <summary>Registers a configuration-reloadable LakeWright runtime feature gate.</summary>
public static class FeatureGateServiceCollectionExtensions
{
    /// <summary>
    /// Replaces the default always-on gate with one bound to <c>LakeWright:Features:Enabled</c>.
    /// </summary>
    public static IServiceCollection AddLakeWrightFeatureGate(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<FeatureGateOptions>()
            .Bind(configuration.GetSection(FeatureGateOptions.SectionName));
        services.Replace(ServiceDescriptor.Singleton<ILakeWrightFeatureGate, OptionsMonitorFeatureGate>());
        return services;
    }
}
