using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation;

/// <summary>
/// Talents that read how many enemies are around (ADR 0008, R46). A count rule writes the enemies alive in the group, within its
/// range if it has one and with its status if it names one (ADR 0011), to its key: a number up to its max, or with atLeast true or
/// false. With an event it reads then and holds
/// that until the event comes round again, starting from none (Incessant Chatter at each reload's end). Without one it follows
/// the group. Enemies don't move in a fight, so a count with no status changes only as they die or join.
/// </summary>
internal sealed class EnemyCountState
{
    internal const string Kind = "count";
    private readonly ImmutableArray<SimTriggerDefinition> rules;
    private readonly ImmutableHashSet<string> events;
    private readonly Dictionary<string, int> held = new(StringComparer.Ordinal);
    // Readings taken on a killing blow, before the group has moved on.
    private readonly HashSet<string> early = new(StringComparer.Ordinal);

    public EnemyCountState(CompiledPackExpressions pack, IEnumerable<SimTriggerDefinition> rules)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(rules);
        this.rules = [.. rules];
        events = this.rules.Select(rule => rule.On).Where(name => name.Length > 0).ToImmutableHashSet(StringComparer.Ordinal);
        foreach (var rule in this.rules)
        {
            var numeric = pack.Pack.NumericContextInputs.FirstOrDefault(input => input.ID == rule.Key);
            var fits = rule.AtLeast > 0 ? TriggerInputs.IsTrueOnly(pack.Pack.Selectors.FirstOrDefault(input => input.ID == rule.Key)) :
                numeric is not null && numeric.Min == 0 && numeric.Max >= rule.Max && TriggerInputs.OnGrid(1, 0, numeric.Step);
            if (!fits || !pack.Pack.Simulation.Triggers.Contains(rule))
            { throw new SimulationException("trigger-pack-mismatch", "Enemy counts must bind to declared pack rules and inputs"); }
        }
    }

    private EnemyCountState(ImmutableArray<SimTriggerDefinition> rules, ImmutableHashSet<string> events) => (this.rules, this.events) = (rules, events);

    /// <summary>Whether a rule needs enemies' distances.</summary>
    public bool NeedsDistance => rules.Any(rule => rule.Within > 0);

    /// <summary>The same rules for a new fight, with nothing read yet.</summary>
    public EnemyCountState Fresh() => new(rules, events);

    /// <summary>The rules on this event read the count now, for talents that react to it: all of them with no set of those that do.</summary>
    /// <param name="name">The event.</param>
    /// <param name="eligible">The talents that react, or null for all.</param>
    /// <param name="dying">The target has just died, and the group hasn't moved on yet.</param>
    /// <param name="at">When.</param>
    /// <param name="present">How many enemies a rule counts at a time.</param>
    public void Sample(string name, IReadOnlySet<string>? eligible, bool dying, double at, Func<SimTriggerDefinition, double, int> present)
    {
        if (!events.Contains(name)) { return; }
        foreach (var rule in rules.Where(rule => rule.On == name && eligible?.Contains(rule.Talent) != false))
        {
            held[rule.Key] = present(rule, at);
            if (dying) { early.Add(rule.Key); } else { early.Remove(rule.Key); }
        }
    }

    /// <summary>The group has moved on: a reading taken on the killing blow sees it as it is now.</summary>
    public void ReadAgain(double at, Func<SimTriggerDefinition, double, int> present)
    {
        if (early.Count == 0) { return; }
        foreach (var rule in rules.Where(rule => early.Contains(rule.Key))) { held[rule.Key] = present(rule, at); }
        early.Clear();
    }

    /// <param name="read">The state so far.</param>
    /// <param name="present">How many enemies a rule counts. Only a rule with no event reads it, and those have no status, so no time.</param>
    public ImmutableDictionary<string, string> Context(ImmutableDictionary<string, string> read, Func<SimTriggerDefinition, double, int> present)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(present);
        foreach (var rule in rules)
        {
            var count = rule.On.Length > 0 ? held.GetValueOrDefault(rule.Key) : present(rule, 0);
            if (rule.AtLeast == 0) { read = read.SetItem(rule.Key, Math.Min(count, rule.Max).ToString(CultureInfo.InvariantCulture)); }
            else if (count >= rule.AtLeast) { read = read.SetItem(rule.Key, "true"); }
            else { read = read.Remove(rule.Key); }
        }
        return read;
    }
}
