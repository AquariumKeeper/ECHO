using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Core.Engine;

public sealed record SelectedSkillModInputs(
    ImmutableDictionary<string, ImmutableDictionary<string, double>> Inputs,
    ImmutableArray<BuildDiagnostic> Diagnostics);

public static class SkillModInputSelection
{
    public static SelectedSkillModInputs Select(BuildSnapshot build, ImmutableDictionary<string, ActiveSkillDefinition> accepted)
    {
        var result = accepted.ToDictionary(pair => pair.Key,
            pair => pair.Value.ModSlots.SelectMany(slot => slot.Choices).Select(choice => choice.Input).Distinct(StringComparer.Ordinal)
                .ToDictionary(input => input, _ => 0d, StringComparer.Ordinal), StringComparer.Ordinal);
        var diagnostics = ImmutableArray.CreateBuilder<BuildDiagnostic>();
        void Invalid(string subject, string message) => diagnostics.Add(new("error", "invalid-skill-mod", message, subject));
        foreach (var (slotId, selection) in build.SkillMods.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!accepted.TryGetValue(slotId, out var skill) || skill.ModSlots.IsEmpty || selection.SkillId != skill.ID || selection.Mods is null)
            { Invalid(slotId, "Skill mods require an accepted matching variant with supported mod slots"); continue; }
            var inputs = result[slotId];
            var blocked = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (modSlotId, roll) in selection.Mods.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var choice = skill.ModSlots.FirstOrDefault(slot => slot.ID == modSlotId)?.Choices.FirstOrDefault(choice => choice.ID == roll.ModId);
                var subject = slotId + "." + modSlotId;
                if (choice is null || !new AttributeRollBounds(choice.Min, choice.Max, choice.Step).Accepts(roll.Value))
                { Invalid(subject, "Skill mod requires a compatible choice and a finite in-range, on-step roll"); continue; }
                if (blocked.Contains(choice.Input)) { continue; }
                var sum = inputs[choice.Input] + roll.Value;
                if (!double.IsFinite(sum))
                {
                    inputs[choice.Input] = 0;
                    blocked.Add(choice.Input);
                    Invalid(subject, "Combined skill mod input is non-finite; the entire input is withheld");
                }
                else { inputs[choice.Input] = sum; }
            }
        }
        return new(result.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.ToImmutableDictionary(StringComparer.Ordinal), StringComparer.Ordinal), diagnostics.ToImmutable());
    }
}
