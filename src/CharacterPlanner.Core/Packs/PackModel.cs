using System.Collections.Immutable;
using CharacterPlanner.Core.Expressions;

namespace CharacterPlanner.Core.Packs;

// The in-memory Division 2 pack (schema v2, docs/PACK_SCHEMA_V2.md). These records
// are independent of any file format: the v2 loader and the legacy v1 converter both
// produce them. Shared templates are already expanded here.

public static class ModifierOps
{
    public const string Flat = "flat";
    public const string Mult = "mult";
    public const string Override = "override";

    public static bool IsKnown(string op) => op is Flat or Mult or Override;
}

public static class StatFormats
{
    public const string Integer = "integer";
    public const string Decimal = "decimal";
    public const string Percent = "percent";

    public static bool IsKnown(string format) => format is Integer or Decimal or Percent;
}

/// <summary>What a skill preview output is, when it is one a view picks out: a damage estimate, a rate, a shield's health or its regeneration.</summary>
public static class PreviewOutputKinds
{
    public const string Damage = "damage";
    public const string Rate = "rate";
    public const string Shield = "shield";
    public const string Regeneration = "regeneration";

    public static bool IsKnown(string kind) => kind is "" or Damage or Rate or Shield or Regeneration;
}

public sealed record StatModifier
{
    public string Stat { get; init; } = "";
    public string Op { get; init; } = "";
    public ModifierValue Value { get; init; }
    /// <summary>An optional expression; it may use functions and context values but no stats.</summary>
    public string When { get; init; } = "";
    /// <summary>Selector id to required value. Every entry must match the current assumptions.</summary>
    public ImmutableDictionary<string, string>? RequiresContext { get; init; }
    /// <summary>Applies only while the owning weapon is holstered (talents on weapons only).</summary>
    public bool WhileHolstered { get; init; }
}

public sealed record StatDefinition
{
    public string ID { get; init; } = "";
    public string Name { get; init; } = "";
    public string ShortName { get; init; } = "";
    public string Description { get; init; } = "";
    public string Format { get; init; } = StatFormats.Integer;
    public double? Min { get; init; }
    public double? Max { get; init; }
    public string Formula { get; init; } = "";
}

