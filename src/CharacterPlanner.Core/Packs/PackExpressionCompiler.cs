using System.Collections.Immutable;
using CharacterPlanner.Core.Diagnostics;
using CharacterPlanner.Core.Expressions;

namespace CharacterPlanner.Core.Packs;

// This result covers expression and dependency validation. Whole-pack acceptance
// also runs the catalog, simulation and presentation checks (PackValidator).
// Dictionary keys are the engine's owner paths (ModifierBatch, SkillPreviewResolver);
// diagnostics use the rules.json names for the same places ("skills", not "activeSkills").
// AllyEffectExpressions is keyed "talents.<id>.allyEffects.<effectId>.modifiers[<i>].value".
public sealed record CompiledPackExpressions(
    GamePack Pack,
    ImmutableDictionary<string, Expression> Formulas,
    ImmutableDictionary<string, Expression> ModifierExpressions,
    ImmutableDictionary<string, Expression> SkillPreviewExpressions,
    ImmutableDictionary<string, Expression> BuildOutputExpressions,
    ImmutableArray<string> Order,
    ImmutableHashSet<string> DerivedValueReads,
    ImmutableDictionary<string, Expression> AllyEffectExpressions)
{
    public ImmutableDictionary<string, StatDefinition> DerivedStats { get; } =
        Pack.Stats.ToImmutableDictionary(stat => stat.ID, StringComparer.Ordinal);
}

public sealed record PackExpressionResult(ImmutableArray<PackDiagnostic> Diagnostics, CompiledPackExpressions? Compiled);

public static class PackExpressionCompiler
{
    public static PackExpressionResult Compile(GamePack pack) => new Compiler(pack).Compile();

    private sealed class Compiler(GamePack pack)
    {
        private readonly List<PackDiagnostic> diagnostics = [];
        // Every v2 stat is computed from its formula; "derived" and "stats" are the same set.
        private readonly ImmutableHashSet<string> derived = pack.Stats.Select(stat => stat.ID).ToImmutableHashSet(StringComparer.Ordinal);
        private readonly ImmutableHashSet<string> stats = pack.Stats.Select(stat => stat.ID).ToImmutableHashSet(StringComparer.Ordinal);
        private readonly ImmutableHashSet<string> talents = pack.Talents.Select(talent => talent.ID).ToImmutableHashSet(StringComparer.Ordinal);
        private readonly ImmutableHashSet<string> numeric = pack.NumericContextInputs.Select(input => input.ID).ToImmutableHashSet(StringComparer.Ordinal);
        private readonly Dictionary<string, ImmutableHashSet<string>> dependencies = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Expression> formulas = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Expression> modifiers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Expression> previews = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Expression> outputs = new(StringComparer.Ordinal);
        private readonly HashSet<string> derivedReads = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Expression> allyEffects = new(StringComparer.Ordinal);

        private void Error(string code, string path, string message) => diagnostics.Add(new("error", code, Display(path), message));

        private static string Display(string path) =>
            path.StartsWith("activeSkills.", StringComparison.Ordinal) ? "skills." + path["activeSkills.".Length..] :
            path.StartsWith("activeSkillSlots.", StringComparison.Ordinal) ? "skillSlots." + path["activeSkillSlots.".Length..] :
            path.StartsWith("numericContextInputs", StringComparison.Ordinal) ? "numericInputs" + path["numericContextInputs".Length..] : path;
        private void Duplicates(IEnumerable<string> ids, string kind, string path)
        {
            foreach (var group in ids.GroupBy(id => id, StringComparer.Ordinal).Where(group => group.Count() > 1))
            { Error("duplicate-id", path, $"Duplicate {kind} id {ExpressionParser.Quote(group.Key)}"); }
        }

