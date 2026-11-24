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
    /// <summary>
    /// The value an unset selector reads, one of <see cref="Values"/>; empty for none, so an unset selector matches no
    /// <c>requires</c>. Versatile's bands (ADR 0019): "any", the weapon's own band, keeps its bonus where no distance is known.
    /// </summary>
    public string Default { get; init; } = "";
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

    /// <summary>A value's option label, or the value itself as invariant text when it has none.</summary>
    public string Shown(double value) => Options.IsDefaultOrEmpty || Options.FirstOrDefault(option => option.Value == value) is not { } option
        ? value.ToString("0.##########", System.Globalization.CultureInfo.InvariantCulture) : option.Label;

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
    /// <summary>
    /// On a stacks rule with a duration: one timer, started by the gain that takes the count from 0, ends every stack together; later
    /// gains don't move it (Cover Shooter, R112). <see cref="RefreshOn"/> may restart it.
    /// </summary>
    public bool FromFirstGain { get; init; }
    /// <summary>
    /// On a <c>skill_out</c> buff (ADR 0033: the Precision Smart Cover): while it's on, each weapon swap refills the weapon put away to a
    /// full magazine, with no reload event.
    /// </summary>
    public bool RefillOnSwap { get; init; }
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
    /// <summary>
    /// With <see cref="Refill"/> (the pack's <c>refill: "both"</c>, ADR 0019: Faster Than Reloading): the holstered weapon of a rotation
    /// is refilled too, not only the weapon in hand.
    /// </summary>
    public bool RefillBoth { get; init; }
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
    /// <summary>
    /// On a ready rule without an event (ADR 0019: Perturb, Faster Than Reloading): the key is ready once the rule's talent's weapon
    /// has been put away this many seconds without a break, and drawing the weapon spends it. Rules on the draw (swapped) read it as
    /// it stood before, with whileKey.
    /// </summary>
    public double PrimeHolstered { get; init; }
    /// <summary>
    /// On a buff or stacks rule with an event (ADR 0019: Unwavering, Back and Forth): with the talent on both rotated weapons, the
    /// rule isn't copied per weapon. It drives one key from whichever weapon is in hand, so a swap's cancel on the weapon leaving
    /// holds for the one drawn, and a gain on the draw replaces the other copy's.
    /// </summary>
    public bool ShareCopies { get; init; }
    /// <summary>
    /// The item tags that name weapon types (ADR 0019: Versatile), for <see cref="DifferentTypes"/> and <see cref="EveryPerType"/>. A
    /// weapon's type is the first of these it carries.
    /// </summary>
    public ImmutableArray<string> WeaponTypes { get; init; } = [];
    /// <summary>On a buff or stacks rule: it gains only when the event's weapon and the other rotated weapon are both typed, by
    /// <see cref="WeaponTypes"/>, and of different types.</summary>
    public bool DifferentTypes { get; init; }
    /// <summary>On a buff or stacks rule with <see cref="Every"/>: the cooldown runs for each type of the event's weapon apart, and a
    /// weapon without a type gains nothing.</summary>
    public bool EveryPerType { get; init; }
    /// <summary>On a distance rule: a fight without a distance doesn't refuse it; the rule steps aside and its key reads the user's
    /// setting, and the result says so (ADR 0019: Versatile keeps its assumption).</summary>
    public bool AssumeWithoutDistance { get; init; }
    /// <summary>On a buff with <see cref="CancelOn"/> and <see cref="CancelFor"/> (ADR 0019: Unwavering): the event starts the lockout
    /// whether the buff is on or not.</summary>
    public bool LockoutAlways { get; init; }
    /// <summary>On such a buff: when the lockout ends with the rule's talent's weapon in hand, the buff gains for its duration (Unwavering's
    /// bonus after a lockout). A draw during the lockout gains nothing then.</summary>
    public bool GainAtLockoutEnd { get; init; }
    /// <summary>
    /// Whether this stacks rule and another can write one key (Chemical Agent's enemy and player parts): one talent's, with the same
    /// cap, duration, refresh and decay. Neither may clear, purge, force a headshot or keep state of its own beside the stacks (a pool,
    /// bank, clear grant, refreshOn, holstered gain, shared timer or magazine cap), so the two gaining in one event come out as one gain
    /// of their sum, in either order. Each gains its own way into the one count.
    /// </summary>
    public bool SharesStacksWith(SimTriggerDefinition other) => FitsSharedStacks(this) && FitsSharedStacks(other) && Talent == other.Talent &&
        Max == other.Max && Duration == other.Duration && DurationStat == other.DurationStat && Refresh == other.Refresh && Decay == other.Decay;

    /// <summary>What <see cref="SharesStacksWith"/> asks, for the validator's and the fight's refusals.</summary>
    public const string SharedStacksRule = "Stacks rules on one key must be one talent's, with the same max, duration, refresh and decay, and none may " +
        "reset, consume, purge, force a headshot, or have a pool, bank, clear grant, refreshOn, gainWhileHolstered, fromFirstGain or maxMagazine";

    // Plain timed stacks on the key, gained and expiring only.
    private static bool FitsSharedStacks(SimTriggerDefinition rule) => rule.Kind == "stacks" && rule.Pool.Length == 0 && rule.Bank.Length == 0 &&
        rule.ClearGrant.Length == 0 && rule.RefreshOn.Length == 0 && rule.GainWhileHolstered == 0 && !rule.FromFirstGain && rule.MaxMagazine == 0 &&
        rule.ResetOn.Length == 0 && rule.AlsoResetOn.Length == 0 && rule.ConsumeOn.Length == 0 && !rule.Purge && !rule.ForceHeadshot;

    /// <summary>
    /// Whether <see cref="LockoutAlways"/> and <see cref="GainAtLockoutEnd"/> sit on a rule they suit: a timed buff with a lockout and
    /// nothing that consumes, resets, counts or cools it down. The gain at a lockout's end passes no gate, chance or activation delay
    /// and gives no grant or refill, so a rule with one of those can't take it. The validator and the fight both ask this.
    /// </summary>
    public bool LockoutFieldsFit => !(LockoutAlways || GainAtLockoutEnd) ||
        Kind == "buff" && CancelOn.Length > 0 && CancelFor > 0 && Duration > 0 && ConsumeOn.Length == 0 && ResetOn.Length == 0 &&
        !CooldownFromEnd && Count == 0 && Every == 0 &&
        (!GainAtLockoutEnd || WhileKey.Length == 0 && WhileStatus.Length == 0 && WhilePlayerStatus.Length == 0 && WhileArmorBelow == 0 &&
            WhileStatAtLeast is null && Chance >= 1 && Grant.Length == 0 && !Refill && ActiveAfter == 0);
    /// <summary>The longest a weapon must be put away to prime a rule.</summary>
    public const double MaximumPrimeHolstered = 600;
    public string WhileStatus { get; init; } = "";
    /// <summary>
    /// On a buff or stacks rule: it gains only while this status (or with "any", some status) is on the player, from an enemy's hit
    /// (ADR 0018). A fight without statuses on the player never opens it.
    /// </summary>
    public string WhilePlayerStatus { get; init; } = "";
    /// <summary>
    /// With <see cref="WhileStatus"/> or <see cref="WhilePlayerStatus"/> "any": statuses that don't count toward it, so "any" means any
    /// other status (Chemical Agent's check leaves out ensnared, R122).
    /// </summary>
    public ImmutableArray<string> AnyStatusExcept { get; init; } = [];

    /// <summary>
    /// Whether <see cref="AnyStatusExcept"/> fits: unset, or with "any" on either gate, distinct statuses that <paramref name="known"/>
    /// accepts, leaving at least one of the <paramref name="count"/> known statuses to count. The validator and the fight both ask this.
    /// </summary>
    public bool AnyStatusExceptFits(Func<string, bool> known, int count) => AnyStatusExcept.IsDefaultOrEmpty ||
        (WhileStatus == "any" || WhilePlayerStatus == "any") && AnyStatusExcept.All(known) &&
        AnyStatusExcept.Distinct(StringComparer.Ordinal).Count() == AnyStatusExcept.Length && AnyStatusExcept.Length < count;
    public string Pool { get; init; } = "";
    public long PoolMax { get; init; }
    /// <summary>A buff ends when this event fires while it is active, and can't be gained again for <see cref="CancelFor"/> seconds.</summary>
    public string CancelOn { get; init; } = "";
    public double CancelFor { get; init; }
    /// <summary>
    /// A buff that is on restarts its duration when this event fires, and the event gains nothing while it's off (ADR 0019:
    /// Unwavering's kills).
    /// </summary>
    public string RefreshOn { get; init; } = "";

    /// <summary>The events that clear, spend, cancel or refresh the rule, set or not; its gains are <see cref="On"/> and <see cref="Also"/>.</summary>
    public IEnumerable<string> OtherEvents => [ResetOn, AlsoResetOn, ConsumeOn, CancelOn, RefreshOn];
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
    /// <summary>With <see cref="ForceHeadshot"/>: the forced hit is also a critical hit (ADR 0023: Perfect Determined).</summary>
    public bool ForceCritical { get; init; }
    /// <summary>
    /// With <see cref="ForceHeadshot"/>: every landed pellet of the forced shot is forced, not only the first (ADR 0023: Determined, by
    /// the owner's call).
    /// </summary>
    public bool ForceWholeShot { get; init; }
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
    /// <summary>
    /// The output with the radius, in metres, of each impact's area (ADR 0016): an impact lands on every living enemy within it of the
    /// enemy being shot, with the burn or bleed it puts on. Empty for one enemy.
    /// </summary>
    public string Area { get; init; } = "";
    /// <summary>
    /// The output with the radius, in metres, within which an area's impact deals full damage (R107). Beyond it the damage falls in a
    /// straight line to <see cref="AreaEdge"/> of it at the area's radius. Empty for full damage throughout.
    /// </summary>
    public string AreaFull { get; init; } = "";
    /// <summary>The share of an area's impact an enemy at its radius takes, above 0 up to 1, with <see cref="AreaFull"/>.</summary>
    public double AreaEdge { get; init; }
    /// <summary>The widest skill area, in metres.</summary>
    public const double MaximumArea = 50;
    /// <summary>
    /// Whether a control skill's <see cref="Status"/> (shocked, disoriented, ensnared, blinded or disrupted) goes on the enemy being shot as the
    /// skill is used, for the duration its <see cref="Window"/> output gives, and with an <see cref="Area"/> on those in it (ADR 0017).
    /// </summary>
    public bool ControlStatus { get; init; }
    /// <summary>
    /// A control status's <see cref="Area"/> measured from the player rather than the enemy being shot (ADR 0035: the Jammer Pulse):
    /// an enemy is reached when its own distance from the player is within it.
    /// </summary>
    public bool AreaFromPlayer { get; init; }
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
    /// <summary>
    /// Selector values the fight's context must hold for the skill to act (ADR 0027: the Demolisher Firefly's weakpoint). Read once for
    /// the whole fight; when they aren't held, the skill steps aside as a <see cref="SimSkillAside"/> skill does.
    /// </summary>
    public ImmutableDictionary<string, string>? RequiresContext { get; init; }
    /// <summary>
    /// The output with the most distinct enemies a use's impact strikes, one each, as it's used (ADR 0027: the Demolisher Firefly's
    /// targets): the enemy being shot, then the nearest others. Empty for the enemy being shot alone.
    /// </summary>
    public string Targets { get; init; } = "";
    /// <summary>
    /// The output with the armor each landed impact repairs on the player, under enemy fire, capped at the maximum, whether or not
    /// it's needed (ADR 0028: the Leech Hive). Empty for none.
    /// </summary>
    public string ImpactRepair { get; init; } = "";
    /// <summary>The seconds between a use's last impact and its end, which a cooldown from the end waits out (ADR 0028: 2 s).</summary>
    public double EndDelay { get; init; }
    /// <summary>The longest <see cref="EndDelay"/>, in seconds.</summary>
    public const double MaximumEndDelay = 60;
    /// <summary>
    /// Seconds from each landed impact to its <see cref="ImpactRepair"/> (ADR 0030: the Leech Hive's travel time plus 0.8 s); 0 for a
    /// repair with the impact. A repair due at or after the fight's end lands nothing.
    /// </summary>
    public double RepairDelay { get; init; }
    /// <summary>
    /// The share of the player's maximum armor their armor must be below for the skill to be used (ADR 0030: 1, below full, for the
    /// repair skills); 0 for no such wait. A ready skill waits until it holds. Without enemy fire it never does, so the skill steps aside.
    /// </summary>
    public double UseArmorBelow { get; init; }
    /// <summary>
    /// A repair skill's (ADR 0030): the output with the armor each repair restores on the player, capped, or with a
    /// <see cref="RepairCloud"/> the armor a second. Empty for a skill that repairs nothing this way. Through the use's window,
    /// every <see cref="RepairEvery"/> seconds from the use, the skill checks the player's armor: below <see cref="RepairBelow"/>
    /// of the maximum (below full when 0), and with the last repair landed and its cloud over, it repairs, landing
    /// <see cref="RepairLag"/> seconds later. Each repair spends one of <see cref="RepairCount"/>'s, if it has one; with all spent the
    /// use ends, <see cref="EndDelay"/> after the last landed.
    /// </summary>
    public string Repair { get; init; } = "";
    public double RepairEvery { get; init; }
    public double RepairLag { get; init; }
    public double RepairBelow { get; init; }
    /// <summary>The output with how many repairs a use holds (charges, mines); empty for as many as its window allows.</summary>
    public string RepairCount { get; init; } = "";
    /// <summary>
    /// The output with the seconds each repair lasts as a cloud, restoring half of <see cref="Repair"/> every 0.5 s from 0.5 s after it
    /// lands, whatever the player's armor; empty for a repair that lands at once.
    /// </summary>
    public string RepairCloud { get; init; } = "";
    /// <summary>
    /// A buff hive's (ADR 0031): the output with the seconds each charge's buff lasts. Its charges are timed as a repair skill's
    /// (<see cref="RepairEvery"/>, <see cref="RepairLag"/>, <see cref="RepairCount"/>), with no armor test: each lands as a
    /// <c>skill_charge</c> event that only the skill's own trigger rules hear, and the next waits until this long after it. Empty for none.
    /// </summary>
    public string ChargeFor { get; init; } = "";
    /// <summary>
    /// Whether a buff hive's charges go to the build's other skills (ADR 0031: the Artificer), not the player: each to a use of another
    /// slot's skill whose window runs, and the same use again only <see cref="ChargeFor"/> after the last one landed on it.
    /// </summary>
    public bool ChargeSkills { get; init; }
    /// <summary>The output with the seconds each charge adds to the running use it lands on (ADR 0031); empty for none.</summary>
    public string Refresh { get; init; } = "";
    /// <summary>
    /// A skill that draws enemy fire (ADR 0034: the Decoy): the output with its health. While its use runs and its health lasts, enemy
    /// hits land on it instead of the player, each spending the stat <c>decoy_damage_share</c> of its raw damage; spent, the use ends.
    /// Empty for none.
    /// </summary>
    public string DecoyHealth { get; init; } = "";
    /// <summary>A skill marking zones on the enemy being shot (ADR 0036: the Achilles Pulse); null for none.</summary>
    public SimSkillZones? Zones { get; init; }
    /// <summary>A skill reviving its user once a use, when enemy fire downs them while it's out (ADR 0037: the Reviver Hive); null for none.</summary>
    public SimSkillRevive? Revive { get; init; }
    /// <summary>
    /// A hive charging skills while holstered (ADR 0031 stage 2c): the seconds between its pulses whenever it isn't out, each sending a
    /// charge to every running use that may take one without spending any, and the seconds from a pulse to its landing (the distance read
    /// as seconds), after which the target takes the next only as long again. 0 for no pulses.
    /// </summary>
    public double HolsteredEvery { get; init; }
    public double HolsteredLag { get; init; }
    /// <summary>The shortest <see cref="HolsteredEvery"/>, in seconds.</summary>
    public const double MinimumHolsteredEvery = 0.05;
    /// <summary>The longest <see cref="RepairEvery"/>, in seconds.</summary>
    public const double MaximumRepairEvery = 10;
    /// <summary>Whether the impact stops at the enemy's armor and never reaches health (ADR 0029: The Howl).</summary>
    public bool ArmorOnly { get; init; }
    /// <summary>
    /// Seconds from a use to its effects (ADR 0029: The Howl's wind-up and wave), or <see cref="FarImpactDelay"/> in a fight whose
    /// enemy distance is beyond <see cref="FarFrom"/> metres. The cooldown still counts from the use.
    /// </summary>
    public double ImpactDelay { get; init; }
    public double FarImpactDelay { get; init; }
    public double FarFrom { get; init; }
    /// <summary>Seconds after a use that its cooldown starts, for a cooldown not from the use's end (ADR 0029: 1.2 s).</summary>
    public double CooldownAfter { get; init; }
    /// <summary>The longest <see cref="ImpactDelay"/>, <see cref="FarImpactDelay"/>, <see cref="CooldownAfter"/> or <see cref="KillDelay"/>, in seconds.</summary>
    public const double MaximumUseDelay = 10;
    /// <summary>
    /// The output with the chance (0 to 1) that a landed impact kills an enemy outright (ADR 0029: The Howl), rolled once per enemy it
    /// reaches whose profile is in <see cref="KillProfiles"/> and whose health is below full. The enemy dies <see cref="KillDelay"/> later,
    /// with no kill repair or kill event for talents. Empty for none.
    /// </summary>
    public string KillChance { get; init; } = "";
    /// <summary>The <c>enemy_target</c> profiles an outright kill can reach (ADR 0029: Normal and Veteran).</summary>
    public ImmutableArray<int> KillProfiles { get; init; } = [];
    public double KillDelay { get; init; }
    /// <summary>
    /// Whether the <see cref="Area"/> is a cloud's (ADR 0024): a status window's with no impact, putting its status on everyone in it
    /// while the use applies. An impact's area puts its status on once per impact instead.
    /// </summary>
    public bool IsCloud => Area.Length > 0 && Impact.Length == 0 && StatusRate.Length > 0;
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
/// A talent's blast (ADR 0013): on its event, <see cref="Amount"/> damage to each enemy in the group within <see cref="Within"/>
/// metres of the enemy the event is about (a kill's victim, a hit's target), on that enemy's own pools. Enemies are placed by the
/// fight's enemySpacing; without it, or with one enemy at a time, a blast finds nobody (Regicide's 400% within 5 m).
/// </summary>
public sealed record SimBlastDefinition
{
    public string Talent { get; init; } = "";
    public string On { get; init; } = "";
    /// <summary>The stat giving each enemy's damage, read as the fight's talent damage totals are; empty for a blast that only marks.</summary>
    public string Amount { get; init; } = "";
    /// <summary>
    /// A stacks rule of the same talent held per enemy (cleared at <c>target_death</c>), which gains a stack on each enemy reached and
    /// left alive (Unnerve's marks). Empty for none.
    /// </summary>
    public string Mark { get; init; } = "";
    public double Within { get; init; }
    /// <summary>
    /// A status the blast puts on each enemy it reaches and leaves alive (Regicide's bleed), for <see cref="StatusDuration"/> seconds
    /// scaled as the player's own statuses are; it ticks there as any status does. Empty for none.
    /// </summary>
    public string Status { get; init; } = "";
    public double StatusDuration { get; init; }
    /// <summary>The widest blast, in metres.</summary>
    public const double MaximumWithin = 50;
}

