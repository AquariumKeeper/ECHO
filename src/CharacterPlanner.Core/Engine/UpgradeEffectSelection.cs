using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Diagnostics.ReferenceText;

namespace CharacterPlanner.Core.Engine;

public sealed record SelectedUpgradeEffects(
    ImmutableArray<ModifierBatch> AccountBatches,
    ImmutableArray<ModifierBatch> EquipmentBatches,
    ImmutableArray<BuildDiagnostic> AccountDiagnostics,
    ImmutableArray<BuildDiagnostic> EquipmentDiagnostics);

/// <summary>Validates account ranks and item-bound equipment ranks before selecting their effects.</summary>
public static class UpgradeEffectSelection
{
    public static SelectedUpgradeEffects Select(GamePack pack, BuildSnapshot build, ResolvedLoadout loadout, ImmutableDictionary<string, string> context)
    {
        var batches = ImmutableArray.CreateBuilder<ModifierBatch>();
        var diagnostics = ImmutableArray.CreateBuilder<BuildDiagnostic>();
        var index = PackIndex.Of(pack);
        var accounts = index.AccountUpgrades;
        foreach (var (id, rank) in build.AccountUpgrades.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!accounts.TryGetValue(id, out var upgrade))
            { diagnostics.Add(new("error", "unknown-account-upgrade", $"Unknown account upgrade {Quote(id)}", id)); continue; }
            if (rank < 0 || rank > upgrade.MaxRank)
            { diagnostics.Add(new("error", "account-upgrade-rank-invalid", $"{upgrade.Name} rank {Number((long)rank)} is outside [0, {Number(upgrade.MaxRank)}]", id)); continue; }
            batches.Add(ModifierBatch.AccountUpgrade(upgrade, rank));
        }

        var accountBatches = batches.ToImmutable();
        var accountDiagnostics = diagnostics.ToImmutable();
        batches.Clear();
        diagnostics.Clear();
        var slots = index.SlotIds;
        var items = index.Items;
        var equipped = loadout.Equipped.ToDictionary(entry => entry.SlotId, StringComparer.Ordinal);
        foreach (var (slot, selection) in build.EquipmentUpgrades.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            void Invalid(string message) => diagnostics.Add(new("error", "equipment-upgrade-invalid", message, slot));
            if (!slots.Contains(slot)) { Invalid($"Unknown equipment slot {Quote(slot)}"); continue; }
            var id = build.Equipment.GetValueOrDefault(slot, "");
            if (id.Length == 0 || id != selection.ItemId) { Invalid($"Upgrade in {slot} is not bound to its equipped item"); continue; }
            if (!items.TryGetValue(id, out var item)) { Invalid($"Unknown item {Quote(id)} for equipment upgrade"); continue; }
            if (item.Slot != slot) { Invalid($"{item.Name} is not equipped in its own slot"); continue; }
            if (item.Upgrade is not { } upgrade) { Invalid($"{item.Name} has no ranked upgrade"); continue; }
            if (selection.Rank < 0 || selection.Rank > upgrade.MaxRank)
            { Invalid($"{upgrade.Name} rank {Number((long)selection.Rank)} is outside [0, {Number(upgrade.MaxRank)}]"); continue; }
            if (selection.Rank == 0 || !equipped.TryGetValue(slot, out var entry) || !entry.EffectsActive(context)) { continue; }
            batches.Add(ModifierBatch.ItemUpgrade(item, selection.Rank, slot));
        }
        return new(accountBatches, batches.ToImmutable(), accountDiagnostics, diagnostics.ToImmutable());
    }
}
