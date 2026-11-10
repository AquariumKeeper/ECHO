using System.Text.Json.Serialization;

namespace CharacterPlanner.Core.Packs.Wire;

// Exact pack schema v2 shapes (docs/PACK_SCHEMA_V2.md). Every property names its
// JSON key explicitly; there is no naming policy. Required members use `required`;
// optional members are nullable and null means "absent" (a JSON null is rejected
// before deserialization), so the mapper applies each documented default itself.

internal sealed record ManifestWire
{
    [JsonPropertyName("schemaVersion")] public required long SchemaVersion { get; init; }
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("version")] public required string Version { get; init; }
    [JsonPropertyName("gameVersion")] public required string GameVersion { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("capabilities")] public required List<string> Capabilities { get; init; }
    [JsonPropertyName("files")] public required Dictionary<string, FileHashWire> Files { get; init; }
    [JsonPropertyName("assetsSha256")] public string? AssetsSha256 { get; init; }
}

internal sealed record FileHashWire
{
    [JsonPropertyName("sha256")] public required string Sha256 { get; init; }
    [JsonPropertyName("bytes")] public required long Bytes { get; init; }
}

internal sealed record ModifierWire
{
    [JsonPropertyName("stat")] public required string Stat { get; init; }
    [JsonPropertyName("op")] public required string Op { get; init; }
    [JsonPropertyName("value")] public required ModifierValue Value { get; init; }
    [JsonPropertyName("when")] public string? When { get; init; }
    [JsonPropertyName("requires")] public Dictionary<string, string>? Requires { get; init; }
    [JsonPropertyName("whileHolstered")] public bool? WhileHolstered { get; init; }
}

internal sealed record RulesWire
{
    [JsonPropertyName("level")] public required long Level { get; init; }
    [JsonPropertyName("stats")] public required List<StatWire> Stats { get; init; }
    [JsonPropertyName("curves")] public Dictionary<string, List<double[]>>? Curves { get; init; }
    [JsonPropertyName("selectors")] public List<SelectorWire>? Selectors { get; init; }
    [JsonPropertyName("numericInputs")] public List<NumericInputWire>? NumericInputs { get; init; }
    [JsonPropertyName("buildOutputs")] public List<BuildOutputWire>? BuildOutputs { get; init; }
    [JsonPropertyName("attributes")] public List<AttributeWire>? Attributes { get; init; }
    [JsonPropertyName("templates")] public TemplatesWire? Templates { get; init; }
    [JsonPropertyName("slots")] public required List<SlotWire> Slots { get; init; }
    [JsonPropertyName("equipmentLimits")] public List<EquipmentLimitWire>? EquipmentLimits { get; init; }
    [JsonPropertyName("items")] public required List<ItemWire> Items { get; init; }
    [JsonPropertyName("mods")] public List<ModWire>? Mods { get; init; }
    [JsonPropertyName("talents")] public List<TalentWire>? Talents { get; init; }
    [JsonPropertyName("sets")] public List<SetWire>? Sets { get; init; }
    [JsonPropertyName("prototype")] public PrototypeWire? Prototype { get; init; }
    [JsonPropertyName("accountUpgrades")] public List<AccountUpgradeWire>? AccountUpgrades { get; init; }
    [JsonPropertyName("skillSlots")] public List<SkillSlotWire>? SkillSlots { get; init; }
    [JsonPropertyName("skills")] public List<SkillWire>? Skills { get; init; }
    [JsonPropertyName("specializations")] public List<SpecializationWire>? Specializations { get; init; }
    [JsonPropertyName("renamedIds")] public Dictionary<string, Dictionary<string, string>>? RenamedIds { get; init; }
}

internal sealed record EquipmentLimitWire
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("tag")] public required string Tag { get; init; }
    [JsonPropertyName("slots")] public required List<string> Slots { get; init; }
    [JsonPropertyName("maximum")] public required long Maximum { get; init; }
}

internal sealed record StatWire
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("shortName")] public string? ShortName { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("format")] public required string Format { get; init; }
    [JsonPropertyName("formula")] public required string Formula { get; init; }
    [JsonPropertyName("min")] public double? Min { get; init; }
    [JsonPropertyName("max")] public double? Max { get; init; }
}

