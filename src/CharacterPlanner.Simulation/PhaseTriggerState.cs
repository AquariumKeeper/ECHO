using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation;

/// <summary>Pure phase readings from fight time and the active weapon's current magazine.</summary>
public sealed class PhaseTriggerState
{
    internal static readonly ImmutableHashSet<string> Kinds = ImmutableHashSet.Create(StringComparer.Ordinal, "magazine", "cycle");
    private readonly ImmutableArray<SimTriggerDefinition> rules;

    public PhaseTriggerState(IEnumerable<SimTriggerDefinition> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        this.rules = [.. rules];
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in this.rules)
        {
            var rest = rule with { Talent = "", Key = "", Kind = "", Phases = [], Also = [], Percent = false };
            if (!Kinds.Contains(rule.Kind) || rule.Key.Length == 0 || rule.Talent.Length == 0 ||
                !rule.Also.IsEmpty || rest != new SimTriggerDefinition() || rule.Kind == "cycle" && rule.Percent ||
                rule.Percent && !rule.Phases.IsEmpty || !rule.Percent && rule.Phases.IsEmpty)
            { throw new SimulationException("trigger-unsupported", "Only pure magazine and cycle phase readings are supported"); }
            TriggerInputs.RequireIncreasingPhases(rule.Phases, "Phases require increasing positive finite boundaries and values");
            if (rule.Kind == "magazine" && !rule.Percent && rule.Phases[^1].Until != 1)
            { throw new SimulationException("trigger-phase-invalid", "Magazine phases must end at one full magazine"); }
            if (!keys.Add(rule.Key)) { throw new SimulationException("trigger-key-conflict", "Phase keys require one owner"); }
        }
    }

    public MagazinePhaseState TrackMagazine(double capacity) => new(rules, capacity);

    public ImmutableDictionary<string, string> Context(ImmutableDictionary<string, string> assumptions, double time, double remaining, double capacity)
    {
        ArgumentNullException.ThrowIfNull(assumptions);
        if (!double.IsFinite(time) || time < 0) { throw new ArgumentOutOfRangeException(nameof(time)); }
        if (!double.IsFinite(capacity) || capacity < 1 || capacity != Math.Floor(capacity) ||
            !double.IsFinite(remaining) || remaining < 0 || remaining > capacity || remaining != Math.Floor(remaining))
        { throw new ArgumentOutOfRangeException(nameof(remaining), "Magazine readings require whole rounds within a positive whole capacity"); }
        var result = assumptions.ToBuilder();
        foreach (var rule in rules)
        {
            var fired = capacity - remaining;
            if (rule.Percent)
            {
                var percent = Math.Floor(fired <= double.MaxValue / 100 ? fired * 100 / capacity : fired / capacity * 100);
                result[rule.Key] = percent.ToString(CultureInfo.InvariantCulture);
                continue;
            }
            if (rule.Kind == "cycle")
            {
                var cycle = CycleAt(time, rule.Phases[^1].Until);
                result[rule.Key] = rule.Phases.First(phase => time < Boundary(rule, cycle, phase.Until)).Value;
            }
            else
            {
                result[rule.Key] = rule.Phases.FirstOrDefault(phase => fired / capacity < phase.Until)?.Value ?? rule.Phases[^1].Value;
            }
        }
        return result.ToImmutable();
    }

    /// <summary>The next absolute cycle boundary strictly after time; magazine readings change only on weapon events.</summary>
    public double? NextTransition(double time)
    {
        if (!double.IsFinite(time) || time < 0) { throw new ArgumentOutOfRangeException(nameof(time)); }
        double? next = null;
        foreach (var rule in rules.Where(rule => rule.Kind == "cycle"))
        {
            var cycle = CycleAt(time, rule.Phases[^1].Until);
            var boundary = rule.Phases.Select(phase => Boundary(rule, cycle, phase.Until)).FirstOrDefault(at => at > time);
            if (!double.IsFinite(boundary) || boundary <= time)
            { throw new SimulationException("time-not-advancing", "Cycle boundaries cannot advance time"); }
            next = next is null ? boundary : Math.Min(next.Value, boundary);
        }
        return next;
    }

    /// <summary>Seconds spent in each cycle value from fight start through duration.
    /// Full periods are integrated arithmetically, so work depends on phase count, not cycle count.</summary>
    public ImmutableDictionary<string, ImmutableDictionary<string, double>> MeasureCycles(double duration,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(duration) || duration < 0) { throw new ArgumentOutOfRangeException(nameof(duration)); }
        cancellationToken.ThrowIfCancellationRequested();
        var result = ImmutableDictionary.CreateBuilder<string, ImmutableDictionary<string, double>>(StringComparer.Ordinal);
        foreach (var rule in rules.Where(rule => rule.Kind == "cycle"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cycle = CycleAt(duration, rule.Phases[^1].Until);
            var values = ImmutableDictionary.CreateBuilder<string, double>(StringComparer.Ordinal);
            var previous = 0d;
            foreach (var phase in rule.Phases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var start = cycle * rule.Phases[^1].Until + previous;
                var end = Boundary(rule, cycle, phase.Until);
                var seconds = cycle * (phase.Until - previous) + Math.Max(0, Math.Min(duration, end) - start);
                values[phase.Value] = values.GetValueOrDefault(phase.Value) + seconds;
                previous = phase.Until;
            }
            result[rule.Key] = values.ToImmutable();
        }
        return result.ToImmutable();
    }

    // Use the same absolute boundaries for reads and scheduling; modulo may put an exact
    // decimal wrap into the previous cycle because its quotient rounds down.
    private static double CycleAt(double time, double period)
    {
        var cycle = Math.Floor(time / period);
        if (!double.IsFinite(cycle)) { throw new SimulationException("time-not-advancing", "Cycle index overflowed"); }
        if (cycle * period > time) { cycle--; }
        if ((cycle + 1) * period <= time) { cycle++; }
        if ((cycle + 1) * period <= time)
        { throw new SimulationException("time-not-advancing", "Cycle period cannot advance time"); }
        return cycle;
    }

    private static double Boundary(SimTriggerDefinition rule, double cycle, double until) =>
        until == rule.Phases[^1].Until ? (cycle + 1) * until : cycle * rule.Phases[^1].Until + until;

    internal void ValidatePack(CompiledPackExpressions pack)
    {
        foreach (var rule in rules)
        {
            var selector = pack.Pack.Selectors.FirstOrDefault(input => input.ID == rule.Key);
            var numeric = pack.Pack.NumericContextInputs.FirstOrDefault(input => input.ID == rule.Key);
            bool Fits(string value) => selector is not null ? selector.Values.Contains(value) : numeric is not null &&
                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) &&
                number >= numeric.Min && number <= numeric.Max && numeric.Step > 0 && TriggerInputs.OnGrid(number, numeric.Min, numeric.Step);
            if (!pack.Pack.Simulation.Triggers.Contains(rule) ||
                (rule.Percent ? selector is not null || numeric is null || Enumerable.Range(0, 101).Any(value => !Fits(value.ToString(CultureInfo.InvariantCulture)))
                    : rule.Phases.Any(phase => !Fits(phase.Value))))
            { throw new SimulationException("trigger-pack-mismatch", "Phase rules and all driven values must belong to the resolved pack"); }
        }
    }

    public ResolvedBuild Resolve(CompiledPackExpressions pack, BuildSnapshot build, ImmutableDictionary<string, string> assumptions,
        double time, double remaining, double capacity)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(build);
        if (build.PackId != pack.Pack.ID) { throw new SimulationException("pack-mismatch", "The build belongs to another pack"); }
        ValidatePack(pack);
        return BuildResolver.Resolve(pack, build, Context(assumptions, time, remaining, capacity));
    }
}
