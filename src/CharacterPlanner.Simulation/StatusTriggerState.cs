using System.Collections.Frozen;
using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation;

/// <summary>
/// The statuses on one enemy: when each ends, when it was applied, and the duration factor it started with. A skill's burn or bleed
/// is held apart: it's the same status (R61), so it counts as being on, but it starts no ticks. Its damage is its own rate, or the skill's rate,
/// or isn't in fights at all when the rate only carries it.
/// </summary>
public sealed class TargetStatuses
{
    internal Dictionary<string, double> Deadlines { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, double> AppliedAt { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, double> StartFactors { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, double> FromSkills { get; } = new(StringComparer.Ordinal);
    // Until when a skill's rate is itself this status's damage, so no second one runs beside it.
    internal Dictionary<string, double> RateIsStatus { get; } = new(StringComparer.Ordinal);
    // A skill's own burn or bleed: its damage per second, kept from its first application, and how far it's been dealt, so two uses don't both count.
    internal Dictionary<string, double> SkillRates { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, double> SkillDealt { get; } = new(StringComparer.Ordinal);
    // Those waiting for a talent's status to end, whose next damage is a new application.
    internal HashSet<string> SkillWaits { get; } = new(StringComparer.Ordinal);

    /// <summary>Whether this status is on the enemy at this time, from a talent or a skill.</summary>
    public bool Has(string status, double time) => time < Deadlines.GetValueOrDefault(status, double.NegativeInfinity) ||
        time < FromSkills.GetValueOrDefault(status, double.NegativeInfinity);
}

/// <summary>The statuses on the enemy in hand, which it holds itself, and the equipped talents' persistent arming counters.</summary>
public sealed class StatusTriggerState
{
    internal const string Kind = "status";
    internal static readonly ImmutableHashSet<string> Names = ImmutableHashSet.Create(StringComparer.Ordinal,
        "burning", "bleeding", "shocked", "ensnared", "blinded", "disoriented");
    // No shot carries a status applied on a whole second (Dragon's Glare), so it lands on the current target at the event.
    internal static readonly ImmutableHashSet<string> DirectEvents = ImmutableHashSet.Create(StringComparer.Ordinal, "second");
    // Hit events a rule with applyOnHit applies its status on: the triggering hit carries it (Trauma).
    internal static readonly ImmutableHashSet<string> HitEvents = ImmutableHashSet.Create(StringComparer.Ordinal, "hit", "hit_pellet", "crit", "crit_pellet", "headshot", "body_hit", "unarmored_hit");
    private readonly CompiledPackExpressions pack;
    private readonly ImmutableArray<SimTriggerDefinition> rules;
    private readonly Dictionary<string, string> keys = new(StringComparer.Ordinal);
    private readonly HashSet<string> selectorKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> events = new(StringComparer.Ordinal);
    private TargetStatuses held = new();
    private Dictionary<string, double> deadlines => held.Deadlines;
    private Dictionary<string, double> appliedAt => held.AppliedAt;
    private readonly Dictionary<string, double> elapsed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> measuredAt = new(StringComparer.Ordinal);
    private readonly long[] counted;
    // When each rule was armed; NaN while unarmed. A rule armed by a shot applies on a later hit only.
    private readonly double[] armedAt;
    // When each rule last armed or applied; a rule with every ignores its event until that long after.
    private readonly double[] lastFired;
    private double clock;
    // The latest time read since the last event, so NextExpiry skips deadlines a read already passed.
    private double observed;
    private (double Time, int Version, ImmutableHashSet<string> Statuses) active = (double.NaN, -1, []);
    private int version;
    // Each rule's also events, which count toward its one count beside its own (Ignited's skill kills); null with none.
    private readonly FrozenSet<string>?[] alsoEvents;

    public StatusTriggerState(CompiledPackExpressions pack, IEnumerable<SimTriggerDefinition> rules)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(rules);
        this.pack = pack;
        this.rules = [.. rules];
        counted = new long[this.rules.Length];
        alsoEvents = [.. this.rules.Select(rule => rule.Also.IsEmpty ? null : rule.Also.Select(gain => gain.On).ToFrozenSet(StringComparer.Ordinal))];
        armedAt = Enumerable.Repeat(double.NaN, this.rules.Length).ToArray();
        lastFired = Enumerable.Repeat(double.NegativeInfinity, this.rules.Length).ToArray();
        foreach (var rule in this.rules)
        {
            var rest = rule with { Talent = "", Key = "", Kind = "", On = "", Status = "", Duration = 0, Count = 0, Every = 0, Within = 0, ApplyOnHit = false, Also = [], Phases = [], WhileKey = "", WhileFull = false };
            // A counted rule's also adds other events to its one count; each adds one.
            if (rule.Kind != Kind || rule.Key.Length == 0 || rule.Talent.Length == 0 ||
                !(Names.Contains(rule.Status) || rule.Status == "any") || !SimTriggerDefinition.StatusAlsoAllowed(rule) || !rule.Phases.IsEmpty || rest != new SimTriggerDefinition() ||
                rule.Count < 0 || !double.IsFinite(rule.Within) || rule.Within < 0 || !double.IsFinite(rule.Duration) || !double.IsFinite(rule.Every) || rule.Every < 0 ||
                rule.ApplyOnHit && !HitEvents.Contains(rule.On) ||
                rule.WhileFull && rule.WhileKey.Length == 0 || rule.WhileKey.Length > 0 && rule.On.Length == 0 ||
                (rule.On.Length == 0 ? rule.Duration != 0 || rule.Count != 0 || rule.Every != 0 : rule.Status == "any" || rule.Duration <= 0))
            {
                throw new SimulationException("trigger-unsupported",
                    "Status rules must be pure observers, or counted events that arm the next hit or apply on a whole second");
            }
            if (keys.TryGetValue(rule.Key, out var status) && status != rule.Status)
            { throw new SimulationException("trigger-key-conflict", "Shared status keys must observe the same status"); }
            keys[rule.Key] = rule.Status;
            var selector = pack.Pack.Selectors.FirstOrDefault(input => input.ID == rule.Key);
            var numeric = pack.Pack.NumericContextInputs.FirstOrDefault(input => input.ID == rule.Key);
            if (!pack.Pack.Simulation.Triggers.Contains(rule) || (selector is not null ? !TriggerInputs.IsTrueOnly(selector) :
                numeric is null || numeric.Min != 0 || numeric.Max < 1 || !TriggerInputs.OnGrid(1, 0, numeric.Step)))
            { throw new SimulationException("trigger-pack-mismatch", "Status rules must bind to true-only selectors or zero/one numeric readings"); }
            if (selector is not null) { selectorKeys.Add(rule.Key); }
            if (rule.On.Length > 0) { events.Add(rule.On); }
            foreach (var gain in rule.Also) { events.Add(gain.On); }
            if (rule.On.Length > 0 && !DirectEvents.Contains(rule.On) && !rule.ApplyOnHit) { events.Add("hit"); }
        }
    }

    /// <summary>The statuses the last <see cref="Fire"/> newly put on the enemy held (a refresh isn't one), for a spread (ADR 0013).</summary>
    internal IReadOnlyList<string> NewlyOn => newlyOnStatuses;
    private readonly List<string> newlyOnStatuses = [];

    /// <summary>Whether the last <see cref="Fire"/> started a talent's status here, so its ticks start, even on an enemy where a skill's rate only carries that status.</summary>
    internal bool Started { get; private set; }

    public double? NextExpiry => deadlines.Values.Concat(held.FromSkills.Values).Where(until => until > Math.Max(clock, observed)).Select(until => (double?)until).Min();

    /// <summary>A read: it never moves the event clock, so a later event may still fire at an earlier time.</summary>
    public ImmutableHashSet<string> Active(double time)
    {
        CheckTime(time);
        observed = Math.Max(observed, time);
        if (active.Time != time || active.Version != version)
        { active = (time, version, deadlines.Concat(held.FromSkills).Where(pair => time < pair.Value).Select(pair => pair.Key).ToImmutableHashSet(StringComparer.Ordinal)); }
        return active.Statuses;
    }

    /// <summary>
    /// Applies an already armed status on hit, then counts/arms this event, or applies a direct event's status to the current
    /// target; returns whether a status went on that wasn't already running on this target. Refreshing a running status extends
    /// it, but isn't an application: the game's condition is on the status being enabled (R59). Without a target present a
    /// direct event does nothing, not even count.
    /// A rule with whileKey reacts only while <paramref name="gates"/> has its key open; without gates it never does.
    /// </summary>
    /// <param name="distance">How far the enemy is, for a rule with a range; a fight with no distance takes the range as met.</param>
    public bool Fire(string eventName, double time, double durationFactor = 1, IReadOnlySet<string>? eligibleTalents = null, bool targetPresent = true,
        TriggerGates? gates = null, double? distance = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventName);
        CheckTime(time);
        if (!double.IsFinite(durationFactor) || durationFactor <= 0) { throw new ArgumentOutOfRangeException(nameof(durationFactor)); }
        Started = false;
        newlyOnStatuses.Clear();
        if (!events.Contains(eventName)) { Advance(time); return false; }
        var nextCounted = (long[])counted.Clone();
        var nextArmed = (double[])armedAt.Clone();
        var nextFired = (double[])lastFired.Clone();
        var updates = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var index = 0; index < rules.Length; index++)
        {
            var rule = rules[index];
            if (eligibleTalents is not null && !eligibleTalents.Contains(rule.Talent)) { continue; }
            if (rule.On.Length == 0) { continue; }
            if (rule.Within > 0 && distance > rule.Within) { continue; }
            // One status per target (R63): while a skill's rate is this status's damage, a talent's can't start. It stays armed for the next hit after.
            if (eventName == "hit" && nextArmed[index] < time && !Yields(rule.Status, time))
            {
                var until = time + rule.Duration * FactorFor(rule.Status, time, durationFactor);
                if (!double.IsFinite(until) || until <= time)
                { throw new SimulationException("trigger-time-invalid", "Status duration cannot advance time"); }
                updates[rule.Status] = Math.Max(updates.GetValueOrDefault(rule.Status, deadlines.GetValueOrDefault(rule.Status)), until);
                nextArmed[index] = double.NaN;
            }
            if (rule.On != eventName && alsoEvents[index]?.Contains(eventName) != true || time - nextFired[index] < rule.Every) { continue; }
            if (rule.WhileKey.Length > 0 && gates?.Open(rule.WhileKey, rule.WhileFull) != true) { continue; }
            // A whole second, or the triggering hit of an applyOnHit rule, applies the status at once.
            var direct = DirectEvents.Contains(eventName) || rule.ApplyOnHit;
            if (direct && (!targetPresent || Yields(rule.Status, time))) { continue; }
            if (rule.Count > 0 && ++nextCounted[index] < rule.Count) { continue; }
            nextCounted[index] = 0;
            if (direct)
            {
                var until = time + rule.Duration * FactorFor(rule.Status, time, durationFactor);
                if (!double.IsFinite(until) || until <= time)
                { throw new SimulationException("trigger-time-invalid", "Status duration cannot advance time"); }
                updates[rule.Status] = Math.Max(updates.GetValueOrDefault(rule.Status, deadlines.GetValueOrDefault(rule.Status)), until);
                nextFired[index] = time;
            }
            else if (double.IsNaN(nextArmed[index]))
            {
                nextArmed[index] = time;
                nextFired[index] = time;
            }
        }
        Advance(time);
        nextCounted.CopyTo(counted, 0);
        nextArmed.CopyTo(armedAt, 0);
        nextFired.CopyTo(lastFired, 0);
        // Read as Active does, so a status ending at this moment is off and goes on afresh.
        var newlyOn = false;
        foreach (var status in updates.Keys)
        {
            if (time < deadlines.GetValueOrDefault(status)) { continue; }
            held.StartFactors[status] = durationFactor;
            Started = true;
            if (!held.Has(status, time)) { newlyOnStatuses.Add(status); }
            newlyOn |= !held.Has(status, time);
        }
        if (updates.Count > 0) { Accumulate("any", time); }
        foreach (var status in updates.Keys) { Accumulate(status, time); }
        foreach (var (status, until) in updates)
        {
            deadlines[status] = until;
            appliedAt[status] = time;
        }
        if (updates.Count > 0) { version++; }
        return newlyOn;
    }

    /// <summary>
    /// A status put straight on the enemy held now, by a blast (ADR 0013): on for its duration from now, scaled as the player's statuses
    /// are, or a running one extended. As with a talent's, one status per target (R63): while a skill's rate is that status's damage on
    /// this enemy, it doesn't go on.
    /// </summary>
    /// <param name="scale">False for a duration already scaled, such as what's left of a status spread from another enemy.</param>
    /// <returns>Whether it went on, or was extended; false when it yields to a skill's.</returns>
    public bool ApplyDirect(string status, double duration, double time, double durationFactor, bool scale = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(status);
        CheckTime(time);
        if (!double.IsFinite(durationFactor) || durationFactor <= 0) { throw new ArgumentOutOfRangeException(nameof(durationFactor)); }
        if (Yields(status, time)) { return false; }
        var until = time + duration * (scale ? FactorFor(status, time, durationFactor) : 1);
        if (!double.IsFinite(until) || until <= time) { throw new SimulationException("trigger-time-invalid", "Status duration cannot advance time"); }
        Advance(time);
        if (!(time < deadlines.GetValueOrDefault(status))) { held.StartFactors[status] = durationFactor; }
        Accumulate("any", time);
        Accumulate(status, time);
        deadlines[status] = Math.Max(deadlines.GetValueOrDefault(status), until);
        appliedAt[status] = time;
        version++;
        return true;
    }

    /// <summary>
    /// A skill's status is on the current target until this time, and it starts no ticks. Returns
    /// whether it wasn't already on that target as the batch began, from a talent or a skill: a second application is no new one, and a
    /// batch ending with its window isn't one either.
    /// </summary>
    /// <param name="rateIsStatus">Whether the skill's rate is this status's damage, not something else that carries it, such as a Stinger Hive's stings.</param>
    public bool ApplyFromSkill(string status, double until, double batchStart, double time, bool rateIsStatus)
    {
        ArgumentException.ThrowIfNullOrEmpty(status);
        CheckTime(time);
        var newlyOn = !held.Has(status, batchStart);
        MarkFromSkill(status, until, rateIsStatus);
        return newlyOn;
    }

    private void MarkFromSkill(string status, double until, bool rateIsStatus)
    {
        if (until > held.FromSkills.GetValueOrDefault(status))
        {
            held.FromSkills[status] = until;
            version++;
        }
        if (rateIsStatus && until > held.RateIsStatus.GetValueOrDefault(status)) { held.RateIsStatus[status] = until; }
    }

    /// <summary>
    /// A stretch of a skill's own burn or bleed on the current target, and the damage it deals (R65). There's one per enemy, so it keeps its first
    /// amount, two uses don't add, and while a talent's runs it deals nothing and waits for that to end. It lands only while the use is applying:
    /// what follows runs only on an enemy it landed on.
    /// </summary>
    /// <param name="until">When the use's status ends.</param>
    /// <param name="applying">Whether the use is still applying it.</param>
    /// <param name="newlyOn">Whether this is an application, as <see cref="ApplyFromSkill"/> counts them.</param>
    public double DealSkillStatus(string status, double rate, double start, double time, double until, bool applying, out bool newlyOn)
    {
        ArgumentException.ThrowIfNullOrEmpty(status);
        CheckTime(time);
        newlyOn = false;
        var talent = TalentUntil(status);
        var on = start < held.FromSkills.GetValueOrDefault(status);
        if (!on && !applying) { return 0; }
        if (!on || !held.SkillRates.ContainsKey(status)) { held.SkillRates[status] = rate; }
        if (applying) { MarkFromSkill(status, until, true); }
        if (talent >= time)
        {
            held.SkillWaits.Add(status);
            return 0;
        }
        newlyOn = !on | held.SkillWaits.Remove(status);
        var from = Math.Max(Math.Max(start, talent), held.SkillDealt.GetValueOrDefault(status));
        if (from >= time) { return 0; }
        held.SkillDealt[status] = time;
        return held.SkillRates[status] * (time - from);
    }

    /// <summary>When a talent's status ends on the current target, so a skill's own deals nothing until then.</summary>
    internal double TalentUntil(string status) => deadlines.GetValueOrDefault(status);

    // A talent's status not yet on, where a skill's rate is that status's damage.
    private bool Yields(string status, double time) => !(time < deadlines.GetValueOrDefault(status)) && time < held.RateIsStatus.GetValueOrDefault(status);

    // A status reads its bonuses as it starts, so a refresh extends it by the factor it started with (R61).
    private double FactorFor(string status, double time, double durationFactor) =>
        time < deadlines.GetValueOrDefault(status) ? held.StartFactors.GetValueOrDefault(status, durationFactor) : durationFactor;

    /// <summary>The latest application of a status on the current target and its deadline, or null when none.</summary>
    public (double AppliedAt, double Until)? Application(string status) =>
        appliedAt.TryGetValue(status, out var at) ? (at, deadlines[status]) : null;

    /// <summary>The enemy this state now reads and writes. Uptime is settled here, so it counts the enemy being shot.</summary>
    public void Hold(TargetStatuses statuses, double time = 0)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        if (deadlines.Count > 0 || statuses.Deadlines.Count > 0)
        {
            var at = Math.Max(time, clock);
            Accumulate("any", at);
            foreach (var name in Names) { Accumulate(name, at); }
        }
        held = statuses;
        version++;
    }

