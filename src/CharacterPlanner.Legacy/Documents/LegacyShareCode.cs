using System.IO.Compression;
using System.Text.Json;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Legacy.Documents;

/// <summary>
/// Reads Go-era share codes (v1–v12) for one-time import; writing them is retired (ADR 0002).
/// A code is a positional JSON tuple, raw-DEFLATE compressed and base64url encoded. The tuple is
/// rewritten as a v1 document, so the document importer's accept and reject rules apply unchanged.
/// </summary>
/// <remarks>
/// Tuple layout: <c>[version, packId, packVersion, level, allocations, equipment, (v2) scenarios,
/// (v3) passiveSkills, (v4) attributeRolls, (v5) config, (v6) skills, name, notes]</c>. Allocations,
/// equipment, scenarios and passive skills are arrays in the pack's fixed <see cref="LegacyShareOrder"/>;
/// config and skills are objects whose members each arrived in a known version.
/// </remarks>
public static class LegacyShareCode
{
    public const long FirstVersion = 1;
    public const long LastVersion = 12;
    public const int MaxCodeLength = ShareCodeWrapping.MaxCodeLength;
    public const int MaxPayloadBytes = ShareCodeWrapping.MaxPayloadBytes;

    // Member, v1 document field, and the version that introduced it.
    private static readonly (string Member, string Field, long Since)[] ConfigMembers =
    [
        ("mods", "modSelections", 5), ("talents", "talentSelections", 5), ("modRolls", "modRolls", 8),
        ("equipmentUpgrades", "equipmentUpgrades", 9), ("prototypes", "prototypes", 12)
    ];
    private static readonly (string Member, string Field, long Since)[] SkillMembers =
    [
        ("activeSkills", "activeSkillSelections", 6), ("specializationId", "specializationId", 6), ("nodeSelections", "specializationNodeSelections", 7),
        ("accountUpgrades", "accountUpgrades", 9), ("skillUpgrades", "skillUpgrades", 10), ("skillMods", "skillMods", 11)
    ];

