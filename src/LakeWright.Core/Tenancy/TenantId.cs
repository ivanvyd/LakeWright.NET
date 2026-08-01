namespace LakeWright.Core.Tenancy;

/// <summary>
/// Identifies a tenant. A distinct type rather than a <see cref="Guid"/> so that a tenant
/// identifier cannot be passed where some other identifier is expected, and so that
/// "which id is this" is answered by the signature instead of the parameter name.
/// </summary>
public readonly record struct TenantId(Guid Value)
{
    public static TenantId New() => new(Guid.CreateVersion7());

    public static TenantId Parse(string value) => new(Guid.Parse(value));

    public override string ToString() => Value.ToString();
}
