using System.Collections.Immutable;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CharacterPlanner.Core.Diagnostics;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Core.Tests;

/// <summary>A small purpose-built v2 pack that exercises every part of the schema, and helpers around it.</summary>
internal static class TestPacks
{
    public static readonly byte[] IconBytes = Encoding.UTF8.GetBytes("icon");

    private static readonly ItemUpgradeDefinition Expertise = new()
    {
        Name = "Expertise",
        MaxRank = 10,
        ModifiersPerRank = [new() { Stat = "weapon_damage", Op = ModifierOps.Flat, Value = new(0.01) }]
    };

    private static ImmutableArray<ItemTalentSlotDefinition> WeaponTalents { get; } = [new() { ID = "talent", Name = "Talent", Choices = ["talent_killer", "talent_holster"] }];

    public static GamePack Tiny { get; } = new()
    {
        ID = "tiny",
        Name = "Tiny",
        Version = "1",
        GameVersion = "Test",
        Description = "A purpose-built test pack",
        Capabilities = [PackCapabilities.Weapons, PackCapabilities.Prototype, PackCapabilities.SkillMods, PackCapabilities.Party, PackCapabilities.TalentCopy, PackCapabilities.Simulation],
        Level = 40,
        Stats =
        [
            new() { ID = "weapon_damage", Name = "Weapon Damage", Format = StatFormats.Percent, Formula = "0" },
            new() { ID = "weapon_base_damage", Name = "Base Damage", Format = StatFormats.Integer, Formula = "0" },
            new() { ID = "weapon_rpm", Name = "RPM", Format = StatFormats.Integer, Formula = "0" },
            new() { ID = "armor", Name = "Armor", ShortName = "AR", Format = StatFormats.Integer, Formula = "0", Min = 0 },
            new() { ID = "skill_tier", Name = "Skill Tier", Format = StatFormats.Integer, Formula = "0", Min = 0, Max = 6 },
            new() { ID = "total_damage", Name = "Total Damage", Format = StatFormats.Decimal, Formula = "weapon_base_damage * (1 + weapon_damage)" }
        ],
        Curves = ImmutableDictionary<string, ImmutableArray<Expressions.CurvePoint>>.Empty.Add("tiers", [new(0, 0), new(6, 0.6)]),
        Selectors =
        [
            new() { ID = "headshot_mode", Name = "Hit location", Values = ["body", "headshot"], Section = "Weapon" },
            new() { ID = "overwatch_active", Name = "Overwatch active", Values = ["true"], Section = "Talents", Description = "On while Overwatch runs" }
        ],
        NumericContextInputs = [new() { ID = "stacks", Name = "Stacks", Min = 0, Max = 5, Step = 1, Default = 0, Section = "Talents" }],
        BuildOutputs = [new() { ID = "dps", Name = "DPS", Format = StatFormats.Decimal, Formula = "total_damage * weapon_rpm / 60 + skill_1_dps" }],
        Attributes =
        [
            new() { ID = "weapon_damage_core", Name = "Weapon Damage", Stat = "weapon_damage", Min = 0, Max = 0.15, Step = 0.01, Format = StatFormats.Percent, Category = "offensive", Core = true, PrototypeMax = 0.2 },
            new() { ID = "armor_core", Name = "Armor", Stat = "armor", Min = 0, Max = 170000, Step = 1000, Format = StatFormats.Integer, Category = "defensive", Core = true }
        ],
        Slots =
        [
            new() { ID = "mask", Name = "Mask", Accepts = ["gear"] },
            new() { ID = "primary_weapon", Name = "Primary Weapon", Accepts = ["weapon"], Weapon = true },
            new() { ID = "secondary_weapon", Name = "Secondary Weapon", Accepts = ["weapon"], Weapon = true }
        ],
        Items =
        [
            new()
            {
                ID = "mask_a", Name = "Mask A", Slot = "mask", Tags = ["gear", "brand_x"],
                Modifiers = [new() { Stat = "armor", Op = ModifierOps.Flat, Value = new(1000), RequiresContext = ImmutableDictionary<string, string>.Empty.Add("overwatch_active", "true") }],
                AttributeSlots = [new() { ID = "core", Name = "Core", Choices = [new() { AttributeID = "armor_core" }] }],
                ModSlots = [new() { ID = "mod", Name = "Mod", Choices = ["mod_crit"] }],
                TalentSlots = [new() { ID = "talent", Name = "Talent", Choices = ["talent_copier", "talent_party"] }]
            },
            new()
            {
                ID = "rifle_a", Name = "Rifle A", Slot = "primary_weapon", Tags = ["weapon", "weapon_type_rifle"],
                Weapon = [new("weapon_base_damage", 100), new("weapon_rpm", 600)],
                Modifiers = [new() { Stat = "weapon_damage", Op = ModifierOps.Flat, Value = new(0.1) }],
                AttributeSlots = [new() { ID = "core", Name = "Core", Choices = [new() { AttributeID = "weapon_damage_core" }] }],
                TalentSlots = WeaponTalents,
                Upgrade = Expertise
            },
            new()
            {
                ID = "rifle_b", Name = "Rifle B", Slot = "secondary_weapon", Tags = ["weapon", "weapon_type_rifle"],
                Weapon = [new("weapon_rpm", 300), new("weapon_base_damage", 200)],
                AttributeSlots = [new() { ID = "core", Name = "Core", Choices = [new() { AttributeID = "weapon_damage_core" }] }],
                TalentSlots = WeaponTalents,
                Upgrade = Expertise
            }
        ],
        Mods = [new() { ID = "mod_crit", Name = "Crit Mod", Modifiers = [new() { Stat = "weapon_damage", Op = ModifierOps.Flat, Value = new(0.05) }] }],
        Talents =
        [
            new() { ID = "talent_killer", Name = "Killer", Modifiers = [new() { Stat = "weapon_damage", Op = ModifierOps.Flat, Value = new(0.2), When = "has_selected_tag('weapon_type_rifle')" }] },
            new() { ID = "talent_holster", Name = "Holster", Modifiers = [new() { Stat = "weapon_damage", Op = ModifierOps.Flat, Value = new(0.05), WhileHolstered = true }] },
            new() { ID = "talent_copier", Name = "Copier", CopiesTalent = new() { From = "secondary_weapon", To = "primary_weapon", SameTagPrefix = "weapon_type_", ExcludeTags = ["exotic"], ExcludeTalents = ["talent_holster"] } },
            new()
            {
                ID = "talent_party", Name = "Party", SetPieceCredit = 1, ModeledBySets = ["set_x"],
                AllyEffects = [new() { ID = "party_boost", Stack = "party_boost", Name = "Boost", Modifiers = [new() { Stat = "weapon_damage", Op = ModifierOps.Flat, Value = new("0.01 * skill_tier + curve('tiers', skill_tier)") }] }]
            }
        ],
        Sets = [new() { ID = "set_x", Name = "Set X", Items = ["mask_a"], Bonuses = [new() { Pieces = 1, Modifiers = [new() { Stat = "armor", Op = ModifierOps.Flat, Value = new(500) }] }] }],
        Prototype = new()
        {
            Slots = ["mask"],
            ExcludeTags = ["exotic"],
            NormalizeInput = "stacks",
            Augments = [new() { ID = "augment_echo", Name = "Echo", Stat = "weapon_damage", Base = 0.01, PerLevel = 0.001, Levels = 10 }]
        },
        AccountUpgrades = [new() { ID = "shd_level", Name = "SHD Level", MaxRank = 100, EffectCap = 50, ModifiersPerRank = [new() { Stat = "armor", Op = ModifierOps.Flat, Value = new(10) }] }],
        ActiveSkillSlots = [new() { ID = "skill_1", Name = "Skill 1", Choices = ["turret"] }],
        ActiveSkills =
        [
            new()
            {
                ID = "turret", Name = "Turret", Family = "turret",
                Upgrade = new() { Name = "Expertise", MaxRank = 30 },
                ModSlots = [new() { ID = "mod", Name = "Mod", Choices = [new() { ID = "damage", Name = "Damage", Input = "skill_mod_damage", Min = 0, Max = 0.2, Step = 0.01, Format = StatFormats.Percent }] }],
                PreviewOutputs =
                [
                    new() { ID = "damage", Name = "Damage", Format = StatFormats.Integer, Formula = "1000 * (1 + curve('tiers', skill_tier) + skill_mod_damage)", Kind = "damage" },
                    new() { ID = "dps", Name = "DPS", Formula = "10", Kind = "rate", Headline = true },
                    new() { ID = "duration", Name = "Duration", Formula = "20 + skill_upgrade_rank", When = "level >= 30" }
                ]
            }
        ],
        Specializations =
        [
            new()
            {
                ID = "gunner", Name = "Gunner",
                Modifiers = [new() { Stat = "armor", Op = ModifierOps.Mult, Value = new(0.1) }],
                Budgets = [new() { ID = "total", Name = "Total", Points = 10 }, new() { ID = "hub", Name = "Hub", Points = 5, ParentID = "total" }],
                Nodes =
                [
                    new() { ID = "node_a", Name = "Node A", BudgetID = "hub", Cost = 5, Tier = 1, Modifiers = [new() { Stat = "weapon_damage", Op = ModifierOps.Flat, Value = new(0.05) }] },
                    new() { ID = "node_b", Name = "Node B", BudgetID = "total", Cost = 5, Tier = 2, Prerequisites = ["node_a"] }
                ]
            }
        ],
        RenamedIds = ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty.Add("item", ImmutableDictionary<string, string>.Empty.Add("old_rifle", "rifle_a")),
        Simulation = new()
        {
            Triggers =
            [
                new() { Talent = "talent_killer", Key = "overwatch_active", On = "kill", Kind = "buff", Duration = 10 },
                new() { Talent = "talent_holster", Key = "stacks", On = "hit", Kind = "stacks", Max = 5, Duration = 5 }
            ],
            Skills = [new() { Skill = "turret", Rate = "dps", Window = "duration", Cooldown = "duration", Impact = "damage" }]
        },
        Presentation = new()
        {
            Theme = new() { Variant = "dark", Primary = "#FF8A24", Colors = ImmutableDictionary<string, string>.Empty.Add("background", "#050505").Add("hover", "#FFFFFF14") },
            Layout = new()
            {
                SlotGroups = [new() { Name = "Weapons", Slots = ["primary_weapon", "secondary_weapon"], CompareStats = ["total_damage"] }, new() { Name = "Gear", Slots = ["mask"], CountAttributes = true, Columns = 2 }],
                Headline = [new() { Stat = "total_damage", Label = "Damage" }, new() { SkillRates = true }, new() { BuildOutput = "dps", Label = "DPS", LowerIsBetter = false }],
                Summary = [new() { Name = "Weapon", Stats = ["weapon_damage", "total_damage"], Conditional = ["armor"] }],
                AssumptionSections = [new("Weapon", true), new("Talents", false)],
                AttributeCategories = [new("offensive", "Offensive", "#FF0000"), new("defensive", "Defensive", "#0000FF")],
                Rarities = [new() { ID = "named", Name = "Named", Color = "#FFD700", Tags = ["named"], TagPrefixes = ["brand_"] }]
            },
            Icons = ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty
                .Add(PresentationIcons.Slots, ImmutableDictionary<string, string>.Empty.Add("mask", "icon_mask"))
                .Add(PresentationIcons.Talents, ImmutableDictionary<string, string>.Empty.Add("talent_killer", "icon_mask")),
            SpecializationImages = ImmutableDictionary<string, string>.Empty.Add("gunner", "icon_mask"),
            ItemArt = ImmutableDictionary<string, ItemArtwork>.Empty.Add("rifle_a", new("", "icon_mask")),
            ArtworkSources = [new() { ID = "source", Name = "Test source", URL = "https://example.invalid/", License = "CC0" }],
            ArtworkAssets = [new() { ID = "icon_mask", Path = "assets/icon_mask.png", Kind = "icon", SHA256 = PackFiles.Sha256(IconBytes), SourceID = "source" }]
        },
        LegacyShareOrder = new() { Slots = ["mask", "primary_weapon", "secondary_weapon"], Stats = ["armor"] }
    };

