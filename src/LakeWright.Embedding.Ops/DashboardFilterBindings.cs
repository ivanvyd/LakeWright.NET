using System.Text.Json;
using System.Text.RegularExpressions;
using LakeWright.Core;

namespace LakeWright.Embedding.Ops;

/// <summary>Describes how one portal filter is represented by a named dashboard query parameter.</summary>
public sealed record FilterBinding(string FromField, string ToParameter, FilterBindingDateRole DateRole = FilterBindingDateRole.None);

/// <summary>Identifies the role played by a portal value in a date filter.</summary>
public enum FilterBindingDateRole
{
    None,
    Exact,
    RangeStart,
    RangeEnd,
}

/// <summary>Checks that a portal filter contract matches the published dashboard viewers receive.</summary>
public interface IDashboardFilterBindingValidator
{
    /// <summary>
    /// Validates bindings against a published dashboard. This intentionally requires an
    /// authoritative published-definition reader: the public published-dashboard endpoint returns
    /// revision metadata only and cannot prove the named parameters used by viewers.
    /// </summary>
    Task ValidatePublishedAsync(
        string dashboardId,
        IReadOnlyCollection<FilterBinding> bindings,
        CancellationToken cancellationToken = default);
}

/// <summary>Raised when the portal's filter contract cannot be proven against the published dashboard.</summary>
public sealed class DashboardFilterBindingValidationException(string message) : LakeWrightException(message);

/// <summary>Fail-closed validator for portal filter bindings.</summary>
public sealed class DashboardFilterBindingValidator(
    IDashboardMetadataCatalog catalog,
    IPublishedDashboardDefinitionReader publishedDefinitionReader) : IDashboardFilterBindingValidator
{
    private static readonly Regex NamedParameter = new(@"(?<!:):(?<name>[A-Za-z_][A-Za-z0-9_]*)|\{\{\s*(?<mustache>[A-Za-z_][A-Za-z0-9_]*)\s*\}\}", RegexOptions.CultureInvariant);

    public async Task ValidatePublishedAsync(
        string dashboardId,
        IReadOnlyCollection<FilterBinding> bindings,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dashboardId);
        ArgumentNullException.ThrowIfNull(bindings);
        ValidateContract(bindings);

        // C4 proves the published revision exists through the operations principal before the
        // host-provided reader supplies the serialized artifact that viewers actually receive.
        _ = await catalog.GetPublishedAsync(dashboardId, cancellationToken).ConfigureAwait(false);
        var serialized = await publishedDefinitionReader.ReadAsync(dashboardId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(serialized))
        {
            throw new DashboardFilterBindingValidationException(
                $"Dashboard '{dashboardId}' has no authoritative published definition; filter bindings cannot be verified.");
        }

        var parameters = ReadPublishedParameters(serialized);
        var missing = bindings
            .Select(binding => binding.ToParameter)
            .Where(parameter => !parameters.Contains(parameter))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new DashboardFilterBindingValidationException(
                $"Dashboard '{dashboardId}' does not use the published query parameter(s): {string.Join(", ", missing)}.");
        }
    }

    internal static IReadOnlySet<string> ReadPublishedParameters(string serializedDashboard)
    {
        try
        {
            using var document = JsonDocument.Parse(serializedDashboard);
            if (!document.RootElement.TryGetProperty("datasets", out var datasets) || datasets.ValueKind != JsonValueKind.Array)
            {
                throw new DashboardFilterBindingValidationException("The published dashboard definition has no datasets.");
            }

            var parameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dataset in datasets.EnumerateArray())
            {
                if (dataset.ValueKind != JsonValueKind.Object)
                {
                    throw new DashboardFilterBindingValidationException("The published dashboard definition contains a non-object dataset.");
                }

                AddParameters(ReadQuery(dataset), parameters);
            }

            return parameters;
        }
        catch (JsonException exception)
        {
            throw new DashboardFilterBindingValidationException($"The published dashboard definition is invalid JSON: {exception.Message}");
        }
    }

    private static void ValidateContract(IReadOnlyCollection<FilterBinding> bindings)
    {
        var declared = new HashSet<(string FromField, FilterBindingDateRole DateRole)>();
        foreach (var binding in bindings)
        {
            ArgumentNullException.ThrowIfNull(binding);
            if (string.IsNullOrWhiteSpace(binding.FromField) || string.IsNullOrWhiteSpace(binding.ToParameter))
            {
                throw new DashboardFilterBindingValidationException("Each filter binding needs both FromField and ToParameter.");
            }

            if (!Enum.IsDefined(binding.DateRole))
            {
                throw new DashboardFilterBindingValidationException($"'{binding.DateRole}' is not a recognized date role.");
            }

            if (!declared.Add((binding.FromField, binding.DateRole)))
            {
                throw new DashboardFilterBindingValidationException(
                    $"The filter field '{binding.FromField}' declares '{binding.DateRole}' more than once.");
            }
        }
    }

    private static string ReadQuery(JsonElement dataset)
    {
        if (dataset.TryGetProperty("query", out var query) && query.ValueKind == JsonValueKind.String)
        {
            return query.GetString()!;
        }

        if (dataset.TryGetProperty("queryLines", out var lines) && lines.ValueKind == JsonValueKind.Array)
        {
            return string.Join(Environment.NewLine, lines.EnumerateArray()
                .Where(line => line.ValueKind == JsonValueKind.String)
                .Select(line => line.GetString()));
        }

        throw new DashboardFilterBindingValidationException("A published dashboard dataset has neither query nor queryLines.");
    }

    private static void AddParameters(string query, HashSet<string> parameters)
    {
        foreach (Match match in NamedParameter.Matches(query))
        {
            parameters.Add(match.Groups["name"].Success ? match.Groups["name"].Value : match.Groups["mustache"].Value);
        }
    }
}
