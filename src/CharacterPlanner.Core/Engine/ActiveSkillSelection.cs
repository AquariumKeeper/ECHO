using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Diagnostics.ReferenceText;

namespace CharacterPlanner.Core.Engine;

public sealed record SelectedActiveSkills(
    ImmutableDictionary<string, ActiveSkillDefinition> Accepted,
    ImmutableArray<ModifierBatch> Batches,
    ImmutableArray<BuildDiagnostic> Diagnostics);

public static class ActiveSkillSelection
{
    public static SelectedActiveSkills Select(GamePack pack, BuildSnapshot build)
    {
        var diagnostics = ImmutableArray.CreateBuilder<BuildDiagnostic>();
        var batches = ImmutableArray.CreateBuilder<ModifierBatch>();
        var accepted = ImmutableDictionary.CreateBuilder<string, ActiveSkillDefinition>(StringComparer.Ordinal);
        var index = PackIndex.Of(pack);
        var slots = index.ActiveSkillSlotIds;
        foreach (var id in build.ActiveSkillSelections.Keys.Where(id => !slots.Contains(id)).Order(StringComparer.Ordinal))
        { diagnostics.Add(new("error", "unknown-active-skill-slot", $"Unknown active skill slot {Quote(id)}", id)); }
        var skills = index.ActiveSkills;
        var families = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var slot in pack.ActiveSkillSlots)
        {
            var id = build.ActiveSkillSelections.GetValueOrDefault(slot.ID, "");
            if (id.Length == 0)
            { diagnostics.Add(new("warning", "active-skill-unconfigured", $"{slot.Name} has no selected active skill", slot.ID)); continue; }
            if (!skills.TryGetValue(id, out var skill))
            { diagnostics.Add(new("error", "unknown-active-skill", $"Unknown active skill {Quote(id)}", slot.ID)); continue; }
            if (!slot.Choices.Contains(id))
            { diagnostics.Add(new("error", "active-skill-not-allowed", $"{skill.Name} is not allowed in {slot.Name}", slot.ID)); continue; }
            if (skill.Family.Length > 0 && families.TryGetValue(skill.Family, out var earlier))
            { diagnostics.Add(new("error", "duplicate-active-skill-family", $"{skill.Name} can't be equipped alongside {earlier}: both are {skill.Family} variants", slot.ID)); continue; }
            if (skill.Family.Length > 0) { families[skill.Family] = skill.Name; }
            accepted[slot.ID] = skill;
            batches.Add(ModifierBatch.ActiveSkill(skill));
        }
        return new(accepted.ToImmutable(), batches.ToImmutable(), diagnostics.ToImmutable());
    }
}
