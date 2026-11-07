using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using static CharacterPlanner.Core.Diagnostics.ReferenceText;

using CharacterPlanner.Core.Builds;
using CharacterPlanner.Legacy.PackV1;

namespace CharacterPlanner.Legacy.Documents;

public sealed class LegacyDocumentException(string message) : FormatException(message);

/// <summary>
/// A v1 document read into the trimmed snapshot. Level, allocations, scenarios and passive skills
/// are still type-checked as v1 required, then dropped: v2 fixes the level and removes the rest.
/// </summary>
/// <param name="Level">The document's level. v2 fixes the level, so a caller should report one that differs from the pack's.</param>
/// <param name="DroppedFields">The removed fields that held non-zero values, in v1 schema order, so the import can say what was lost.</param>
public sealed record BuildDocumentImport(BuildSnapshot Build, long Level, ImmutableArray<string> DroppedFields);

/// <summary>Reads Go-era (v1) portable build JSON for one-time import. Writing v1 is retired (ADR 0002).</summary>
public static partial class BuildDocumentJson
{
    public const long SchemaVersion = 1;
    /// <summary>The same limit v2 documents have.</summary>
    public const int MaxBytes = BuildDocument.MaxBytes;
    /// <summary>Comfortably above the schema's own depth (the deepest frozen document has 7 levels).</summary>
    public const int MaxDepth = 64;

    public static BuildDocumentImport Decode(ReadOnlyMemory<byte> json)
    {
        if (BuildDocument.SizeProblem(json.Length) is { } problem) { throw Error(problem); }
        CheckUnambiguousJson(json.Span);
        using var document = JsonDocument.Parse(json, new() { MaxDepth = MaxDepth });
        CheckShape(document.RootElement);
        return new Reader(document.RootElement).Read();
    }