internal sealed record SelectorWire
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("values")] public required List<string> Values { get; init; }
    [JsonPropertyName("section")] public string? Section { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
}

internal sealed record NumericInputWire
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("min")] public required double Min { get; init; }
    [JsonPropertyName("max")] public required double Max { get; init; }
    [JsonPropertyName("step")] public double? Step { get; init; }
    [JsonPropertyName("default")] public required double Default { get; init; }
    [JsonPropertyName("global")] public bool? Global { get; init; }
    [JsonPropertyName("options")] public List<NumericOptionWire>? Options { get; init; }
    [JsonPropertyName("group")] public string? Group { get; init; }
    [JsonPropertyName("groupName")] public string? GroupName { get; init; }
    [JsonPropertyName("section")] public string? Section { get; init; }
}

internal sealed record NumericOptionWire
{
    [JsonPropertyName("value")] public required double Value { get; init; }
    [JsonPropertyName("label")] public required string Label { get; init; }
}

internal sealed record BuildOutputWire
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("format")] public required string Format { get; init; }
    [JsonPropertyName("formula")] public required string Formula { get; init; }
}

internal sealed record AttributeWire
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("stat")] public required string Stat { get; init; }
    [JsonPropertyName("min")] public required double Min { get; init; }
    [JsonPropertyName("max")] public required double Max { get; init; }
    [JsonPropertyName("step")] public required double Step { get; init; }
    [JsonPropertyName("format")] public string? Format { get; init; }
    [JsonPropertyName("category")] public string? Category { get; init; }
    [JsonPropertyName("core")] public bool? Core { get; init; }
    [JsonPropertyName("prototype")] public AttributePrototypeWire? Prototype { get; init; }
    [JsonPropertyName("countStat")] public string? CountStat { get; init; }
}

internal sealed record AttributePrototypeWire
{
    [JsonPropertyName("max")] public required double Max { get; init; }
    [JsonPropertyName("min")] public double? Min { get; init; }
}

internal sealed record TemplatesWire
{
    [JsonPropertyName("attributeSlots")] public Dictionary<string, List<AttributeSlotWire>>? AttributeSlots { get; init; }
    [JsonPropertyName("modSlots")] public Dictionary<string, List<ChoiceSlotWire>>? ModSlots { get; init; }
    [JsonPropertyName("talentSlots")] public Dictionary<string, List<ChoiceSlotWire>>? TalentSlots { get; init; }
    [JsonPropertyName("upgrades")] public Dictionary<string, UpgradeWire>? Upgrades { get; init; }
}

internal sealed record AttributeSlotWire
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("choices")] public required List<AttributeChoiceWire> Choices { get; init; }
}

internal sealed record AttributeChoiceWire
{
    [JsonPropertyName("attribute")] public required string Attribute { get; init; }
    [JsonPropertyName("min")] public double? Min { get; init; }
    [JsonPropertyName("max")] public double? Max { get; init; }
    [JsonPropertyName("step")] public double? Step { get; init; }
    [JsonPropertyName("countStat")] public string? CountStat { get; init; }
}

/// <summary>A mod or talent slot; both list choice ids.</summary>
internal sealed record ChoiceSlotWire
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("choices")] public required List<string> Choices { get; init; }
}

internal sealed record UpgradeWire
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("maxRank")] public required long MaxRank { get; init; }
    [JsonPropertyName("modifiersPerRank")] public required List<ModifierWire> ModifiersPerRank { get; init; }
}

internal sealed record SlotWire
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("accepts")] public List<string>? Accepts { get; init; }
    [JsonPropertyName("weapon")] public bool? Weapon { get; init; }
}

