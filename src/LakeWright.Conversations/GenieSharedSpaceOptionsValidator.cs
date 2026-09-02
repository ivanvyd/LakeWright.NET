using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LakeWright.Conversations;

internal sealed class GenieSharedSpaceOptionsValidator(ILoggerFactory? loggerFactory) : IValidateOptions<GenieOptions>
{
    private static readonly Action<ILogger, Exception?> SharedSpaceWarning = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(1, "SharedSpaceEnabled"),
        "Genie shared-space mode is enabled. It is for internal staff-only tools and provides no tenant isolation.");

    private readonly ILogger? _logger = loggerFactory?.CreateLogger<GenieSharedSpaceOptionsValidator>();

    public ValidateOptionsResult Validate(string? name, GenieOptions options)
    {
        var hasSharedSpace = !string.IsNullOrWhiteSpace(options.SharedSpaceId);
        if (hasSharedSpace && !options.AcknowledgeNoTenantIsolation)
        {
            return ValidateOptionsResult.Fail(
                "Genie:SharedSpaceId requires Genie:AcknowledgeNoTenantIsolation=true because shared mode is never tenant-isolated.");
        }

        if (!hasSharedSpace && options.AcknowledgeNoTenantIsolation)
        {
            return ValidateOptionsResult.Fail(
                "Genie:AcknowledgeNoTenantIsolation requires Genie:SharedSpaceId.");
        }

        if (hasSharedSpace)
        {
            if (_logger is not null)
            {
                SharedSpaceWarning(_logger, null);
            }
        }

        return ValidateOptionsResult.Success;
    }
}
