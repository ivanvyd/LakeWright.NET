using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace LakeWright.Databricks.RawData;

internal static class ExactDecimalParameter
{
    public static StatementParameter Create(RawDataField field, string name, string value)
    {
        if (field.Precision is not { } precision || field.Scale is not { } scale)
        {
            throw new ValidationException("Decimal fields require precision and scale.");
        }
        var unsigned = value.AsSpan();
        var negative = unsigned.StartsWith("-", StringComparison.Ordinal);
        if (negative || unsigned.StartsWith("+", StringComparison.Ordinal))
        {
            unsigned = unsigned[1..];
        }
        var point = unsigned.IndexOf('.');
        var integer = point < 0 ? unsigned : unsigned[..point];
        var fraction = point < 0 ? ReadOnlySpan<char>.Empty : unsigned[(point + 1)..];
        if (integer.IsEmpty || (point >= 0 && fraction.IsEmpty)
            || integer.ContainsAnyExceptInRange('0', '9') || fraction.ContainsAnyExceptInRange('0', '9'))
        {
            throw new ValidationException($"Field '{field.Name}' requires an invariant fixed-point decimal without separators or an exponent.");
        }
        integer = integer.TrimStart('0');
        fraction = fraction.TrimEnd('0');
        if (integer.Length > precision - scale || fraction.Length > scale)
        {
            throw new ValidationException($"Value exceeds the exact precision or scale for field '{field.Name}'; rounding is not performed.");
        }
        var whole = integer.IsEmpty ? "0" : integer.ToString();
        var normalized = (negative && (!integer.IsEmpty || !fraction.IsEmpty) ? "-" : "") + whole
            + (fraction.IsEmpty ? "" : "." + fraction.ToString());
        return new StatementParameter(name, normalized, string.Create(CultureInfo.InvariantCulture, $"DECIMAL({precision},{scale})"));
    }
}