        public PackExpressionResult Compile()
        {
            Duplicates(pack.Stats.Select(stat => stat.ID), "stat", "stats");
            if (stats.Contains("skill_upgrade_rank")) { Error("reserved-stat-id", "stats", "skill_upgrade_rank is reserved for skill-local preview inputs"); }
            foreach (var stat in pack.Stats.Where(stat => stat.ID.StartsWith("skill_mod_", StringComparison.Ordinal)))
            { Error("reserved-stat-id", "stats", "skill_mod_ names are reserved for skill-local inputs"); }
            CheckNumericInputs();
            foreach (var (name, points) in pack.Curves.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                // The frozen validator accepts empty curves, then panics evaluating them (ledger K02).
                if (points.IsDefaultOrEmpty)
                { Error("curve-empty", $"curves.{name}", $"Curve {ExpressionParser.Quote(name)} needs at least one point"); continue; }
                for (var i = 1; i < points.Length; i++)
                {
                    if (points[i].X < points[i - 1].X)
                    { Error("curve-unsorted", $"curves.{name}[{i}]", $"Curve points must be sorted by x; {ReferenceText.Number(points[i].X)} follows {ReferenceText.Number(points[i - 1].X)}"); }
                }
            }
            foreach (var stat in pack.Stats)
            {
                var path = $"stats.{stat.ID}.formula";
                var parsed = Parse(stat.Formula, path, stats);
                if (parsed is null) { continue; }
                formulas[stat.ID] = parsed;
                dependencies[stat.ID] = parsed.Identifiers.Intersect(derived).Remove(stat.ID);
                if (parsed.Identifiers.Contains(stat.ID)) { Error("self-reference", path, $"Stat {stat.ID} references itself"); }
            }
            foreach (var item in pack.Items)
            {
                var path = $"items.{item.ID}";
                CheckModifiers(item.Modifiers, path);
                if (item.Upgrade is { } upgrade) { CheckModifiers(upgrade.ModifiersPerRank, path + ".upgrade", "modifiersPerRank"); }
            }
            foreach (var mod in pack.Mods) { CheckModifiers(mod.Modifiers, $"mods.{mod.ID}"); }
            foreach (var talent in pack.Talents)
            {
                CheckModifiers(talent.Modifiers, $"talents.{talent.ID}");
                foreach (var effect in talent.AllyEffects) { CheckAllyEffect(effect, $"talents.{talent.ID}.allyEffects.{effect.ID}"); }
            }
            foreach (var set in pack.Sets)
            {
                for (var i = 0; i < set.Bonuses.Length; i++) { CheckModifiers(set.Bonuses[i].Modifiers, $"sets.{set.ID}.bonuses[{i}]"); }
            }
            foreach (var upgrade in pack.AccountUpgrades) { CheckModifiers(upgrade.ModifiersPerRank, $"accountUpgrades.{upgrade.ID}", "modifiersPerRank"); }
            foreach (var specialization in pack.Specializations)
            {
                CheckModifiers(specialization.Modifiers, $"specializations.{specialization.ID}");
                foreach (var node in specialization.Nodes) { CheckModifiers(node.Modifiers, $"specializations.{specialization.ID}.nodes.{node.ID}"); }
            }
            foreach (var skill in pack.ActiveSkills)
            {
                var owner = $"activeSkills.{skill.ID}";
                CheckModifiers(skill.Modifiers, owner);
                var scope = stats.Add("level").Union(skill.ModSlots.SelectMany(slot => slot.Choices).Select(choice => choice.Input));
                if (skill.Upgrade is not null) { scope = scope.Add("skill_upgrade_rank"); }
                Duplicates(skill.PreviewOutputs.Select(output => output.ID), "active skill preview output", owner + ".previewOutputs");
                foreach (var output in skill.PreviewOutputs)
                {
                    var path = $"{owner}.previewOutputs.{output.ID}";
                    var formula = Parse(output.Formula, path, scope, "Stat or skill input");
                    if (formula is null) { continue; }
                    previews[path] = formula;
                    if (output.When.Length > 0 && Parse(output.When, path + ".when", scope, "Stat or skill input") is { } when) { previews[path + ".when"] = when; }
                }
            }
            var buildScope = stats;
            foreach (var slot in pack.ActiveSkillSlots)
            {
                foreach (var suffix in new[] { "_dps", "_shield" })
                {
                    var id = slot.ID + suffix;
                    if (stats.Contains(id)) { Error("skill-rate-identifier-conflict", "activeSkillSlots." + slot.ID, $"Skill rate identifier {ExpressionParser.Quote(id)} is also a stat id"); }
                    buildScope = buildScope.Add(id);
                }
            }
            Duplicates(pack.BuildOutputs.Select(output => output.ID), "build output", "buildOutputs");
            foreach (var output in pack.BuildOutputs)
            {
                if (!PackIds.IsSlug(output.ID) || string.IsNullOrWhiteSpace(output.Name) || string.IsNullOrWhiteSpace(output.Formula)) { continue; }
                if (Parse(output.Formula, "buildOutputs." + output.ID, buildScope, "Stat or skill rate") is { } formula) { outputs[output.ID] = formula; }
            }
            var order = DependencyOrder.Create(dependencies);
            if (order.Blocked.Length > 0) { Error("cycle", "stats", $"Derived stats form a dependency cycle: {string.Join(", ", order.Blocked)}"); }
            var compiled = diagnostics.Any(diagnostic => diagnostic.Severity == "error") ? null : new CompiledPackExpressions(pack,
                formulas.ToImmutableDictionary(StringComparer.Ordinal), modifiers.ToImmutableDictionary(StringComparer.Ordinal), previews.ToImmutableDictionary(StringComparer.Ordinal),
                outputs.ToImmutableDictionary(StringComparer.Ordinal), order.Order, derivedReads.ToImmutableHashSet(StringComparer.Ordinal),
                allyEffects.ToImmutableDictionary(StringComparer.Ordinal));
            return new(diagnostics.ToImmutableArray(), compiled);
        }

