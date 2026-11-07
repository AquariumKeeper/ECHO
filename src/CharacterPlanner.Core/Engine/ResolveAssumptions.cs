using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Diagnostics.ReferenceText;

namespace CharacterPlanner.Core.Engine;

public sealed record ResolvedAssumptions(
    ImmutableDictionary<string, string> Explicit,
    ImmutableDictionary<string, double> Numeric,
    ImmutableArray<BuildDiagnostic> Diagnostics);

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
        return new(context, numeric.ToImmutable(), diagnostics.ToImmutable());
    }

    public static bool Matches(ImmutableDictionary<string, string>? required, ImmutableDictionary<string, string> context) =>
        required is null || required.All(pair => context.GetValueOrDefault(pair.Key, "") == pair.Value);
}
