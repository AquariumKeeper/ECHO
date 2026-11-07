using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Core.Engine;

public sealed record SelectedTalentCopy(string CopierSlot, TalentDefinition Copier, TalentDefinition Talent);

/// <summary>Selects one pass of copied ordinary talent effects without copying ownership or set credit.</summary>
public static class TalentCopySelection
{
    public static ImmutableArray<ModifierBatch> Select(GamePack pack, BuildSnapshot build, ResolvedLoadout loadout, ImmutableDictionary<string, string> context)
    {
        var sources = Sources(pack, build, loadout, context);
        return sources.IsEmpty ? [] : [.. sources.Select(copy => ModifierBatch.CopiedTalent(copy.Talent, copy.Copier))];
    }

    /// <summary>The eligible source talents, using exactly the same ownership rules as copied stat batches.</summary>
    public static ImmutableArray<SelectedTalentCopy> Sources(GamePack pack, BuildSnapshot build, ResolvedLoadout loadout, ImmutableDictionary<string, string> context)
    {
        var talents = PackIndex.Of(pack).Talents;
        var equipped = loadout.Equipped;
        var bySlot = equipped.ToDictionary(entry => entry.SlotId, StringComparer.Ordinal);
        var copies = ImmutableArray.CreateBuilder<SelectedTalentCopy>();
        TalentDefinition? Selected(EquippedItem entry, ItemTalentSlotDefinition slot)
        {
            var id = build.TalentSelections.GetValueOrDefault(entry.SlotId)?.GetValueOrDefault(slot.ID) ?? "";
            return id.Length > 0 && slot.Choices.Contains(id) && talents.TryGetValue(id, out var talent) ? talent : null;
        }
        foreach (var copier in equipped)
        {
            if (!copier.EffectsActive(context)) { continue; }
            foreach (var slot in copier.Item.TalentSlots)
            {
                var copierTalent = Selected(copier, slot);
                if (copierTalent?.CopiesTalent is not { } rule || !bySlot.TryGetValue(rule.From, out var from) || !bySlot.TryGetValue(rule.To, out var to) ||
                    !to.EffectsActive(context) || !Allowed(rule, from.Item, to.Item)) { continue; }
                foreach (var sourceSlot in from.Item.TalentSlots)
                {
                    var copied = Selected(from, sourceSlot);
                    if (copied is not null && !rule.ExcludeTalents.Contains(copied.ID)) { copies.Add(new(copier.SlotId, copierTalent, copied)); }
                }
            }
        }
        return copies.ToImmutable();
    }

    private static bool Allowed(TalentCopyDefinition rule, ItemDefinition from, ItemDefinition to) =>
        !rule.ExcludeTags.Any(tag => from.Tags.Contains(tag) || to.Tags.Contains(tag)) &&
        from.Tags.Any(tag => tag.StartsWith(rule.SameTagPrefix, StringComparison.Ordinal) && to.Tags.Contains(tag));
}
