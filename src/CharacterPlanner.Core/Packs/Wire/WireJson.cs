using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.Unicode;
using CharacterPlanner.Core.Diagnostics;

namespace CharacterPlanner.Core.Packs.Wire;

/// <summary>Strict System.Text.Json settings and the byte-level checks the serializer cannot make.</summary>
internal static class WireJson
{
    // Source-generated metadata, so a process's first pack load doesn't build it by reflection: that was most of the CLI's
    // per-call time. The wire types carry no defaults of their own (the mapper applies them; a test holds them to that), so
    // the generator's handling of omitted init-only members changes nothing. There's no reflection fallback: a type the
    // context doesn't reach (its roots and their members' types) fails in tests instead of quietly costing the time back.
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            TypeInfoResolver = PackWireContext.Default,
            AllowDuplicateProperties = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            PropertyNameCaseInsensitive = false,
            RespectNullableAnnotations = true,
            NumberHandling = JsonNumberHandling.Strict,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
            IndentSize = 2,
            NewLine = "\n",
            // Pack text is data, not HTML: keep non-ASCII names readable in diffs.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new ModifierValueConverter());
        options.MakeReadOnly();
        return options;
    }

    /// <summary>Parses one pack file, or reports why it is not strict v2 JSON. Never throws for bad data.</summary>
    public static T? Parse<T>(string file, ReadOnlyMemory<byte> bytes, List<PackDiagnostic> diagnostics) where T : class
    {
        var span = bytes.Span;
        void Fail(string code, string path, string message) => diagnostics.Add(new("error", code, path, message));
        if (span.StartsWith("﻿"u8)) { Fail("file-encoding", file, "The file starts with a byte-order mark; packs are UTF-8 without one"); return null; }
        if (!Utf8.IsValid(span)) { Fail("file-encoding", file, "The file is not valid UTF-8"); return null; }
        if (span.Contains((byte)'\r')) { Fail("file-encoding", file, "Line endings must be LF"); return null; }
        if (Inspect(file, span) is { } problem) { diagnostics.Add(problem); return null; }
        try
        {
            if (JsonSerializer.Deserialize<T>(span, Options) is { } value) { return value; }
            Fail("json-structure", file + ":$", "The document must be an object");
        }
        catch (JsonException exception)
        {
            var line = exception.LineNumber is { } number ? $" (line {number + 1})" : "";
            Fail("json-structure", $"{file}:{exception.Path ?? "$"}", Describe(exception) + line);
        }
        return null;
    }

    public static byte[] Serialize<T>(T value)
    {
        var text = JsonSerializer.Serialize(value, Options);
        return Encoding.UTF8.GetBytes(text + "\n");
    }

    // The serializer's own messages name .NET types; restate them in pack terms.
    // Only the wording depends on this matching; the code and path do not.
    private static string Describe(JsonException exception)
    {
        var message = exception.Message;
        if (message.Contains("could not be mapped", StringComparison.Ordinal))
        { return $"Unknown property {ReferenceText.Quote(Between(message, '\'', '\''))}"; }
        if (message.Contains("missing required properties", StringComparison.Ordinal))
        {
            var start = message.IndexOf("including: ", StringComparison.Ordinal);
            return "Missing required property " + (start < 0 ? "" : message[(start + "including: ".Length)..].TrimEnd('.').Replace('\'', '"'));
        }
        if (message.StartsWith(PackMessage, StringComparison.Ordinal)) { return message[PackMessage.Length..]; }
        return "The value has the wrong type";
    }

    private static string Between(string text, char open, char close)
    {
        var start = text.IndexOf(open, StringComparison.Ordinal);
        var end = start < 0 ? -1 : text.IndexOf(close, start + 1);
        return end < 0 ? "" : text[(start + 1)..end];
    }

    /// <summary>Marks converter messages that are already worded for pack authors.</summary>
    public const string PackMessage = "pack: ";

    // One forward pass reports syntax errors, nulls and duplicate keys with a JSON path.
    // System.Text.Json would also reject duplicates, but this pass words them consistently.
    private static bool HasLoneSurrogate(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) { i++; }
            else if (char.IsSurrogate(value[i])) { return true; }
        }
        return false;
    }

    private static PackDiagnostic? Inspect(string file, ReadOnlySpan<byte> json)
    {
        var frames = new List<Frame>();
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 64 });
        string Path(string? extra = null)
        {
            var builder = new StringBuilder("$");
            foreach (var frame in frames)
            {
                if (frame.Names is null) { builder.Append('[').Append(frame.Index).Append(']'); }
                else if (frame.Property is not null) { builder.Append('.').Append(frame.Property); }
            }
            if (extra is not null) { builder.Append('.').Append(extra); }
            return file + ":" + builder;
        }
        try
        {
            while (reader.Read())
            {
                var token = reader.TokenType;
                if (token is JsonTokenType.EndObject or JsonTokenType.EndArray) { frames.RemoveAt(frames.Count - 1); continue; }
                if (token == JsonTokenType.PropertyName)
                {
                    var frame = frames[^1];
                    var name = reader.GetString()!;
                    if (reader.ValueIsEscaped && HasLoneSurrogate(name)) { return new("error", "file-encoding", Path(), "Property names must be valid Unicode (no unpaired surrogate escapes)"); }
                    if (!frame.Names!.Add(name)) { return new("error", "json-duplicate-property", Path(name), $"Property {ReferenceText.Quote(name)} appears more than once"); }
                    frame.Property = name;
                    continue;
                }
                if (frames.Count > 0 && frames[^1].Names is null) { frames[^1].Index++; }
                if (token == JsonTokenType.Null) { return new("error", "json-null", Path(), "null is not allowed; omit an optional property instead"); }
                // Raw bytes are already valid UTF-8; an escape such as \uD800 can still encode invalid text.
                if (token == JsonTokenType.String && reader.ValueIsEscaped && HasLoneSurrogate(reader.GetString()!))
                { return new("error", "file-encoding", Path(), "Strings must be valid Unicode (no unpaired surrogate escapes)"); }
                // An overflowing literal such as 1e400 would otherwise parse as infinity.
                if (token == JsonTokenType.Number && !(reader.TryGetDouble(out var number) && double.IsFinite(number)))
                { return new("error", "json-number", Path(), "Numbers must be finite"); }
                if (token == JsonTokenType.StartObject) { frames.Add(new() { Names = new(StringComparer.Ordinal) }); }
                else if (token == JsonTokenType.StartArray) { frames.Add(new() { Index = -1 }); }
            }
        }
        catch (JsonException exception)
        {
            var at = exception.LineNumber is { } line ? $" at line {line + 1}, byte {exception.BytePositionInLine + 1}" : "";
            return new("error", "json-syntax", file, $"The file is not valid JSON{at}");
        }
        catch (InvalidOperationException)
        { return new("error", "file-encoding", file, "The file contains a string that is not valid Unicode"); }
        return null;
    }

    private sealed class Frame
    {
        public HashSet<string>? Names { get; init; }
        public string? Property { get; set; }
        public int Index { get; set; }
    }
}

