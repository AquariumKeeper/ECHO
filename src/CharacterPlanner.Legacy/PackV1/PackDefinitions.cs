using System.Collections.Immutable;
using System.Text.Json.Serialization;
using CharacterPlanner.Core.Expressions;

using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Legacy.PackV1;

public sealed record NumericContextInputDefinition
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("min")]
    public double Min { get; init; }

    [JsonPropertyName("max")]
    public double Max { get; init; }

    [JsonPropertyName("step")]
    public double Step { get; init; } = 1;

    [JsonPropertyName("default")]
    public double Default { get; init; }

    [JsonPropertyName("global")]
    public bool Global { get; init; }

    [JsonPropertyName("options")]
    public ImmutableArray<NumericContextOption> Options { get; init; }

    [JsonPropertyName("group")]
    public string Group { get; init; } = "";

    [JsonPropertyName("groupName")]
    public string GroupName { get; init; } = "";

    [JsonPropertyName("section")]
    public string Section { get; init; } = "";

    /// <summary>Finite, within bounds and on a step; the reference falls back to the default otherwise.</summary>
    public bool Accepts(double value) => double.IsFinite(value) && value >= Min && value <= Max && IsAligned(value, Min, Step);

    internal static bool IsAligned(double value, double min, double step) => Math.Abs((value - min) / step - Math.Round((value - min) / step)) <= CharacterPlanner.Core.Packs.StepTolerance.NumericInput;
}

public sealed record NumericContextOption
{
    [JsonPropertyName("value")]
    public double Value { get; init; }

    [JsonPropertyName("label")]
    public string Label { get; init; } = "";

}

public sealed record StatModifier
{
    [JsonPropertyName("stat")]
    public string Stat { get; init; } = "";

    [JsonPropertyName("op")]
    public string Op { get; init; } = "";

    [JsonPropertyName("bucket")]
    public string Bucket { get; init; } = "";

    [JsonPropertyName("value")]
    public ModifierValue Value { get; init; }

    [JsonPropertyName("when")]
    public string When { get; init; } = "";

    [JsonPropertyName("requiresContext")]
    [JsonConverter(typeof(PackJson.ContextGateConverter))]
    public ImmutableDictionary<string, string>? RequiresContext { get; init; }

    [JsonPropertyName("whileHolstered")]
    public bool WhileHolstered { get; init; }

}

public sealed record AccountUpgradeDefinition
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("maxRank")]
    public long MaxRank { get; init; }

    [JsonPropertyName("effectCap")]
    public long? EffectCap { get; init; }

    [JsonPropertyName("modifiersPerRank")]
    public ImmutableArray<StatModifier> ModifiersPerRank { get; init; } = [];

}

public sealed record ItemUpgradeDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("maxRank")]
    public long MaxRank { get; init; }

    [JsonPropertyName("modifiersPerRank")]
    public ImmutableArray<StatModifier> ModifiersPerRank { get; init; } = [];

}

public sealed record AttributeDefinition
{
    [JsonPropertyName("countStat")]
    public string CountStat { get; init; } = "";

    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("stat")]
    public string Stat { get; init; } = "";

    [JsonPropertyName("op")]
    public string Op { get; init; } = "";

    [JsonPropertyName("bucket")]
    public string Bucket { get; init; } = "";

    [JsonPropertyName("min")]
    public double Min { get; init; }

    [JsonPropertyName("max")]
    public double Max { get; init; }

    [JsonPropertyName("step")]
    public double? Step { get; init; }

    [JsonPropertyName("format")]
    public string Format { get; init; } = "decimal";

    [JsonPropertyName("category")]
    public string Category { get; init; } = "";

    [JsonPropertyName("core")]
    public bool Core { get; init; }

    [JsonPropertyName("prototypeMin")]
    public double? PrototypeMin { get; init; }

    [JsonPropertyName("prototypeMax")]
    public double? PrototypeMax { get; init; }

}

public sealed record SimTriggerDefinition
{
    [JsonPropertyName("talent")]
    public string Talent { get; init; } = "";

    [JsonPropertyName("key")]
    public string Key { get; init; } = "";