internal sealed record ItemWire
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("slot")] public required string Slot { get; init; }
    [JsonPropertyName("tags")] public List<string>? Tags { get; init; }
    [JsonPropertyName("weapon"), JsonConverter(typeof(OrderedNumberMapConverter))] public List<KeyValuePair<string, double>>? Weapon { get; init; }
    [JsonPropertyName("modifiers")] public List<ModifierWire>? Modifiers { get; init; }
    [JsonPropertyName("attributeSlots")] public string? AttributeSlots { get; init; }
    [JsonPropertyName("modSlots")] public string? ModSlots { get; init; }
    [JsonPropertyName("talentSlots")] public string? TalentSlots { get; init; }
    [JsonPropertyName("upgrade")] public string? Upgrade { get; init; }
}

internal sealed record ModWire
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("modifiers")] public List<ModifierWire>? Modifiers { get; init; }
    [JsonPropertyName("rollAttribute")] public string? RollAttribute { get; init; }
}

internal sealed record TalentWire
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("modifiers")] public List<ModifierWire>? Modifiers { get; init; }
    [JsonPropertyName("modeledBySets")] public List<string>? ModeledBySets { get; init; }
    [JsonPropertyName("setPieceCredit")] public long? SetPieceCredit { get; init; }
    [JsonPropertyName("copiesTalent")] public TalentCopyWire? CopiesTalent { get; init; }
    [JsonPropertyName("allyEffects")] public List<AllyEffectWire>? AllyEffects { get; init; }
    [JsonPropertyName("coverage")] public TalentCoverageWire? Coverage { get; init; }
}

internal sealed record TalentCoverageWire
{
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("omits")] public List<string>? Omits { get; init; }
    [JsonPropertyName("evidence")] public required string Evidence { get; init; }
    [JsonPropertyName("settleBy")] public string? SettleBy { get; init; }
}

internal sealed record TalentCopyWire
{
    [JsonPropertyName("from")] public required string From { get; init; }
    [JsonPropertyName("to")] public required string To { get; init; }
    [JsonPropertyName("sameTagPrefix")] public string? SameTagPrefix { get; init; }
    [JsonPropertyName("excludeTags")] public List<string>? ExcludeTags { get; init; }
    [JsonPropertyName("excludeTalents")] public List<string>? ExcludeTalents { get; init; }
}

internal sealed record AllyEffectWire
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("stack")] public required string Stack { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("modifiers")] public required List<ModifierWire> Modifiers { get; init; }
}

internal sealed record SetWire
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("items")] public required List<string> Items { get; init; }
    [JsonPropertyName("bonuses")] public required List<SetBonusWire> Bonuses { get; init; }
}

internal sealed record SetBonusWire
{
    [JsonPropertyName("pieces")] public required long Pieces { get; init; }
    [JsonPropertyName("modifiers")] public required List<ModifierWire> Modifiers { get; init; }
}

internal sealed record PrototypeWire
{
    [JsonPropertyName("slots")] public required List<string> Slots { get; init; }
    [JsonPropertyName("excludeTags")] public List<string>? ExcludeTags { get; init; }
    [JsonPropertyName("countStat")] public string? CountStat { get; init; }
    [JsonPropertyName("normalizeInput")] public string? NormalizeInput { get; init; }
    [JsonPropertyName("augments")] public List<AugmentWire>? Augments { get; init; }
}

internal sealed record AugmentWire
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("stat")] public required string Stat { get; init; }
    [JsonPropertyName("base")] public required double Base { get; init; }
    [JsonPropertyName("perLevel")] public required double PerLevel { get; init; }
    [JsonPropertyName("levels")] public required long Levels { get; init; }
}

internal sealed record AccountUpgradeWire
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("maxRank")] public required long MaxRank { get; init; }
    [JsonPropertyName("effectCap")] public long? EffectCap { get; init; }
    [JsonPropertyName("modifiersPerRank")] public required List<ModifierWire> ModifiersPerRank { get; init; }
}

internal sealed record SkillSlotWire
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("choices")] public required List<string> Choices { get; init; }
}

internal sealed record SkillWire
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("family")] public string? Family { get; init; }
    [JsonPropertyName("modifiers")] public List<ModifierWire>? Modifiers { get; init; }
    [JsonPropertyName("upgrade")] public SkillUpgradeWire? Upgrade { get; init; }
    [JsonPropertyName("modSlots")] public List<SkillModSlotWire>? ModSlots { get; init; }
    [JsonPropertyName("previewOutputs")] public List<PreviewOutputWire>? PreviewOutputs { get; init; }
}