/// <summary>Serializes a modifier amount as a number or expression string.</summary>
public sealed class ModifierValueConverter : JsonConverter<ModifierValue>
{
    public override ModifierValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var number) && double.IsFinite(number)) { return new(number); }
        if (reader.TokenType == JsonTokenType.String && reader.GetString() is { Length: > 0 } formula) { return new(formula); }
        throw new JsonException(WireJson.PackMessage + "A modifier value must be a finite number or a non-empty expression");
    }

    public override void Write(Utf8JsonWriter writer, ModifierValue value, JsonSerializerOptions options)
    {
        if (value.Formula is { } formula) { writer.WriteStringValue(formula); }
        else { writer.WriteNumberValue(value.Number); }
    }
}

/// <summary>A JSON object of numbers whose key order is kept (weapon base stats apply in authored order).</summary>
internal sealed class OrderedNumberMapConverter : JsonConverter<List<KeyValuePair<string, double>>>
{
    public override List<KeyValuePair<string, double>> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) { throw new JsonException(WireJson.PackMessage + "Expected an object of numbers"); }
        var values = new List<KeyValuePair<string, double>>();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString()!;
            reader.Read();
            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetDouble(out var value) || !double.IsFinite(value))
            { throw new JsonException(WireJson.PackMessage + $"{ReferenceText.Quote(name)} must be a finite number"); }
            values.Add(new(name, value));
        }
        return values;
    }

    public override void Write(Utf8JsonWriter writer, List<KeyValuePair<string, double>> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (name, number) in value) { writer.WriteNumber(name, number); }
        writer.WriteEndObject();
    }
}