    [JsonPropertyName("on")]
    public string On { get; init; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    [JsonPropertyName("duration")]
    public double Duration { get; init; }

    [JsonPropertyName("max")]
    public long Max { get; init; }

    [JsonPropertyName("refresh")]
    public bool Refresh { get; init; }

    [JsonPropertyName("every")]
    public double Every { get; init; }

    [JsonPropertyName("resetOn")]
    public string ResetOn { get; init; } = "";

    [JsonPropertyName("consumeOn")]
    public string ConsumeOn { get; init; } = "";

    [JsonPropertyName("phases")]
    public ImmutableArray<SimPhase> Phases { get; init; } = [];

    [JsonPropertyName("percent")]
    public bool Percent { get; init; }

    [JsonPropertyName("within")]
    public double Within { get; init; }

    [JsonPropertyName("count")]
    public long Count { get; init; }

    [JsonPropertyName("refill")]
    public bool Refill { get; init; }

    [JsonPropertyName("gain")]
    public long Gain { get; init; }

    [JsonPropertyName("also")]
    public ImmutableArray<SimGain> Also { get; init; } = [];

    [JsonPropertyName("decay")]
    public double Decay { get; init; }

    [JsonPropertyName("atLeast")]
    public long AtLeast { get; init; }

    [JsonPropertyName("purge")]
    public bool Purge { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    [JsonPropertyName("whileStatus")]
    public string WhileStatus { get; init; } = "";

    [JsonPropertyName("pool")]
    public string Pool { get; init; } = "";

    [JsonPropertyName("poolMax")]
    public long PoolMax { get; init; }

}

public sealed record SimGain
{
    [JsonPropertyName("on")]
    public string On { get; init; } = "";

    [JsonPropertyName("gain")]
    public long Gain { get; init; }

    [JsonPropertyName("count")]
    public long Count { get; init; }

}

public sealed record SimPhase
{
    [JsonPropertyName("until")]
    public double Until { get; init; }

    [JsonPropertyName("value")]
    public string Value { get; init; } = "";

}

public sealed record SimSkillDefinition
{
    [JsonPropertyName("skill")]
    public string Skill { get; init; } = "";

    [JsonPropertyName("rate")]
    public string Rate { get; init; } = "";

    [JsonPropertyName("window")]
    public string Window { get; init; } = "";

    [JsonPropertyName("cooldown")]
    public string Cooldown { get; init; } = "";

    [JsonPropertyName("impact")]
    public string Impact { get; init; } = "";

    [JsonPropertyName("cooldownFromEnd")]
    public bool CooldownFromEnd { get; init; }

    [JsonPropertyName("impacts")]
    public string Impacts { get; init; } = "";

    [JsonPropertyName("shield")]
    public string Shield { get; init; } = "";

    [JsonPropertyName("regeneration")]
    public string Regeneration { get; init; } = "";

    [JsonPropertyName("weapons")]
    public ImmutableArray<string> Weapons { get; init; } = [];

    [JsonPropertyName("excludeTags")]
    public ImmutableArray<string> ExcludeTags { get; init; } = [];

    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

}

public sealed record TalentCopyDefinition
{
    [JsonPropertyName("from")]
    public string From { get; init; } = "";

    [JsonPropertyName("to")]
    public string To { get; init; } = "";

    [JsonPropertyName("sameTagPrefix")]
    public string SameTagPrefix { get; init; } = "";

    [JsonPropertyName("excludeTags")]
    public ImmutableArray<string> ExcludeTags { get; init; } = [];

    [JsonPropertyName("excludeTalents")]
    public ImmutableArray<string> ExcludeTalents { get; init; } = [];

}

public sealed record AllyEffect
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("stack")]
    public string Stack { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("modifiers")]
    public ImmutableArray<StatModifier> Modifiers { get; init; } = [];

}

public sealed record PrototypeDefinition
{
    [JsonPropertyName("slots")]
    public ImmutableArray<string> Slots { get; init; } = [];

    [JsonPropertyName("excludeTags")]
    public ImmutableArray<string> ExcludeTags { get; init; } = [];

    [JsonPropertyName("countStat")]
    public string CountStat { get; init; } = "";

