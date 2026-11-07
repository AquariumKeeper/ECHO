using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CharacterPlanner.Legacy.Documents;

/// <summary>
/// One record of the Go app's build library: its id, when it was saved, and either the build it holds (read as a v1
/// document) or why it couldn't be read.
/// </summary>
public sealed record LegacyLibraryRecord(string Id, DateTimeOffset? SavedAt, BuildDocumentImport? Import, string? Problem);

/// <summary>
/// Reads the Go app's build library (<c>character-planner/builds.json</c>) for the one-time import (ADR 0002): a JSON
/// array of records whose builds are Go's build structure, field names and all. Each build becomes the v1 document
/// Go's <c>planner.FromBuild</c> would have written, and is read by <see cref="BuildDocumentJson"/>, so the library
/// imports by the same rules as a v1 document. A record that can't be read is reported, and the others still import.
/// </summary>
public static class LegacyLibrary
{
    /// <summary>The Go library's own size limit.</summary>
    public const int MaxBytes = 8 << 20;

    /// <summary>Go's build fields, and their names in a v1 document.</summary>
    private static readonly (string Go, string Document, Missing Missing)[] Fields =
    [
        ("PackID", "packId", Missing.Text), ("PackVersion", "packVersion", Missing.Text), ("Level", "level", Missing.Zero),
        ("Allocations", "allocations", Missing.Map), ("Equipment", "equipment", Missing.Map), ("AttributeRolls", "attributeRolls", Missing.Map),
        ("ModSelections", "modSelections", Missing.Map), ("ModRolls", "modRolls", Missing.Map), ("TalentSelections", "talentSelections", Missing.Map),
        ("ActiveSkillSelections", "activeSkillSelections", Missing.Map), ("SkillUpgrades", "skillUpgrades", Missing.Map), ("SkillMods", "skillMods", Missing.Map),
        ("SpecializationID", "specializationId", Missing.Text), ("SpecializationNodeSelections", "specializationNodeSelections", Missing.List),
        ("ActiveScenarios", "activeScenarios", Missing.Map), ("AllocatedSkills", "allocatedSkills", Missing.Map), ("AccountUpgrades", "accountUpgrades", Missing.Map),
        ("EquipmentUpgrades", "equipmentUpgrades", Missing.Map), ("Prototypes", "prototypes", Missing.Omit), ("Party", "party", Missing.Omit)
    ];

    /// <summary>What Go's FromBuild wrote for a nil field: an empty value, or nothing (omitempty).</summary>
    private enum Missing { Text, Zero, Map, List, Omit }

    /// <exception cref="LegacyDocumentException">The file isn't a Go library: too large, not JSON, or not an array of records.</exception>
    public static ImmutableArray<LegacyLibraryRecord> Read(ReadOnlyMemory<byte> json)
    {
        if (json.Length > MaxBytes) { throw new LegacyDocumentException($"the library is larger than {MaxBytes} bytes"); }
        JsonNode? root;
        try { root = JsonNode.Parse(json.Span, documentOptions: new() { MaxDepth = BuildDocumentJson.MaxDepth }); }
        catch (JsonException exception) { throw new LegacyDocumentException($"the library isn't valid JSON: {exception.Message}"); }
        if (root is not JsonArray records) { throw new LegacyDocumentException("the library must be a JSON array of records"); }
        return [.. records.Select((node, index) => Record(node, index))];
    }

    private static LegacyLibraryRecord Record(JsonNode? node, int index)
    {
        if (node is not JsonObject record) { return new($"#{index + 1}", null, null, "the record isn't an object"); }
        var id = record["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var text) && text.Trim().Length > 0 ? text : $"#{index + 1}";
        DateTimeOffset? savedAt = record["savedAt"] is JsonValue savedValue && savedValue.TryGetValue<string>(out var saved)
            && DateTimeOffset.TryParse(saved, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) ? when : null;
        if (record["build"] is not JsonObject build) { return new(id, savedAt, null, "the record has no build"); }
        try
        {
            var document = new JsonObject { ["schemaVersion"] = BuildDocumentJson.SchemaVersion, ["build"] = Document(build) };
            return new(id, savedAt, BuildDocumentJson.Decode(JsonSerializer.SerializeToUtf8Bytes(document)), null);
        }
        catch (LegacyDocumentException exception) { return new(id, savedAt, null, exception.Message); }
    }

    /// <summary>Go's build structure as the v1 document Go's FromBuild wrote; fields it doesn't know are kept, so the reader rejects them.</summary>
    private static JsonObject Document(JsonObject build)
    {
        var known = Fields.Select(field => field.Go).Append("Meta").ToHashSet(StringComparer.Ordinal);
        var document = new JsonObject();
        foreach (var (go, name, missing) in Fields)
        {
            var value = build[go]?.DeepClone();
            if (missing == Missing.Omit && value is null or JsonObject { Count: 0 } or JsonArray { Count: 0 }) { continue; }
            document[name] = value ?? missing switch { Missing.Text => "", Missing.Zero => 0, Missing.List => new JsonArray(), _ => new JsonObject() };
        }
        var meta = build["Meta"] as JsonObject;
        document["meta"] = new JsonObject { ["name"] = meta?["Name"]?.DeepClone() ?? "", ["notes"] = meta?["Notes"]?.DeepClone() ?? "" };
        foreach (var (key, value) in build.Where(pair => !known.Contains(pair.Key))) { document[key] = value?.DeepClone(); }
        return document;
    }
}
