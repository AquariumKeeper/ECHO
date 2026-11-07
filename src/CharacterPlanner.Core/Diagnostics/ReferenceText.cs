using System.Globalization;
using System.Text;

namespace CharacterPlanner.Core.Diagnostics;

internal static class ReferenceText
{
    public static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        foreach (var rune in value.EnumerateRunes())
        {
            var escaped = rune.Value switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\a' => "\\a",
                '\b' => "\\b",
                '\f' => "\\f",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                '\v' => "\\v",
                _ => null
            };
            if (escaped is not null) { result.Append(escaped); continue; }
            var category = Rune.GetUnicodeCategory(rune);
            var printable = rune.Value == ' ' || Rune.IsLetterOrDigit(rune) || Rune.IsNumber(rune) || Rune.IsPunctuation(rune) || Rune.IsSymbol(rune) ||
                category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
            if (printable) { result.Append(rune.ToString()); }
            else if (rune.Value < 0x20 || rune.Value == 0x7f) { result.Append("\\x").Append(rune.Value.ToString("x2", CultureInfo.InvariantCulture)); }
            else if (rune.Value <= 0xffff) { result.Append("\\u").Append(rune.Value.ToString("x4", CultureInfo.InvariantCulture)); }
            else { result.Append("\\U").Append(rune.Value.ToString("x8", CultureInfo.InvariantCulture)); }
        }
        return result.Append('"').ToString();
    }

    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    public static string Number(double? value) => Number(value.GetValueOrDefault());

    // Go's %v for float64 uses shortest round-trip digits and scientific notation
    // below 1e-4 or at/above 1e6. .NET's default thresholds and exponent case differ.
    public static string Number(double value)
    {
        if (double.IsNaN(value)) { return "NaN"; }
        if (double.IsPositiveInfinity(value)) { return "+Inf"; }
        if (double.IsNegativeInfinity(value)) { return "-Inf"; }
        var raw = value.ToString("R", CultureInfo.InvariantCulture);
        var negative = raw.StartsWith('-');
        var unsigned = negative ? raw[1..] : raw;
        var parts = unsigned.Split('E');
        var exponent = parts.Length == 2 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        var dot = parts[0].IndexOf('.');
        var position = dot < 0 ? parts[0].Length : dot;
        var allDigits = parts[0].Replace(".", "", StringComparison.Ordinal);
        var digits = allDigits.TrimStart('0');
        if (digits.Length == 0) { return negative ? "-0" : "0"; }
        exponent += position - 1 - (allDigits.Length - digits.Length);
        digits = digits.TrimEnd('0');
        var sign = negative ? "-" : "";
        if (exponent < -4 || exponent >= 6)
        {
            return sign + digits[0] + (digits.Length > 1 ? "." + digits[1..] : "") + "e" + (exponent < 0 ? "-" : "+") +
                Math.Abs(exponent).ToString("D2", CultureInfo.InvariantCulture);
        }
        if (parts.Length == 1) { return raw; }
        if (exponent < 0) { return sign + "0." + new string('0', -exponent - 1) + digits; }
        var padded = digits.PadRight(exponent + 1, '0');
        return sign + (exponent + 1 < padded.Length ? padded.Insert(exponent + 1, ".") : padded);
    }
}
