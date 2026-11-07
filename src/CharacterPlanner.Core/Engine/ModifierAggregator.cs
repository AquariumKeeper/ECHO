using System.Collections.Immutable;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Core.Engine;

public sealed record ModifierSource(string Kind, string Id, string Name, long Rank = 0, string SlotId = "");
public sealed record Contribution(ModifierSource Source, string Op, double Value);
public sealed record AggregatedStat(double Base, double Final, ImmutableArray<Contribution> Contributions, bool Clamped);

public static class ModifierAggregator
{
    public static AggregatedStat Aggregate(double basis, ImmutableArray<Contribution> contributions, double? min = null, double? max = null)
    {
        double flat = 0, multiplier = 1;
        double? replacement = null;
        foreach (var contribution in contributions)
        {
            switch (contribution.Op)
            {
                case ModifierOps.Flat: flat += contribution.Value; break;
                case ModifierOps.Mult: multiplier *= 1 + contribution.Value; break;
                case ModifierOps.Override: replacement = contribution.Value; break;
                default: throw new InvalidOperationException($"Unknown modifier op {contribution.Op}; pack validation should have rejected it.");
            }
        }
        var unclamped = replacement ?? (basis + flat) * multiplier;
        var value = unclamped;
        // Math.Max/Min keep v1's values exactly (including -0 becoming 0 at a zero bound).
        if (min is { } lower) { value = Math.Max(value, lower); }
        if (max is { } upper) { value = Math.Min(value, upper); }
        // Report clamping only when a bound changed the value: NaN never is clamped (CR10).
        return new(basis, value, contributions, !double.IsNaN(unclamped) && value != unclamped);
    }
}
