namespace LakeWright.Core.Features;

/// <summary>Controls whether a LakeWright capability may perform work at runtime.</summary>
public interface ILakeWrightFeatureGate
{
    /// <summary>Returns whether <paramref name="feature"/> is currently enabled.</summary>
    bool IsEnabled(string feature);
}

/// <summary>Stable feature names consulted by LakeWright packages.</summary>
public static class LakeWrightFeatures
{
    public const string Embedding = "embedding";
    public const string Statements = "statements";
    public const string Operations = "operations";
    public const string Conversations = "conversations";
}

/// <summary>Default gate that leaves every capability enabled.</summary>
public sealed class AlwaysOnFeatureGate : ILakeWrightFeatureGate
{
    public bool IsEnabled(string feature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feature);
        return true;
    }
}

/// <summary>Thrown before a disabled LakeWright capability makes an external call.</summary>
public sealed class FeatureDisabledException(string feature)
    : InvalidOperationException($"LakeWright feature '{feature}' is disabled.")
{
    public string Feature { get; } = feature;
}

/// <summary>Helpers for fail-closed feature checks at LakeWright's external boundaries.</summary>
public static class LakeWrightFeatureGateExtensions
{
    public static void EnsureEnabled(this ILakeWrightFeatureGate gate, string feature)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentException.ThrowIfNullOrWhiteSpace(feature);
        if (!gate.IsEnabled(feature))
        {
            throw new FeatureDisabledException(feature);
        }
    }
}
