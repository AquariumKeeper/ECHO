using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation;

/// <summary>Occupancy of actual magazine readings. Observations follow ammo mutations, including reload completion.</summary>
public sealed class MagazinePhaseState
{
    private readonly PhaseTriggerState reader;
    private readonly double capacity;
    private ImmutableDictionary<string, string> current;
    private readonly Dictionary<string, Dictionary<string, double>> seconds = new(StringComparer.Ordinal);
    private double clock;

    public MagazinePhaseState(IEnumerable<SimTriggerDefinition> rules, double capacity)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var magazines = rules.Where(rule => rule.Kind == "magazine").ToArray();
        reader = new PhaseTriggerState(magazines);
        this.capacity = capacity;
        current = reader.Context(ImmutableDictionary<string, string>.Empty, 0, capacity, capacity);
        foreach (var rule in magazines)
        {
            var values = rule.Percent ? Enumerable.Range(0, 101).Select(value => value.ToString(CultureInfo.InvariantCulture)) : rule.Phases.Select(phase => phase.Value).Distinct(StringComparer.Ordinal);
            seconds[rule.Key] = values.ToDictionary(value => value, _ => 0d, StringComparer.Ordinal);
        }
    }

    public void Observe(double remaining, double time)
    {
        CheckTime(time);
        var next = reader.Context(ImmutableDictionary<string, string>.Empty, time, remaining, capacity);
        foreach (var (key, value) in current) { seconds[key][value] += time - clock; }
        current = next;
        clock = time;
    }

    /// <summary>A read of seconds per key/value; never commits a future observation.</summary>
    public ImmutableDictionary<string, ImmutableDictionary<string, double>> Measure(double time)
    {
        CheckTime(time);
        return seconds.ToImmutableDictionary(pair => pair.Key,
            pair => pair.Value.ToImmutableDictionary(value => value.Key,
                value => value.Value + (current[pair.Key] == value.Key ? time - clock : 0), StringComparer.Ordinal), StringComparer.Ordinal);
    }

    private void CheckTime(double time)
    {
        if (!double.IsFinite(time) || time < clock) { throw new ArgumentOutOfRangeException(nameof(time), "Magazine observation time must be finite and monotonic"); }
    }
}

/// <summary>Occupancy of magazine readings under a weapon rotation: a key counts only while it is read, that is while its
/// owning weapon (or, for a gear rule, any weapon) is in hand. Observations are the driven readings at each refresh.</summary>
internal sealed class HeldMagazinePhaseState
{
    private readonly Dictionary<string, Dictionary<string, double>> seconds = new(StringComparer.Ordinal);
    private Dictionary<string, string> current = new(StringComparer.Ordinal);
    private double clock;

    internal HeldMagazinePhaseState(IEnumerable<SimTriggerDefinition> rules)
    {
        foreach (var rule in rules.Where(rule => rule.Kind == "magazine"))
        {
            var values = rule.Percent ? Enumerable.Range(0, 101).Select(value => value.ToString(CultureInfo.InvariantCulture)) : rule.Phases.Select(phase => phase.Value).Distinct(StringComparer.Ordinal);
            seconds[rule.Key] = values.ToDictionary(value => value, _ => 0d, StringComparer.Ordinal);
        }
    }

    internal void Observe(IReadOnlyDictionary<string, string> readings, double time)
    {
        CheckTime(time);
        foreach (var (key, value) in current) { seconds[key][value] += time - clock; }
        current = seconds.Keys.Where(readings.ContainsKey).ToDictionary(key => key, key => readings[key], StringComparer.Ordinal);
        clock = time;
    }

    internal ImmutableDictionary<string, ImmutableDictionary<string, double>> Measure(double time)
    {
        CheckTime(time);
        return seconds.ToImmutableDictionary(pair => pair.Key,
            pair => pair.Value.ToImmutableDictionary(value => value.Key,
                value => value.Value + (current.GetValueOrDefault(pair.Key) == value.Key ? time - clock : 0), StringComparer.Ordinal), StringComparer.Ordinal);
    }

    private void CheckTime(double time)
    {
        if (!double.IsFinite(time) || time < clock) { throw new ArgumentOutOfRangeException(nameof(time), "Magazine observation time must be finite and monotonic"); }
    }
}