/// <summary>A declared on/off or choice assumption that modifier gates may read.</summary>
public sealed record SelectorDefinition
{
    public string ID { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Section { get; init; } = "";
    public ImmutableArray<string> Values { get; init; } = [];
}

public sealed record NumericContextInputDefinition
{
    public string ID { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public double Min { get; init; }
    public double Max { get; init; }
    public double Step { get; init; } = 1;
    public double Default { get; init; }
    public bool Global { get; init; }
    /// <summary>Empty when the input has no labelled options.</summary>
    public ImmutableArray<NumericContextOption> Options { get; init; } = [];
    public string Group { get; init; } = "";
    public string GroupName { get; init; } = "";
    public string Section { get; init; } = "";

    /// <summary>Finite, within bounds and on a step; the resolver falls back to the default otherwise.</summary>
    public bool Accepts(double value) => double.IsFinite(value) && value >= Min && value <= Max && IsAligned(value, Min, Step);

    internal static bool IsAligned(double value, double min, double step) => Math.Abs((value - min) / step - Math.Round((value - min) / step)) <= StepTolerance.NumericInput;

    /// <summary>An accepted value moved exactly onto its step, so a 0/1 toggle stores exactly 0 or 1.</summary>
    public double Snap(double value) => Accepts(value)
        ? Math.Clamp(Math.Round(Min + Math.Round((value - Min) / Step) * Step, 12), Min, Max)
        : value;
}

public sealed record NumericContextOption(double Value, string Label);

public sealed record BuildOutputDefinition
{
    public string ID { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Format { get; init; } = "";
    public string Formula { get; init; } = "";
}

public sealed record AttributeDefinition
{
    public string ID { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>The stat the rolled value is added to (attributes are always flat).</summary>
    public string Stat { get; init; } = "";
    public double Min { get; init; }
    public double Max { get; init; }
    public double Step { get; init; }
    public string Format { get; init; } = StatFormats.Decimal;
    public string Category { get; init; } = "";
    public bool Core { get; init; }
    public double? PrototypeMin { get; init; }
    public double? PrototypeMax { get; init; }
    public string CountStat { get; init; } = "";
}

public sealed record ItemAttributeChoice
{
    public string AttributeID { get; init; } = "";
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Step { get; init; }
    /// <summary>
    /// A stat a valid configured roll of this choice adds 1 to, beside the attribute's own count stat. Being on the slot
    /// choice, it counts only rolls on the items using that slot template (Slotted's colour counts on the Investor mask).
    /// </summary>
    public string CountStat { get; init; } = "";
}

public sealed record ItemAttributeSlotDefinition
{
    public string ID { get; init; } = "";
    public string Name { get; init; } = "";
    public ImmutableArray<ItemAttributeChoice> Choices { get; init; } = [];
}

public sealed record ItemModSlotDefinition
{
    public string ID { get; init; } = "";
    public string Name { get; init; } = "";
    public ImmutableArray<string> Choices { get; init; } = [];
}

public sealed record ItemTalentSlotDefinition
{
    public string ID { get; init; } = "";
    public string Name { get; init; } = "";
    public ImmutableArray<string> Choices { get; init; } = [];
}

public sealed record ItemUpgradeDefinition
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public long MaxRank { get; init; }
    public ImmutableArray<StatModifier> ModifiersPerRank { get; init; } = [];
}

public sealed record WeaponBaseStat(string Stat, double Value);

public sealed record SlotDefinition
{
    public string ID { get; init; } = "";
    public string Name { get; init; } = "";
    public ImmutableArray<string> Accepts { get; init; } = [];
    /// <summary>The slot can hold the active weapon, selected by the weapon_slot assumption.</summary>
    public bool Weapon { get; init; }
}

public sealed record ItemDefinition
{
    public string ID { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Slot { get; init; } = "";
    public ImmutableArray<string> Tags { get; init; } = [];
    /// <summary>Base stats overridden while this weapon is active, in authored order. Empty for non-weapons.</summary>
    public ImmutableArray<WeaponBaseStat> Weapon { get; init; } = [];
    public ImmutableArray<StatModifier> Modifiers { get; init; } = [];
    public ImmutableArray<ItemAttributeSlotDefinition> AttributeSlots { get; init; } = [];
    public ImmutableArray<ItemModSlotDefinition> ModSlots { get; init; } = [];
    public ImmutableArray<ItemTalentSlotDefinition> TalentSlots { get; init; } = [];
    public ItemUpgradeDefinition? Upgrade { get; init; }
}

public sealed record ModDefinition
{
    public string ID { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public ImmutableArray<StatModifier> Modifiers { get; init; } = [];
    public string RollAttributeID { get; init; } = "";
}

public sealed record TalentCopyDefinition
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string SameTagPrefix { get; init; } = "";
    public ImmutableArray<string> ExcludeTags { get; init; } = [];
    public ImmutableArray<string> ExcludeTalents { get; init; } = [];
}

public sealed record AllyEffect
{
    public string ID { get; init; } = "";
    public string Stack { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public ImmutableArray<StatModifier> Modifiers { get; init; } = [];
}

public sealed record TalentDefinition
{
    public string ID { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public ImmutableArray<StatModifier> Modifiers { get; init; } = [];
    public ImmutableArray<string> ModeledBySets { get; init; } = [];
    public long SetPieceCredit { get; init; }
    public TalentCopyDefinition? CopiesTalent { get; init; }
    public ImmutableArray<AllyEffect> AllyEffects { get; init; } = [];
    /// <summary>The producer's source audit of how much of the talent's mechanic the pack models (R35); null when not audited.</summary>
    public TalentCoverage? Coverage { get; init; }
}

/// <summary>
/// A producer's comparison of a talent's source mechanic with what the pack models: <c>complete</c>, <c>partial</c>,
/// <c>utility</c> (nothing to model for damage, armor or skill stats), <c>unmodeled</c> or <c>unknown</c>.
/// <see cref="Omits"/> names what is left out (partial, unmodeled, unknown); <see cref="SettleBy"/> names the reading that
/// would decide an unknown.
/// </summary>
public sealed record TalentCoverage
{
    public static readonly ImmutableArray<string> Statuses = ["complete", "partial", "utility", "unmodeled", "unknown"];
    public string Status { get; init; } = "";
    public ImmutableArray<string> Omits { get; init; } = [];
    public string Evidence { get; init; } = "";
    public string SettleBy { get; init; } = "";
}

public sealed record SetBonus
{
    public long Pieces { get; init; }
    public ImmutableArray<StatModifier> Modifiers { get; init; } = [];
}

public sealed record SetDefinition
{
    public string ID { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public ImmutableArray<string> Items { get; init; } = [];
    public ImmutableArray<SetBonus> Bonuses { get; init; } = [];
}

public sealed record AugmentDefinition
{
    public string ID { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Stat { get; init; } = "";
    public double Base { get; init; }
    public double PerLevel { get; init; }
    public long Levels { get; init; }
}

public sealed record PrototypeDefinition
{
    public ImmutableArray<string> Slots { get; init; } = [];
    public ImmutableArray<string> ExcludeTags { get; init; } = [];
    public string CountStat { get; init; } = "";
    public string NormalizeInput { get; init; } = "";
    public ImmutableArray<AugmentDefinition> Augments { get; init; } = [];
}

public sealed record AccountUpgradeDefinition
{
    public string ID { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public long MaxRank { get; init; }
    public long? EffectCap { get; init; }
    public ImmutableArray<StatModifier> ModifiersPerRank { get; init; } = [];
}

public sealed record ActiveSkillSlotDefinition
{
    public string ID { get; init; } = "";
    public string Name { get; init; } = "";
    public ImmutableArray<string> Choices { get; init; } = [];
}

public sealed record SkillUpgradeDefinition
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public long MaxRank { get; init; }
}

public sealed record ActiveSkillPreviewOutputDefinition
{
    public string ID { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Format { get; init; } = StatFormats.Decimal;
    public string Formula { get; init; } = "";
    public string When { get; init; } = "";
    public string Kind { get; init; } = "";
    public bool Headline { get; init; }
}

public sealed record ActiveSkillModDefinition
{
    public string ID { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Input { get; init; } = "";
    public double Min { get; init; }
    public double Max { get; init; }
    public double? Step { get; init; }
    public string Format { get; init; } = StatFormats.Decimal;
    public string StatName { get; init; } = "";
}

public sealed record ActiveSkillModSlotDefinition
{
    public string ID { get; init; } = "";
    public string Name { get; init; } = "";
    public ImmutableArray<ActiveSkillModDefinition> Choices { get; init; } = [];
}

public sealed record ActiveSkillDefinition
{
    public string ID { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Family { get; init; } = "";
    public ImmutableArray<StatModifier> Modifiers { get; init; } = [];
    public ImmutableArray<ActiveSkillPreviewOutputDefinition> PreviewOutputs { get; init; } = [];
    public SkillUpgradeDefinition? Upgrade { get; init; }
    public ImmutableArray<ActiveSkillModSlotDefinition> ModSlots { get; init; } = [];
}

public sealed record SpecializationBudgetDefinition
{
    public string ID { get; init; } = "";
    public string Name { get; init; } = "";
    public long Points { get; init; }
    public string ParentID { get; init; } = "";
}

public sealed record SpecializationNodeDefinition
{
    public string ID { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string BudgetID { get; init; } = "";
    public long Cost { get; init; }
    public long Tier { get; init; }
    public ImmutableArray<string> Prerequisites { get; init; } = [];
    public ImmutableArray<StatModifier> Modifiers { get; init; } = [];
}

public sealed record SpecializationDefinition
{
    public string ID { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public ImmutableArray<StatModifier> Modifiers { get; init; } = [];
    public ImmutableArray<SpecializationBudgetDefinition> Budgets { get; init; } = [];
    public ImmutableArray<SpecializationNodeDefinition> Nodes { get; init; } = [];
}

/// <summary>A stat a rule waits on, and the value it has to reach.</summary>
public sealed record SimStatGate
{
    public string Stat { get; init; } = "";
    public double Value { get; init; }
}

public sealed record SimGain
{
    public string On { get; init; } = "";
    public long Gain { get; init; }
    public long Count { get; init; }
}

public sealed record SimPhase
{
    public double Until { get; init; }
    public string Value { get; init; } = "";
}

public sealed record SimTriggerDefinition
{
    public string Talent { get; init; } = "";
    public string Key { get; init; } = "";
    public string On { get; init; } = "";
    public string Kind { get; init; } = "";
    public double Duration { get; init; }
    public long Max { get; init; }
    public bool Refresh { get; init; }
    public double Every { get; init; }
    /// <summary>
    /// On a buff, stacks or ready rule with an <see cref="Every"/>: rules naming the same group share their cooldown, so one fires only if
    /// no rule in its group fired within its own every (Future Perfect and Future Perfection grant one Overcharge per player, R72).
    /// </summary>
    public string EveryGroup { get; init; } = "";
    /// <summary>
    /// On a buff or ready rule with an event: the share of the events that pass its gates which fire it (Skilled's 25%), drawn from
    /// the fight's own seeded chance stream; a failed draw neither counts nor fires. 1, the default, always fires (P13).
    /// </summary>
    public double Chance { get; init; } = 1;
    /// <summary>
    /// On a buff or ready rule with an event: selector values the fight's context must hold for the rule to take part, as a
    /// modifier's <c>requires</c> (Calculated: <c>in_cover</c> true, which a cover cycle sets for the whole fight). The
    /// selectors can't be keys a rule drives, so the fight decides once, at its start (P13).
    /// </summary>
    public ImmutableDictionary<string, string>? RequiresContext { get; init; }
    /// <summary>Counts <see cref="Every"/> from the buff being spent by its consumeOn event, not from the gain, and holds it from regaining while it's up.</summary>
    public bool CooldownFromEnd { get; init; }
    public string ResetOn { get; init; } = "";
    /// <summary>A second event that resets a rule as <see cref="ResetOn"/> does, for one with two ways to clear besides consumeOn (R55).</summary>
    public string AlsoResetOn { get; init; } = "";
    public string ConsumeOn { get; init; } = "";
    public ImmutableArray<SimPhase> Phases { get; init; } = [];
    public bool Percent { get; init; }
    public double Within { get; init; }
    public long Count { get; init; }
    public bool Refill { get; init; }
    public long Gain { get; init; }
    public ImmutableArray<SimGain> Also { get; init; } = [];
    public double Decay { get; init; }
    public long AtLeast { get; init; }
    public bool Purge { get; init; }
    public string Status { get; init; } = "";
    /// <summary>
    /// On a status rule whose event is a hit: the status lands on the hit that triggers it (Trauma) instead of the next
    /// hit (Ignited, Sadist, Eyeless).
    /// </summary>
    public bool ApplyOnHit { get; init; }
    /// <summary>
    /// On a stacks rule with weapon tags: while the weapon in hand carries none of them, the rule gains 1 stack on every
    /// tick of a free-running clock with this period, up to its cap (Quick Draw: 0.3 s while no pistol is in hand).
    /// </summary>
    public double GainWhileHolstered { get; init; }
    public string WhileStatus { get; init; } = "";
    public string Pool { get; init; } = "";
    public long PoolMax { get; init; }
    /// <summary>A buff ends when this event fires while it is active, and can't be gained again for <see cref="CancelFor"/> seconds.</summary>
    public string CancelOn { get; init; } = "";
    public double CancelFor { get; init; }
    /// <summary>A pooled stacks rule's lasting key: when the pool reaches its cap, the rule's live stacks move here, replacing the previous value.</summary>
    public string Bank { get; init; } = "";
    /// <summary>
    /// What a stacks rule grants when its reset or consume event clears live stacks: "temporary_armor" (a share of max armor
    /// per stack, lasting <see cref="ClearDuration"/> seconds) or "shield_repair" (a share of the shield's health per stack).
    /// </summary>
    public string ClearGrant { get; init; } = "";
    public double ClearAmount { get; init; }
    public double ClearDuration { get; init; }
    /// <summary>
    /// When set, the rule reacts to a weapon event (shot, hit, kill, reload, swap…) only when the weapon producing it carries
    /// one of these item tags; events with no weapon behind them are unaffected.
    /// </summary>
    public ImmutableArray<string> WeaponTags { get; init; } = [];
    /// <summary>
    /// On a ready rule triggered by hit or shot: while it is ready, the next landed hit from an eligible weapon resolves as a
    /// headshot, and that shot's own event spends the rule.
    /// </summary>
    public bool ForceHeadshot { get; init; }
    /// <summary>
    /// What a buff or ready rule grants each time it fires: "temporary_armor" (<see cref="GrantAmount"/> of max armor as
    /// bonus armor for <see cref="GrantDuration"/> seconds), "shield_repair" (that share of the shield's health),
    /// "armor_repair" (that share of max armor, at once or spread evenly over <see cref="GrantDuration"/>) or
    /// "missing_armor_repair" (that share of the armor missing at the event, at once). R41: Preservation, Clutch, Doctor Home.
    /// "skill_cooldown" takes that share of each skill's own cooldown off every running one, the shield's included (1 is a
    /// reset), and "skill_cooldown_seconds" takes <see cref="GrantAmount"/> seconds (up to 600) off them; both are instant (P13).
    /// "armor_kit_refund", only on the armor_kit event, gives back the kit just spent (<see cref="GrantAmount"/> 1), so the use
    /// that fired it costs none (Efficient, P13).
    /// </summary>
    public string Grant { get; init; } = "";
    public double GrantAmount { get; init; }
    public double GrantDuration { get; init; }
    /// <summary>
    /// On a "skill_cooldown" or "skill_cooldown_seconds" grant: when the rule fires with no skill cooldown running, the shield's
    /// included, the grant is held instead, and applies this many seconds after a skill cooldown next starts (Skilled's banked
    /// reset, 1 s). Firing again while the same grant is held adds nothing. 0, the default, applies it at once whatever is running (P13).
    /// </summary>
    public double GrantHoldDelay { get; init; }
    /// <summary>The largest <see cref="GrantAmount"/> a grant kind takes: seconds for "skill_cooldown_seconds", a share for the rest.</summary>
    public static double MaximumGrantAmount(string grant) => grant == "skill_cooldown_seconds" ? 600 : 1;
    /// <summary>The longest <see cref="GrantHoldDelay"/>, in seconds.</summary>
    public const double MaximumGrantHoldDelay = 60;
    /// <summary>
    /// In place of <see cref="GrantAmount"/>: the grant's amount is this stat's value at the fight's start, within the grant's own
    /// limits (a specialization kit bonus scaled by Efficient's factor, R82). Not for "armor_kit_refund".
    /// </summary>
    public string GrantAmountStat { get; init; } = "";
    /// <summary>
    /// On a buff or ready rule with an event: the rule takes no part until this many seconds into the fight, so an event before then
    /// neither counts nor fires (Efficient's 10 s activation delay, R82). 0, the default, is active from the start.
    /// </summary>
    public double ActiveAfter { get; init; }
    /// <summary>
    /// Whether a status rule's <see cref="Also"/> is well formed: none, or on a counted rule armed by an event other than a whole
    /// second and without applyOnHit, each entry another distinct event that adds one (gain left out or 1, no count). A whole second,
    /// a target's death and a status going on can't count: the first applies at once, the second has no status rule, and the last
    /// would let a rule count its own applications. Event names are the validator's to check.
    /// </summary>
    public static bool StatusAlsoAllowed(SimTriggerDefinition rule) => rule.Also.IsEmpty ||
        rule.Count > 0 && !rule.ApplyOnHit && rule.On.Length > 0 && rule.On != "second" &&
        rule.Also.All(gain => gain.On.Length > 0 && gain.On is not ("second" or "target_death" or "status_applied") && gain.On != rule.On && gain.Gain is 0 or 1 && gain.Count == 0) &&
        rule.Also.Select(gain => gain.On).Distinct(StringComparer.Ordinal).Count() == rule.Also.Length;
    /// <summary>The latest <see cref="ActiveAfter"/>, in seconds.</summary>
    public const double MaximumActiveAfter = 600;
    /// <summary>
    /// For an "armor_repair" spread over time: "independent", so each firing starts its own repair beside any running one
    /// (Preservation, R41). Required then; other modes are not defined.
    /// </summary>
    public string GrantInstances { get; init; } = "";
    /// <summary>
    /// On a counted buff or stacks rule: the partial count clears when no counted event arrives within this many seconds of
    /// the last one (Pummel's 20 s drop-off). Completing the count clears it as before.
    /// </summary>
    public double CountWindow { get; init; }
    /// <summary>
    /// On a stacks rule: the cap is this share of the owning weapon's magazine, rounded down, from 1 up to <see cref="Max"/>
    /// (Ardent's heat meter holds half the magazine).
    /// </summary>
    public double MaxMagazine { get; init; }
    /// <summary>
    /// On a status, buff or ready rule with an event: the rule counts, arms, applies or fires only while this buff, ready or
    /// stacks key is active, or with <see cref="WhileFull"/> while the stacks key is at its cap. A status rule's bullet
    /// events read the key as it stood when the bullet was fired (Ardent burns only from a meter that was already full); a
    /// buff or ready rule reads it as it stood before the event. With <see cref="WhileArmorBelow"/> either gate opens it.
    /// </summary>
    public string WhileKey { get; init; } = "";
    public bool WhileFull { get; init; }
    /// <summary>
    /// On a buff or ready rule with an event: the rule fires only while the player's armor is below this share of max armor
    /// (strictly), read at the event. With <see cref="WhileKey"/>, either gate opens it (Clutch: low armor, or its post-kill
    /// flag). Only fights with incoming fire track armor; elsewhere armor is full.
    /// </summary>
    public double WhileArmorBelow { get; init; }
    /// <summary>
    /// On a buff or ready rule with an event and no other gate: the rule fires only while this stat is at the value or above,
    /// resolved as the build stood before the event, so an event doesn't open its own gate (Future Perfect's kill at tier 6).
    /// </summary>
    public SimStatGate? WhileStatAtLeast { get; init; }
    /// <summary>
    /// On a buff or ready rule with no <see cref="Duration"/>: the duration is this stat's value at the fight's start
    /// (Clutch's post-kill window, 4 s plus 1 s per offensive core).
    /// </summary>
    public string DurationStat { get; init; } = "";
}

public sealed record SimSkillDefinition
{
    public string Skill { get; init; } = "";
    public string Rate { get; init; } = "";
    public string Window { get; init; } = "";
    public string Cooldown { get; init; } = "";
    public string Impact { get; init; } = "";
    public bool CooldownFromEnd { get; init; }
    public string Impacts { get; init; } = "";
    /// <summary>The output with the seconds between a use's impacts. Without it they land together, as the use deploys.</summary>
    public string ImpactInterval { get; init; } = "";
    public string Shield { get; init; } = "";
    public string Regeneration { get; init; } = "";
    public ImmutableArray<string> Weapons { get; init; } = [];
    public ImmutableArray<string> ExcludeTags { get; init; } = [];
    public string Status { get; init; } = "";
    /// <summary>A stat that adds to the armor factor of this skill's rate, so armor takes the bonus and health doesn't (R20).</summary>
    public string RateArmorBonus { get; init; } = "";
    /// <summary>"live" for a rate that follows the player's state while the skill runs; empty for one held from deployment.</summary>
    public string RateRead { get; init; } = "";
    /// <summary>The output counting charges a full skill holds. It starts full, and one comes back each cooldown.</summary>
    public string Stock { get; init; } = "";
    /// <summary>The seconds between uses while charges are held.</summary>
    public double StockInterval { get; init; }
    /// <summary>The output with the status's own damage per second. Its presence says the status deals damage of its own, apart from the rate (R65).</summary>
    public string StatusRate { get; init; } = "";
    /// <summary>The output with how long the status lasts after its last application.</summary>
    public string StatusWindow { get; init; } = "";
    /// <summary>A stat that adds to the armor factor of the status's damage, as <see cref="RateArmorBonus"/> does for the rate.</summary>
    public string StatusArmorBonus { get; init; } = "";
}

/// <summary>Damage a target takes while a status is on it: a tick of the resolved stat every period.</summary>
public sealed record SimStatusDamageDefinition
{
    public string Status { get; init; } = "";
    public string Tick { get; init; } = "";
    public double Period { get; init; }
    public bool TickOnApply { get; init; }
    /// <summary>A stat that adds to the armor factor of these ticks, read at each tick, so armor takes the bonus and health doesn't (R20).</summary>
    public string ArmorBonus { get; init; } = "";
}

/// <summary>
/// Damage a talent deals over time on its own (Toxic Delivery): its event starts a debuff on the current target that spreads
/// the resolved <see cref="Total"/> stat over one tick per <see cref="Period"/> for <see cref="Duration"/> seconds. The event
/// starts another only while fewer than <see cref="MaxStacks"/> run on the target; at the cap it does nothing.
/// </summary>
public sealed record SimTalentDamageDefinition
{
    public string Talent { get; init; } = "";
    public string On { get; init; } = "";
    public string Total { get; init; } = "";
    public double Duration { get; init; }
    public double Period { get; init; }
    /// <summary>How many of this debuff can run on one target at once, each on its own schedule (Plague of the Outcasts' 50); 1 by default.</summary>
    public int MaxStacks { get; init; } = 1;
}

/// <summary>
/// A numeric assumption whose default follows the distance (the enemy hit rate, P12): in a fight with a set distance, or for
/// each target with target distances, an input the user hasn't set takes the value of the first band whose <see cref="SimDistanceBand.UpTo"/> reaches the
/// distance, or the last band's beyond them all.
/// </summary>
public sealed record SimDistanceDefault
{
    public string Input { get; init; } = "";
    public ImmutableArray<SimDistanceBand> Bands { get; init; } = [];
}

/// <summary>One band: up to and including <see cref="UpTo"/> metres, or beyond every earlier band when it has none.</summary>
public sealed record SimDistanceBand
{
    public double? UpTo { get; init; }
    public double Value { get; init; }
}

/// <summary>
/// The armor kit a fight can use (ADR 0010): it repairs <see cref="Repair"/> of max armor when its use ends. Each hit during use
/// adds <see cref="ExtendSeconds"/>, at most once per that long, up to <see cref="MaxUseSeconds"/> in all. A build with one of
/// the replacing specializations or talents has a different kit, which fights don't model yet.
/// </summary>
public sealed record SimArmorKit
{
    public double Repair { get; init; }
    public double UseSeconds { get; init; }
    public double ExtendSeconds { get; init; }
    public double MaxUseSeconds { get; init; }
    public ImmutableArray<string> ReplacedBySpecializations { get; init; } = [];
    public ImmutableArray<string> ReplacedByTalents { get; init; } = [];
}

public sealed record SimulationRules
{
    public ImmutableArray<SimTriggerDefinition> Triggers { get; init; } = [];
    public ImmutableArray<SimSkillDefinition> Skills { get; init; } = [];
    public ImmutableArray<SimStatusDamageDefinition> StatusDamage { get; init; } = [];
    public ImmutableArray<SimTalentDamageDefinition> TalentDamage { get; init; } = [];
    public ImmutableArray<SimDistanceDefault> DistanceDefaults { get; init; } = [];
    public SimArmorKit? ArmorKit { get; init; }
}

public sealed record Theme
{
    public string Primary { get; init; } = "";
    public string Background { get; init; } = "";
    public string Variant { get; init; } = "";
    public ImmutableDictionary<string, string> Colors { get; init; } = ImmutableDictionary<string, string>.Empty;
}

public sealed record LayoutSlotGroup
{
    public string Name { get; init; } = "";
    public ImmutableArray<string> Slots { get; init; } = [];
    public bool CountAttributes { get; init; }
    public long Columns { get; init; }
    public ImmutableArray<string> CompareStats { get; init; } = [];
}

public sealed record LayoutHeadlineStat
{
    public string Stat { get; init; } = "";
    public string BuildOutput { get; init; } = "";
    public bool SkillRates { get; init; }
    public string Label { get; init; } = "";
    public bool Conditional { get; init; }
    public string Unavailable { get; init; } = "";
    public bool LowerIsBetter { get; init; }
}

public sealed record LayoutSummaryGroup
{
    public string Name { get; init; } = "";
    public ImmutableArray<string> Stats { get; init; } = [];
    public ImmutableArray<string> Conditional { get; init; } = [];
}

public sealed record LayoutAssumptionSection(string Name, bool Default);
public sealed record LayoutAttributeCategory(string ID, string Name, string Color);

public sealed record LayoutRarity
{
    public string ID { get; init; } = "";
    public string Name { get; init; } = "";
    public string Color { get; init; } = "";
    public ImmutableArray<string> Tags { get; init; } = [];
    public ImmutableArray<string> TagPrefixes { get; init; } = [];
}

public sealed record PackLayout
{
    public ImmutableArray<LayoutSlotGroup> SlotGroups { get; init; } = [];
    public ImmutableArray<LayoutSummaryGroup> Summary { get; init; } = [];
    public ImmutableArray<LayoutHeadlineStat> Headline { get; init; } = [];
    public ImmutableArray<LayoutAttributeCategory> AttributeCategories { get; init; } = [];
    public ImmutableArray<LayoutRarity> Rarities { get; init; } = [];
    public ImmutableArray<LayoutAssumptionSection> AssumptionSections { get; init; } = [];
    /// <summary>How a Prototype item shows, beside the rarities (R74): a build choice, not an item tag, so it has no tags.</summary>
    public LayoutPrototype? Prototype { get; init; }
}

/// <summary>Prototype quality's name and colour, as the game's loot quality table gives them (R74).</summary>
public sealed record LayoutPrototype
{
    public string Name { get; init; } = "";
    public string Color { get; init; } = "";
}

public sealed record ArtworkSource
{
    public string ID { get; init; } = "";
    public string Name { get; init; } = "";
    public string URL { get; init; } = "";
    public string Attribution { get; init; } = "";
    public string License { get; init; } = "";
    public string Notes { get; init; } = "";
}

public sealed record ArtworkAsset
{
    public string ID { get; init; } = "";
    public string Path { get; init; } = "";
    public string Kind { get; init; } = "";
    public string SHA256 { get; init; } = "";
    public string SourceID { get; init; } = "";
}

public sealed record ItemArtwork(string Image, string Icon);

/// <summary>Read by the desktop app only; never by the calculation engine.</summary>
public sealed record PackPresentation
{
    public Theme? Theme { get; init; }
    public PackLayout? Layout { get; init; }
    /// <summary>Icon asset ids keyed by group ("slots", "talents", "skills", "specializations", "nodes") then object id.</summary>
    public ImmutableDictionary<string, ImmutableDictionary<string, string>> Icons { get; init; } = ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty;
    /// <summary>Specialization artwork (large images) keyed by specialization id.</summary>
    public ImmutableDictionary<string, string> SpecializationImages { get; init; } = ImmutableDictionary<string, string>.Empty;
    public ImmutableDictionary<string, ItemArtwork> ItemArt { get; init; } = ImmutableDictionary<string, ItemArtwork>.Empty;
    public ImmutableArray<ArtworkSource> ArtworkSources { get; init; } = [];
    public ImmutableArray<ArtworkAsset> ArtworkAssets { get; init; } = [];
}

/// <summary>Fixed v1 orders needed to decode legacy (v1–v12) share codes.</summary>
public sealed record LegacyShareOrder
{
    public ImmutableArray<string> Slots { get; init; } = [];
    public ImmutableArray<string> Stats { get; init; } = [];
    public ImmutableArray<string> Scenarios { get; init; } = [];
    public ImmutableArray<string> PassiveSkills { get; init; } = [];
}

/// <summary>A pack-owned limit on items with one tag across explicitly named equipment slots.</summary>
public sealed record EquipmentLimitDefinition
{
    public string ID { get; init; } = "";
    public string Name { get; init; } = "";
    public string Tag { get; init; } = "";
    public ImmutableArray<string> Slots { get; init; } = [];
    public long Maximum { get; init; }
}

public sealed record GamePack
{
    public const string WeaponSlotSelector = "weapon_slot";