    public void ResetTarget(double time)
    {
        CheckTime(time);
        Advance(time);
        Accumulate("any", time);
        foreach (var status in deadlines.Keys) { Accumulate(status, time); }
        deadlines.Clear();
        appliedAt.Clear();
        held.StartFactors.Clear();
        held.FromSkills.Clear();
        held.RateIsStatus.Clear();
        held.SkillRates.Clear();
        held.SkillDealt.Clear();
        held.SkillWaits.Clear();
        version++;
    }

    public ImmutableDictionary<string, string> Context(ImmutableDictionary<string, string> assumptions, double time)
    {
        ArgumentNullException.ThrowIfNull(assumptions);
        var statuses = Active(time);
        var result = assumptions.ToBuilder();
        foreach (var (key, status) in keys)
        {
            var on = status == "any" ? statuses.Count > 0 : statuses.Contains(status);
            if (selectorKeys.Contains(key))
            {
                if (on) { result[key] = "true"; }
                else { result.Remove(key); }
            }
            else { result[key] = on ? "1" : "0"; }
        }
        return result.ToImmutable();
    }

    public ResolvedBuild Resolve(BuildSnapshot build, ImmutableDictionary<string, string> assumptions, double time)
    {
        ArgumentNullException.ThrowIfNull(build);
        if (build.PackId != pack.Pack.ID) { throw new SimulationException("pack-mismatch", "The build belongs to another pack"); }
        return BuildResolver.Resolve(pack, build, Context(assumptions, time));
    }

    /// <summary>Cumulative seconds on living targets through time, including the union under "any".
    /// Reads do not move either clock or commit a future measurement.</summary>
    public ImmutableDictionary<string, double> UptimeSeconds(double time)
    {
        CheckTime(time);
        return Names.Append("any").ToImmutableDictionary(name => name,
            name => elapsed.GetValueOrDefault(name) + Interval(name, time), StringComparer.Ordinal);
    }

    private double Interval(string name, double time)
    {
        var until = name == "any" ? deadlines.Values.DefaultIfEmpty(0).Max() : deadlines.GetValueOrDefault(name);
        return Math.Max(0, Math.Min(time, until) - measuredAt.GetValueOrDefault(name));
    }

    private void Accumulate(string name, double time)
    {
        elapsed[name] = elapsed.GetValueOrDefault(name) + Interval(name, time);
        measuredAt[name] = time;
    }

    // An event may land before an earlier read's time, so it restarts the read watermark at the event.
    private void Advance(double time)
    {
        clock = time;
        observed = time;
    }

    private void CheckTime(double time)
    {
        if (!double.IsFinite(time) || time < clock) { throw new ArgumentOutOfRangeException(nameof(time), "Status time must be finite and monotonic"); }
    }
}
