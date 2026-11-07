using System.Text;
using System.Text.Json;

namespace CharacterPlanner.Core.Builds;

public sealed class BuildDocumentException(string message, Exception? inner = null) : FormatException(message, inner);

/// <summary>
/// The app's saved-build file (ADR 0004): <c>{"schemaVersion": 2, "build": …}</c>, UTF-8 JSON indented for
/// reading and diffing. The build is the canonical object share codes use (<see cref="BuildJson"/>), plus the
/// party. Assumptions and history are session state, not part of a build, and the level is fixed by the pack.
/// </summary>
public static class BuildDocument
{
    public const int SchemaVersion = 2;
    public const int MaxBytes = 4 * 1024 * 1024;

    /// <exception cref="ArgumentException">The build can't be saved; the message names the field.</exception>
    public static byte[] Encode(BuildSnapshot build)
    {
        ArgumentNullException.ThrowIfNull(build);
        if (PartyRules.Problem(build.Party) is { } problem) { throw new ArgumentException("build.party: " + problem, nameof(build)); }
        var json = new StringBuilder("{\n  \"schemaVersion\": ").Append(SchemaVersion).Append(",\n  \"build\": ");
        BuildJson.WriteCanonical(json, BuildJson.Write(build, party: true), "build", indented: true, depth: 1);
        var bytes = Encoding.UTF8.GetBytes(json.Append("\n}\n").ToString());
        // Decode refuses larger files, so writing one would lose the build.
        return bytes.Length <= MaxBytes ? bytes : throw new ArgumentException($"build is larger than {MaxBytes} bytes when saved", nameof(build));
    }

    /// <summary>Reads a v2 document; a leading UTF-8 byte order mark is ignored.</summary>
    public static BuildSnapshot Decode(ReadOnlyMemory<byte> json)
    {
        if (SizeProblem(json.Length) is { } tooLarge) { throw Error(tooLarge); }
        if (json.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) { json = json[3..]; }
        JsonDocument document;
        try { document = JsonDocument.Parse(json, new() { MaxDepth = 32 }); }
        catch (JsonException exception) { throw new BuildDocumentException("build document is not valid JSON: " + exception.Message, exception); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { throw Error("build document is not an object"); }
            var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                var name = BuildJson.Name(property, "build document", (message, inner) => new BuildDocumentException(message, inner));
                if (!members.TryAdd(name, property.Value)) { throw Error($"build document lists \"{name}\" twice"); }
            }
            // The version is judged first, so an older or newer document gets the right advice before its contents are.
            if (!members.TryGetValue("schemaVersion", out var version)) { throw Error("build document has no schemaVersion"); }
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt64(out var number)) { throw Error("build document schemaVersion is not a whole number"); }
            if (number != SchemaVersion)
            {
                throw Error(number switch
                {
                    1 => "build document is a v1 (Go-era) document; import it instead of opening it",
                    > SchemaVersion => "build document was made by a newer version of the planner",
                    _ => $"unsupported build document schemaVersion {number}"
                });
            }
            if (members.Keys.Where(name => name is not ("schemaVersion" or "build")).Order(StringComparer.Ordinal).FirstOrDefault() is { } unknown)
            { throw Error($"build document has unknown member \"{unknown}\""); }
            if (!members.TryGetValue("build", out var element)) { throw Error("build document has no build"); }
            var build = BuildJson.Read(element, party: true, (message, inner) => new BuildDocumentException("build document " + message, inner));
            return PartyRules.Problem(build.Party) is { } problem ? throw Error("build document build.party: " + problem) : build;
        }
    }

    private static BuildDocumentException Error(string message) => new(message);

    /// <summary>The size check v1 imports share with v2 documents, so both report it alike.</summary>
    internal static string? SizeProblem(int length) => length > MaxBytes ? $"build document is larger than {MaxBytes} bytes" : null;
}