        private Expression? Parse(string source, string path, ImmutableHashSet<string> scope, string kind = "Stat", bool allowContext = true)
        {
            Expression parsed;
            try { parsed = ExpressionParser.Parse(source); }
            catch (ExpressionException exception)
            { Error("parse-error", path, $"{exception.Message} in {ExpressionParser.Quote(source)}"); return null; }
            foreach (var call in parsed.DescendantsAndSelf().OfType<Expression.Call>()) { CheckCall(call, path, allowContext); }
            foreach (var id in parsed.Identifiers.Order(StringComparer.Ordinal))
            { if (!scope.Contains(id)) { Error("unknown-stat", path, $"{kind} {ExpressionParser.Quote(id)} is not available here"); } }
            foreach (var name in parsed.FunctionNames.Order(StringComparer.Ordinal))
            { if (!ExpressionEvaluator.FunctionNames.Contains(name)) { Error("unknown-function", path, $"Unknown function {ExpressionParser.Quote(name)}"); } }
            foreach (var name in parsed.CurveNames.Order(StringComparer.Ordinal))
            { if (!pack.Curves.ContainsKey(name)) { Error("unknown-curve", path, $"Unknown curve {ExpressionParser.Quote(name)}"); } }
            return parsed;
        }

        // Ranked upgrades keep "modifiers" in their engine key but are "modifiersPerRank" in rules.json.
        private void CheckModifiers(ImmutableArray<StatModifier> values, string owner, string list = "modifiers")
        {
            for (var i = 0; i < values.Length; i++)
            {
                var modifier = values[i];
                var path = $"{owner}.modifiers[{i}]";
                if (!stats.Contains(modifier.Stat)) { Error("unknown-stat", $"{owner}.{list}[{i}].stat", $"Unknown stat {ExpressionParser.Quote(modifier.Stat)}"); }
                if (modifier.Value.Formula is { } source && Parse(source, path + ".value", stats) is { } value)
                {
                    modifiers[path + ".value"] = value;
                    foreach (var id in value.Identifiers.Intersect(derived).Order(StringComparer.Ordinal))
                    {
                        if (id == modifier.Stat) { Error("self-reference", path + ".value", $"Modifier on {modifier.Stat} reads its own stat"); continue; }
                        if (derived.Contains(modifier.Stat))
                        { dependencies[modifier.Stat] = dependencies.GetValueOrDefault(modifier.Stat, ImmutableHashSet<string>.Empty).Add(id); }
                        derivedReads.Add(path + ".value");
                    }
                }
                // Conditions are evaluated before any stat exists, so they may not read stats.
                if (modifier.When.Length > 0 && Parse(modifier.When, path + ".when", ImmutableHashSet<string>.Empty) is { } when) { modifiers[path + ".when"] = when; }
            }
        }

        private static readonly ImmutableHashSet<string> LiteralFirst = ["curve", "has_tag", "has_selected_tag", "has_talent", "context_value"];

        // Arity and literal arguments are checked here so that a compiled pack cannot fail
        // on a malformed call at resolve time. Curve names are checked by the caller.
        private void CheckCall(Expression.Call call, string path, bool allowContext)
        {
            var name = call.Name;
            var count = call.Arguments.Length;
            var arity = name switch
            {
                "curve" => 2,
                "has_tag" or "has_selected_tag" or "has_talent" or "context_value" or "floor" or "ceil" or "round" or "abs" or "sqrt" => 1,
                "clamp" or "lerp" => 3,
                _ => -1
            };
            if (arity >= 0 && count != arity) { Error("function-arity", path, $"{name}() takes {arity} argument{(arity == 1 ? "" : "s")}, not {count}"); }
            if (name is "min" or "max" && count == 0) { Error("function-arity", path, $"{name}() needs at least one argument"); }
            if (!LiteralFirst.Contains(name)) { return; }
            if (!allowContext && name != "curve")
            { Error("function-unavailable", path, $"{name}() is not available here: only stats and curves are"); return; }
            if (call.Arguments is not [Expression.Text text, ..])
            { Error("literal-argument-required", path, $"The first argument of {name}() must be a string literal"); return; }
            if (name == "has_talent" && !talents.Contains(text.Value)) { Error("unknown-talent", path, $"Unknown talent {ExpressionParser.Quote(text.Value)}"); }
            if (name == "context_value" && !numeric.Contains(text.Value)) { Error("unknown-numeric-input", path, $"Unknown numeric input {ExpressionParser.Quote(text.Value)}"); }
        }

