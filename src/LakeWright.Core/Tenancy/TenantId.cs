namespace LakeWright.Core.Tenancy;

/// <summary>
/// Identifies a tenant. A distinct type rather than a <see cref="Guid"/> so that a tenant
/// identifier cannot be passed where some other identifier is expected, and so that
/// "which id is this" is answered by the signature instead of the parameter name.
/// </summary>
public readonly record struct TenantId(Guid Value)
{
    public static TenantId New() => new(NewGuid());

    private static Guid NewGuid() =>
#if NET9_0_OR_GREATER
        Guid.CreateVersion7();
#else
        // net8.0 fallback. v7 (timestamp-ordered) is .NET 9+; the multi-target settles for v4
        // there. TenantId is a value object, not a sort key, so the loss of monotonicity
        // costs nothing — and a v4 in a net8.0 test still round-trips through the same parse.
        Guid.NewGuid();
#endif

    public static TenantId Parse(string value) => new(Guid.Parse(value));

    public override string ToString() => Value.ToString();
}