internal sealed record SkillUpgradeWire
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("maxRank")] public required long MaxRank { get; init; }
}

internal sealed record SkillModSlotWire
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("choices")] public required List<SkillModWire> Choices { get; init; }
}

internal sealed record SkillModWire
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("input")] public required string Input { get; init; }
    [JsonPropertyName("min")] public required double Min { get; init; }
    [JsonPropertyName("max")] public required double Max { get; init; }
    [JsonPropertyName("step")] public double? Step { get; init; }
    [JsonPropertyName("format")] public string? Format { get; init; }
    [JsonPropertyName("statName")] public string? StatName { get; init; }
}

internal sealed record PreviewOutputWire
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("format")] public string? Format { get; init; }
    [JsonPropertyName("formula")] public required string Formula { get; init; }
    [JsonPropertyName("when")] public string? When { get; init; }
    [JsonPropertyName("kind")] public string? Kind { get; init; }
    [JsonPropertyName("headline")] public bool? Headline { get; init; }
}

internal sealed record SpecializationWire
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("modifiers")] public List<ModifierWire>? Modifiers { get; init; }
    [JsonPropertyName("budgets")] public List<BudgetWire>? Budgets { get; init; }
    [JsonPropertyName("nodes")] public List<NodeWire>? Nodes { get; init; }
}

internal sealed record BudgetWire
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("points")] public required long Points { get; init; }
    [JsonPropertyName("parentId")] public string? ParentId { get; init; }
}

internal sealed record NodeWire
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("budgetId")] public required string BudgetId { get; init; }
    [JsonPropertyName("cost")] public long? Cost { get; init; }
    [JsonPropertyName("tier")] public long? Tier { get; init; }
    [JsonPropertyName("prerequisites")] public List<string>? Prerequisites { get; init; }
    [JsonPropertyName("modifiers")] public List<ModifierWire>? Modifiers { get; init; }
}

internal sealed record SimulationWire
{
    [JsonPropertyName("triggers")] public List<TriggerWire>? Triggers { get; init; }
    [JsonPropertyName("skills")] public List<SimSkillWire>? Skills { get; init; }
    [JsonPropertyName("statusDamage")] public List<StatusDamageWire>? StatusDamage { get; init; }
    [JsonPropertyName("talentDamage")] public List<TalentDamageWire>? TalentDamage { get; init; }
    [JsonPropertyName("distanceDefaults")] public List<DistanceDefaultWire>? DistanceDefaults { get; init; }
    [JsonPropertyName("armorKit")] public ArmorKitWire? ArmorKit { get; init; }
}

internal sealed record ArmorKitWire
{
    [JsonPropertyName("repair")] public required double Repair { get; init; }
    [JsonPropertyName("useSeconds")] public required double UseSeconds { get; init; }
    [JsonPropertyName("extendSeconds")] public required double ExtendSeconds { get; init; }
    [JsonPropertyName("maxUseSeconds")] public required double MaxUseSeconds { get; init; }
    [JsonPropertyName("replacedBySpecializations")] public List<string>? ReplacedBySpecializations { get; init; }
    [JsonPropertyName("replacedByTalents")] public List<string>? ReplacedByTalents { get; init; }
}

internal sealed record DistanceDefaultWire
{
    [JsonPropertyName("input")] public required string Input { get; init; }
    [JsonPropertyName("bands")] public required List<DistanceBandWire> Bands { get; init; }
}

internal sealed record DistanceBandWire
{
    [JsonPropertyName("upTo")] public double? UpTo { get; init; }
    [JsonPropertyName("value")] public required double Value { get; init; }
}

internal sealed record TalentDamageWire
{
    [JsonPropertyName("talent")] public required string Talent { get; init; }
    [JsonPropertyName("on")] public required string On { get; init; }
    [JsonPropertyName("total")] public required string Total { get; init; }
    [JsonPropertyName("duration")] public required double Duration { get; init; }
    [JsonPropertyName("period")] public required double Period { get; init; }
    [JsonPropertyName("maxStacks")] public int? MaxStacks { get; init; }
}

