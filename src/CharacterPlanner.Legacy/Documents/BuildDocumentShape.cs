using System.Text.Json;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Diagnostics.ReferenceText;

using CharacterPlanner.Core.Builds;
using CharacterPlanner.Legacy.PackV1;

namespace CharacterPlanner.Legacy.Documents;

public static partial class BuildDocumentJson
{
    private static void CheckUnambiguousJson(ReadOnlySpan<byte> json)
    {
        // The reader allows one level more, so too deep a document is caught here, as a container opens, with its own
        // message; a syntax error at any legal depth keeps its syntax message.
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = MaxDepth + 1, AllowMultipleValues = true });
        var objects = new Stack<HashSet<string>?>();
        var complete = false;
        try
        {
            while (reader.Read())
            {
                if (complete) { throw Error("trailing JSON value"); }
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject or JsonTokenType.StartArray when reader.CurrentDepth >= MaxDepth:
                        throw Error($"build document is nested more than {MaxDepth} levels deep");
                    case JsonTokenType.StartObject: objects.Push(new(StringComparer.Ordinal)); break;
                    case JsonTokenType.StartArray: objects.Push(null); break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray: objects.Pop(); break;
                    case JsonTokenType.PropertyName:
                        var name = PackJsonStrings.Decode(ref reader)!;
                        if (!objects.Peek()!.Add(name)) { throw Error($"duplicate JSON key {Quote(name)}"); }
                        break;
                }
                complete = reader.CurrentDepth == 0 && reader.TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray or JsonTokenType.PropertyName);
            }
            if (!complete) { throw Error("EOF"); }
        }
        catch (JsonException)
        {
            // Share syntax wording and Unicode decoding only; pack null, unknown
            // field and number policies do not apply to portable documents.
            throw Error(PackJsonSyntax.Describe(json) ?? "invalid JSON input");
        }
    }

    private static void CheckShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) { throw Error("build document must be a JSON object"); }
        Keys(root, "build document", ["schemaVersion", "build"]);
        var state = Get(root, "build");
        if (state.ValueKind != JsonValueKind.Object) { throw Error("build document requires a non-null build object"); }
        Keys(state, "build document build", ["packId", "packVersion", "level", "allocations", "equipment", "attributeRolls", "modSelections", "modRolls", "talentSelections",
            "activeSkillSelections", "skillUpgrades", "skillMods", "specializationId", "specializationNodeSelections", "activeScenarios", "allocatedSkills", "accountUpgrades", "equipmentUpgrades", "prototypes", "party", "meta"]);
        var attributes = Get(state, "attributeRolls");
        if (!Missing(attributes))
        {
            foreach (var slot in Object(attributes, "build.attributeRolls"))
            {
                foreach (var roll in Object(slot.Value, "build.attributeRolls." + PackJsonStrings.Name(slot)))
                { Record(roll.Value, $"build.attributeRolls.{PackJsonStrings.Name(slot)}.{PackJsonStrings.Name(roll)}", ["attributeId", "value"]); }
            }
        }
        Records(Get(state, "skillUpgrades"), "build.skillUpgrades", ["skillId", "rank"]);
        var skillMods = Get(state, "skillMods");
        if (!Missing(skillMods))
        {
            foreach (var slot in Object(skillMods, "build.skillMods"))
            {
                var path = "build.skillMods." + PackJsonStrings.Name(slot);
                Record(slot.Value, path, ["skillId", "mods"]);
                var mods = slot.Value.GetProperty("mods");
                if (mods.ValueKind != JsonValueKind.Object)
                {
                    try { Object(mods, path + ".mods"); }
                    catch (LegacyDocumentException exception) { throw Error($"{path}.mods is required: {exception.Message}"); }
                }
                foreach (var mod in mods.EnumerateObject()) { Record(mod.Value, path + ".mods." + PackJsonStrings.Name(mod), ["modId", "value"]); }
            }
        }
        Records(Get(state, "equipmentUpgrades"), "build.equipmentUpgrades", ["itemId", "rank"]);
        Records(Get(state, "prototypes"), "build.prototypes", ["itemId", "augment", "level"]);
        var party = Get(state, "party");
        if (!Missing(party))
        {
            if (party.ValueKind != JsonValueKind.Array) { throw Error("build document build.party must be an array"); }
            var index = 0;
            foreach (var member in party.EnumerateArray())
            {
                var path = "build.party." + index++;
                if (member.ValueKind == JsonValueKind.Null) { throw Error(path + " must be a non-null object"); }
                Object(member, path);
                Keys(member, "build document " + path, ["name", "code", "armor", "effects"]);
                var effects = Get(member, "effects");
                if (Missing(effects)) { throw Error(path + ".effects is required"); }
                if (effects.ValueKind != JsonValueKind.Array) { throw Error($"build document {path}.effects must be an array"); }
                var effectIndex = 0;
                foreach (var effect in effects.EnumerateArray()) { Record(effect, $"{path}.effects.{effectIndex++}", ["id", "values", "active"]); }
            }
        }
        var meta = Get(state, "meta");
        if (!Missing(meta))
        {
            if (meta.ValueKind != JsonValueKind.Object) { throw Error("build document build.meta must be an object"); }
            Keys(meta, "build document build.meta", ["name", "notes"]);
        }
    }

    private static JsonElement.ObjectEnumerator Object(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Object) { return value.EnumerateObject(); }
        if (Missing(value)) { throw Error(path + ": must be a JSON object"); }
        var kind = value.ValueKind switch { JsonValueKind.Array => "array", JsonValueKind.String => "string", JsonValueKind.Number => "number", _ => "bool" };
        throw Error($"{path}: json: cannot unmarshal {kind} into Go value of type map[string]jsontext.Value");
    }
    private static void Keys(JsonElement value, string path, string[] allowed)
    {
        foreach (var property in value.EnumerateObject())
        { if (!allowed.Contains(PackJsonStrings.Name(property), StringComparer.Ordinal)) { throw Error($"{path}: unknown or incorrectly cased field {Quote(PackJsonStrings.Name(property))}"); } }
    }
    private static void Record(JsonElement value, string path, string[] fields)
    {
        if (value.ValueKind == JsonValueKind.Null) { throw Error(path + " must be a non-null object"); }
        Object(value, path);
        Keys(value, path, fields);
        foreach (var field in fields)
        { if (Missing(Get(value, field))) { throw Error(path + "." + field + " is required"); } }
    }
    private static void Records(JsonElement value, string path, string[] fields)
    {
        if (Missing(value)) { return; }
        foreach (var record in Object(value, path)) { Record(record.Value, path + "." + PackJsonStrings.Name(record), fields); }
    }
}
