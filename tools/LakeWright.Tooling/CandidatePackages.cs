using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;

internal sealed record CandidatePackage(string Id, string Version, string Path, string Hash, string[] Dependencies);

internal static partial class CandidatePackages
{
    public static IReadOnlyDictionary<string, CandidatePackage> Read(string source, string version)
    {
        if (!ExactVersion().IsMatch(version))
        {
            throw new ArgumentException("Specify an exact three-part package version, optionally with a prerelease suffix.", nameof(version));
        }
        if (!Directory.Exists(source))
        {
            throw new ArgumentException("The candidate package directory does not exist.", nameof(source));
        }
        var available = new Dictionary<string, CandidatePackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(source, "*.nupkg"))
        {
            using var archive = ZipFile.OpenRead(path);
            var manifest = archive.Entries.SingleOrDefault(entry => entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("A candidate package omitted its nuspec.");
            using var stream = manifest.Open();
            var document = XDocument.Load(stream);
            var metadata = document.Root?.Elements().SingleOrDefault(element => element.Name.LocalName == "metadata")
                ?? throw new InvalidOperationException("A candidate nuspec omitted its metadata.");
            var id = metadata.Elements().SingleOrDefault(element => element.Name.LocalName == "id")?.Value
                ?? throw new InvalidOperationException("A candidate nuspec omitted its id.");
            if (id.Length > 100 || !PackageId().IsMatch(id))
            {
                throw new InvalidOperationException("A candidate nuspec contains an invalid package id.");
            }
            var packageVersion = metadata.Elements().SingleOrDefault(element => element.Name.LocalName == "version")?.Value;
            if (!id.StartsWith("LakeWright.", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(packageVersion, version, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var dependencies = metadata.Descendants().Where(element => element.Name.LocalName == "dependency")
                .Select(element => element.Attribute("id")?.Value ?? throw new InvalidOperationException("A dependency omitted its id."))
                .Where(dependency => dependency.StartsWith("LakeWright.", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            using var bytes = File.OpenRead(path);
            var candidate = new CandidatePackage(id, version, path, Convert.ToBase64String(SHA512.HashData(bytes)), dependencies);
            if (!available.TryAdd(id, candidate))
            {
                throw new InvalidOperationException($"Duplicate candidate package: {id} {version}.");
            }
        }

        var selected = new Dictionary<string, CandidatePackage>(StringComparer.OrdinalIgnoreCase);
        var remaining = new Queue<string>(["LakeWright.Embedding", "LakeWright.Databricks"]);
        while (remaining.TryDequeue(out var id))
        {
            if (selected.ContainsKey(id))
            {
                continue;
            }
            if (!available.TryGetValue(id, out var package))
            {
                throw new InvalidOperationException($"Candidate source is missing {id} {version}. Cached or public packages cannot satisfy this check.");
            }
            selected.Add(id, package);
            foreach (var dependency in package.Dependencies)
            {
                remaining.Enqueue(dependency);
            }
        }
        return selected;
    }

    [GeneratedRegex(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExactVersion();

    [GeneratedRegex(@"\A[A-Za-z0-9_.-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex PackageId();
}
