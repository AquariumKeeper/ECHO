using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Expressions;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Diagnostics.ReferenceText;

namespace CharacterPlanner.Core.Engine;

public sealed record SkillPreviewOutput(string Id, string Name, string Description, string Format, string Formula, string When,
    ImmutableDictionary<string, double> Inputs, double Value, string Kind);
public sealed record SkillPreview(string SkillId, ImmutableArray<SkillPreviewOutput> Outputs);
public sealed record ResolvedSkillPreviews(ImmutableDictionary<string, SkillPreview> Previews, ImmutableArray<BuildDiagnostic> Diagnostics);

public static class SkillPreviewResolver
{
    public static ResolvedSkillPreviews Resolve(CompiledPackExpressions compiled, BuildSnapshot build, SelectedActiveSkills skills, EvaluationScope scope)
    {
        var diagnostics = ImmutableArray.CreateBuilder<BuildDiagnostic>();
        var ranks = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (slot, upgrade) in build.SkillUpgrades.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!skills.Accepted.TryGetValue(slot, out var skill) || skill.Upgrade is null || upgrade.SkillId != skill.ID || upgrade.Rank < 0 || upgrade.Rank > skill.Upgrade.MaxRank)
            { diagnostics.Add(new("error", "invalid-skill-upgrade", $"Skill upgrade in {Quote(slot)} requires an accepted matching skill and a supported integer rank", slot)); continue; }
            ranks[slot] = upgrade.Rank;
        }
        var mods = SkillModInputSelection.Select(build, skills.Accepted);
        diagnostics.AddRange(mods.Diagnostics);
        var previews = ImmutableDictionary.CreateBuilder<string, SkillPreview>(StringComparer.Ordinal);
        foreach (var slot in compiled.Pack.ActiveSkillSlots)
        {
            if (!skills.Accepted.TryGetValue(slot.ID, out var skill)) { continue; }
            var stats = scope.Stats.ToImmutableDictionary(StringComparer.Ordinal).SetItem("level", compiled.Pack.Level)
                .SetItem("skill_upgrade_rank", ranks.GetValueOrDefault(slot.ID)).SetItems(mods.Inputs[slot.ID]);
            var local = scope with { Stats = stats };
            var outputs = ImmutableArray.CreateBuilder<SkillPreviewOutput>();
            foreach (var output in skill.PreviewOutputs)
            {
                var path = $"activeSkills.{skill.ID}.previewOutputs.{output.ID}";
                void Error(string message) => diagnostics.Add(new("error", "active-skill-preview-eval", $"Could not evaluate {skill.Name} {output.Name}: {message}", slot.ID + "." + output.ID));
                if (!compiled.SkillPreviewExpressions.TryGetValue(path, out var formula)) { Error("compiled preview formula is missing"); continue; }
                compiled.SkillPreviewExpressions.TryGetValue(path + ".when", out var condition);
                if (output.When.Length > 0)
                {
                    if (condition is null) { Error("compiled preview condition is missing"); continue; }
                    if (!EvaluationGuard.TryEvaluate(condition, local, "condition", out var eligible, out var error)) { Error(error); continue; }
                    if (eligible == 0) { continue; }
                }
                if (!EvaluationGuard.TryEvaluate(formula, local, "formula", out var value, out var formulaError)) { Error(formulaError); continue; }
                var identifiers = formula.Identifiers;
                if (output.When.Length > 0 && condition is not null) { identifiers = identifiers.Union(condition.Identifiers); }
                var inputs = identifiers.Where(id => id != "level").ToImmutableDictionary(id => id, id => stats.GetValueOrDefault(id), StringComparer.Ordinal);
                outputs.Add(new(output.ID, output.Name, output.Description, output.Format.Length == 0 ? "decimal" : output.Format, output.Formula, output.When, inputs, value, output.Kind));
            }
            previews[slot.ID] = new(skill.ID, outputs.ToImmutable());
        }
        return new(previews.ToImmutable(), diagnostics.ToImmutable());
    }
}
