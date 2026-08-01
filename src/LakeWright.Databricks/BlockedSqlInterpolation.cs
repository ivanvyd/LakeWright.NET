using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LakeWright.Databricks;

/// <summary>
/// Exists so that passing an interpolated string where SQL is expected fails to compile.
/// </summary>
/// <remarks>
/// The compiler prefers an interpolated string handler parameter over <see cref="string"/> when
/// the argument is an interpolated string literal. Marking the overload that takes this type
/// <c>Obsolete(error: true)</c> therefore turns
/// <c>Create(ctx, $"... {value} ...")</c> into a build failure, while a plain constant still binds
/// to the <see cref="string"/> overload.
///
/// An earlier attempt used a <see cref="FormattableString"/> overload on the theory that EF Core
/// works that way. It does not, and neither did this: overload resolution prefers
/// <see cref="string"/> for an interpolated literal, so the guard compiled and did nothing. EF Core
/// separates <c>FromSqlRaw</c> from <c>FromSqlInterpolated</c> by name, not by overload. The
/// handler is the mechanism that actually binds.
/// </remarks>
[InterpolatedStringHandler]
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct BlockedSqlInterpolation
{
    public BlockedSqlInterpolation(int literalLength, int formattedCount) =>
        throw new InvalidOperationException(
            "Interpolated SQL is not supported. Pass a constant statement and supply values as " +
            "StatementParameter arguments.");

    // The interpolated string handler pattern requires these as instance methods with these exact
    // signatures. They are never reached: the constructor throws, and the overload taking this
    // type does not compile. CA1822 does not know about the pattern.
#pragma warning disable CA1822
    public void AppendLiteral(string value) { }

    public void AppendFormatted<T>(T value) { }
#pragma warning restore CA1822
}