    public static ImmutableSortedDictionary<string, ReadOnlyMemory<byte>> Files(GamePack? pack = null) => PackWriter.Write(pack ?? Tiny);

    /// <summary>Replaces one file and refreshes its manifest entry, so a test reaches the checks after the hash check.</summary>
    public static ImmutableSortedDictionary<string, ReadOnlyMemory<byte>> WithFile(string name, string content, GamePack? pack = null) =>
        WithFile(name, Encoding.UTF8.GetBytes(content), pack);

    public static ImmutableSortedDictionary<string, ReadOnlyMemory<byte>> WithFile(string name, byte[] content, GamePack? pack = null)
    {
        var files = Files(pack).SetItem(name, content);
        if (name == PackFiles.Manifest) { return files; }
        var manifest = JsonNode.Parse(files[PackFiles.Manifest].Span)!;
        manifest["files"]![name] = new JsonObject { ["sha256"] = PackFiles.Sha256(content), ["bytes"] = content.Length };
        return files.SetItem(PackFiles.Manifest, Encoding.UTF8.GetBytes(manifest.ToJsonString()));
    }

    public static string Text(ImmutableSortedDictionary<string, ReadOnlyMemory<byte>> files, string name) => Encoding.UTF8.GetString(files[name].Span);

    /// <summary>Edits one file as a JSON tree; the manifest is refreshed.</summary>
    public static ImmutableSortedDictionary<string, ReadOnlyMemory<byte>> Edit(string name, Action<JsonNode> edit, GamePack? pack = null)
    {
        var node = JsonNode.Parse(Files(pack)[name].Span)!;
        edit(node);
        return WithFile(name, node.ToJsonString(), pack);
    }

    // Records hold ImmutableArrays, which compare by reference, so packs compare by canonical JSON.
    private static readonly JsonSerializerOptions Canonical = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Canonicalize(GamePack pack) => Sort(JsonSerializer.SerializeToNode(pack, Canonical)!).ToJsonString(Canonical);

    private static JsonNode Sort(JsonNode node) => node switch
    {
        JsonObject value => new JsonObject(value.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => KeyValuePair.Create(pair.Key, pair.Value is null ? null : Sort(pair.Value)))),
        JsonArray value => new JsonArray(value.Select(item => item is null ? null : Sort(item)).ToArray()),
        _ => node.DeepClone()
    };

    public static ImmutableArray<string> Codes(IEnumerable<PackDiagnostic> diagnostics) => [.. diagnostics.Select(diagnostic => diagnostic.Code)];
}