/// <summary>
/// A talent's pass-on (ADR 0013): when an enemy dies with that talent's debuff stacks on it (its talentDamage row), they move to the
/// nearest living enemy in the group within <see cref="Within"/> metres, keeping their schedules, up to the receiver's stack cap
/// (Plague of the Outcasts, 25 m). A receiver dying passes them on again.
/// </summary>
public sealed record SimPassOnDefinition
{
    public string Talent { get; init; } = "";
    public double Within { get; init; }
    /// <summary>A stat giving the reach in metres at the fight's start, in place of <see cref="Within"/> (Proliferation's 15 m). Empty for none.</summary>
    public string WithinStat { get; init; } = "";
    /// <summary>
    /// For a status pass-on (Indirect Transmission): the status on the dying enemy that passes on, or "any" for each one on it, in
    /// place of a debuff. Empty for the talent's debuff.
    /// </summary>
    public string Status { get; init; } = "";
    /// <summary>A status pass-on's share of the status's duration as it went on the dying enemy (Indirect Transmission's 50%).</summary>
    public double Share { get; init; }
    /// <summary>A stat giving a status pass-on's share at the fight's start, in place of <see cref="Share"/> (Proliferation's 75%). Empty for none.</summary>
    public string ShareStat { get; init; } = "";
    /// <summary>
    /// Whether a status pass-on also takes a skill's own burn or bleed on the dying enemy (ADR 0015), as the player's status, for its
    /// share of one application.
    /// </summary>
    public bool SkillStatuses { get; init; }
    /// <summary>"nearest" (the default) or, for a status, "all" the living enemies in reach.</summary>
    public string To { get; init; } = "";
    /// <summary>The farthest pass-on, in metres.</summary>
    public const double MaximumWithin = 50;
}

