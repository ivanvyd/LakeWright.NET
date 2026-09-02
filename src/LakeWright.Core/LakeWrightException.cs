namespace LakeWright.Core;

/// <summary>Base class for failures that an adopter can safely map without parsing messages.</summary>
public abstract class LakeWrightException : Exception
{
    protected LakeWrightException(string message)
        : base(message)
    {
    }

    protected LakeWrightException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