    [JsonPropertyName("normalizeInput")]
    public string NormalizeInput { get; init; } = "";

    [JsonPropertyName("augments")]
    public ImmutableArray<AugmentDefinition> Augments { get; init; } = [];

}

public sealed record AugmentDefinition
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("stat")]
    public string Stat { get; init; } = "";

    [JsonPropertyName("base")]
    public double Base { get; init; }

    [JsonPropertyName("perLevel")]
    public double PerLevel { get; init; }

    [JsonPropertyName("levels")]
    public long Levels { get; init; }

}

public sealed record ItemAttributeChoice
{
    [JsonPropertyName("attributeId")]
    public string AttributeID { get; init; } = "";

    [JsonPropertyName("min")]
    public double? Min { get; init; }

    [JsonPropertyName("max")]
    public double? Max { get; init; }

    [JsonPropertyName("step")]
    public double? Step { get; init; }

}

public sealed record ItemAttributeSlotDefinition
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("choices")]
    public ImmutableArray<ItemAttributeChoice> Choices { get; init; } = [];

}

public sealed record ModDefinition
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("modifiers")]
    public ImmutableArray<StatModifier> Modifiers { get; init; } = [];

    [JsonPropertyName("rollAttributeId")]
    public string RollAttributeID { get; init; } = "";

}

public sealed record ItemModSlotDefinition
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("choices")]
    public ImmutableArray<string> Choices { get; init; } = [];

}

public sealed record TalentDefinition
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("icon")]
    public string Icon { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("modifiers")]
    public ImmutableArray<StatModifier> Modifiers { get; init; } = [];

    [JsonPropertyName("modeledBySets")]
    public ImmutableArray<string> ModeledBySets { get; init; } = [];

    [JsonPropertyName("setPieceCredit")]
    public long SetPieceCredit { get; init; }

    [JsonPropertyName("copiesTalent")]
    public TalentCopyDefinition? CopiesTalent { get; init; }

    [JsonPropertyName("allyEffects")]
    public ImmutableArray<AllyEffect> AllyEffects { get; init; } = [];

}

public sealed record ItemTalentSlotDefinition
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("choices")]
    public ImmutableArray<string> Choices { get; init; } = [];

}

public sealed record StatDefinition
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("shortName")]
    public string ShortName { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("format")]
    public string Format { get; init; } = "integer";

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    [JsonPropertyName("base")]
    public double? Base { get; init; }

    [JsonPropertyName("min")]
    public double? Min { get; init; }

    [JsonPropertyName("max")]
    public double? Max { get; init; }

    [JsonPropertyName("formula")]
    public string Formula { get; init; } = "";

}

public sealed record SlotDefinition
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("accepts")]
    public ImmutableArray<string> Accepts { get; init; } = [];

    [JsonPropertyName("icon")]
    public string Icon { get; init; } = "";

}

public sealed record ItemDefinition
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("tier")]
    public long? Tier { get; init; }

    [JsonPropertyName("tags")]
    public ImmutableArray<string> Tags { get; init; } = [];

    [JsonPropertyName("occupies")]
    public ImmutableArray<string> Occupies { get; init; } = [];

    [JsonPropertyName("requirements")]
    public ImmutableDictionary<string, double> Requirements { get; init; } = ImmutableDictionary<string, double>.Empty;

    [JsonPropertyName("effectsRequireContext")]
    [JsonConverter(typeof(PackJson.ContextGateConverter))]
    public ImmutableDictionary<string, string>? EffectsRequireContext { get; init; }

    [JsonPropertyName("modifiers")]
    public ImmutableArray<StatModifier> Modifiers { get; init; } = [];

    [JsonPropertyName("attributeSlots")]
    public ImmutableArray<ItemAttributeSlotDefinition> AttributeSlots { get; init; } = [];

    [JsonPropertyName("modSlots")]
    public ImmutableArray<ItemModSlotDefinition> ModSlots { get; init; } = [];

    [JsonPropertyName("talentSlots")]
    public ImmutableArray<ItemTalentSlotDefinition> TalentSlots { get; init; } = [];

    [JsonPropertyName("upgrade")]
    public ItemUpgradeDefinition? Upgrade { get; init; }

    [JsonPropertyName("artwork")]
    public ItemArtwork? Artwork { get; init; }

}

