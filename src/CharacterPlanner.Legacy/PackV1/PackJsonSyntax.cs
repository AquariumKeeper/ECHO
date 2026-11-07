using System.Globalization;
using System.Text.Json;

using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Legacy.PackV1;

// System.Text.Json remains the parser. This scanner runs only after a syntax
// failure to preserve the reference's public diagnostic wording, without relying
// on the framework's localized exception messages or line/column conventions.
internal static class PackJsonSyntax
{
    private enum State { Value, ObjectStart, ObjectKey, Colon, ObjectEnd, ArrayStart, ArrayEnd, End }

    public static string? Describe(ReadOnlySpan<byte> input)
    {
        var containers = new Stack<bool>(); // true = object; false = array
        var state = State.Value;
        var i = 0;
        State Completed() => containers.Count == 0 ? State.End : containers.Peek() ? State.ObjectEnd : State.ArrayEnd;
        try
        {
            while (true)
            {
                while (i < input.Length && input[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') { i++; }
                if (i == input.Length) { return state == State.End ? null : "unexpected end of JSON input"; }
                var b = input[i];
                switch (state)
                {
                    case State.End: return Invalid(b, "after top-level value");
                    case State.ObjectStart:
                    case State.ObjectKey:
                        if (b == '}' && state == State.ObjectStart) { containers.Pop(); i++; state = Completed(); break; }
                        if (b != '"') { return Invalid(b, "looking for beginning of object key string"); }
                        ReadString(input, ref i);
                        state = State.Colon;
                        break;
                    case State.Colon:
                        if (b != ':') { return Invalid(b, "after object key"); }
                        i++;
                        state = State.Value;
                        break;
                    case State.ObjectEnd:
                        if (b == ',') { i++; state = State.ObjectKey; }
                        else if (b == '}') { containers.Pop(); i++; state = Completed(); }
                        else { return Invalid(b, "after object key:value pair"); }
                        break;
                    case State.ArrayEnd:
                        if (b == ',') { i++; state = State.Value; }
                        else if (b == ']') { containers.Pop(); i++; state = Completed(); }
                        else { return Invalid(b, "after array element"); }
                        break;
                    case State.ArrayStart when b == ']':
                        containers.Pop(); i++; state = Completed();
                        break;
                    default:
                        if (b is (byte)'{' or (byte)'[')
                        {
                            containers.Push(b == '{');
                            if (containers.Count > 10000) { return "exceeded max depth"; }
                            i++;
                            state = b == '{' ? State.ObjectStart : State.ArrayStart;
                        }
                        else if (b == '"') { ReadString(input, ref i); state = Completed(); }
                        else if (b == '-' || Digit(b)) { ReadNumber(input, ref i); state = Completed(); }
                        else if (b is (byte)'t' or (byte)'f' or (byte)'n')
                        {
                            var literal = b == 't' ? "true" : b == 'f' ? "false" : "null";
                            foreach (var expected in literal)
                            {
                                if (i == input.Length) { return "unexpected end of JSON input"; }
                                if (input[i] != expected) { return Invalid(input[i], $"in literal {literal} (expecting '{expected}')"); }
                                i++;
                            }
                            state = Completed();
                        }
                        else { return Invalid(b, "looking for beginning of value"); }
                        break;
                }
            }
        }
        catch (JsonException exception) { return exception.Message; }
    }

    private static bool Digit(byte value) => value is >= (byte)'0' and <= (byte)'9';
    private static string Invalid(byte value, string context) => $"invalid character {QuoteByte(value)} {context}";
    private static string QuoteByte(byte value) => value switch
    {
        (byte)'\n' => "'\\n'",
        (byte)'\r' => "'\\r'",
        (byte)'\t' => "'\\t'",
        (byte)'\b' => "'\\b'",
        (byte)'\f' => "'\\f'",
        (byte)'\\' => "'\\\\'",
        (byte)'\'' => "'\\\''",
        < 0x20 => "'\\x" + value.ToString("x2", CultureInfo.InvariantCulture) + "'",
        _ => "'" + (char)value + "'"
    };

    private static void ReadString(ReadOnlySpan<byte> input, ref int i)
    {
        i++;
        while (i < input.Length)
        {
            var b = input[i++];
            if (b == '"') { return; }
            if (b < 0x20) { throw new JsonException(Invalid(b, "in string")); }
            if (b != '\\') { continue; }
            if (i == input.Length) { break; }
            var escaped = input[i++];
            if (escaped == 'u')
            {
                var start = i - 2;
                for (var digit = 0; digit < 4; digit++)
                {
                    if (i == input.Length) { throw new JsonException("unexpected end of JSON input"); }
                    var hex = input[i++];
                    if (!(Digit(hex) || hex is >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F'))
                    {
                        var end = Math.Min(input.Length, start + 6);
                        throw new JsonException($"invalid escape sequence `{System.Text.Encoding.UTF8.GetString(input[start..end])}` in string");
                    }
                }
            }
            else if (escaped is not ((byte)'"' or (byte)'\\' or (byte)'/' or (byte)'b' or (byte)'f' or (byte)'n' or (byte)'r' or (byte)'t'))
            { throw new JsonException($"invalid escape sequence `\\{(char)escaped}` in string"); }
        }
        throw new JsonException("unexpected end of JSON input");
    }

    private static void ReadNumber(ReadOnlySpan<byte> input, ref int i)
    {
        if (input[i] == '-') { i++; }
        if (i == input.Length) { throw new JsonException("unexpected end of JSON input"); }
        if (!Digit(input[i])) { throw new JsonException(Invalid(input[i], "in numeric literal")); }
        if (input[i] == '0') { i++; }
        else { while (i < input.Length && Digit(input[i])) { i++; } }
        if (i < input.Length && input[i] == '.')
        {
            i++;
            if (i == input.Length) { throw new JsonException("unexpected end of JSON input"); }
            if (!Digit(input[i])) { throw new JsonException(Invalid(input[i], "in numeric literal")); }
            while (i < input.Length && Digit(input[i])) { i++; }
        }
        if (i < input.Length && input[i] is (byte)'e' or (byte)'E')
        {
            i++;
            if (i < input.Length && input[i] is (byte)'+' or (byte)'-') { i++; }
            if (i == input.Length) { throw new JsonException("unexpected end of JSON input"); }
            if (!Digit(input[i])) { throw new JsonException(Invalid(input[i], "in numeric literal")); }
            while (i < input.Length && Digit(input[i])) { i++; }
        }
    }
}