    public string ID { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string GameVersion { get; init; } = "";
    public string Description { get; init; } = "";
    public ImmutableArray<string> Capabilities { get; init; } = [];
    /// <summary>The fixed character level (40 for Division 2), visible to skill previews as "level".</summary>
    public long Level { get; init; }
    public ImmutableArray<StatDefinition> Stats { get; init; } = [];
    public ImmutableDictionary<string, ImmutableArray<CurvePoint>> Curves { get; init; } = ImmutableDictionary<string, ImmutableArray<CurvePoint>>.Empty;
    public ImmutableArray<SelectorDefinition> Selectors { get; init; } = [];
    public ImmutableArray<NumericContextInputDefinition> NumericContextInputs { get; init; } = [];
    public ImmutableArray<BuildOutputDefinition> BuildOutputs { get; init; } = [];
    public ImmutableArray<AttributeDefinition> Attributes { get; init; } = [];
    public ImmutableArray<SlotDefinition> Slots { get; init; } = [];
    public ImmutableArray<EquipmentLimitDefinition> EquipmentLimits { get; init; } = [];
    public ImmutableArray<ItemDefinition> Items { get; init; } = [];
    public ImmutableArray<ModDefinition> Mods { get; init; } = [];
    public ImmutableArray<TalentDefinition> Talents { get; init; } = [];
    public ImmutableArray<SetDefinition> Sets { get; init; } = [];
    public PrototypeDefinition? Prototype { get; init; }
    public ImmutableArray<AccountUpgradeDefinition> AccountUpgrades { get; init; } = [];
    public ImmutableArray<ActiveSkillSlotDefinition> ActiveSkillSlots { get; init; } = [];
    public ImmutableArray<ActiveSkillDefinition> ActiveSkills { get; init; } = [];
    public ImmutableArray<SpecializationDefinition> Specializations { get; init; } = [];
    /// <summary>Collection name ("item", "talent", ...) to old id to current id.</summary>
    public ImmutableDictionary<string, ImmutableDictionary<string, string>> RenamedIds { get; init; } = ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty;
    public SimulationRules Simulation { get; init; } = new();
    public PackPresentation Presentation { get; init; } = new();
    public LegacyShareOrder LegacyShareOrder { get; init; } = new();
}