    /// <summary>Decodes a legacy code; surrounding whitespace from a paste is ignored.</summary>
    public static BuildDocumentImport Decode(string code, LegacyShareOrder order)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(order);
        var compressed = ShareCodeWrapping.Base64(ShareCodeWrapping.Trim(code));
        using var input = new DeflateStream(new MemoryStream(compressed), CompressionMode.Decompress);
        var document = ToDocument(ShareCodeWrapping.Inflate(input, "share code is not compressed build data"), order);
        try { return BuildDocumentJson.Decode(document); }
        catch (LegacyDocumentException exception) { throw new ShareCodeException("share code: " + exception.Message, exception); }
    }

    private static ShareCodeException Error(string message) => new(message);

    private static byte[] ToDocument(byte[] payload, LegacyShareOrder order)
    {
        JsonDocument tuple;
        // Rewriting the tuple as a document nests a value at most one level deeper, so this keeps within the document limit.
        try { tuple = JsonDocument.Parse(payload, new() { MaxDepth = BuildDocumentJson.MaxDepth - 1 }); }
        catch (JsonException exception) { throw new ShareCodeException("share code payload is not JSON, or is nested too deeply", exception); }
        using (tuple)
        {
            var fields = tuple.RootElement.ValueKind == JsonValueKind.Array ? tuple.RootElement.EnumerateArray().ToArray() : throw Error("share code payload is not a tuple");
            var version = fields.Length > 0 && fields[0].ValueKind == JsonValueKind.Number && fields[0].TryGetInt64(out var number) ? number : throw Error("share code has no version");
            if (version is < FirstVersion or > LastVersion) { throw Error($"unsupported share code version {version}"); }
            // v1 has eight fields and v2–v6 each added one; v7–v12 extended the config and skills objects.
            var expected = 7 + Math.Min(version, 6);
            if (fields.Length != expected) { throw Error($"share code v{version} has {fields.Length} fields; expected {expected}"); }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", BuildDocumentJson.SchemaVersion);
                writer.WriteStartObject("build");
                Raw(writer, "packId", fields[1]);
                Raw(writer, "packVersion", fields[2]);
                Raw(writer, "level", fields[3]);
                Positional(writer, "allocations", fields[4], order.Stats);
                // Equipment lists every legacy slot; null and missing positions are empty slots.
                writer.WriteStartObject("equipment");
                var equipment = Array(fields[5], "equipment", order.Slots.Length);
                for (var index = 0; index < order.Slots.Length; index++)
                {
                    writer.WritePropertyName(order.Slots[index]);
                    if (index < equipment.Length && equipment[index].ValueKind != JsonValueKind.Null) { writer.WriteRawValue(equipment[index].GetRawText(), skipInputValidation: true); }
                    else { writer.WriteStringValue(""); }
                }
                writer.WriteEndObject();
                if (version >= 2) { Flags(writer, "activeScenarios", fields[6], order.Scenarios); }
                if (version >= 3) { Flags(writer, "allocatedSkills", fields[7], order.PassiveSkills); }
                if (version >= 4) { Raw(writer, "attributeRolls", fields[8]); }
                if (version >= 5) { Members(writer, "config", fields[9], ConfigMembers, version); }
                if (version >= 6) { Members(writer, "skills", fields[10], SkillMembers, version); }
                writer.WriteStartObject("meta");
                Raw(writer, "name", fields[^2]);
                Raw(writer, "notes", fields[^1]);
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            return stream.ToArray();
        }
    }

    private static void Raw(Utf8JsonWriter writer, string field, JsonElement value)
    {
        writer.WritePropertyName(field);
        writer.WriteRawValue(value.GetRawText(), skipInputValidation: true);
    }

    private static JsonElement[] Array(JsonElement value, string field, int limit)
    {
        if (value.ValueKind == JsonValueKind.Null) { return []; }
        var items = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : throw Error($"share code {field} is not a list");
        return items.Length <= limit ? items : throw Error($"share code {field} has {items.Length} entries; the pack's legacy order has {limit}");
    }

    private static void Positional(Utf8JsonWriter writer, string field, JsonElement value, IReadOnlyList<string> order)
    {
        var items = Array(value, field, order.Count);
        writer.WriteStartObject(field);
        for (var index = 0; index < items.Length; index++) { Raw(writer, order[index], items[index]); }
        writer.WriteEndObject();
    }

    /// <summary>A list of booleans in legacy order becomes v1's set of enabled ids.</summary>
    private static void Flags(Utf8JsonWriter writer, string field, JsonElement value, IReadOnlyList<string> order)
    {
        var items = Array(value, field, order.Count);
        writer.WriteStartObject(field);
        for (var index = 0; index < items.Length; index++)
        {
            if (items[index].ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { throw Error($"share code {field} entry {index} is not true or false"); }
            if (items[index].GetBoolean()) { writer.WriteStartObject(order[index]); writer.WriteEndObject(); }
        }
        writer.WriteEndObject();
    }

    /// <summary>Copies a config or skills object's members to their v1 document fields.</summary>
    private static void Members(Utf8JsonWriter writer, string field, JsonElement value, (string Member, string Field, long Since)[] members, long version)
    {
        if (value.ValueKind == JsonValueKind.Null) { return; }
        if (value.ValueKind != JsonValueKind.Object) { throw Error($"share code {field} is not an object"); }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            var known = System.Array.FindIndex(members, member => member.Member == property.Name);
            if (known < 0 || members[known].Since > version) { throw Error($"share code v{version} {field} has unknown member \"{property.Name}\""); }
            if (!seen.Add(property.Name)) { throw Error($"share code {field} lists \"{property.Name}\" twice"); }
            Raw(writer, members[known].Field, property.Value);
        }
    }
}