internal sealed record StatusDamageWire
{
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("tick")] public required string Tick { get; init; }
    [JsonPropertyName("period")] public required double Period { get; init; }
    [JsonPropertyName("tickOnApply")] public bool? TickOnApply { get; init; }
    [JsonPropertyName("armorBonus")] public string? ArmorBonus { get; init; }
}

internal sealed record TriggerWire
{
    [JsonPropertyName("talent")] public required string Talent { get; init; }
    [JsonPropertyName("key")] public required string Key { get; init; }
    [JsonPropertyName("on")] public string? On { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("duration")] public double? Duration { get; init; }
    [JsonPropertyName("max")] public long? Max { get; init; }
    [JsonPropertyName("refresh")] public bool? Refresh { get; init; }
    [JsonPropertyName("every")] public double? Every { get; init; }
    [JsonPropertyName("everyGroup")] public string? EveryGroup { get; init; }
    [JsonPropertyName("chance")] public double? Chance { get; init; }
    [JsonPropertyName("requires")] public Dictionary<string, string>? Requires { get; init; }
    [JsonPropertyName("cooldownFromEnd")] public bool? CooldownFromEnd { get; init; }
    [JsonPropertyName("resetOn")] public string? ResetOn { get; init; }
    [JsonPropertyName("alsoResetOn")] public string? AlsoResetOn { get; init; }
    [JsonPropertyName("consumeOn")] public string? ConsumeOn { get; init; }
    [JsonPropertyName("phases")] public List<PhaseWire>? Phases { get; init; }
    [JsonPropertyName("percent")] public bool? Percent { get; init; }
    [JsonPropertyName("within")] public double? Within { get; init; }
    [JsonPropertyName("count")] public long? Count { get; init; }
    [JsonPropertyName("refill")] public bool? Refill { get; init; }
    [JsonPropertyName("gain")] public long? Gain { get; init; }
    [JsonPropertyName("also")] public List<GainWire>? Also { get; init; }
    [JsonPropertyName("decay")] public double? Decay { get; init; }
    [JsonPropertyName("atLeast")] public long? AtLeast { get; init; }
    [JsonPropertyName("purge")] public bool? Purge { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("applyOnHit")] public bool? ApplyOnHit { get; init; }
    [JsonPropertyName("gainWhileHolstered")] public double? GainWhileHolstered { get; init; }
    [JsonPropertyName("whileStatus")] public string? WhileStatus { get; init; }
    [JsonPropertyName("pool")] public string? Pool { get; init; }
    [JsonPropertyName("poolMax")] public long? PoolMax { get; init; }
    [JsonPropertyName("cancelOn")] public string? CancelOn { get; init; }
    [JsonPropertyName("cancelFor")] public double? CancelFor { get; init; }
    [JsonPropertyName("bank")] public string? Bank { get; init; }
    [JsonPropertyName("clearGrant")] public string? ClearGrant { get; init; }
    [JsonPropertyName("clearAmount")] public double? ClearAmount { get; init; }
    [JsonPropertyName("clearDuration")] public double? ClearDuration { get; init; }
    [JsonPropertyName("weaponTags")] public List<string>? WeaponTags { get; init; }
    [JsonPropertyName("forceHeadshot")] public bool? ForceHeadshot { get; init; }
    [JsonPropertyName("grant")] public string? Grant { get; init; }
    [JsonPropertyName("grantAmount")] public double? GrantAmount { get; init; }
    [JsonPropertyName("grantDuration")] public double? GrantDuration { get; init; }
    [JsonPropertyName("grantInstances")] public string? GrantInstances { get; init; }
    [JsonPropertyName("grantHoldDelay")] public double? GrantHoldDelay { get; init; }
    [JsonPropertyName("grantAmountStat")] public string? GrantAmountStat { get; init; }
    [JsonPropertyName("activeAfter")] public double? ActiveAfter { get; init; }
    [JsonPropertyName("countWindow")] public double? CountWindow { get; init; }
    [JsonPropertyName("maxMagazine")] public double? MaxMagazine { get; init; }
    [JsonPropertyName("whileKey")] public string? WhileKey { get; init; }
    [JsonPropertyName("whileFull")] public bool? WhileFull { get; init; }
    [JsonPropertyName("whileArmorBelow")] public double? WhileArmorBelow { get; init; }
    [JsonPropertyName("whileStatAtLeast")] public StatGateWire? WhileStatAtLeast { get; init; }
    [JsonPropertyName("durationStat")] public string? DurationStat { get; init; }
}

