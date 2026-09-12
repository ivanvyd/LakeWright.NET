using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;

internal static class ConsumerFloor
{
    private static readonly string[] PersistenceMarkers = ["entityframework", "npgsql", "postgresql"];

    public static async Task<int> VerifyAsync(string source, string version)
    {
        var candidates = CandidatePackages.Read(Path.GetFullPath(source), version);
        var temporary = Path.Combine(Path.GetTempPath(), "lakewright-floor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var snapshot = Directory.CreateDirectory(Path.Combine(temporary, "candidates")).FullName;
            var cache = Path.Combine(temporary, "packages");
            foreach (var candidate in candidates.Values)
            {
                File.Copy(candidate.Path, Path.Combine(snapshot, $"{candidate.Id}.{candidate.Version}.nupkg"));
            }
            await File.WriteAllTextAsync(Path.Combine(temporary, "NuGet.Config"), $"""
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="candidate" value="{SecurityElement.Escape(snapshot)}" />
                    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                  </packageSources>
                  <fallbackPackageFolders><clear /></fallbackPackageFolders>
                  <packageSourceMapping>
                    <clear />
                    <packageSource key="candidate"><package pattern="LakeWright.*" /></packageSource>
                    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
                  </packageSourceMapping>
                </configuration>
                """).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(temporary, "Floor.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
                    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                    <RestoreFallbackFolders></RestoreFallbackFolders>
                    <DisableImplicitNuGetFallbackFolder>true</DisableImplicitNuGetFallbackFolder>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="LakeWright.Embedding" Version="[{version}]" />
                    <PackageReference Include="LakeWright.Databricks" Version="[{version}]" />
                    <PackageReference Include="Microsoft.Extensions.Configuration" Version="[8.0.0]" />
                    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="[8.0.1]" />
                    <PackageReference Include="Microsoft.Extensions.Http" Version="[8.0.1]" />
                    <PackageReference Include="Microsoft.Extensions.Options" Version="[8.0.2]" />
                  </ItemGroup>
                </Project>
                """).ConfigureAwait(false);
            using var fixture = typeof(ConsumerFloor).Assembly.GetManifestResourceStream("ConsumerFloor.Program.cs")
                ?? throw new InvalidOperationException("The packaged consumer fixture is missing.");
            using var fixtureReader = new StreamReader(fixture);
            await File.WriteAllTextAsync(Path.Combine(temporary, "Program.cs"), await fixtureReader.ReadToEndAsync().ConfigureAwait(false)).ConfigureAwait(false);
            var restore = await RunDotnetAsync(temporary, "restore", "Floor.csproj", "--configfile", "NuGet.Config", "--packages", cache, "--no-http-cache").ConfigureAwait(false);
            if (restore.ExitCode != 0)
            {
                Console.Error.Write(restore.Output);
                return 1;
            }
            await VerifyResolvedAsync(temporary, cache, candidates).ConfigureAwait(false);
            var run = await RunDotnetAsync(temporary, "run", "--project", "Floor.csproj", "-c", "Release", "--no-restore").ConfigureAwait(false);
            if (run.ExitCode != 0)
            {
                Console.Error.WriteLine($"Consumer fixture failed (exit {run.ExitCode}).");
                Console.Error.Write(run.Output);
                return 1;
            }
            foreach (var package in candidates.Values.OrderBy(package => package.Id, StringComparer.Ordinal))
            {
                Console.WriteLine($"Verified candidate {package.Id} {package.Version} SHA512={package.Hash}");
            }
            Console.WriteLine("Consumer floor passed against the supplied candidate bytes.");
            return 0;
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    private static async Task VerifyResolvedAsync(string temporary, string cache, IReadOnlyDictionary<string, CandidatePackage> candidates)
    {
        using var assets = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(temporary, "obj", "project.assets.json")).ConfigureAwait(false));
        var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in assets.RootElement.GetProperty("libraries").EnumerateObject())
        {
            var identity = library.Name.Split('/');
            var id = identity[0];
            if (PersistenceMarkers.Any(marker => id.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Consumer floor acquired a persistence dependency: {id}.");
            }
            if (!id.StartsWith("LakeWright.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!candidates.TryGetValue(id, out var candidate) || identity.Length != 2
                || !string.Equals(candidate.Version, identity[1], StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Restore resolved an unexpected LakeWright package identity.");
            }
            var archive = Path.Combine(cache, id.ToLowerInvariant(), identity[1].ToLowerInvariant(), $"{id.ToLowerInvariant()}.{identity[1].ToLowerInvariant()}.nupkg");
            using var stream = File.OpenRead(archive);
            if (Convert.ToBase64String(await SHA512.HashDataAsync(stream).ConfigureAwait(false)) != candidate.Hash)
            {
                throw new InvalidOperationException($"Restored bytes differ from the supplied candidate: {id}.");
            }
            resolved.Add(id);
        }
        if (candidates.Keys.Any(id => !resolved.Contains(id)))
        {
            throw new InvalidOperationException("A required candidate was not present in the consumer graph.");
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
        startInfo.Environment["NUGET_PACKAGES"] = Path.Combine(directory, "packages");
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
}
