using System.Diagnostics;
using System.Security;
using System.Text.Json;
using LakeWright.Embedding;

return await LakeWrightTool.RunAsync(args);

internal static class LakeWrightTool
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private static readonly string[] PersistenceMarkers = ["entityframework", "npgsql", "postgresql"];

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
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or HttpRequestException or JsonException)
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
        var verdict = DashboardPublishGate.InspectDashboard(serialized);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
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

        var source = Path.GetFullPath(args[0]);
        if (!Directory.Exists(source))
        {
            throw new ArgumentException($"Package source does not exist: {source}", nameof(args));
        }
        var version = args[1];
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new ArgumentException("Package version is required.", nameof(args));
        }
        var packageVersion = SecurityElement.Escape(version) ?? throw new ArgumentException("Package version is invalid.", nameof(args));

        var temporary = Path.Combine(Path.GetTempPath(), "lakewright-floor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(temporary, "Floor.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="LakeWright.Embedding" Version="{packageVersion}" />
                    <PackageReference Include="LakeWright.Databricks" Version="{packageVersion}" />
                    <PackageReference Include="Microsoft.Extensions.Configuration" Version="[8.0.0]" />
                    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="[8.0.1]" />
                    <PackageReference Include="Microsoft.Extensions.Http" Version="[8.0.1]" />
                    <PackageReference Include="Microsoft.Extensions.Options" Version="[8.0.2]" />
                  </ItemGroup>
                </Project>
                """).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(temporary, "Program.cs"), "using System; using LakeWright.Databricks; using LakeWright.Embedding; Console.WriteLine(typeof(IStatementExecutor).Name + typeof(IDashboardTokenBroker).Name);").ConfigureAwait(false);
            var build = await RunDotnetAsync(temporary, "build", "Floor.csproj", "-c", "Release", $"-p:RestoreAdditionalProjectSources={source}").ConfigureAwait(false);
            if (build.ExitCode != 0)
            {
                Console.Error.Write(build.Output);
                return 1;
            }

            var assets = await File.ReadAllTextAsync(Path.Combine(temporary, "obj", "project.assets.json")).ConfigureAwait(false);
            var persistence = PersistenceMarkers
                .Where(term => assets.Contains(term, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (persistence.Length > 0)
            {
                Console.Error.WriteLine($"Consumer floor acquired persistence dependencies: {string.Join(", ", persistence)}");
                return 1;
            }

            Console.WriteLine("Consumer floor passed.");
            return 0;
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunDotnetAsync(string directory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start dotnet.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        return (process.ExitCode, await standardOutput.ConfigureAwait(false) + await standardError.ConfigureAwait(false));
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: lakewright inspect-dashboard <file|dashboard-id> | verify-floor <package-source> <version>");
        return 2;
    }
}