public sealed record SetBonus
{
    [JsonPropertyName("pieces")]
    public long Pieces { get; init; }

    [JsonPropertyName("modifiers")]
    public ImmutableArray<StatModifier> Modifiers { get; init; } = [];

}

public sealed record SetDefinition
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("items")]
    public ImmutableArray<string> Items { get; init; } = [];

    [JsonPropertyName("bonuses")]
    public ImmutableArray<SetBonus> Bonuses { get; init; } = [];

}

public sealed record ProgressionDefinition
{
    [JsonPropertyName("startingLevel")]
    public long StartingLevel { get; init; }

    [JsonPropertyName("maxLevel")]
    public long MaxLevel { get; init; }

    [JsonPropertyName("pointsPerLevel")]
    public long PointsPerLevel { get; init; }

    [JsonPropertyName("startingPoints")]
    public long StartingPoints { get; init; }

    [JsonPropertyName("skillPointsPerLevel")]
    public long SkillPointsPerLevel { get; init; }

}

public sealed record SkillUpgradeDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("maxRank")]
    public long MaxRank { get; init; }

}

public sealed record ActiveSkillDefinition
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("icon")]
    public string Icon { get; init; } = "";

    [JsonPropertyName("family")]
    public string Family { get; init; } = "";

    [JsonPropertyName("modifiers")]
    public ImmutableArray<StatModifier> Modifiers { get; init; } = [];

    [JsonPropertyName("previewOutputs")]
    public ImmutableArray<ActiveSkillPreviewOutputDefinition> PreviewOutputs { get; init; } = [];

    [JsonPropertyName("upgrade")]
    public SkillUpgradeDefinition? Upgrade { get; init; }

    [JsonPropertyName("modSlots")]
    public ImmutableArray<ActiveSkillModSlotDefinition> ModSlots { get; init; } = [];

}

public sealed record ActiveSkillPreviewOutputDefinition
{
    [JsonPropertyName("when")]
    public string When { get; init; } = "";

    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("format")]
    public string Format { get; init; } = "decimal";

    [JsonPropertyName("formula")]
    public string Formula { get; init; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    [JsonPropertyName("headline")]
    public bool Headline { get; init; }

}

public sealed record BuildOutputDefinition
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("format")]
    public string Format { get; init; } = "";

    [JsonPropertyName("formula")]
    public string Formula { get; init; } = "";

}

public sealed record ActiveSkillSlotDefinition
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("choices")]
    public ImmutableArray<string> Choices { get; init; } = [];

}

public sealed record SpecializationBudgetDefinition
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("points")]
    public long Points { get; init; }

    [JsonPropertyName("parentId")]
    public string ParentID { get; init; } = "";

}

public sealed record SpecializationNodeDefinition
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("icon")]
    public string Icon { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("budgetId")]
    public string BudgetID { get; init; } = "";

    [JsonPropertyName("cost")]
    public long Cost { get; init; }

    [JsonPropertyName("tier")]
    public long Tier { get; init; }

    [JsonPropertyName("prerequisites")]
    public ImmutableArray<string> Prerequisites { get; init; } = [];

    [JsonPropertyName("modifiers")]
    public ImmutableArray<StatModifier> Modifiers { get; init; } = [];

}

public sealed record SpecializationDefinition
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("icon")]
    public string Icon { get; init; } = "";

    [JsonPropertyName("image")]
    public string Image { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("modifiers")]
    public ImmutableArray<StatModifier> Modifiers { get; init; } = [];

    [JsonPropertyName("budgets")]
    public ImmutableArray<SpecializationBudgetDefinition> Budgets { get; init; } = [];

    [JsonPropertyName("nodes")]
    public ImmutableArray<SpecializationNodeDefinition> Nodes { get; init; } = [];

}

public sealed record Theme
{
    [JsonPropertyName("primary")]
    public string Primary { get; init; } = "";

    [JsonPropertyName("background")]
    public string Background { get; init; } = "";

    [JsonPropertyName("variant")]
    public string Variant { get; init; } = "";

