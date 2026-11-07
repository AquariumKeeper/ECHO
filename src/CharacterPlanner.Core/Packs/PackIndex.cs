using System.Collections.Immutable;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;

namespace CharacterPlanner.Core.Packs;

/// <summary>
/// A pack's definitions by id, built once per pack instance rather than on every resolve (L4). The resolver's
/// stages take a <see cref="GamePack"/>, so the index lives in a side table keyed by identity instead of on the
/// compiled pack: a pack made with <c>with</c> gets its own index, and an index never outlives its pack. Each
/// lookup is built when first used, so a duplicate id fails only the stage that reads that collection.
/// </summary>
internal sealed class PackIndex
{
    private static readonly ConditionalWeakTable<GamePack, PackIndex> Cache = [];

    public static PackIndex Of(GamePack pack) => Cache.GetValue(pack, static pack => new PackIndex(pack));

    private readonly Lazy<FrozenDictionary<string, ItemDefinition>> items;
    private readonly Lazy<FrozenDictionary<string, AttributeDefinition>> attributes;
    private readonly Lazy<FrozenDictionary<string, ModDefinition>> mods;
    private readonly Lazy<FrozenDictionary<string, TalentDefinition>> talents;
    private readonly Lazy<FrozenDictionary<string, ActiveSkillDefinition>> activeSkills;
    private readonly Lazy<FrozenDictionary<string, AccountUpgradeDefinition>> accountUpgrades;
    private readonly Lazy<FrozenSet<string>> slotIds;
    private readonly Lazy<ImmutableArray<TalentDefinition>> slotlessSetTalents;
    private readonly Lazy<ImmutableArray<TalentDefinition>> ruledSetTalents;
    private readonly Lazy<FrozenSet<string>> activeSkillSlotIds;

    private PackIndex(GamePack pack)
    {
        items = new(() => pack.Items.ToFrozenDictionary(item => item.ID, StringComparer.Ordinal));
        attributes = new(() => pack.Attributes.ToFrozenDictionary(attribute => attribute.ID, StringComparer.Ordinal));
        mods = new(() => pack.Mods.ToFrozenDictionary(mod => mod.ID, StringComparer.Ordinal));
        talents = new(() => pack.Talents.ToFrozenDictionary(talent => talent.ID, StringComparer.Ordinal));
        slotlessSetTalents = new(() =>
        {
            var offered = pack.Items.SelectMany(item => item.TalentSlots).SelectMany(slot => slot.Choices).ToHashSet(StringComparer.Ordinal);
            return [.. pack.Talents.Where(talent => !talent.ModeledBySets.IsEmpty && !offered.Contains(talent.ID))];
        });
        ruledSetTalents = new(() =>
        {
            var ruled = pack.Simulation.Triggers.Select(rule => rule.Talent).Concat(pack.Simulation.TalentDamage.Select(rule => rule.Talent)).ToHashSet(StringComparer.Ordinal);
            return [.. SlotlessSetTalents.Where(talent => ruled.Contains(talent.ID))];
        });
        activeSkills = new(() => pack.ActiveSkills.ToFrozenDictionary(skill => skill.ID, StringComparer.Ordinal));
        accountUpgrades = new(() => pack.AccountUpgrades.ToFrozenDictionary(upgrade => upgrade.ID, StringComparer.Ordinal));
        slotIds = new(() => pack.Slots.Select(slot => slot.ID).ToFrozenSet(StringComparer.Ordinal));
        activeSkillSlotIds = new(() => pack.ActiveSkillSlots.Select(slot => slot.ID).ToFrozenSet(StringComparer.Ordinal));
    }

    /// <summary>Set talents no item's talent slot offers.</summary>
    public ImmutableArray<TalentDefinition> SlotlessSetTalents => slotlessSetTalents.Value;
    /// <summary>Those with a fight rule, which a fight runs at the set's top bonus.</summary>
    public ImmutableArray<TalentDefinition> RuledSetTalents => ruledSetTalents.Value;
    public FrozenDictionary<string, ItemDefinition> Items => items.Value;
    public FrozenDictionary<string, AttributeDefinition> Attributes => attributes.Value;
    public FrozenDictionary<string, ModDefinition> Mods => mods.Value;
    public FrozenDictionary<string, TalentDefinition> Talents => talents.Value;
    public FrozenDictionary<string, ActiveSkillDefinition> ActiveSkills => activeSkills.Value;
    public FrozenDictionary<string, AccountUpgradeDefinition> AccountUpgrades => accountUpgrades.Value;
    public FrozenSet<string> SlotIds => slotIds.Value;
    public FrozenSet<string> ActiveSkillSlotIds => activeSkillSlotIds.Value;
}
