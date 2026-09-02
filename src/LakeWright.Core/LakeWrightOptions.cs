using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace LakeWright.Core;

/// <summary>Registers one startup-time summary for all configured LakeWright options.</summary>
public static class LakeWrightOptions
{
    /// <summary>
    /// Adds <typeparamref name="TOptions"/> to the one LakeWright startup validation summary.
    /// Callers register their options normally; all failures are reported together when the host
    /// starts instead of surfacing as unrelated hosted-service failures.
    /// </summary>
    public static IServiceCollection ValidateOnStart<TOptions>(IServiceCollection services)
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILakeWrightOptionsValidator, OptionsStartupValidator<TOptions>>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, LakeWrightOptionsStartupService>());
        return services;
    }
}

internal interface ILakeWrightOptionsValidator
{
    IEnumerable<string> Validate();
}

internal sealed class OptionsStartupValidator<TOptions>(IOptions<TOptions> options) : ILakeWrightOptionsValidator
    where TOptions : class
{
    public IEnumerable<string> Validate()
    {
        try
        {
            _ = options.Value;
            return [];
        }
        catch (OptionsValidationException exception)
        {
            return exception.Failures.Select(static failure => failure.Trim());
        }
    }
}

internal sealed class LakeWrightOptionsStartupService(IEnumerable<ILakeWrightOptionsValidator> validators) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var failures = validators.SelectMany(static validator => validator.Validate())
            .Where(static failure => !string.IsNullOrWhiteSpace(failure))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (failures.Length > 0)
        {
            throw new OptionsValidationException("LakeWright", typeof(LakeWrightOptions), failures);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