/// <summary>
/// A talent's spread (ADR 0013): when a status newly goes on the enemy being shot, the same status goes on each enemy in the group
/// within <see cref="Within"/> metres of it, for what's left of its duration, at most every <see cref="Every"/> seconds (Creeping
/// Death's 8 m and 15 s). A refresh isn't an application, and a spread status sets off no other.
/// </summary>
public sealed record SimSpreadDefinition
{
    public string Talent { get; init; } = "";
    public double Within { get; init; }
    public double Every { get; init; }
    /// <summary>
    /// Whether a skill's own burn or bleed newly on the enemy being shot spreads too (ADR 0015), as the player's status, for what's left
    /// of it up to one application.
    /// </summary>
    public bool SkillStatuses { get; init; }
    /// <summary>The widest spread, in metres.</summary>
    public const double MaximumWithin = 50;
}

/// <summary>
/// A talent's trap (ADR 0013, The Trap): with its weapon in hand, an enemy the player has hit for <see cref="TagAfter"/> seconds is
/// tagged, up to <see cref="MaxTags"/>, the oldest dropped. A headshot kill while the trap is ready, with another enemy tagged, puts
/// <see cref="Status"/> on every other tagged enemy for <see cref="StatusDuration"/> seconds, at any distance, clears the tags and
/// starts a cooldown of <see cref="Every"/> seconds, which each later headshot kill cuts by <see cref="Cut"/>.
/// </summary>
public sealed record SimTrapDefinition
{
    public string Talent { get; init; } = "";
    public double TagAfter { get; init; }
    public int MaxTags { get; init; }
    public string Status { get; init; } = "";
    public double StatusDuration { get; init; }
    public double Every { get; init; }
    public double Cut { get; init; }
    /// <summary>The most enemies a trap can tag.</summary>
    public const int MaximumTags = 10;
}

