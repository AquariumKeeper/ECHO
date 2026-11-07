using System.Collections.Immutable;
using CharacterPlanner.Core.Expressions;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Core.Engine;

public sealed record ResolvedBuildOutput(string Id, string Name, string Description, string Format, double Value);
public sealed record ResolvedBuildOutputs(ImmutableArray<ResolvedBuildOutput> Outputs, ImmutableArray<BuildDiagnostic> Diagnostics);

public static class BuildOutputResolver
{
    public static ResolvedBuildOutputs Resolve(CompiledPackExpressions compiled, ImmutableDictionary<string, double> stats,
        ImmutableDictionary<string, SkillPreview> previews, SelectedActiveSkills skills, EvaluationScope scope)
    {
        var values = stats.ToBuilder();
        foreach (var slot in compiled.Pack.ActiveSkillSlots)
        {
            double rate = 0, shield = 0;
            if (skills.Accepted.TryGetValue(slot.ID, out var skill) && previews.TryGetValue(slot.ID, out var preview))
            {
                foreach (var definition in skill.PreviewOutputs.Where(output => output.Headline || output.Kind == PreviewOutputKinds.Shield))
                {
                    foreach (var output in preview.Outputs.Where(output => output.Id == definition.ID))
                    {
                        if (definition.Headline) { rate = output.Value; }
                        else { shield += output.Value; }
                    }
                }
            }
            values[slot.ID + "_dps"] = rate;
            values[slot.ID + "_shield"] = shield;
        }
        var local = scope with { Stats = values.ToImmutable() };
        var outputs = ImmutableArray.CreateBuilder<ResolvedBuildOutput>();
        var diagnostics = ImmutableArray.CreateBuilder<BuildDiagnostic>();
        foreach (var definition in compiled.Pack.BuildOutputs)
        {
            void Error(string message) => diagnostics.Add(new("error", "build-output-eval", $"Could not evaluate {definition.Name}: {message}", definition.ID));
            if (!compiled.BuildOutputExpressions.TryGetValue(definition.ID, out var formula)) { Error("compiled formula is missing"); continue; }
            if (!EvaluationGuard.TryEvaluate(formula, local, "formula", out var value, out var error)) { Error(error); continue; }
            outputs.Add(new(definition.ID, definition.Name, definition.Description, definition.Format.Length == 0 ? "decimal" : definition.Format, value));
        }
        return new(outputs.ToImmutable(), diagnostics.ToImmutable());
    }
}
