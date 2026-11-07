using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation;

/// <summary>Pure readings of distance, prior-shot pellet hits and the current target's remaining pools.</summary>
public sealed class CombatTriggerState
{
    internal static readonly ImmutableHashSet<string> Kinds = ImmutableHashSet.Create(StringComparer.Ordinal, "distance", "pellets", "target");
    private readonly CompiledPackExpressions pack;
    private readonly ImmutableArray<SimTriggerDefinition> rules;
    // Numeric readings resolve their input once; Context runs on every refresh.
    private readonly Dictionary<string, NumericContextInputDefinition> inputs = new(StringComparer.Ordinal);
    public bool NeedsDistance => rules.Any(rule => rule.Kind == "distance");

    public CombatTriggerState(CompiledPackExpressions pack, IEnumerable<SimTriggerDefinition> rules)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(rules);
        this.pack = pack;
        this.rules = [.. rules];
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in this.rules)
        {
            var rest = rule with { Talent = "", Key = "", Kind = "", Also = [], Phases = [], Within = 0, AtLeast = 0 };
            if (!Kinds.Contains(rule.Kind) || rule.Key.Length == 0 || rule.Talent.Length == 0 ||
                !rule.Also.IsEmpty || rest != new SimTriggerDefinition() || !double.IsFinite(rule.Within) || rule.Within < 0 || rule.AtLeast < 0 ||
                rule.Kind != "distance" && (rule.Within != 0 || !rule.Phases.IsEmpty) ||
                rule.Kind != "pellets" && rule.AtLeast != 0 || rule.Within > 0 && !rule.Phases.IsEmpty)
            { throw new SimulationException("trigger-unsupported", "Only pure distance, prior-pellet and target readings are supported"); }
            TriggerInputs.RequireIncreasingPhases(rule.Phases, "Distance phases require increasing positive finite boundaries and values");
            if (!keys.Add(rule.Key)) { throw new SimulationException("trigger-key-conflict", "Combat reading keys require one owner"); }
            var selector = pack.Pack.Selectors.FirstOrDefault(input => input.ID == rule.Key);
            var numeric = pack.Pack.NumericContextInputs.FirstOrDefault(input => input.ID == rule.Key);
            var boolean = rule.Within > 0 || rule.AtLeast > 0;
            if (!pack.Pack.Simulation.Triggers.Contains(rule) || (boolean ? !TriggerInputs.IsTrueOnly(selector) :
                !rule.Phases.IsEmpty ? selector is null || rule.Phases.Any(phase => !selector.Values.Contains(phase.Value)) :
                numeric is null || numeric.Min != 0 || rule.Kind == "target" && (numeric.Max < 100 || !TriggerInputs.OnGrid(100, 0, numeric.Step))))
            { throw new SimulationException("trigger-pack-mismatch", "Combat readings must bind to declared pack rules and inputs"); }
            if (!boolean && rule.Phases.IsEmpty) { inputs[rule.Key] = numeric!; }
        }
    }

    /// <summary>Rejects a numeric pellet reading that cannot hold every pellet the weapon can land.</summary>
    public void ValidatePelletCapacity(int pellets)
    {
        if (pellets is < 1 or > 1000) { throw new ArgumentOutOfRangeException(nameof(pellets)); }
        foreach (var rule in rules.Where(rule => rule.Kind == "pellets" && inputs.ContainsKey(rule.Key)))
        {
            if (inputs[rule.Key].Max < pellets || !TriggerInputs.OnGrid(1, 0, inputs[rule.Key].Step))
            { throw new SimulationException("trigger-reading-out-of-range", "A pellet reading's input cannot hold every pellet the weapon can land"); }
        }
    }

    public ImmutableDictionary<string, string> Context(ImmutableDictionary<string, string> assumptions, double distance, int priorPelletHits, double targetRemainingPercent)
    {
        ArgumentNullException.ThrowIfNull(assumptions);
        if (!double.IsFinite(distance) || distance < 0 || priorPelletHits is < 0 or > 1000 ||
            !double.IsFinite(targetRemainingPercent) || targetRemainingPercent is < 0 or > 100)
        { throw new ArgumentOutOfRangeException(nameof(distance), "Combat readings must be finite and within their domain"); }
        var result = assumptions.ToBuilder();
        foreach (var rule in rules)
        {
            if (rule.Within > 0 || rule.AtLeast > 0)
            {
                if (rule.Kind == "distance" ? distance <= rule.Within : priorPelletHits >= rule.AtLeast) { result[rule.Key] = "true"; }
                else { result.Remove(rule.Key); }
                continue;
            }
            if (!rule.Phases.IsEmpty)
            {
                result[rule.Key] = rule.Phases.FirstOrDefault(phase => distance < phase.Until)?.Value ?? rule.Phases[^1].Value;
                continue;
            }
            var input = inputs[rule.Key];
            var step = input.Step > 0 ? input.Step : 1;
            var value = rule.Kind switch
            {
                "distance" => Math.Round(distance, MidpointRounding.AwayFromZero),
                "pellets" => priorPelletHits,
                // Tolerate representation error so an exact tier is never floored one step low,
                // then snap to the grid so 300 steps of 0.1 reads 30, not 30.000000000000004.
                _ => Math.Round(Math.Floor(targetRemainingPercent / step + TriggerInputs.Tolerance) * step, 9)
            };
            if (value > input.Max || !TriggerInputs.OnGrid(value, 0, input.Step))
            { throw new SimulationException("trigger-reading-out-of-range", "A combat reading cannot be represented by its declared input"); }
            result[rule.Key] = value.ToString(CultureInfo.InvariantCulture);
        }
        return result.ToImmutable();
    }

    public ResolvedBuild Resolve(BuildSnapshot build, ImmutableDictionary<string, string> assumptions, double distance, int priorPelletHits, double targetRemainingPercent)
    {
        ArgumentNullException.ThrowIfNull(build);
        if (build.PackId != pack.Pack.ID) { throw new SimulationException("pack-mismatch", "The build belongs to another pack"); }
        return BuildResolver.Resolve(pack, build, Context(assumptions, distance, priorPelletHits, targetRemainingPercent));
    }
}
