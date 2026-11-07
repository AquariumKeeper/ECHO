using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Diagnostics.ReferenceText;

namespace CharacterPlanner.Core.Engine;

public sealed record BuildDiagnostic(string Severity, string Code, string Message, string Subject = "", string Target = "");

/// <summary>An item equipped in its own slot. Weapon slots hold the weapons the active-weapon assumption chooses between.</summary>
public sealed record EquippedItem(ItemDefinition Item, string SlotId, bool WeaponSlot)
{
    /// <summary>
    /// Whether the item's own effects apply: always for gear, and for a weapon only while it is the
    /// active weapon. (Pack v1 expressed this as effectsRequireContext {weapon_slot: own slot}.)
    /// </summary>
    public bool EffectsActive(ImmutableDictionary<string, string> context) =>
        !WeaponSlot || context.GetValueOrDefault(GamePack.WeaponSlotSelector, "") == SlotId;
}

public sealed record ResolvedLoadout(ImmutableArray<EquippedItem> Equipped, ImmutableArray<BuildDiagnostic> Diagnostics);

/// <summary>Places the build's items in their slots, in authored slot order, before any effects apply.</summary>
public static class LoadoutResolver
{
    public const string EquipmentLimitExceeded = "equipment-limit-exceeded";

    public static BuildDiagnostic? EquipmentLimitProblem(GamePack pack, BuildSnapshot build) =>
        pack.EquipmentLimits.IsDefaultOrEmpty ? null : Resolve(pack, build).Diagnostics.FirstOrDefault(IsEquipmentLimitError);

    public static bool IsEquipmentLimitError(BuildDiagnostic diagnostic) =>
        diagnostic.Severity == "error" && diagnostic.Code == EquipmentLimitExceeded;

    public static ResolvedLoadout Resolve(GamePack pack, BuildSnapshot build)
    {
        var diagnostics = ImmutableArray.CreateBuilder<BuildDiagnostic>();
        var items = PackIndex.Of(pack).Items;
        var equipped = ImmutableArray.CreateBuilder<EquippedItem>();
        foreach (var slot in pack.Slots)
        {
            var id = build.Equipment.GetValueOrDefault(slot.ID, "");
            if (id.Length == 0) { continue; }
            if (!items.TryGetValue(id, out var item))
            { diagnostics.Add(new("error", "unknown-item", $"Unknown item {Quote(id)}", slot.ID)); continue; }
            if (item.Slot != slot.ID)
            { diagnostics.Add(new("error", "wrong-slot", $"{item.Name} cannot be equipped in {slot.Name}", slot.ID)); continue; }
            equipped.Add(new(item, slot.ID, slot.Weapon));
        }
        foreach (var limit in pack.EquipmentLimits)
        {
            var matching = equipped.Where(entry => limit.Slots.Contains(entry.SlotId) && entry.Item.Tags.Contains(limit.Tag)).ToArray();
            if (matching.Length <= limit.Maximum) { continue; }
            var message = $"{limit.Name}: at most {limit.Maximum} matching items may be equipped; {matching.Length} are equipped ({string.Join(", ", matching.Select(entry => entry.Item.Name))}). Remove {matching.Length - limit.Maximum} matching items.";
            foreach (var entry in matching)
            { diagnostics.Add(new("error", EquipmentLimitExceeded, message, entry.SlotId, limit.ID)); }
        }
        return new(equipped.ToImmutable(), diagnostics.ToImmutable());
    }
}