        // Ally effects are evaluated from the giver's resolved stats, with curves but no tags,
        // talents or assumptions, so only stats, curves and numeric functions are in scope.
        private void CheckAllyEffect(AllyEffect effect, string owner)
        {
            for (var i = 0; i < effect.Modifiers.Length; i++)
            {
                var modifier = effect.Modifiers[i];
                var path = $"{owner}.modifiers[{i}]";
                if (!stats.Contains(modifier.Stat)) { Error("unknown-stat", path + ".stat", $"Unknown stat {ExpressionParser.Quote(modifier.Stat)}"); }
                if (modifier.Value.Formula is { } source && Parse(source, path + ".value", stats, allowContext: false) is { } value) { allyEffects[path + ".value"] = value; }
            }
        }

        private void CheckNumericInputs()
        {
            static bool Aligned(double value, double min, double step) => NumericContextInputDefinition.IsAligned(value, min, step);
            var groups = new Dictionary<string, NumericContextInputDefinition>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < pack.NumericContextInputs.Length; i++)
            {
                var input = pack.NumericContextInputs[i];
                var path = $"numericContextInputs[{i}]";
                if (!PackIds.IsSlug(input.ID) || string.IsNullOrWhiteSpace(input.Name)) { Error("numeric-context-input-invalid", path, "Numeric context inputs need a lower_snake_case id and non-empty name"); }
                if (!seen.Add(input.ID)) { Error("duplicate-id", "numericContextInputs", $"Duplicate numeric context input id {ExpressionParser.Quote(input.ID)}"); }
                if (!double.IsFinite(input.Min) || !double.IsFinite(input.Max) || !double.IsFinite(input.Step) || input.Step <= 0 || input.Max < input.Min)
                { Error("numeric-context-input-range", path, "Numeric context bounds and step must be finite, step positive, and max at least min"); continue; }
                if (!double.IsFinite(input.Default) || input.Default < input.Min || input.Default > input.Max || !Aligned(input.Default, input.Min, input.Step))
                { Error("numeric-context-default-invalid", path, "Numeric context default must be within bounds and aligned to its step"); }
                if (!input.Options.IsDefaultOrEmpty)
                {
                    var count = Math.Floor((input.Max - input.Min) / input.Step + StepTolerance.NumericInput) + 1;
                    var ticks = new HashSet<double>();
                    var valid = count == input.Options.Length;
                    foreach (var option in input.Options)
                    {
                        valid &= !string.IsNullOrWhiteSpace(option.Label) && double.IsFinite(option.Value) && option.Value >= input.Min && option.Value <= input.Max &&
                            Aligned(option.Value, input.Min, input.Step) && ticks.Add(Math.Round((option.Value - input.Min) / input.Step));
                    }
                    if (!valid) { Error("numeric-context-options-invalid", path + ".options", "Options must label each allowed value exactly once with a non-blank label"); }
                }
            }
            for (var i = 0; i < pack.NumericContextInputs.Length; i++)
            {
                var input = pack.NumericContextInputs[i];
                var path = $"numericContextInputs[{i}]";
                if (input.Group.Length == 0 && input.GroupName.Length == 0) { continue; }
                if (input.Group.Length == 0 || string.IsNullOrWhiteSpace(input.GroupName) || !input.Options.IsDefaultOrEmpty)
                { Error("numeric-context-group-invalid", path, "Grouped numeric inputs need a group id and name, and no options"); continue; }
                if (!groups.TryGetValue(input.Group, out var first)) { groups[input.Group] = input; }
                else if (input.GroupName != first.GroupName || input.Min != first.Min || input.Max != first.Max || input.Step != first.Step || input.Default != first.Default)
                { Error("numeric-context-group-invalid", path, $"Numeric input {ExpressionParser.Quote(input.ID)} must match group {ExpressionParser.Quote(input.Group)}'s name, range and default"); }
            }
        }
    }
}
