using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Diagnostics.ReferenceText;

namespace CharacterPlanner.Core.Engine;

public sealed record SelectedPrototypeEffects(ImmutableArray<ModifierBatch> Batches, ImmutableArray<BuildDiagnostic> Diagnostics);

public static class PrototypeEffectSelection
{
    public static SelectedPrototypeEffects Select(GamePack pack, BuildSnapshot build, ResolvedLoadout loadout, ResolvedAssumptions assumptions)
    {
        var context = assumptions.Explicit;
        var batches = ImmutableArray.CreateBuilder<ModifierBatch>();
        var diagnostics = ImmutableArray.CreateBuilder<BuildDiagnostic>();
        var items = PackIndex.Of(pack).Items;
        var equipped = loadout.Equipped.ToDictionary(entry => entry.SlotId, StringComparer.Ordinal);
        var normalized = PrototypeRules.IsNormalized(pack, assumptions.Numeric);
        foreach (var (slot, selection) in build.Prototypes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            void Invalid(string message) => diagnostics.Add(new("error", "prototype-invalid", message, slot));
            var id = build.Equipment.GetValueOrDefault(slot, "");
            if (id.Length == 0 || id != selection.ItemId || !items.TryGetValue(id, out var item))
            { Invalid($"Prototype in {slot} is not bound to its equipped item"); continue; }
            if (!PrototypeRules.Eligible(pack, item, slot)) { Invalid($"{item.Name} can't be Prototype"); continue; }
            var prototype = pack.Prototype!;
            AugmentDefinition? augment = null;
            if (selection.Augment.Length > 0)
            {
                augment = prototype.Augments.FirstOrDefault(value => value.ID == selection.Augment);
                if (augment is null) { Invalid($"Unknown augment {Quote(selection.Augment)} on {item.Name}"); continue; }
                if (selection.Level < 1 || selection.Level > augment.Levels)
                { Invalid($"{augment.Name} level {Number((long)selection.Level)} is outside [1, {Number(augment.Levels)}]"); continue; }
            }
            if (!equipped.TryGetValue(slot, out var entry) || !entry.EffectsActive(context)) { continue; }
            if (prototype.CountStat.Length > 0)
            { batches.Add(ModifierBatch.PrototypeCount(item, prototype.CountStat, slot)); }
            if (augment is not null && !normalized) { batches.Add(ModifierBatch.PrototypeAugment(item, augment, selection.Level, slot)); }
        }
        return new(batches.ToImmutable(), diagnostics.ToImmutable());
    }
}