    [JsonPropertyName("colors")]
    public ImmutableDictionary<string, string> Colors { get; init; } = ImmutableDictionary<string, string>.Empty;

}

public sealed record ScenarioToggle
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("tag")]
    public string Tag { get; init; } = "";

}

public sealed record SkillNode
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("cost")]
    public long Cost { get; init; }

    [JsonPropertyName("prerequisites")]
    public ImmutableArray<string> Prerequisites { get; init; } = [];

    [JsonPropertyName("modifiers")]
    public ImmutableArray<StatModifier> Modifiers { get; init; } = [];

    [JsonPropertyName("tier")]
    public long? Tier { get; init; }

}

public sealed record GamePack
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("schemaVersion")]
    public long SchemaVersion { get; init; }

    [JsonPropertyName("version")]
    public string Version { get; init; } = "";

    [JsonPropertyName("gameVersion")]
    public string GameVersion { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("progression")]
    public ProgressionDefinition Progression { get; init; } = new();

    [JsonPropertyName("stats")]
    public ImmutableArray<StatDefinition> Stats { get; init; } = [];

    [JsonPropertyName("attributes")]
    public ImmutableArray<AttributeDefinition> Attributes { get; init; } = [];

    [JsonPropertyName("mods")]
    public ImmutableArray<ModDefinition> Mods { get; init; } = [];

    [JsonPropertyName("talents")]
    public ImmutableArray<TalentDefinition> Talents { get; init; } = [];

    [JsonPropertyName("activeSkills")]
    public ImmutableArray<ActiveSkillDefinition> ActiveSkills { get; init; } = [];

    [JsonPropertyName("activeSkillSlots")]
    public ImmutableArray<ActiveSkillSlotDefinition> ActiveSkillSlots { get; init; } = [];

    [JsonPropertyName("buildOutputs")]
    public ImmutableArray<BuildOutputDefinition> BuildOutputs { get; init; } = [];

    [JsonPropertyName("specializations")]
    public ImmutableArray<SpecializationDefinition> Specializations { get; init; } = [];

    [JsonPropertyName("curves")]
    public ImmutableDictionary<string, ImmutableArray<CurvePoint>> Curves { get; init; } = ImmutableDictionary<string, ImmutableArray<CurvePoint>>.Empty;

    [JsonPropertyName("slots")]
    public ImmutableArray<SlotDefinition> Slots { get; init; } = [];

    [JsonPropertyName("items")]
    public ImmutableArray<ItemDefinition> Items { get; init; } = [];

    [JsonPropertyName("sets")]
    public ImmutableArray<SetDefinition> Sets { get; init; } = [];

    [JsonPropertyName("scenarios")]
    public ImmutableArray<ScenarioToggle> Scenarios { get; init; } = [];

    [JsonPropertyName("skills")]
    public ImmutableArray<SkillNode> Skills { get; init; } = [];

    [JsonPropertyName("accountUpgrades")]
    public ImmutableArray<AccountUpgradeDefinition> AccountUpgrades { get; init; } = [];

    [JsonPropertyName("numericContextInputs")]
    public ImmutableArray<NumericContextInputDefinition> NumericContextInputs { get; init; } = [];

    [JsonPropertyName("theme")]
    public Theme? Theme { get; init; }

    [JsonPropertyName("artwork")]
    public ArtworkRegistry? Artwork { get; init; }

    [JsonPropertyName("prototype")]
    public PrototypeDefinition? Prototype { get; init; }

    [JsonPropertyName("simTriggers")]
    public ImmutableArray<SimTriggerDefinition> SimTriggers { get; init; } = [];

    [JsonPropertyName("simSkills")]
    public ImmutableArray<SimSkillDefinition> SimSkills { get; init; } = [];

    [JsonPropertyName("layout")]
    public PackLayout? Layout { get; init; }

}

public sealed record ArtworkRegistry
{
    [JsonPropertyName("sources")]
    public ImmutableArray<ArtworkSource> Sources { get; init; } = [];

    [JsonPropertyName("assets")]
    public ImmutableArray<ArtworkAsset> Assets { get; init; } = [];

}