internal sealed record PhaseWire
{
    [JsonPropertyName("until")] public required double Until { get; init; }
    [JsonPropertyName("value")] public required string Value { get; init; }
}

internal sealed record StatGateWire
{
    [JsonPropertyName("stat")] public required string Stat { get; init; }
    [JsonPropertyName("value")] public required double Value { get; init; }
}

internal sealed record GainWire
{
    [JsonPropertyName("on")] public required string On { get; init; }
    // Optional: a stacks rule's also needs a positive gain (the validator says so), and a status rule's adds one without it.
    [JsonPropertyName("gain")] public long? Gain { get; init; }
    [JsonPropertyName("count")] public long? Count { get; init; }
}

internal sealed record SimSkillWire
{
    [JsonPropertyName("skill")] public required string Skill { get; init; }
    [JsonPropertyName("rate")] public string? Rate { get; init; }
    [JsonPropertyName("window")] public string? Window { get; init; }
    [JsonPropertyName("cooldown")] public string? Cooldown { get; init; }
    [JsonPropertyName("impact")] public string? Impact { get; init; }
    [JsonPropertyName("cooldownFromEnd")] public bool? CooldownFromEnd { get; init; }
    [JsonPropertyName("impacts")] public string? Impacts { get; init; }
    [JsonPropertyName("impactInterval")] public string? ImpactInterval { get; init; }
    [JsonPropertyName("shield")] public string? Shield { get; init; }
    [JsonPropertyName("regeneration")] public string? Regeneration { get; init; }
    [JsonPropertyName("weapons")] public List<string>? Weapons { get; init; }
    [JsonPropertyName("excludeTags")] public List<string>? ExcludeTags { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("rateArmorBonus")] public string? RateArmorBonus { get; init; }
    [JsonPropertyName("rateRead")] public string? RateRead { get; init; }
    [JsonPropertyName("stock")] public string? Stock { get; init; }
    [JsonPropertyName("stockInterval")] public double? StockInterval { get; init; }
    [JsonPropertyName("statusRate")] public string? StatusRate { get; init; }
    [JsonPropertyName("statusWindow")] public string? StatusWindow { get; init; }
    [JsonPropertyName("statusArmorBonus")] public string? StatusArmorBonus { get; init; }
}

internal sealed record PresentationWire
{
    [JsonPropertyName("theme")] public ThemeWire? Theme { get; init; }
    [JsonPropertyName("layout")] public LayoutWire? Layout { get; init; }
    [JsonPropertyName("icons")] public IconsWire? Icons { get; init; }
    [JsonPropertyName("specializationImages")] public Dictionary<string, string>? SpecializationImages { get; init; }
    [JsonPropertyName("itemArt")] public Dictionary<string, ItemArtWire>? ItemArt { get; init; }
    [JsonPropertyName("artwork")] public ArtworkWire? Artwork { get; init; }
}

internal sealed record ThemeWire
{
    [JsonPropertyName("primary")] public string? Primary { get; init; }
    [JsonPropertyName("background")] public string? Background { get; init; }
    [JsonPropertyName("variant")] public string? Variant { get; init; }
    [JsonPropertyName("colors")] public Dictionary<string, string>? Colors { get; init; }
}

