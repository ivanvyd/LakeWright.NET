using System.Text.Json;
using LakeWright.Embedding;

return await LakeWrightTool.RunAsync(args);

internal static class LakeWrightTool
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            return args.FirstOrDefault() switch
            {
                "inspect-dashboard" => await InspectDashboardAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
                "verify-floor" => await VerifyFloorAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
                _ => Usage(),
            };
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or HttpRequestException or JsonException or IOException or System.Xml.XmlException)
        {
            Console.Error.WriteLine($"lakewright: {exception.Message}");
            return 2;
        }
    }

    private static async Task<int> InspectDashboardAsync(string[] args)
    {
        if (args.Length != 1)
        {
            return Usage();
        }

        var serialized = File.Exists(args[0])
            ? await File.ReadAllTextAsync(args[0]).ConfigureAwait(false)
            : await ReadDashboardAsync(args[0]).ConfigureAwait(false);
        var verdict = DashboardMarkerLint.InspectDashboard(serialized);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Scope = "Executable marker presence only; this does not prove tenant isolation or authorize publication.",
            verdict.Passed,
            verdict.Reason,
            Datasets = verdict.Datasets.Select(dataset => new
            {
                dataset.DatasetIndex,
                dataset.Name,
                dataset.Verdict.Passed,
                dataset.Verdict.Reason,
                Hits = dataset.Verdict.Hits.Select(hit => hit.Offset),
            }),
        }, IndentedJson));
        return verdict.Passed ? 0 : 1;
    }

    private static async Task<string> ReadDashboardAsync(string dashboardId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dashboardId);
        var host = Environment.GetEnvironmentVariable("DATABRICKS_HOST")?.TrimEnd('/');
        var token = Environment.GetEnvironmentVariable("DATABRICKS_TOKEN");
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "A dashboard id requires DATABRICKS_HOST and DATABRICKS_TOKEN. Pass a local .lvdash.json file for an offline inspection.");
        }

        using var client = new HttpClient { BaseAddress = new Uri(host + "/") };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await client.GetAsync($"api/2.0/lakeview/dashboards/{Uri.EscapeDataString(dashboardId)}").ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStreamAsync().ConfigureAwait(false));
        return payload.RootElement.TryGetProperty("serialized_dashboard", out var serialized)
            && serialized.ValueKind == JsonValueKind.String
            ? serialized.GetString()!
            : throw new InvalidOperationException("The dashboard response omitted serialized_dashboard.");
    }

    private static async Task<int> VerifyFloorAsync(string[] args)
    {
        if (args.Length != 2)
        {
            return Usage();
        }

        return await ConsumerFloor.VerifyAsync(args[0], args[1]).ConfigureAwait(false);
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: lakewright inspect-dashboard <file|dashboard-id> | verify-floor <package-source> <version>");
        return 2;
    }
}
