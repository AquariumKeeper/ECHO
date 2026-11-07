using System.Collections.Immutable;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Core.Engine;

/// <summary>
/// An already selected effect source, in resolution order. Factories derive the
/// compiled pack path from the definition, so a batch can only read its own expressions.
/// </summary>
public sealed record ModifierBatch
{
    private ModifierBatch(ModifierSource source, string ownerPath, ImmutableArray<StatModifier> modifiers, long? effectiveRank = null, bool? whileHolstered = null)
    {
        Source = source;
        OwnerPath = ownerPath;
        Modifiers = modifiers;
        EffectiveRank = effectiveRank;
        WhileHolstered = whileHolstered;
    }

    public ModifierSource Source { get; }
    public string OwnerPath { get; }
    public ImmutableArray<StatModifier> Modifiers { get; }
    /// <summary>Ranked upgrades scale each numeric value by this rank; null for unranked sources.</summary>
    public long? EffectiveRank { get; }
    public bool? WhileHolstered { get; }

    public static ModifierBatch Party(PartyBuff buff) => new(new("party", buff.Effect.Id, $"Party: {buff.Member}, {buff.Effect.Name}"), "",
        buff.Effect.Modifiers.Select(modifier => new StatModifier { Stat = modifier.Stat, Op = "flat", Value = new(modifier.Value) }).ToImmutableArray());

    public static ModifierBatch CopiedTalent(TalentDefinition talent, TalentDefinition copier) =>
        new(new("talent", talent.ID, $"{talent.Name} ({copier.Name})"), $"talents.{talent.ID}", talent.Modifiers, whileHolstered: false);

    public static ModifierBatch PrototypeCount(ItemDefinition item, string stat, string slotId) =>
        new(new("prototype", item.ID, item.Name + ": Prototype", SlotId: slotId), "", [new() { Stat = stat, Op = "flat", Value = new(1) }]);

    public static ModifierBatch PrototypeAugment(ItemDefinition item, AugmentDefinition augment, long level, string slotId) =>
        new(new("prototype", augment.ID, $"{item.Name}: {augment.Name} (level {level})", level, slotId), "",
            [new() { Stat = augment.Stat, Op = "flat", Value = new(augment.Base + augment.PerLevel * (level - 1)) }]);

    // A counted roll adds 1 to the attribute's count stat and to its slot choice's, when they declare one.
    public static ModifierBatch Attribute(AttributeDefinition attribute, double value, ModifierSource source, bool includeCount, string choiceCountStat = "") =>
        new(source, $"attributes.{attribute.ID}", [new() { Stat = attribute.Stat, Op = ModifierOps.Flat, Value = new(value) },
            .. (includeCount ? new[] { attribute.CountStat, choiceCountStat } : []).Where(stat => stat.Length > 0)
                .Select(stat => new StatModifier { Stat = stat, Op = ModifierOps.Flat, Value = new(1) })]);

    public static ModifierBatch Talent(TalentDefinition talent, bool whileHolstered) =>
        new(new("talent", talent.ID, talent.Name), $"talents.{talent.ID}", talent.Modifiers, whileHolstered: whileHolstered);

    public static ModifierBatch Item(ItemDefinition item) =>
        new(new("item", item.ID, item.Name), $"items.{item.ID}", item.Modifiers);

    /// <summary>
    /// The active weapon's base stats as overrides from the item itself, so breakdowns match
    /// pack v1, which authored them as the first item modifiers. Apply before <see cref="Item"/>.
    /// </summary>
    public static ModifierBatch WeaponBase(ItemDefinition item) =>
        new(new("item", item.ID, item.Name), "",
            item.Weapon.Select(stat => new StatModifier { Stat = stat.Stat, Op = ModifierOps.Override, Value = new(stat.Value) }).ToImmutableArray());

    public static ModifierBatch SetBonus(SetDefinition set, int bonusIndex)
    {
        var bonus = set.Bonuses[bonusIndex];
        return new(new("set", set.ID, $"{set.Name} ({bonus.Pieces}pc)"), $"sets.{set.ID}.bonuses[{bonusIndex}]", bonus.Modifiers);
    }

    public static ModifierBatch Mod(ModDefinition mod) => new(new("mod", mod.ID, mod.Name), $"mods.{mod.ID}", mod.Modifiers);

    public static ModifierBatch ActiveSkill(ActiveSkillDefinition skill) =>
        new(new("active-skill", skill.ID, skill.Name), $"activeSkills.{skill.ID}", skill.Modifiers);

    public static ModifierBatch Specialization(SpecializationDefinition specialization) =>
        new(new("specialization", specialization.ID, specialization.Name), $"specializations.{specialization.ID}", specialization.Modifiers);

    public static ModifierBatch SpecializationNode(SpecializationDefinition specialization, SpecializationNodeDefinition node) =>
        new(new("specialization-node", node.ID, node.Name), $"specializations.{specialization.ID}.nodes.{node.ID}", node.Modifiers);

    /// <summary>The reference multiplies by the rank capped at effectCap; the source still shows the selected rank.</summary>
    public static ModifierBatch AccountUpgrade(AccountUpgradeDefinition upgrade, long rank)
    {
        CheckRank(rank, upgrade.MaxRank);
        var effective = upgrade.EffectCap is { } cap ? Math.Min(rank, cap) : rank;
        return Ranked(new("account-upgrade", upgrade.ID, $"{upgrade.Name} (rank {rank})", rank), $"accountUpgrades.{upgrade.ID}", upgrade.ModifiersPerRank, effective);
    }

    public static ModifierBatch ItemUpgrade(ItemDefinition item, long rank, string slotId)
    {
        var upgrade = item.Upgrade ?? throw new ArgumentException($"Item {item.ID} has no upgrade.", nameof(item));
        CheckRank(rank, upgrade.MaxRank);
        return Ranked(new("item-upgrade", item.ID, $"{item.Name}: {upgrade.Name} (rank {rank})", rank, slotId), $"items.{item.ID}.upgrade", upgrade.ModifiersPerRank, rank);
    }

    // Rank zero contributes nothing in the reference, not zero-valued contributions
    // (which would let a zero override replace the stat).
    private static ModifierBatch Ranked(ModifierSource source, string ownerPath, ImmutableArray<StatModifier> modifiers, long effective) =>
        new(source, ownerPath, effective == 0 ? [] : modifiers, effective);

    // Build validation reports out-of-range ranks; the resolver refuses them.
    private static void CheckRank(long rank, long maxRank)
    {
        if (rank < 0 || rank > maxRank) { throw new ArgumentOutOfRangeException(nameof(rank), rank, $"Rank must be within [0, {maxRank}]."); }
    }
}
