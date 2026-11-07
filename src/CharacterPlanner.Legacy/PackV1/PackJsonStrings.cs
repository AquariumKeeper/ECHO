using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Legacy.PackV1;

internal static class PackJsonStrings
{
    public static string? Decode(JsonElement value)
    {
        try { return value.GetString(); }
        catch (InvalidOperationException) when (value.ValueKind == JsonValueKind.String)
        { return Repair(Encoding.UTF8.GetBytes(value.GetRawText()[1..^1]), true); }
    }

    public static string? Decode(ref Utf8JsonReader reader)
    {
        try { return reader.GetString(); }
        catch (InvalidOperationException) when (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName)
        { return Repair(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan, reader.ValueIsEscaped); }
    }

    public static string Name(JsonProperty property)
    {
        try { return property.Name; }
        catch (InvalidOperationException)
        {
            var bytes = Encoding.UTF8.GetBytes("{" + property + "}");
            var reader = new Utf8JsonReader(bytes);
            reader.Read();
            reader.Read();
            return Decode(ref reader)!;
        }
    }

    private static string Repair(ReadOnlySpan<byte> bytes, bool escaped)
    {
        var value = Encoding.UTF8.GetString(bytes);
        if (escaped) { value = Regex.Unescape(value); }
        // The reference substitutes U+FFFD for invalid UTF-8 and unpaired UTF-16
        // escapes. Valid surrogate pairs round-trip unchanged through UTF-8.
        return Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(value));
    }
}