/// <summary>
/// A talent's redirect (ADR 0013, Bewildered): each weapon hit on a living enemy moves <see cref="Share"/> of its damage to another
/// living enemy within <see cref="Within"/> metres of it, chosen at random, which takes that share as a fixed amount. With nobody
/// in reach the hit lands whole.
/// </summary>
public sealed record SimRedirectDefinition
{
    public string Talent { get; init; } = "";
    public double Share { get; init; }
    public double Within { get; init; }
    /// <summary>The widest redirect, in metres.</summary>
    public const double MaximumWithin = 50;
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

/// <summary>
/// An input's default in fights with enemy fire (owner, 2026-11-14): an input the user hasn't set takes <see cref="Value"/> there, and keeps
/// its ordinary default elsewhere. The enemy that fires is the enemy shot, so a target default sets both. Only the target takes one for now.
/// </summary>
public sealed record SimIncomingDefault
{
    public string Input { get; init; } = "";
    public double Value { get; init; }
}

/// <summary>
/// A status an enemy's hit puts on the player (ADR 0018): in a fight with enemy fire, each hit that lands from an enemy of profile
/// <see cref="Target"/> puts <see cref="Status"/> on the player for <see cref="Duration"/> seconds, to the later end if it's on. With
/// <see cref="Tick"/>, the resolved stat is dealt every <see cref="Period"/> through bonus armor, armor then health, as a hit is. The
/// <see cref="Resistances"/> stats, summed and capped at 1, shorten the duration by that share, not below <see cref="MinDuration"/>;
/// at 1 the status doesn't go on. A control status goes on with no effect on the player yet.
/// </summary>
public sealed record SimEnemyStatusDefinition
{
    public int Target { get; init; }
    public string Status { get; init; } = "";
    public double Duration { get; init; }
    public string Tick { get; init; } = "";
    public double Period { get; init; }
    public bool TickOnApply { get; init; }
    public ImmutableArray<string> Resistances { get; init; } = [];
    public double MinDuration { get; init; }
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
/// the replacing specializations or talents has a different kit: fights model it only for a talent in <see cref="Abilities"/>.
/// </summary>
public sealed record SimArmorKit
{
    /// <summary>
    /// Talents whose ability is the build's kit (ADR 0021: Hackstep Protocol). In a fight with kits, the ability is used, as a kit is,
    /// when armor is below the kit threshold at the weapon's next decision, but at once, with no use time, on its own cooldown and
    /// with no count; it fires <c>kit_ability</c>, whose rules grant its repair and bonus armor. While it cools, an
    /// <see cref="Instant"/> kit from the carried count is used instead.
    /// </summary>
    public ImmutableArray<SimKitAbility> Abilities { get; init; } = [];
    /// <summary>The instant kit (a double press): used only in fights with a kit ability, while the ability cools.</summary>
    public SimInstantKit? Instant { get; init; }
    public double Repair { get; init; }
    public double UseSeconds { get; init; }
    public double ExtendSeconds { get; init; }
    public double MaxUseSeconds { get; init; }
    public ImmutableArray<string> ReplacedBySpecializations { get; init; } = [];
    public ImmutableArray<string> ReplacedByTalents { get; init; } = [];
}

/// <summary>
/// A talent's cycle of consecutive headshots with weapons carrying <see cref="WeaponTag"/> (ADR 0021: Headache, R126). Each shot with
/// such a weapon counts: a headshot adds 1, and from <see cref="KillFrom"/> on only a headshot that kills does. Any other shot (a body
/// hit, a shot that lands nothing, or a headshot that doesn't kill from <see cref="KillFrom"/>) is a miss: the count goes back to 0,
/// unless the <see cref="Allowance"/> stat's misses per cycle forgive it. Swapping weapons resets the cycle. On the new count: at 1,
/// <see cref="Key"/> goes on until the next headshot with any weapon has landed; at 2, <see cref="Repair"/> of max armor is repaired, or
/// at full armor added as lasting bonus armor while bonus armor is below <see cref="BonusArmorCap"/> of max armor; at 3, the magazine
/// is refilled; past 3, all three. Stats are read as the fight starts.
/// </summary>
public sealed record SimHeadshotCycle
{
    public string Talent { get; init; } = "";
    public string WeaponTag { get; init; } = "";
    public string Key { get; init; } = "";
    public string Repair { get; init; } = "";
    public string BonusArmorCap { get; init; } = "";
    public string Allowance { get; init; } = "";
    public int KillFrom { get; init; }
}

/// <summary>
/// A talent's bonus on the hit after a headshot kill (ADR 0023: Headhunter). A headshot kill with any weapon arms it: <see cref="Share"/>
/// of the killing pellet's damage as dealt (the player's own bonuses, before the dead enemy's armor and the health it had left), up to
/// the <see cref="Cap"/> stat of the weapon in hand. The next pellet to land on a living enemy, any weapon and body shots included, adds
/// it and spends it, and meets that enemy's armor like the rest of the hit. A new headshot kill replaces it, sized without the bonus it
/// carried; it lapses after <see cref="Window"/> seconds. The cap is read for each weapon as the fight starts.
/// </summary>
public sealed record SimNextHitBonus
{
    public string Talent { get; init; } = "";
    public double Share { get; init; }
    public string Cap { get; init; } = "";
    public double Window { get; init; }
}

/// <summary>A talent's kit ability: its <see cref="Cooldown"/> stat, in seconds, read as the fight starts.</summary>
public sealed record SimKitAbility
{
    public string Talent { get; init; } = "";
    public string Cooldown { get; init; } = "";
}

/// <summary>
/// A skill that plays no part in a fight (ADR 0022): a fight with skills on runs without it, never using it, rather than refusing the
/// build. <see cref="Why"/> says what it does that fights don't model, for the result's note.
/// </summary>
public sealed record SimSkillAside
{
    public string Skill { get; init; } = "";
    public string Why { get; init; } = "";
}

/// <summary>
/// Zones a skill's use marks on the enemy being shot for the use's window (ADR 0036: the Achilles Pulse). While it's marked and the
/// zones' health lasts, <see cref="Share"/> of the weapon's body hits on it land on a zone: each deals the body hit's damage with the
/// zone multiplier, <see cref="Multiplier"/> × <see cref="Factor"/> (at least 1), added to critical damage as a headshot's is, and
/// spends that much of the zones' health. It stays a body hit for every event and talent.
/// </summary>
public sealed record SimSkillZones
{
    /// <summary>The output with how many zones a use marks.</summary>
    public string Count { get; init; } = "";
    /// <summary>The output with each zone's health.</summary>
    public string Health { get; init; } = "";
    /// <summary>The output multiplying the stat's multiplier (Expertise), read as the use deploys; empty for 1.</summary>
    public string Factor { get; init; } = "";
    /// <summary>The stat with the share of body hits landing on a zone, held to 0 to 1, read at each shot.</summary>
    public string Share { get; init; } = "";
    /// <summary>The stat with the multiplier a zone hit takes in place of a body hit's 1, read at each shot.</summary>
    public string Multiplier { get; init; } = "";
    /// <summary>The selector that reads "true" on the marked enemy in fights driving keys, over the user's own setting; empty for none.</summary>
    public string Key { get; init; } = "";
}

/// <summary>
/// A skill's self-revive (ADR 0037: the Reviver Hive). The user going down while a use runs spends it: they're out of the fight for
/// <see cref="Seconds"/>, fire landing on a downed pool, then back at full health and <see cref="Armor"/> of maximum armor.
/// </summary>
public sealed record SimSkillRevive
{
    /// <summary>The output with the share of maximum armor restored, read as the use deploys; over 1 counts as 1.</summary>
    public string Armor { get; init; } = "";
    /// <summary>The output with the seconds from going down to the revive, read as the use deploys.</summary>
    public string Seconds { get; init; } = "";
    /// <summary>The downed pool, as a share of maximum health.</summary>
    public double PoolShare { get; init; }
    /// <summary>The share of damage a downed player takes.</summary>
    public double DamageTaken { get; init; }
    /// <summary>The seconds an untouched downed pool lasts, bleeding out at a steady rate.</summary>
    public double BleedSeconds { get; init; }
    /// <summary>The seconds after the revive in which nothing hurts the player; 0 for none.</summary>
    public double ImmuneSeconds { get; init; }
    /// <summary>The seconds from the revive to the use's end, where its cooldown starts.</summary>
    public double EndAfter { get; init; }
}

/// <summary>The instant kit: <see cref="Repair"/> of max armor at once, a kit spent, on its own <see cref="Cooldown"/> in seconds.</summary>
public sealed record SimInstantKit
{
    public double Repair { get; init; }
    public double Cooldown { get; init; }
}

public sealed record SimulationRules
{
    public ImmutableArray<SimTriggerDefinition> Triggers { get; init; } = [];
    public ImmutableArray<SimSkillDefinition> Skills { get; init; } = [];
    /// <summary>Skills that step aside in fights (ADR 0022).</summary>
    public ImmutableArray<SimSkillAside> SkillsAside { get; init; } = [];
    public ImmutableArray<SimStatusDamageDefinition> StatusDamage { get; init; } = [];
    public ImmutableArray<SimTalentDamageDefinition> TalentDamage { get; init; } = [];
    /// <summary>Talents' blasts on other enemies (ADR 0013).</summary>
    public ImmutableArray<SimBlastDefinition> Blasts { get; init; } = [];
    /// <summary>Talents' status spreads to other enemies (ADR 0013).</summary>
    public ImmutableArray<SimSpreadDefinition> Spreads { get; init; } = [];
    public ImmutableArray<SimRedirectDefinition> Redirects { get; init; } = [];
    public ImmutableArray<SimTrapDefinition> Traps { get; init; } = [];
    /// <summary>Talents' consecutive-headshot cycles (ADR 0021: Hotshot's Headache).</summary>
    public ImmutableArray<SimHeadshotCycle> HeadshotCycles { get; init; } = [];
    public ImmutableArray<SimNextHitBonus> NextHitBonuses { get; init; } = [];
    /// <summary>Talents' debuffs passing on at a death (ADR 0013).</summary>
    public ImmutableArray<SimPassOnDefinition> PassOns { get; init; } = [];
    public ImmutableArray<SimDistanceDefault> DistanceDefaults { get; init; } = [];
    public ImmutableArray<SimIncomingDefault> IncomingDefaults { get; init; } = [];
    /// <summary>Statuses enemies' hits put on the player (ADR 0018).</summary>
    public ImmutableArray<SimEnemyStatusDefinition> EnemyStatuses { get; init; } = [];
    public SimArmorKit? ArmorKit { get; init; }

