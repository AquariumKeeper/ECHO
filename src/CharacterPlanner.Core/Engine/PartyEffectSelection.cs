using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Expressions;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Core.Engine;

public sealed record AllyModifier(string Stat, double Value);
public sealed record EvaluatedAllyEffect(string Id, string Stack, string Name, string Description, string Source, ImmutableArray<AllyModifier> Modifiers);
public sealed record PartyBuff(string Member, EvaluatedAllyEffect Effect);
public sealed record GiverEffects(ImmutableArray<EvaluatedAllyEffect> Effects, ImmutableArray<BuildDiagnostic> Failures);

public static class PartyEffectSelection
{
    /// <summary>Uses saved ticked values, keeps the strongest per stack (earliest on ties), and returns stack order.</summary>
    public static ImmutableArray<PartyBuff> Select(GamePack pack, BuildSnapshot build)
    {
        var definitions = new Dictionary<string, (AllyEffect Effect, string Talent)>(StringComparer.Ordinal);
        foreach (var talent in pack.Talents)
        {
            foreach (var effect in talent.AllyEffects) { definitions[effect.ID] = (effect, talent.Name); }
        }
        var best = new Dictionary<string, (PartyBuff Buff, double Strength)>(StringComparer.Ordinal);
        foreach (var member in build.Party)
        {
            foreach (var effect in member.Effects)
            {
                if (!effect.Active || !definitions.TryGetValue(effect.Id, out var definition)) { continue; }
                var modifiers = ImmutableArray.CreateBuilder<AllyModifier>();
                double strength = 0;
                foreach (var modifier in definition.Effect.Modifiers)
                {
                    if (!effect.Values.TryGetValue(modifier.Stat, out var value)) { continue; }
                    modifiers.Add(new(modifier.Stat, value));
                    strength += value;
                }
                var evaluated = new EvaluatedAllyEffect(effect.Id, definition.Effect.Stack, definition.Effect.Name, definition.Effect.Description, definition.Talent, modifiers.ToImmutable());
                if (!best.TryGetValue(evaluated.Stack, out var current) || strength > current.Strength)
                { best[evaluated.Stack] = (new(member.Name, evaluated), strength); }
            }
        }
        return best.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value.Buff).ToImmutableArray();
    }

    /// <summary>Resolves a giver without its own party, then snapshots effects from active selected talents.</summary>
    public static ImmutableArray<EvaluatedAllyEffect> EffectsOf(CompiledPackExpressions compiled, BuildSnapshot giver, ImmutableDictionary<string, string>? context = null) =>
        Offered(compiled, giver, context).Effects;

    /// <summary>
    /// <see cref="EffectsOf"/> with the giver's stat failures (M1): the effects are valued from those stats, so a
    /// caller that mustn't accept values from a stat that failed to resolve checks <see cref="GiverEffects.Failures"/>.
    /// </summary>
    public static GiverEffects Offered(CompiledPackExpressions compiled, BuildSnapshot giver, ImmutableDictionary<string, string>? context = null)
    {
        context ??= ImmutableDictionary<string, string>.Empty;
        giver = giver with { Party = [] };
        var resolved = BuildResolver.Resolve(compiled, giver, context);
        var talents = PackIndex.Of(compiled.Pack).Talents;
        var scope = new EvaluationScope { Stats = resolved.Calculation.Stats, Curves = compiled.Pack.Curves };
        var effects = ImmutableArray.CreateBuilder<EvaluatedAllyEffect>();
        foreach (var entry in resolved.Loadout.Equipped.Where(entry => entry.EffectsActive(context)))
        {
            foreach (var slot in entry.Item.TalentSlots)
            {
                var id = giver.TalentSelections.GetValueOrDefault(entry.SlotId)?.GetValueOrDefault(slot.ID) ?? "";
                if (!slot.Choices.Contains(id) || !talents.TryGetValue(id, out var talent)) { continue; }
                foreach (var effect in talent.AllyEffects)
                { effects.Add(new(effect.ID, effect.Stack, effect.Name, effect.Description, talent.Name, Evaluate(compiled, talent, effect, scope))); }
            }
        }
        return new(effects.ToImmutable(), resolved.Calculation.Diagnostics);
    }

    /// <summary>Evaluates an ally effect's values against a giver's stats, using the pack's compiled formulas.</summary>
    public static ImmutableArray<AllyModifier> Evaluate(CompiledPackExpressions compiled, TalentDefinition talent, AllyEffect effect, EvaluationScope scope)
    {
        var modifiers = ImmutableArray.CreateBuilder<AllyModifier>();
        for (var index = 0; index < effect.Modifiers.Length; index++)
        {
            var modifier = effect.Modifiers[index];
            var value = modifier.Value.IsExpression
                ? ExpressionEvaluator.Evaluate(compiled.AllyEffectExpressions[$"talents.{talent.ID}.allyEffects.{effect.ID}.modifiers[{index}].value"], scope)
                : modifier.Value.Number;
            modifiers.Add(new(modifier.Stat, value));
        }
        return modifiers.ToImmutable();
    }
}
