using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Core.Expressions;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Core.Engine;

/// <summary>
/// RejectedContextInputs lists supplied numeric assumptions replaced by their pack default. Diagnostics lists the
/// expressions that failed or gave a non-finite value, and the stats that overflowed, in evaluation order.
/// </summary>
public sealed record StatResolution(
    ImmutableDictionary<string, double> Stats,
    ImmutableDictionary<string, AggregatedStat> Breakdowns,
    ImmutableArray<string> RejectedContextInputs,
    ImmutableArray<BuildDiagnostic> Diagnostics);

/// <summary>
/// Evaluates selected effects and derived stats. Equipment eligibility and build validation belong to earlier stages.
/// A modifier whose condition or value fails to evaluate, or isn't finite, is left out; a derived formula that fails
/// counts as 0, and a stat that overflows takes the value in its bounds nearest 0. Each is reported as an error
/// diagnostic, so every stat stays finite. Only failures the evaluator reports are caught (M1); any other exception
/// is a programming error and is left to propagate.
/// </summary>
public static class StatResolver
{
    /// <summary>Whether a batch's modifier can apply at all: a holstered weapon's batch keeps only its while-holstered modifiers.</summary>
    internal static bool Applies(ModifierBatch batch, StatModifier modifier) => batch.WhileHolstered is not { } holstered || modifier.WhileHolstered == holstered;

    public static StatResolution Resolve(
        CompiledPackExpressions compiled,
        ImmutableArray<ModifierBatch> batches,
        EvaluationScope? assumptions = null,
        ImmutableDictionary<string, string>? context = null)
    {
        assumptions ??= new EvaluationScope();
        context ??= ImmutableDictionary<string, string>.Empty;
        // As in the reference, only the pack's numeric inputs are in scope, and a
        // missing or invalid value uses that input's default.
        var contextValues = new Dictionary<string, double>(StringComparer.Ordinal);
        var rejected = ImmutableArray.CreateBuilder<string>();
        foreach (var input in compiled.Pack.NumericContextInputs)
        {
            var supplied = assumptions.ContextValues.TryGetValue(input.ID, out var value);
            var accepted = supplied && input.Accepts(value);
            if (supplied && !accepted) { rejected.Add(input.ID); }
            contextValues[input.ID] = accepted ? value : input.Default;
        }
        // Conditions and immediate values run before any stat exists.
        var scope = assumptions with { Stats = ImmutableDictionary<string, double>.Empty, Curves = compiled.Pack.Curves, ContextValues = contextValues };
        var diagnostics = ImmutableArray.CreateBuilder<BuildDiagnostic>();
        void Failed(ModifierSource source, string path, string error) =>
            diagnostics.Add(new("error", "modifier-eval", $"Could not evaluate a modifier from {source.Name}, so it is left out: {error}", source.Id, path));
        var immediate = new Dictionary<string, List<Contribution>>(StringComparer.Ordinal);
        var deferred = new Dictionary<string, List<(Contribution Contribution, Expression Expression, string Path)>>(StringComparer.Ordinal);
        void Push(string stat, Contribution contribution)
        {
            if (!immediate.TryGetValue(stat, out var contributions)) { immediate[stat] = contributions = []; }
            contributions.Add(contribution);
        }
        foreach (var batch in batches)
        {
            for (var index = 0; index < batch.Modifiers.Length; index++)
            {
                var modifier = batch.Modifiers[index];
                if (!Applies(batch, modifier)) { continue; }
                if (modifier.RequiresContext is { } gate && gate.Any(pair => context.GetValueOrDefault(pair.Key, "") != pair.Value)) { continue; }
                var contribution = new Contribution(batch.Source, modifier.Op, modifier.Value.Number);
                // Ranked values are numeric and unconditional; pack validation rejects the rest.
                if (batch.EffectiveRank is { } rank) { Push(modifier.Stat, contribution with { Value = modifier.Value.Number * rank }); continue; }
                var path = $"{batch.OwnerPath}.modifiers[{index}]";
                if (modifier.When.Length > 0)
                {
                    if (!EvaluationGuard.TryEvaluate(compiled.ModifierExpressions[$"{path}.when"], scope, "condition", out var eligible, out var error)) { Failed(batch.Source, path + ".when", error); continue; }
                    if (eligible == 0) { continue; }
                }
                if (modifier.Value.IsExpression)
                {
                    var expression = compiled.ModifierExpressions[$"{path}.value"];
                    if (compiled.DerivedValueReads.Contains($"{path}.value"))
                    {
                        if (!deferred.TryGetValue(modifier.Stat, out var pending)) { deferred[modifier.Stat] = pending = []; }
                        pending.Add((contribution, expression, path + ".value"));
                        continue;
                    }
                    if (!EvaluationGuard.TryEvaluate(expression, scope, "value", out var value, out var error)) { Failed(batch.Source, path + ".value", error); continue; }
                    contribution = contribution with { Value = value };
                }
                Push(modifier.Stat, contribution);
            }
        }

        // One mutable map serves every formula: each stat sees the stats finalized
        // before it in dependency order.
        var stats = new Dictionary<string, double>(StringComparer.Ordinal);
        var finalized = scope with { Stats = stats };
        var breakdowns = ImmutableDictionary.CreateBuilder<string, AggregatedStat>(StringComparer.Ordinal);
        foreach (var id in compiled.Order)
        {
            var definition = compiled.DerivedStats[id];
            if (!EvaluationGuard.TryEvaluate(compiled.Formulas[id], finalized, "formula", out var basis, out var formulaError))
            { diagnostics.Add(new("error", "stat-formula-eval", $"Could not evaluate {definition.Name}, so its formula counts as 0: {formulaError}", id)); }
            var contributions = ImmutableArray.CreateBuilder<Contribution>();
            if (immediate.TryGetValue(id, out var values)) { contributions.AddRange(values); }
            // The reference appends derived reads after every immediate contribution,
            // including overrides declared later in the original source list.
            if (deferred.TryGetValue(id, out var pending))
            {
                foreach (var (contribution, expression, path) in pending)
                {
                    if (!EvaluationGuard.TryEvaluate(expression, finalized, "value", out var value, out var error)) { Failed(contribution.Source, path, error); continue; }
                    contributions.Add(contribution with { Value = value });
                }
            }
            var aggregated = ModifierAggregator.Aggregate(basis, contributions.ToImmutable(), definition.Min, definition.Max);
            // Finite inputs can still overflow. The stat then takes the value in its bounds nearest 0, and the
            // breakdown keeps the contributions that overflowed, so it shows why.
            if (!double.IsFinite(aggregated.Final))
            {
                var fallback = Math.Max(definition.Min ?? double.NegativeInfinity, Math.Min(definition.Max ?? double.PositiveInfinity, 0));
                diagnostics.Add(new("error", "stat-non-finite", $"{definition.Name} came to a non-finite value, so it counts as {fallback.ToString(CultureInfo.InvariantCulture)}", id));
                aggregated = aggregated with { Final = fallback, Clamped = fallback != 0 };
            }
            stats[id] = aggregated.Final;
            breakdowns[id] = aggregated;
        }
        return new(stats.ToImmutableDictionary(StringComparer.Ordinal), breakdowns.ToImmutable(), rejected.ToImmutable(), diagnostics.ToImmutable());
    }
}
