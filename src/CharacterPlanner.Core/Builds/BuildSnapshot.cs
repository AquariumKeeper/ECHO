using System.Collections.Immutable;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Core.Builds;

public sealed record BuildMeta(string Name = "New Build", string Notes = "");
public sealed record AttributeRoll(string AttributeId, double Value);
public sealed record EquipmentUpgrade(string ItemId, long Rank);
public sealed record PrototypeSelection(string ItemId, string Augment, long Level);
public sealed record SkillUpgrade(string SkillId, long Rank);
public sealed record SkillModRoll(string ModId, double Value);
public sealed record SkillModSelection(string SkillId, ImmutableDictionary<string, SkillModRoll> Mods);
public sealed record PartyEffect(string Id, ImmutableDictionary<string, double> Values, bool Active = false);
public sealed record PartyMember(string Name, string Code, double Armor, ImmutableArray<PartyEffect> Effects);

/// <summary>
/// In-memory build values, independent of wire formats and resolve assumptions.
/// Edits retain invalid but representable values for the resolver to diagnose.
/// </summary>
public sealed record BuildSnapshot
{
    public string PackId { get; init; } = "";
    public string PackVersion { get; init; } = "";
    public ImmutableDictionary<string, string> Equipment { get; init; } = ImmutableDictionary<string, string>.Empty;
    public ImmutableDictionary<string, ImmutableDictionary<string, AttributeRoll>> AttributeRolls { get; init; } = ImmutableDictionary<string, ImmutableDictionary<string, AttributeRoll>>.Empty;
    public ImmutableDictionary<string, ImmutableDictionary<string, string>> ModSelections { get; init; } = ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty;
    public ImmutableDictionary<string, ImmutableDictionary<string, double>> ModRolls { get; init; } = ImmutableDictionary<string, ImmutableDictionary<string, double>>.Empty;
    public ImmutableDictionary<string, ImmutableDictionary<string, string>> TalentSelections { get; init; } = ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty;
    public ImmutableDictionary<string, string> ActiveSkillSelections { get; init; } = ImmutableDictionary<string, string>.Empty;
    public ImmutableDictionary<string, SkillUpgrade> SkillUpgrades { get; init; } = ImmutableDictionary<string, SkillUpgrade>.Empty;
    public ImmutableDictionary<string, SkillModSelection> SkillMods { get; init; } = ImmutableDictionary<string, SkillModSelection>.Empty;
    public string SpecializationId { get; init; } = "";
    public ImmutableArray<string> SpecializationNodeSelections { get; init; } = [];
    public ImmutableDictionary<string, long> AccountUpgrades { get; init; } = ImmutableDictionary<string, long>.Empty;
    public ImmutableDictionary<string, EquipmentUpgrade> EquipmentUpgrades { get; init; } = ImmutableDictionary<string, EquipmentUpgrade>.Empty;
    public ImmutableDictionary<string, PrototypeSelection> Prototypes { get; init; } = ImmutableDictionary<string, PrototypeSelection>.Empty;
    public ImmutableArray<PartyMember> Party { get; init; } = [];
    public BuildMeta Meta { get; init; } = new();

    public static BuildSnapshot Create(GamePack pack, string name = "") => new()
    {
        PackId = pack.ID,
        PackVersion = pack.Version,
        Equipment = pack.Slots.ToImmutableDictionary(slot => slot.ID, _ => "", StringComparer.Ordinal),
        Meta = new(name.Length == 0 ? "New Build" : name)
    };

    public BuildSnapshot WithEquipment(string slotId, string itemId)
    {
        var next = this with { Equipment = Equipment.SetItem(slotId, itemId) };
        return itemId.Length > 0 && Equipment.GetValueOrDefault(slotId) == itemId ? next : next.ClearEquipmentConfiguration(slotId);
    }

    /// <summary>Replaces the whole loadout, preserving configuration only for unchanged, nonempty items.</summary>
    public BuildSnapshot WithEquipmentAll(IEnumerable<KeyValuePair<string, string>> equipment)
    {
        var next = this with { Equipment = equipment.ToImmutableDictionary(StringComparer.Ordinal) };
        var configuredSlots = AttributeRolls.Keys.Concat(ModSelections.Keys).Concat(ModRolls.Keys)
            .Concat(TalentSelections.Keys).Concat(EquipmentUpgrades.Keys).Concat(Prototypes.Keys).Distinct(StringComparer.Ordinal);
        foreach (var slot in configuredSlots)
        {
            var itemId = next.Equipment.GetValueOrDefault(slot, "");
            if (itemId.Length == 0 || Equipment.GetValueOrDefault(slot) != itemId) { next = next.ClearEquipmentConfiguration(slot); }
        }
        return next;
    }

    private BuildSnapshot ClearEquipmentConfiguration(string slotId) => this with
    {
        AttributeRolls = AttributeRolls.Remove(slotId),
        ModSelections = ModSelections.Remove(slotId),
        ModRolls = ModRolls.Remove(slotId),
        TalentSelections = TalentSelections.Remove(slotId),
        EquipmentUpgrades = EquipmentUpgrades.Remove(slotId),
        Prototypes = Prototypes.Remove(slotId)
    };