internal sealed record LayoutWire
{
    [JsonPropertyName("slotGroups")] public List<SlotGroupWire>? SlotGroups { get; init; }
    [JsonPropertyName("headline")] public List<HeadlineWire>? Headline { get; init; }
    [JsonPropertyName("summary")] public List<SummaryGroupWire>? Summary { get; init; }
    [JsonPropertyName("assumptionSections")] public List<AssumptionSectionWire>? AssumptionSections { get; init; }
    [JsonPropertyName("attributeCategories")] public List<AttributeCategoryWire>? AttributeCategories { get; init; }
    [JsonPropertyName("rarities")] public List<RarityWire>? Rarities { get; init; }
    [JsonPropertyName("prototype")] public PrototypeLayoutWire? Prototype { get; init; }
}

internal sealed record PrototypeLayoutWire
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("color")] public required string Color { get; init; }
}

internal sealed record SlotGroupWire
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("slots")] public required List<string> Slots { get; init; }
    [JsonPropertyName("countAttributes")] public bool? CountAttributes { get; init; }
    [JsonPropertyName("columns")] public long? Columns { get; init; }
    [JsonPropertyName("compareStats")] public List<string>? CompareStats { get; init; }
}

internal sealed record HeadlineWire
{
    [JsonPropertyName("stat")] public string? Stat { get; init; }
    [JsonPropertyName("buildOutput")] public string? BuildOutput { get; init; }
    [JsonPropertyName("skillRates")] public bool? SkillRates { get; init; }
    [JsonPropertyName("label")] public string? Label { get; init; }
    [JsonPropertyName("conditional")] public bool? Conditional { get; init; }
    [JsonPropertyName("unavailable")] public string? Unavailable { get; init; }
    [JsonPropertyName("lowerIsBetter")] public bool? LowerIsBetter { get; init; }
}

internal sealed record SummaryGroupWire
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("stats")] public List<string>? Stats { get; init; }
    [JsonPropertyName("conditional")] public List<string>? Conditional { get; init; }
}

internal sealed record AssumptionSectionWire
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("default")] public bool? Default { get; init; }
}

internal sealed record AttributeCategoryWire
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("color")] public required string Color { get; init; }
}

internal sealed record RarityWire
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("color")] public required string Color { get; init; }
    [JsonPropertyName("tags")] public List<string>? Tags { get; init; }
    [JsonPropertyName("tagPrefixes")] public List<string>? TagPrefixes { get; init; }
}

internal sealed record IconsWire
{
    [JsonPropertyName("slots")] public Dictionary<string, string>? Slots { get; init; }
    [JsonPropertyName("talents")] public Dictionary<string, string>? Talents { get; init; }
    [JsonPropertyName("skills")] public Dictionary<string, string>? Skills { get; init; }
    [JsonPropertyName("specializations")] public Dictionary<string, string>? Specializations { get; init; }
    [JsonPropertyName("nodes")] public Dictionary<string, string>? Nodes { get; init; }
}

internal sealed record ItemArtWire
{
    [JsonPropertyName("image")] public string? Image { get; init; }
    [JsonPropertyName("icon")] public string? Icon { get; init; }
}

internal sealed record ArtworkWire
{
    [JsonPropertyName("sources")] public List<ArtworkSourceWire>? Sources { get; init; }
    [JsonPropertyName("assets")] public List<ArtworkAssetWire>? Assets { get; init; }
}

internal sealed record ArtworkSourceWire
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("attribution")] public string? Attribution { get; init; }
    [JsonPropertyName("license")] public string? License { get; init; }
    [JsonPropertyName("notes")] public string? Notes { get; init; }
}

internal sealed record ArtworkAssetWire
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("sha256")] public required string Sha256 { get; init; }
    [JsonPropertyName("sourceId")] public required string SourceId { get; init; }
}

internal sealed record ShareCodesWire
{
    [JsonPropertyName("legacyOrder")] public required LegacyOrderWire LegacyOrder { get; init; }
}

internal sealed record LegacyOrderWire
{
    [JsonPropertyName("slots")] public required List<string> Slots { get; init; }
    [JsonPropertyName("stats")] public List<string>? Stats { get; init; }
    [JsonPropertyName("scenarios")] public List<string>? Scenarios { get; init; }
    [JsonPropertyName("passiveSkills")] public List<string>? PassiveSkills { get; init; }
}