public sealed record ArtworkSource
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("url")]
    public string URL { get; init; } = "";

    [JsonPropertyName("attribution")]
    public string Attribution { get; init; } = "";

    [JsonPropertyName("license")]
    public string License { get; init; } = "";

    [JsonPropertyName("notes")]
    public string Notes { get; init; } = "";

}

public sealed record ArtworkAsset
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("path")]
    public string Path { get; init; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    [JsonPropertyName("sha256")]
    public string SHA256 { get; init; } = "";

    [JsonPropertyName("sourceId")]
    public string SourceID { get; init; } = "";

}

public sealed record ItemArtwork
{
    [JsonPropertyName("image")]
    public string Image { get; init; } = "";

    [JsonPropertyName("icon")]
    public string Icon { get; init; } = "";

}

public sealed record PackLayout
{
    [JsonPropertyName("slotGroups")]
    public ImmutableArray<LayoutSlotGroup> SlotGroups { get; init; } = [];

    [JsonPropertyName("summary")]
    public ImmutableArray<LayoutSummaryGroup> Summary { get; init; } = [];

    [JsonPropertyName("headline")]
    public ImmutableArray<LayoutHeadlineStat> Headline { get; init; } = [];

    [JsonPropertyName("weaponContextKey")]
    public string WeaponContextKey { get; init; } = "";

    [JsonPropertyName("attributeCategories")]
    public ImmutableArray<LayoutAttributeCategory> AttributeCategories { get; init; } = [];

    [JsonPropertyName("rarities")]
    public ImmutableArray<LayoutRarity> Rarities { get; init; } = [];

    [JsonPropertyName("assumptionSections")]
    public ImmutableArray<LayoutAssumptionSection> AssumptionSections { get; init; } = [];

}

public sealed record LayoutAssumptionSection
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("default")]
    public bool Default { get; init; }

}

public sealed record LayoutSlotGroup
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("slots")]
    public ImmutableArray<string> Slots { get; init; } = [];

    [JsonPropertyName("countAttributes")]
    public bool CountAttributes { get; init; }

    [JsonPropertyName("columns")]
    public long Columns { get; init; }

    [JsonPropertyName("compareStats")]
    public ImmutableArray<string> CompareStats { get; init; } = [];

}

public sealed record LayoutHeadlineStat
{
    [JsonPropertyName("stat")]
    public string Stat { get; init; } = "";

    [JsonPropertyName("buildOutput")]
    public string BuildOutput { get; init; } = "";

    [JsonPropertyName("skillRates")]
    public bool SkillRates { get; init; }

    [JsonPropertyName("label")]
    public string Label { get; init; } = "";

    [JsonPropertyName("conditional")]
    public bool Conditional { get; init; }

    [JsonPropertyName("unavailable")]
    public string Unavailable { get; init; } = "";

    [JsonPropertyName("lowerIsBetter")]
    public bool LowerIsBetter { get; init; }

}

public sealed record LayoutSummaryGroup
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("stats")]
    public ImmutableArray<string> Stats { get; init; } = [];

    [JsonPropertyName("conditional")]
    public ImmutableArray<string> Conditional { get; init; } = [];

}

public sealed record LayoutAttributeCategory
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("color")]
    public string Color { get; init; } = "";

}

public sealed record LayoutRarity
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("color")]
    public string Color { get; init; } = "";

    [JsonPropertyName("tags")]
    public ImmutableArray<string> Tags { get; init; } = [];

    [JsonPropertyName("tagPrefixes")]
    public ImmutableArray<string> TagPrefixes { get; init; } = [];

}

public sealed record ActiveSkillModDefinition
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("input")]
    public string Input { get; init; } = "";

    [JsonPropertyName("min")]
    public double Min { get; init; }

    [JsonPropertyName("max")]
    public double Max { get; init; }

    [JsonPropertyName("step")]
    public double? Step { get; init; }

    [JsonPropertyName("format")]
    public string Format { get; init; } = "decimal";

    [JsonPropertyName("statName")]
    public string StatName { get; init; } = "";

}

public sealed record ActiveSkillModSlotDefinition
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("choices")]
    public ImmutableArray<ActiveSkillModDefinition> Choices { get; init; } = [];

}