    public BuildSnapshot WithAttributeRoll(string slotId, string attributeSlotId, AttributeRoll? roll) =>
        roll is not null && !double.IsFinite(roll.Value) ? this : this with { AttributeRolls = EditNested(AttributeRolls, slotId, attributeSlotId, roll!, roll is null) };

    // The frozen helpers deliberately differ: non-finite attribute rolls are ignored,
    // whereas non-finite mod rolls clear the existing roll.
    public BuildSnapshot WithModRoll(string slotId, string modSlotId, double? value) => this with
    {
        ModRolls = EditNested(ModRolls, slotId, modSlotId, value.GetValueOrDefault(), value is null || !double.IsFinite(value.Value))
    };

    public BuildSnapshot WithModSelection(string slotId, string modSlotId, string modId)
    {
        var next = this;
        if (modId.Length == 0 || ModSelections.GetValueOrDefault(slotId)?.GetValueOrDefault(modSlotId) != modId)
        { next = WithModRoll(slotId, modSlotId, null); }
        return next with { ModSelections = EditNested(ModSelections, slotId, modSlotId, modId, modId.Length == 0) };
    }

    public BuildSnapshot WithTalentSelection(string slotId, string talentSlotId, string talentId) => this with
    { TalentSelections = EditNested(TalentSelections, slotId, talentSlotId, talentId, talentId.Length == 0) };

    public BuildSnapshot WithActiveSkillSelection(string slotId, string skillId)
    {
        var next = this with { ActiveSkillSelections = skillId.Length == 0 ? ActiveSkillSelections.Remove(slotId) : ActiveSkillSelections.SetItem(slotId, skillId) };
        return skillId.Length > 0 && ActiveSkillSelections.GetValueOrDefault(slotId) == skillId ? next : next with
        { SkillUpgrades = SkillUpgrades.Remove(slotId), SkillMods = SkillMods.Remove(slotId) };
    }

    public BuildSnapshot WithSkillMod(string slotId, string skillId, string modSlotId, SkillModRoll? roll)
    {
        var mods = SkillMods.TryGetValue(slotId, out var current) && current.SkillId == skillId ? current.Mods ?? ImmutableDictionary<string, SkillModRoll>.Empty : ImmutableDictionary<string, SkillModRoll>.Empty;
        mods = roll is null ? mods.Remove(modSlotId) : mods.SetItem(modSlotId, roll);
        return this with { SkillMods = mods.IsEmpty ? SkillMods.Remove(slotId) : SkillMods.SetItem(slotId, new(skillId, mods)) };
    }

    public BuildSnapshot WithSkillUpgrade(string slotId, string skillId, long rank) => this with
    { SkillUpgrades = rank == 0 ? SkillUpgrades.Remove(slotId) : SkillUpgrades.SetItem(slotId, new(skillId, rank)) };

    public BuildSnapshot WithSpecialization(string specializationId) => this with
    {
        SpecializationId = specializationId,
        SpecializationNodeSelections = specializationId.Length > 0 && SpecializationId == specializationId ? SpecializationNodeSelections : []
    };

    public BuildSnapshot WithSpecializationNode(string nodeId, bool allocated)
    {
        var selected = SpecializationNodeSelections.ToImmutableHashSet(StringComparer.Ordinal);
        selected = allocated ? selected.Add(nodeId) : selected.Remove(nodeId);
        return this with { SpecializationNodeSelections = selected.Order(StringComparer.Ordinal).ToImmutableArray() };
    }

    public BuildSnapshot WithAccountUpgrade(string upgradeId, long rank) => this with
    { AccountUpgrades = rank == 0 ? AccountUpgrades.Remove(upgradeId) : AccountUpgrades.SetItem(upgradeId, rank) };
    public BuildSnapshot WithEquipmentUpgrade(string slotId, string itemId, long rank) => this with
    { EquipmentUpgrades = rank == 0 ? EquipmentUpgrades.Remove(slotId) : EquipmentUpgrades.SetItem(slotId, new(itemId, rank)) };
    public BuildSnapshot WithPrototype(string slotId, PrototypeSelection? selection) => this with
    { Prototypes = selection is null ? Prototypes.Remove(slotId) : Prototypes.SetItem(slotId, selection) };
    public BuildSnapshot WithParty(IEnumerable<PartyMember> members) => this with { Party = members.ToImmutableArray() };

    private static ImmutableDictionary<string, ImmutableDictionary<string, T>> EditNested<T>(
        ImmutableDictionary<string, ImmutableDictionary<string, T>> source, string slot, string id, T value, bool remove)
    {
        var values = source.GetValueOrDefault(slot) ?? ImmutableDictionary<string, T>.Empty;
        values = remove ? values.Remove(id) : values.SetItem(id, value);
        return values.IsEmpty ? source.Remove(slot) : source.SetItem(slot, values);
    }
}