    /// <summary>
    /// Every talent a fight rule names, in any of the lists above: one place, so a new kind of rule isn't left out of the set talents
    /// that run at their set's top bonus (R127).
    /// </summary>
    public IEnumerable<string> RuleTalents => Triggers.Select(rule => rule.Talent).Concat(TalentDamage.Select(rule => rule.Talent))
        .Concat(Blasts.Select(rule => rule.Talent)).Concat(Spreads.Select(rule => rule.Talent)).Concat(PassOns.Select(rule => rule.Talent))
        .Concat(Redirects.Select(rule => rule.Talent)).Concat(Traps.Select(rule => rule.Talent)).Concat(HeadshotCycles.Select(rule => rule.Talent)).Concat(NextHitBonuses.Select(rule => rule.Talent))
        .Concat(ArmorKit?.Abilities.Select(ability => ability.Talent) ?? []);
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
    /// <summary>The statuses fights know, in order (poison from ADR 0024).</summary>
    // ADR 0035: disrupted (the Jammer Pulse's and EMP Sticky Bomb's) last, so the others keep their order.
    public static readonly ImmutableArray<string> FightStatuses = ["burning", "bleeding", "poisoned", "shocked", "ensnared", "blinded", "disoriented", "disrupted"];
    /// <summary>The fight statuses that deal damage over time, by a <c>statusDamage</c> row or a skill's own rate.</summary>
    public static readonly ImmutableHashSet<string> DamagingStatuses = ImmutableHashSet.Create(StringComparer.Ordinal, "burning", "bleeding", "poisoned");
    /// <summary>
    /// Built in (ADR 0023): "fight" in fights with talent triggers, "static" everywhere else, unset included. A modifier that requires
    /// "static" holds only outside those fights, so the pack can set a static expected value aside where a fight rule plays it.
    /// </summary>
    public const string FightContextSelector = "fight_context";
    public const string FightContextStatic = "static";
    public const string FightContextFight = "fight";

    /// <summary>A selector's value as a requires gate reads it: <see cref="FightContextSelector"/> is "static" while unset.</summary>
    public static string GateValue(IReadOnlyDictionary<string, string> context, string key) =>
        context.TryGetValue(key, out var value) ? value : key == FightContextSelector ? FightContextStatic : "";

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
