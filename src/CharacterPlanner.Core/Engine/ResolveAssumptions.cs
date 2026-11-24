using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Diagnostics.ReferenceText;

namespace CharacterPlanner.Core.Engine;

/// <param name="Explicit">The assumptions as supplied.</param>
/// <param name="Effective">The explicit assumptions with each unset selector's default (<see cref="SelectorDefinition.Default"/>), as the
/// engine reads them.</param>
public sealed record ResolvedAssumptions(
    ImmutableDictionary<string, string> Explicit,
    ImmutableDictionary<string, double> Numeric,
    ImmutableArray<BuildDiagnostic> Diagnostics,
    ImmutableDictionary<string, string> Effective);

/// <summary>Normalizes separate view assumptions without changing their explicitly supplied strings.</summary>
public static partial class ResolveAssumptions
{
    [GeneratedRegex(@"\A-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex NumericSyntax();

    public static ResolvedAssumptions Resolve(GamePack pack, ImmutableDictionary<string, string>? context = null)
    {
        context ??= ImmutableDictionary<string, string>.Empty;
        var numeric = ImmutableDictionary.CreateBuilder<string, double>(StringComparer.Ordinal);
        var diagnostics = ImmutableArray.CreateBuilder<BuildDiagnostic>();
        foreach (var input in pack.NumericContextInputs)
        {
            var value = input.Default;
            if (context.TryGetValue(input.ID, out var raw))
            {
                if (NumericSyntax().IsMatch(raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && input.Accepts(parsed))
                { value = parsed; }
                else
                {
                    diagnostics.Add(new("error", "context-value-invalid",
                        $"Numeric assumption {Quote(input.ID)} must be a valid value in [{Number(input.Min)}, {Number(input.Max)}] aligned to step {Number(input.Step)}", input.ID));
                }
            }
            numeric[input.ID] = value;
        }
        return new(context, numeric.ToImmutable(), diagnostics.ToImmutable(), WithSelectorDefaults(pack, context));
    }

    /// <summary>The context with each unset selector that has a default set to it.</summary>
    public static ImmutableDictionary<string, string> WithSelectorDefaults(GamePack pack, ImmutableDictionary<string, string> context)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(context);
        // Fights resolve often, so the selectors with a default are found once per pack.
        foreach (var selector in Defaulted.GetValue(pack, static pack => [.. pack.Selectors.Where(selector => selector.Default.Length > 0)]))
        {
            if (!context.ContainsKey(selector.ID)) { context = context.SetItem(selector.ID, selector.Default); }
        }
        return context;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GamePack, SelectorDefinition[]> Defaulted = new();

    public static bool Matches(ImmutableDictionary<string, string>? required, ImmutableDictionary<string, string> context) =>
        required is null || required.All(pair => GamePack.GateValue(context, pair.Key) == pair.Value);
}
