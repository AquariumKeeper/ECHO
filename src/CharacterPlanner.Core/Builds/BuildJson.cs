using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CharacterPlanner.Core.Builds;

/// <summary>
/// The canonical build object shared by share codes (ADR 0003) and build documents (ADR 0004). It names
/// everything by id and groups each equipment slot's item and configuration, and each skill slot's skill,
/// grade and mods. Share codes leave the party out; documents keep it.
/// </summary>
internal static class BuildJson
{
    /// <exception cref="ArgumentException">The build has no pack id; the message names the field.</exception>
    public static JsonObject Write(BuildSnapshot build, bool party)
    {
        if (build.PackId.Length == 0) { throw new ArgumentException("build.pack is empty", nameof(build)); }
        return new()
        {
            ["pack"] = build.PackId,
            ["packVersion"] = Optional(build.PackVersion),
            ["name"] = Optional(build.Meta.Name),
            ["notes"] = Optional(build.Meta.Notes),
            ["specialization"] = Optional(build.SpecializationId),
            ["specializationNodes"] = build.SpecializationNodeSelections.IsDefaultOrEmpty ? null : new JsonArray([.. build.SpecializationNodeSelections.Select(node => (JsonNode?)node)]),
            ["account"] = Map(build.AccountUpgrades, rank => rank),
            ["slots"] = Map(EquipmentSlots(build), slot => EquipmentSlot(build, slot)),
            ["skills"] = Map(SkillSlots(build), slot => SkillSlot(build, slot)),
            ["party"] = !party || build.Party.IsDefaultOrEmpty ? null : new JsonArray([.. build.Party.Select(member => (JsonNode?)Member(member))])
        };
    }

    private static string? Optional(string value) => value.Length == 0 ? null : value;

    private static IEnumerable<KeyValuePair<string, string>> EquipmentSlots(BuildSnapshot build) =>
        build.Equipment.Keys.Concat(build.AttributeRolls.Keys).Concat(build.ModSelections.Keys).Concat(build.ModRolls.Keys)
            .Concat(build.TalentSelections.Keys).Concat(build.EquipmentUpgrades.Keys).Concat(build.Prototypes.Keys)
            .Distinct(StringComparer.Ordinal).Select(slot => KeyValuePair.Create(slot, slot));

    private static IEnumerable<KeyValuePair<string, string>> SkillSlots(BuildSnapshot build) =>
        build.ActiveSkillSelections.Keys.Concat(build.SkillUpgrades.Keys).Concat(build.SkillMods.Keys)
            .Distinct(StringComparer.Ordinal).Select(slot => KeyValuePair.Create(slot, slot));

    private static JsonObject EquipmentSlot(BuildSnapshot build, string slot)
    {
        var present = build.Equipment.TryGetValue(slot, out var selected);
        var item = selected ?? "";
        var upgrade = build.EquipmentUpgrades.GetValueOrDefault(slot);
        var prototype = build.Prototypes.GetValueOrDefault(slot);
        return new()
        {
            ["item"] = present ? item : null,
            ["attributes"] = Map(build.AttributeRolls.GetValueOrDefault(slot), roll => new JsonArray(roll.AttributeId, roll.Value)),
            ["mods"] = Map(build.ModSelections.GetValueOrDefault(slot), mod => mod),
            ["modRolls"] = Map(build.ModRolls.GetValueOrDefault(slot), value => value),
            ["talents"] = Map(build.TalentSelections.GetValueOrDefault(slot), talent => talent),
            ["upgrade"] = upgrade is null ? null : new JsonObject { ["rank"] = upgrade.Rank, ["item"] = upgrade.ItemId == item ? null : upgrade.ItemId },
            ["prototype"] = prototype is null ? null : new JsonObject { ["augment"] = prototype.Augment, ["level"] = prototype.Level, ["item"] = prototype.ItemId == item ? null : prototype.ItemId }
        };
    }