    private static LegacyDocumentException Error(string message) => new(message);
    private static JsonElement Get(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : default;
    private static bool Missing(JsonElement value) => value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null;
    private sealed class Reader(JsonElement root)
    {
        /// <summary>
        /// The path of a value, and whether it is an array element. Only an error needs it, so it is found then (M2):
        /// indexing every element up front was quadratic, because <see cref="JsonElement"/> has no hash of its own and
        /// every element of a document hashed alike. Each element's raw text is a slice of the document's buffer, so
        /// the search steps into the one child whose slice contains the target's, and stops at the slice that is it.
        /// </summary>
        private (string Path, bool ArrayElement)? Locate(JsonElement target)
        {
            var (current, path, arrayElement) = (root, "Document", false);
            while (true)
            {
                switch (Placement(JsonMarshal.GetRawUtf8Value(current), JsonMarshal.GetRawUtf8Value(target)))
                {
                    case Place.Same: return (path, arrayElement);
                    case Place.Outside: return null;
                }
                var found = false;
                if (current.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in current.EnumerateObject())
                    {
                        if (Placement(JsonMarshal.GetRawUtf8Value(property.Value), JsonMarshal.GetRawUtf8Value(target)) == Place.Outside) { continue; }
                        (current, path, arrayElement, found) = (property.Value, path + "." + PackJsonStrings.Name(property), false, true);
                        break;
                    }
                }
                else if (current.ValueKind == JsonValueKind.Array)
                {
                    var index = 0;
                    foreach (var item in current.EnumerateArray())
                    {
                        if (Placement(JsonMarshal.GetRawUtf8Value(item), JsonMarshal.GetRawUtf8Value(target)) != Place.Outside)
                        { (current, path, arrayElement, found) = (item, path + "." + index, true, true); break; }
                        index++;
                    }
                }
                if (!found) { return null; }
            }
        }

        private enum Place { Outside, Inside, Same }

        /// <summary>Where one slice of the document's buffer lies relative to another, by address.</summary>
        private static Place Placement(ReadOnlySpan<byte> outer, ReadOnlySpan<byte> inner)
        {
            var offset = (long)Unsafe.ByteOffset(ref MemoryMarshal.GetReference(outer), ref MemoryMarshal.GetReference(inner));
            if (offset < 0 || offset + inner.Length > outer.Length) { return Place.Outside; }
            return offset == 0 && inner.Length == outer.Length ? Place.Same : Place.Inside;
        }
        public BuildDocumentImport Read()
        {
            var state = root.GetProperty("build");
            var version = Integer(Get(root, "schemaVersion"), "Document.schemaVersion");
            var meta = Get(state, "meta");
            // Fields are read in v1's order, so the first type error reported is unchanged.
            var packId = Text(Get(state, "packId"), "BuildDocument.build.packId");
            var packVersion = Text(Get(state, "packVersion"), "BuildDocument.build.packVersion");
            var level = Integer(Get(state, "level"), "BuildDocument.build.level");
            var allocations = Map(Get(state, "allocations"), "build.allocations", "map[string]float64", value => Number(value, "BuildDocument.build.allocations"));
            var build = new BuildSnapshot
            {
                PackId = packId,
                PackVersion = packVersion,
                Equipment = Strings(Get(state, "equipment"), "build.equipment"),
                AttributeRolls = Nested(Get(state, "attributeRolls"), "build.attributeRolls", "map[string]map[string]build.AttributeRoll", value => new AttributeRoll(
                    Text(value.GetProperty("attributeId"), "AttributeRoll.build.attributeRolls.attributeId"), Number(value.GetProperty("value"), "AttributeRoll.build.attributeRolls.value"))),
                ModSelections = NestedStrings(Get(state, "modSelections"), "build.modSelections"),
                ModRolls = Nested(Get(state, "modRolls"), "build.modRolls", "map[string]map[string]float64", value => Number(value, "BuildDocument.build.modRolls")),
                TalentSelections = NestedStrings(Get(state, "talentSelections"), "build.talentSelections"),
                ActiveSkillSelections = Strings(Get(state, "activeSkillSelections"), "build.activeSkillSelections"),
                SkillUpgrades = Map(Get(state, "skillUpgrades"), "build.skillUpgrades", "map[string]build.SkillUpgrade", value => new SkillUpgrade(
                    Text(value.GetProperty("skillId"), "SkillUpgrade.build.skillUpgrades.skillId"), Integer(value.GetProperty("rank"), "SkillUpgrade.build.skillUpgrades.rank"))),
                SkillMods = Map(Get(state, "skillMods"), "build.skillMods", "map[string]build.SkillModSelection", ReadSkillModSelection),
                SpecializationId = Text(Get(state, "specializationId"), "BuildDocument.build.specializationId"),
                SpecializationNodeSelections = Array(Get(state, "specializationNodeSelections"), "build.specializationNodeSelections", "[]string", value => Text(value, "BuildDocument.build.specializationNodeSelections"))
            };
            var activeScenarios = Set(Get(state, "activeScenarios"), "build.activeScenarios");
            var allocatedSkills = Set(Get(state, "allocatedSkills"), "build.allocatedSkills");
            build = build with
            {
                AccountUpgrades = Map(Get(state, "accountUpgrades"), "build.accountUpgrades", "map[string]int", value => Integer(value, "BuildDocument.build.accountUpgrades")),
                EquipmentUpgrades = Map(Get(state, "equipmentUpgrades"), "build.equipmentUpgrades", "map[string]build.EquipmentUpgrade", value => new EquipmentUpgrade(
                    Text(value.GetProperty("itemId"), "EquipmentUpgrade.build.equipmentUpgrades.itemId"), Integer(value.GetProperty("rank"), "EquipmentUpgrade.build.equipmentUpgrades.rank"))),
                Prototypes = Map(Get(state, "prototypes"), "build.prototypes", "map[string]build.PrototypeSelection", value => new PrototypeSelection(
                    Text(value.GetProperty("itemId"), "PrototypeSelection.build.prototypes.itemId"), Text(value.GetProperty("augment"), "PrototypeSelection.build.prototypes.augment"), Integer(value.GetProperty("level"), "PrototypeSelection.build.prototypes.level"))),
                Party = Array(Get(state, "party"), "build.party", "[]build.PartyMember", value => new PartyMember(
                    Text(Get(value, "name"), "PartyMember.build.party.name"), Text(Get(value, "code"), "PartyMember.build.party.code"), Number(Get(value, "armor"), "PartyMember.build.party.armor"),
                    Array(value.GetProperty("effects"), "build.party.effects", "[]build.PartyEffect", effect => new PartyEffect(
                        Text(effect.GetProperty("id"), "PartyEffect.build.party.effects.id"),
                        Map(effect.GetProperty("values"), "build.party.effects.values", "map[schema.StatID]float64", number => Number(number, "PartyEffect.build.party.effects.values")),
                        Boolean(effect.GetProperty("active"), "PartyEffect.build.party.effects.active"))))),
                Meta = new(Text(Get(meta, "name"), "BuildMetaDocument.build.meta.name"), Text(Get(meta, "notes"), "BuildMetaDocument.build.meta.notes"))
            };
            if (version != SchemaVersion) { throw Error($"unsupported build document schemaVersion {CharacterPlanner.Core.Diagnostics.ReferenceText.Number(version)}"); }
            if (build.PackId.Length == 0 || build.PackVersion.Length == 0) { throw Error("build document requires non-empty build.packId and build.packVersion"); }
            if (Missing(Get(state, "level"))) { throw Error("build document requires build.level"); }
            ValidateValues(build);
            var dropped = ImmutableArray.CreateBuilder<string>();
            // Zero allocations were the default for every primary stat; only real points are lost.
            if (allocations.Values.Any(points => points != 0)) { dropped.Add("allocations"); }
            if (!activeScenarios.IsEmpty) { dropped.Add("activeScenarios"); }
            if (!allocatedSkills.IsEmpty) { dropped.Add("allocatedSkills"); }
            return new(build, level, dropped.ToImmutable());
        }

        private SkillModSelection ReadSkillModSelection(JsonElement value)
        {
            var id = Text(value.GetProperty("skillId"), "SkillModSelection.skillId");
            // CR3: a null map stays null for the resolver to diagnose rather than becoming {}.
            // (The shape check already rejects "mods": null in v1 documents.)
            var raw = value.GetProperty("mods");
            var mods = raw.ValueKind == JsonValueKind.Null ? null : Map(raw, "mods", "map[string]build.SkillModRoll", roll =>
            {
                var mod = Text(roll.GetProperty("modId"), "SkillModRoll.modId");
                var number = Number(roll.GetProperty("value"), "SkillModRoll.value");
                if (mod.Length == 0) { throw Error("decode build document: invalid skill mod roll"); }
                return new SkillModRoll(mod, number);
            });
            if (id.Length == 0) { throw Error("decode build document: invalid skill mod selection"); }
            if (mods?.ContainsKey("") == true) { throw Error("decode build document: invalid skill mod slot"); }
            return new(id, mods!);
        }

        private string Text(JsonElement value, string field) => Missing(value) ? "" : value.ValueKind == JsonValueKind.String ? PackJsonStrings.Decode(value)! : throw TypeError(value, field, "string");
        private long Integer(JsonElement value, string field) => Missing(value) ? 0 : value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : throw TypeError(value, field, "int");
        private double Number(JsonElement value, string field) => Missing(value) ? 0 : value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : throw TypeError(value, field, "float64");
        private bool Boolean(JsonElement value, string field) => Missing(value) ? false : value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw TypeError(value, field, "bool");
        private LegacyDocumentException TypeError(JsonElement value, string field, string type)
        {
            var custom = field.StartsWith("SkillModRoll.", StringComparison.Ordinal) || field.StartsWith("SkillModSelection.", StringComparison.Ordinal);
            var located = custom ? null : Locate(value);
            field = custom ? "." + field[(field.LastIndexOf('.') + 1)..] : located?.Path ?? field;
            var target = located?.ArrayElement == true ? field : "Go struct field " + field;
            var kind = value.ValueKind switch { JsonValueKind.Object => "object", JsonValueKind.Array => "array", JsonValueKind.String => "string", JsonValueKind.True or JsonValueKind.False => "bool", _ => type is "int" or "float64" ? "number " + value.GetRawText() : "number" };
            return Error($"decode build document: json: cannot unmarshal {kind} into {target} of type {type}");
        }
        private ImmutableDictionary<string, T> Map<T>(JsonElement value, string field, string type, Func<JsonElement, T> read)
        {
            if (Missing(value)) { return ImmutableDictionary<string, T>.Empty; }
            if (value.ValueKind != JsonValueKind.Object) { throw TypeError(value, "BuildDocument." + field, type); }
            return value.EnumerateObject().ToImmutableDictionary(pair => PackJsonStrings.Name(pair), pair => read(pair.Value), StringComparer.Ordinal);
        }
        private ImmutableArray<T> Array<T>(JsonElement value, string field, string type, Func<JsonElement, T> read)
        {
            if (Missing(value)) { return []; }
            if (value.ValueKind != JsonValueKind.Array) { throw TypeError(value, "BuildDocument." + field, type); }
            return value.EnumerateArray().Select(read).ToImmutableArray();
        }
        private ImmutableDictionary<string, string> Strings(JsonElement value, string field) => Map(value, field, "map[string]string", item => Text(item, "BuildDocument." + field));
        private ImmutableDictionary<string, ImmutableDictionary<string, T>> Nested<T>(JsonElement value, string field, string type, Func<JsonElement, T> read) =>
            Map(value, field, type, item => Map(item, field, type[11..], read));
        private ImmutableDictionary<string, ImmutableDictionary<string, string>> NestedStrings(JsonElement value, string field) =>
            Nested(value, field, "map[string]map[string]string", item => Text(item, "BuildDocument." + field));
        private ImmutableHashSet<string> Set(JsonElement value, string field)
        {
            if (Missing(value)) { return ImmutableHashSet<string>.Empty; }
            if (value.ValueKind != JsonValueKind.Object) { throw TypeError(value, "Document." + field, "map[string]struct {}"); }
            var keys = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                var key = PackJsonStrings.Name(property);
                var item = property.Value;
                if (!Missing(item))
                {
                    if (item.ValueKind != JsonValueKind.Object) { throw TypeError(item, "Document." + field + "." + key, "struct {}"); }
                    foreach (var child in item.EnumerateObject()) { throw Error($"decode build document: json: unknown field {Quote(PackJsonStrings.Name(child))}"); }
                }
                keys.Add(key);
            }
            return keys.ToImmutable();
        }
    }
}
