using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Diagnostics.ReferenceText;

namespace CharacterPlanner.Core.Engine;

public sealed record SelectedEquipmentEffects(
    ImmutableArray<ModifierBatch> Batches,
    ImmutableHashSet<string> SelectedTalents,
    ImmutableArray<BuildDiagnostic> Diagnostics);

/// <summary>Validates item-instance configuration, including Prototype ranges, and selects effects in authored order.</summary>
public static class EquipmentEffectSelection
{
    public static SelectedEquipmentEffects Select(GamePack pack, BuildSnapshot build, ResolvedLoadout loadout, ResolvedAssumptions assumptions)
    {
        var context = assumptions.Explicit;
        var index = PackIndex.Of(pack);
        var attributes = index.Attributes;
        var mods = index.Mods;
        var talents = index.Talents;
        var batches = ImmutableArray.CreateBuilder<ModifierBatch>();
        var diagnostics = ImmutableArray.CreateBuilder<BuildDiagnostic>();
        var selectedTalents = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var entry in loadout.Equipped)
        {
            var item = entry.Item;
            var source = new ModifierSource("item", item.ID, item.Name);
            var effectsActive = entry.EffectsActive(context);
            void Report(string severity, string code, string message, string target) => diagnostics.Add(new(severity, code, message, entry.SlotId, target));
            void UnknownSlots(IEnumerable<string> configured, IEnumerable<string> known, string kind)
            {
                var allowed = known.ToHashSet(StringComparer.Ordinal);
                foreach (var slot in configured.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                {
                    if (!allowed.Contains(slot)) { Report("error", $"unknown-{kind}-slot", $"{item.Name} has no {kind} slot {Quote(slot)}", slot); }
                }
            }
            if (effectsActive)
            {
                if (!item.Weapon.IsEmpty) { batches.Add(ModifierBatch.WeaponBase(item)); }
                batches.Add(ModifierBatch.Item(item));
            }
            var rolls = build.AttributeRolls.GetValueOrDefault(entry.SlotId) ?? ImmutableDictionary<string, AttributeRoll>.Empty;
            UnknownSlots(rolls.Keys, item.AttributeSlots.Select(slot => slot.ID), "attribute");
            foreach (var slot in item.AttributeSlots)
            {
                if (!rolls.TryGetValue(slot.ID, out var roll))
                { Report("warning", "attribute-unconfigured", $"{slot.Name} on {item.Name} has no selected attribute roll", slot.ID); continue; }
                if (!attributes.TryGetValue(roll.AttributeId, out var attribute))
                { Report("error", "unknown-attribute", $"Unknown attribute {Quote(roll.AttributeId)}", slot.ID); continue; }
                var choice = slot.Choices.FirstOrDefault(value => value.AttributeID == roll.AttributeId);
                if (choice is null)
                { Report("error", "attribute-not-allowed", $"{attribute.Name} is not allowed in {slot.Name}", slot.ID); continue; }
                var prototype = PrototypeRules.IsPrototype(pack, build, entry.SlotId, item);
                var bounds = PrototypeRules.RollBounds(attribute, choice, prototype);
                if (!bounds.Accepts(roll.Value))
                { Report("error", "attribute-roll-invalid", $"{attribute.Name} roll {Number(roll.Value)} is outside or off-step for [{Number(bounds.Min)}, {Number(bounds.Max)}]", slot.ID); continue; }
                if (effectsActive)
                {
                    var value = prototype && PrototypeRules.IsNormalized(pack, assumptions.Numeric) ? bounds.Min : roll.Value;
                    batches.Add(ModifierBatch.Attribute(attribute, value, source, includeCount: true, choice.CountStat));
                }
            }

            var selections = build.ModSelections.GetValueOrDefault(entry.SlotId) ?? ImmutableDictionary<string, string>.Empty;
            var modRolls = build.ModRolls.GetValueOrDefault(entry.SlotId) ?? ImmutableDictionary<string, double>.Empty;
            UnknownSlots(selections.Keys.Concat(modRolls.Keys), item.ModSlots.Select(slot => slot.ID), "mod");
            foreach (var slot in item.ModSlots)
            {
                var id = selections.GetValueOrDefault(slot.ID, "");
                var hasRoll = modRolls.TryGetValue(slot.ID, out var value);
                if (id.Length == 0)
                {
                    if (hasRoll) { Report("error", "mod-roll-without-mod", $"{slot.Name} has a roll but no selected mod", slot.ID); }
                    Report("warning", "mod-unconfigured", $"{slot.Name} on {item.Name} has no selected mod", slot.ID);
                    continue;
                }
                if (!mods.TryGetValue(id, out var mod))
                { Report("error", "unknown-mod", $"Unknown mod {Quote(id)}", slot.ID); continue; }
                if (!slot.Choices.Contains(id))
                { Report("error", "mod-not-allowed", $"{mod.Name} is not allowed in {slot.Name}", slot.ID); continue; }
                var modSource = new ModifierSource("mod", mod.ID, mod.Name);
                var rollValid = false;
                AttributeDefinition? attribute = null;
                if (mod.RollAttributeID.Length > 0) { attributes.TryGetValue(mod.RollAttributeID, out attribute); }
                if (attribute is not null)
                {
                    if (!hasRoll) { Report("warning", "mod-roll-unconfigured", $"{mod.Name} in {slot.Name} has no configured roll", slot.ID); }
                    else
                    {
                        rollValid = new AttributeRollBounds(attribute.Min, attribute.Max, attribute.Step).Accepts(value);
                        if (!rollValid) { Report("error", "mod-roll-invalid", $"{mod.Name} roll {Number(value)} is outside or off-step for [{Number(attribute.Min)}, {Number(attribute.Max)}]", slot.ID); }
                    }
                }
                else if (hasRoll) { Report("error", "mod-roll-not-allowed", $"{mod.Name} does not accept a configured roll", slot.ID); }
                if (!effectsActive) { continue; }
                if (rollValid) { batches.Add(ModifierBatch.Attribute(attribute!, value, modSource, includeCount: false)); }
                // A missing/invalid roll does not suppress the mod's fixed modifiers.
                batches.Add(ModifierBatch.Mod(mod));
            }

            var talentSelections = build.TalentSelections.GetValueOrDefault(entry.SlotId) ?? ImmutableDictionary<string, string>.Empty;
            UnknownSlots(talentSelections.Keys, item.TalentSlots.Select(slot => slot.ID), "talent");
            foreach (var slot in item.TalentSlots)
            {
                var id = talentSelections.GetValueOrDefault(slot.ID, "");
                if (id.Length == 0)
                { Report("warning", "talent-unconfigured", $"{slot.Name} on {item.Name} has no selected talent", slot.ID); continue; }
                if (!talents.TryGetValue(id, out var talent))
                { Report("error", "unknown-talent", $"Unknown talent {Quote(id)}", slot.ID); continue; }
                if (!slot.Choices.Contains(id))
                { Report("error", "talent-not-allowed", $"{talent.Name} is not allowed in {slot.Name}", slot.ID); continue; }
                selectedTalents.Add(id);
                var holstered = !effectsActive && entry.WeaponSlot;
                if (effectsActive || holstered) { batches.Add(ModifierBatch.Talent(talent, holstered)); }
            }
        }
        return new(batches.ToImmutable(), selectedTalents.ToImmutable(), diagnostics.ToImmutable());
    }
}