    private static JsonObject SkillSlot(BuildSnapshot build, string slot)
    {
        var present = build.ActiveSkillSelections.TryGetValue(slot, out var selected);
        var skill = selected ?? "";
        var upgrade = build.SkillUpgrades.GetValueOrDefault(slot);
        var mods = build.SkillMods.GetValueOrDefault(slot);
        return new()
        {
            ["skill"] = present ? skill : null,
            ["upgrade"] = upgrade is null ? null : new JsonObject { ["rank"] = upgrade.Rank, ["skill"] = upgrade.SkillId == skill ? null : upgrade.SkillId },
            // A null mod map (CR3) is written as an empty one.
            ["mods"] = mods is null ? null : new JsonObject
            {
                ["rolls"] = new JsonObject((mods.Mods ?? ImmutableDictionary<string, SkillModRoll>.Empty).Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)new JsonArray(pair.Value.ModId, pair.Value.Value)))),
                ["skill"] = mods.SkillId == skill ? null : mods.SkillId
            }
        };
    }

    private static JsonObject Member(PartyMember member) => new()
    {
        ["name"] = member.Name,
        ["code"] = Optional(member.Code),
        ["armor"] = member.Armor == 0 ? null : member.Armor,
        ["effects"] = member.Effects.IsDefaultOrEmpty ? null : new JsonArray([.. member.Effects.Select(effect => (JsonNode?)new JsonObject
        {
            ["id"] = effect.Id,
            ["values"] = Map(effect.Values, value => value),
            ["active"] = effect.Active
        })])
    };

    private static JsonObject? Map<T>(IEnumerable<KeyValuePair<string, T>>? values, Func<T, JsonNode?> convert)
    {
        var pairs = values?.Select(pair => KeyValuePair.Create(pair.Key, convert(pair.Value))).ToList();
        return pairs is null || pairs.Count == 0 ? null : new JsonObject(pairs);
    }

    /// <summary>
    /// Ordinal key order with null members omitted (empty collections are already null, except an empty
    /// skill-mod roll map, which carries meaning). Text is escaped only where JSON requires: the built-in
    /// encoders also escape HTML-sensitive and non-ASCII characters, which would inflate names and notes.
    /// Indented output puts each member on its own line and keeps lists of plain values on one.
    /// </summary>
    /// <exception cref="ArgumentException">A number isn't finite or text isn't valid UTF-16; the message names the field.</exception>
    public static void WriteCanonical(StringBuilder json, JsonNode? node, string path, bool indented, int depth = 0)
    {
        switch (node)
        {
            case JsonObject value:
                var members = value.Where(pair => pair.Value is not null).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList();
                json.Append('{');
                for (var index = 0; index < members.Count; index++)
                {
                    if (index > 0) { json.Append(','); }
                    NewLine(json, indented, depth + 1);
                    WriteText(json, members[index].Key, path + "." + members[index].Key);
                    json.Append(indented ? ": " : ":");
                    WriteCanonical(json, members[index].Value, path + "." + members[index].Key, indented, depth + 1);
                }
                if (members.Count > 0) { NewLine(json, indented, depth); }
                json.Append('}');
                break;
            case JsonArray value:
                var inline = !indented || value.All(child => child is JsonValue);
                json.Append('[');
                for (var index = 0; index < value.Count; index++)
                {
                    if (index > 0) { json.Append(inline && indented ? ", " : ","); }
                    if (!inline) { NewLine(json, indented, depth + 1); }
                    WriteCanonical(json, value[index], path, indented, depth + 1);
                }
                if (!inline && value.Count > 0) { NewLine(json, indented, depth); }
                json.Append(']');
                break;
            case JsonValue value when value.TryGetValue<string>(out var text):
                WriteText(json, text, path);
                break;
            case JsonValue value when value.TryGetValue<bool>(out var flag):
                json.Append(flag ? "true" : "false");
                break;
            case JsonValue value when value.TryGetValue<long>(out var integer):
                json.Append(integer.ToString(CultureInfo.InvariantCulture));
                break;
            case JsonValue value when value.TryGetValue<double>(out var number):
                if (!double.IsFinite(number)) { throw new ArgumentException($"{path} is not a finite number", nameof(node)); }
                json.Append(number.ToString("R", CultureInfo.InvariantCulture));
                break;
            default:
                throw new InvalidOperationException($"{path} has no canonical representation");
        }
    }

    private static void NewLine(StringBuilder json, bool indented, int depth)
    {
        if (indented) { json.Append('\n').Append(' ', 2 * depth); }
    }

    private static void WriteText(StringBuilder json, string text, string path)
    {
        json.Append('"');
        for (var index = 0; index < text.Length;)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out var length) != OperationStatus.Done)
            { throw new ArgumentException($"{path} is not valid text", nameof(text)); }
            _ = rune.Value switch
            {
                '"' => json.Append("\\\""),
                '\\' => json.Append("\\\\"),
                '\n' => json.Append("\\n"),
                '\r' => json.Append("\\r"),
                '\t' => json.Append("\\t"),
                < ' ' => json.Append(CultureInfo.InvariantCulture, $"\\u{rune.Value:x4}"),
                _ => json.Append(text, index, length)
            };
            index += length;
        }
        json.Append('"');
    }

    /// <summary>
    /// Reads a canonical build object, rejecting unknown and repeated members so every input has one meaning.
    /// <paramref name="error"/> turns a message such as "build.pack is missing" into the caller's exception.
    /// </summary>
    public static BuildSnapshot Read(JsonElement element, bool party, Func<string, Exception?, FormatException> error) => new Reader(error).Read(element, party);

    /// <summary>A member's name; one that isn't valid UTF-16 (a lone surrogate escape) is a format error, not a crash.</summary>
    public static string Name(JsonProperty property, string path, Func<string, Exception?, FormatException> error)
    {
        try { return property.Name; }
        catch (InvalidOperationException exception) { throw error($"{path} has a member name that is not valid text", exception); }
    }

    private sealed class Reader(Func<string, Exception?, FormatException> error)
    {
        private FormatException Error(string message) => error(message, null);

        public BuildSnapshot Read(JsonElement element, bool party)
        {
            string[] allowed = ["pack", "packVersion", "name", "notes", "specialization", "specializationNodes", "account", "slots", "skills"];
            var root = Members(element, "build", party ? [.. allowed, "party"] : allowed);
            var pack = Text(root, "pack", "build");
            if (pack.Length == 0) { throw Error("build.pack is missing"); }
            var build = new BuildSnapshot
            {
                PackId = pack,
                PackVersion = Text(root, "packVersion", "build"),
                Meta = new(Text(root, "name", "build"), Text(root, "notes", "build")),
                SpecializationId = Text(root, "specialization", "build"),
                SpecializationNodeSelections = root.TryGetValue("specializationNodes", out var nodes) ? List(nodes, "build.specializationNodes", String) : [],
                AccountUpgrades = Map(root, "account", "build", Integer),
                Party = root.TryGetValue("party", out var members) ? List(members, "build.party", Member) : []
            };
            foreach (var (slot, value) in Map(root, "slots", "build", (element, path) => element))
            { build = EquipmentSlot(build, slot, Members(value, "build.slots." + slot, "item", "attributes", "mods", "modRolls", "talents", "upgrade", "prototype"), "build.slots." + slot); }
            foreach (var (slot, value) in Map(root, "skills", "build", (element, path) => element))
            { build = SkillSlot(build, slot, Members(value, "build.skills." + slot, "skill", "upgrade", "mods"), "build.skills." + slot); }
            return build;
        }

        private BuildSnapshot EquipmentSlot(BuildSnapshot build, string slot, Dictionary<string, JsonElement> entry, string path)
        {
            var item = Text(entry, "item", path);
            var attributes = Map(entry, "attributes", path, (value, at) => Pair(value, at, (id, number) => new AttributeRoll(id, number)));
            var mods = Map(entry, "mods", path, String);
            var modRolls = Map(entry, "modRolls", path, Number);
            var talents = Map(entry, "talents", path, String);
            build = build with
            {
                Equipment = entry.ContainsKey("item") ? build.Equipment.SetItem(slot, item) : build.Equipment,
                AttributeRolls = attributes.IsEmpty ? build.AttributeRolls : build.AttributeRolls.SetItem(slot, attributes),
                ModSelections = mods.IsEmpty ? build.ModSelections : build.ModSelections.SetItem(slot, mods),
                ModRolls = modRolls.IsEmpty ? build.ModRolls : build.ModRolls.SetItem(slot, modRolls),
                TalentSelections = talents.IsEmpty ? build.TalentSelections : build.TalentSelections.SetItem(slot, talents)
            };
            if (entry.TryGetValue("upgrade", out var upgradeValue))
            {
                var upgrade = Members(upgradeValue, path + ".upgrade", "rank", "item");
                build = build with { EquipmentUpgrades = build.EquipmentUpgrades.SetItem(slot, new(Text(upgrade, "item", path + ".upgrade", item), Required(upgrade, "rank", path + ".upgrade", Integer))) };
            }
            if (entry.TryGetValue("prototype", out var prototypeValue))
            {
                var prototype = Members(prototypeValue, path + ".prototype", "augment", "level", "item");
                build = build with
                {
                    Prototypes = build.Prototypes.SetItem(slot, new(Text(prototype, "item", path + ".prototype", item), Required(prototype, "augment", path + ".prototype", String), Required(prototype, "level", path + ".prototype", Integer)))
                };
            }
            return build;
        }

        private BuildSnapshot SkillSlot(BuildSnapshot build, string slot, Dictionary<string, JsonElement> entry, string path)
        {
            var skill = Text(entry, "skill", path);
            if (entry.ContainsKey("skill")) { build = build with { ActiveSkillSelections = build.ActiveSkillSelections.SetItem(slot, skill) }; }
            if (entry.TryGetValue("upgrade", out var upgradeValue))
            {
                var upgrade = Members(upgradeValue, path + ".upgrade", "rank", "skill");
                build = build with { SkillUpgrades = build.SkillUpgrades.SetItem(slot, new(Text(upgrade, "skill", path + ".upgrade", skill), Required(upgrade, "rank", path + ".upgrade", Integer))) };
            }
            if (entry.TryGetValue("mods", out var modsValue))
            {
                var mods = Members(modsValue, path + ".mods", "rolls", "skill");
                var rolls = Required(mods, "rolls", path + ".mods", (value, at) => Map(value, at, (roll, rollPath) => Pair(roll, rollPath, (id, number) => new SkillModRoll(id, number))));
                build = build with { SkillMods = build.SkillMods.SetItem(slot, new(Text(mods, "skill", path + ".mods", skill), rolls)) };
            }
            return build;
        }

        private PartyMember Member(JsonElement value, string path)
        {
            var member = Members(value, path, "name", "code", "armor", "effects");
            return new(Required(member, "name", path, String), Text(member, "code", path), member.TryGetValue("armor", out var armor) ? Number(armor, path + ".armor") : 0,
                member.TryGetValue("effects", out var effects) ? List(effects, path + ".effects", Effect) : []);
        }

        private PartyEffect Effect(JsonElement value, string path)
        {
            var effect = Members(value, path, "id", "values", "active");
            return new(Required(effect, "id", path, String), Map(effect, "values", path, Number), effect.TryGetValue("active", out var active) && Boolean(active, path + ".active"));
        }

        private Dictionary<string, JsonElement> Members(JsonElement value, string path, params string[] allowed)
        {
            if (value.ValueKind != JsonValueKind.Object) { throw Error($"{path} is not an object"); }
            var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                var name = Name(property, path, error);
                if (!allowed.Contains(name, StringComparer.Ordinal)) { throw Error($"{path} has unknown member \"{name}\""); }
                if (!members.TryAdd(name, property.Value)) { throw Error($"{path} lists \"{name}\" twice"); }
            }
            return members;
        }

        private T Required<T>(Dictionary<string, JsonElement> members, string name, string path, Func<JsonElement, string, T> read) =>
            members.TryGetValue(name, out var value) ? read(value, path + "." + name) : throw Error($"{path}.{name} is missing");

        private string Text(Dictionary<string, JsonElement> members, string name, string path, string fallback = "") =>
            members.TryGetValue(name, out var value) ? String(value, path + "." + name) : fallback;

        private ImmutableDictionary<string, T> Map<T>(Dictionary<string, JsonElement> members, string name, string path, Func<JsonElement, string, T> read) =>
            members.TryGetValue(name, out var value) ? Map(value, path + "." + name, read) : ImmutableDictionary<string, T>.Empty;

        private ImmutableDictionary<string, T> Map<T>(JsonElement value, string path, Func<JsonElement, string, T> read)
        {
            if (value.ValueKind != JsonValueKind.Object) { throw Error($"{path} is not an object"); }
            var result = ImmutableDictionary.CreateBuilder<string, T>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                var name = Name(property, path, error);
                if (result.ContainsKey(name)) { throw Error($"{path} lists \"{name}\" twice"); }
                result.Add(name, read(property.Value, path + "." + name));
            }
            return result.ToImmutable();
        }

        private ImmutableArray<T> List<T>(JsonElement value, string path, Func<JsonElement, string, T> read) =>
            value.ValueKind == JsonValueKind.Array ? [.. value.EnumerateArray().Select((item, index) => read(item, $"{path}.{index}"))] : throw Error($"{path} is not a list");

        private T Pair<T>(JsonElement value, string path, Func<string, double, T> create) =>
            value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 2 ? create(String(value[0], path), Number(value[1], path)) : throw Error($"{path} is not an [id, value] pair");

        private string String(JsonElement value, string path)
        {
            if (value.ValueKind != JsonValueKind.String) { throw Error($"{path} is not text"); }
            try { return value.GetString()!; }
            catch (InvalidOperationException exception) { throw error($"{path} is not valid text", exception); }
        }

        private long Integer(JsonElement value, string path) =>
            value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : throw Error($"{path} is not a whole number");

        private double Number(JsonElement value, string path) =>
            value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : throw Error($"{path} is not a finite number");

        private bool Boolean(JsonElement value, string path) =>
            value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw Error($"{path} is not true or false");
    }
}
