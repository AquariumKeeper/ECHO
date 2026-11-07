using System.Collections.Immutable;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation;

/// <summary>One deployment's resolved impact and continuous active window. A fight puts its status on the enemy; this plan doesn't.</summary>
public sealed record SkillUse(string Slot, string Skill, double Time, double WindowEnd, double Rate, double Impact, double StatusRate = 0, double StatusEnd = 0,
    double Impacts = 1, double ImpactInterval = 0)
{
    /// <summary>Until when the use puts its status on an enemy: its window, or its last impact if later (Artillery Turret's shells).</summary>
    public double ApplyingEnd => Math.Max(WindowEnd, Time + (Impacts - 1) * ImpactInterval);
}

/// <summary>Damage meaning is separate from timing. StatusDot is the damage of a skill's own burn or bleed (R65).</summary>
public enum SkillDamageKind { Impact, ContinuousWindow, StatusDot, Deployment }

internal static class SkillDamageKinds
{
    /// <summary>Whether damage runs over a stretch of time, not at a moment.</summary>
    public static bool IsBatch(this SkillDamageKind kind) => kind is SkillDamageKind.ContinuousWindow or SkillDamageKind.StatusDot;
}

/// <summary>One contribution from the resolved deployment plan, with its explicit damage classification.</summary>
public sealed record SkillDamageEvent(double Time, double Damage, string Slot, SkillDamageKind Kind, double Start)
{
    public int HitGroup { get; init; }
}

public sealed class SkillDamageSchedule
{
    /// <summary>A slot's skill as read in one state. <paramref name="Amounts"/> holds its impact and rate against each target profile of a mixed fight.</summary>
    /// <param name="Status">The burn or bleed the use puts on each enemy it lands on, if any, until its window ends.</param>
    /// <param name="StatusRate">The status's own damage per second, if it has one apart from the rate.</param>
    /// <param name="StatusWindow">How long that status lasts after its last application, at the end of the window or the last impact.</param>
    /// <param name="Impacts">How many impacts of <paramref name="Impact"/> a use has, spaced apart. Those landing together are one, of their total.</param>
    /// <param name="ImpactInterval">The seconds between them.</param>
    internal sealed record Plan(string Slot, string Skill, double Window, double Cycle, double Rate, double Impact, string RateArmorBonus, bool LiveRate,
        string Status = "", ImmutableDictionary<int, (double Impact, double Rate, double StatusRate)>? Amounts = null, double Stock = 1, double StockInterval = 0,
        double StatusRate = 0, double StatusWindow = 0, string StatusArmorBonus = "", double Impacts = 1, double ImpactInterval = 0)
    {
        /// <summary>The use deployed at this time, in a fight of this length, whose impacts end with it.</summary>
        public SkillUse UseAt(double time, double duration)
        {
            var impacts = ImpactsBefore(time, duration);
            // Each impact puts the status on, so it applies until the last and runs its own window after that.
            var applying = Math.Max(Window, (impacts - 1) * ImpactInterval);
            return new(Slot, Skill, time, Math.Min(duration, time + Window), Rate, Impact, StatusRate, Math.Min(duration, time + applying + StatusWindow), impacts, ImpactInterval);
        }

        // The division can round up, and an impact at the fight's very end has no timeline bucket.
        private double ImpactsBefore(double time, double duration)
        {
            if (Impacts <= 1) { return 1; }
            var count = Math.Min(Impacts, Math.Ceiling((duration - time) / ImpactInterval));
            return count > 1 && time + (count - 1) * ImpactInterval >= duration ? count - 1 : count;
        }

        /// <summary>When the next use comes after one at this time.</summary>
        public double After(double time)
        {
            var next = time + Cycle;
            if (!double.IsFinite(next) || next <= time) { throw new SimulationException("time-not-advancing", "Skill cycle cannot advance time"); }
            return next;
        }
    }
    private readonly ImmutableArray<Plan> plans;
    // Read for every batch of every fight, so held by slot.
    private readonly ImmutableDictionary<string, string> rateArmorBonuses;

    private SkillDamageSchedule(ImmutableArray<Plan> plans)
    {
        this.plans = plans;
        rateArmorBonuses = plans.ToImmutableDictionary(plan => plan.Slot, plan => plan.RateArmorBonus, StringComparer.Ordinal);
    }

    public static SkillDamageSchedule Create(GamePack pack, ImmutableDictionary<string, SkillPreview> previews, bool shieldsHandled = false)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(previews);
        var plans = ImmutableArray.CreateBuilder<Plan>();
        foreach (var slot in pack.ActiveSkillSlots)
        {
            if (!previews.TryGetValue(slot.ID, out var preview)) { continue; }
            var matches = pack.Simulation.Skills.Where(rule => rule.Skill == preview.SkillId).Take(2).ToArray();
            if (matches.Length == 0) { throw new SimulationException("skill-rule-required", $"No explicit simulation rule for {preview.SkillId}"); }
            if (matches.Length > 1) { throw new SimulationException("skill-rule-duplicate", $"More than one simulation rule for {preview.SkillId}"); }
            var rule = matches[0];
            if (rule.Shield.Length > 0)
            {
                if (shieldsHandled) { continue; }
                throw new SimulationException("skill-shield-unsupported", "Shields require the incoming-damage scheduler");
            }
            double Read(string id)
            {
                if (id.Length == 0) { return 0; }
                var outputs = preview.Outputs.Where(output => output.Id == id).Take(2).ToArray();
                if (outputs.Length > 1) { throw new SimulationException("skill-output-invalid", $"Duplicate {preview.SkillId}.{id}"); }
                var output = outputs.FirstOrDefault();
                if (output is null || !double.IsFinite(output.Value) || output.Value < 0)
                { throw new SimulationException("skill-output-invalid", $"Missing, negative or non-finite {preview.SkillId}.{id}"); }
                return output.Value;
            }
            var window = Read(rule.Window);
            var cooldown = Read(rule.Cooldown);
            var rate = Read(rule.Rate);
            var impact = Read(rule.Impact);
            var impacts = 1d;
            var interval = Read(rule.ImpactInterval);
            if (rule.Impacts.Length > 0)
            {
                var count = Read(rule.Impacts);
                if (count < 1 || count != Math.Floor(count)) { throw new SimulationException("skill-impact-count-invalid", preview.SkillId); }
                if (rule.ImpactInterval.Length == 0) { impact *= count; }
                else if (interval > 0 || count == 1) { impacts = count; }
                else { throw new SimulationException("skill-output-invalid", $"Invalid impact interval for {preview.SkillId}"); }
            }
            var stock = rule.Stock.Length > 0 ? Read(rule.Stock) : 1;
            if (stock < 1 || stock != Math.Floor(stock)) { throw new SimulationException("skill-output-invalid", $"Invalid stock for {preview.SkillId}"); }
            var statusRate = Read(rule.StatusRate);
            var statusWindow = Read(rule.StatusWindow);
            // A use that runs from its end isn't over until its last impact.
            var cycle = cooldown + (rule.CooldownFromEnd ? Math.Max(window, (impacts - 1) * interval) : 0);
            if (!double.IsFinite(cycle) || cycle <= 0 || !double.IsFinite(impact) || !double.IsFinite(rate * window) || !double.IsFinite(statusRate * (window + statusWindow)))
            { throw new SimulationException("skill-cycle-invalid", $"Invalid cycle or damage for {preview.SkillId}"); }
            plans.Add(new(slot.ID, preview.SkillId, window, cycle, rate, impact, rule.RateArmorBonus, rule.RateRead == "live", StatusDamageState.Damaging.Contains(rule.Status) ? rule.Status : "",
                null, stock, rule.StockInterval, statusRate, statusWindow, rule.StatusArmorBonus, impacts, interval));
        }
        return new(plans.ToImmutable());
    }

    /// <summary>This schedule with each slot's amounts against each target profile (P10).</summary>
    internal SkillDamageSchedule WithAmounts(IReadOnlyDictionary<int, SkillDamageSchedule> profiles) =>
        new([.. plans.Select(plan => plan with { Amounts = profiles.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.Damage()[plan.Slot]) })]);

    /// <summary>A slot's plan. A slot that loses its skill in some state has none, which only a broken pack can do.</summary>
    internal Plan For(string slot) => plans.FirstOrDefault(plan => plan.Slot == slot) ??
        throw new SimulationException("skill-output-invalid", $"Skill slot {slot} has no plan in this state");

    /// <summary>
    /// Whether the other schedule deploys the same skills at the same times, with damage of the same kinds: only the amounts may
    /// differ (a skill's damage against another target profile, P10).
    /// </summary>
    internal bool SameTiming(SkillDamageSchedule other) => plans.Length == other.plans.Length && plans.Zip(other.plans).All(pair =>
        pair.First.Slot == pair.Second.Slot && pair.First.Skill == pair.Second.Skill && pair.First.Window == pair.Second.Window &&
        pair.First.Cycle == pair.Second.Cycle && pair.First.Stock == pair.Second.Stock && pair.First.StockInterval == pair.Second.StockInterval && pair.First.StatusWindow == pair.Second.StatusWindow &&
        pair.First.Impacts == pair.Second.Impacts && pair.First.ImpactInterval == pair.Second.ImpactInterval &&
        (pair.First.Rate > 0) == (pair.Second.Rate > 0) && (pair.First.Impact > 0) == (pair.Second.Impact > 0) && (pair.First.StatusRate > 0) == (pair.Second.StatusRate > 0));

    /// <summary>The stat adding to the armor factor of a slot's rate, or empty for none (R20).</summary>
    internal string RateArmorBonus(string slot) => rateArmorBonuses[slot];

    /// <summary>Each slot's impact and continuous rate, for dealing an event's damage as this schedule would (P10).</summary>
    internal ImmutableDictionary<string, (double Impact, double Rate, double StatusRate)> Damage() =>
        plans.ToImmutableDictionary(plan => plan.Slot, plan => (plan.Impact, plan.Rate, plan.StatusRate), StringComparer.Ordinal);

    /// <summary>Impacts plus 100 ms continuous-damage batches, including the final clipped window endpoint.</summary>
    internal ImmutableArray<SkillDamageEvent> DamageEvents(double duration, CancellationToken cancellationToken = default)
    {
        var events = ImmutableArray.CreateBuilder<SkillDamageEvent>();
        void Add(SkillDamageEvent entry)
        {
            if (events.Count == SkillEventStream.EventLimit) { throw new SimulationException("skill-work-limit", "Too many skill damage events"); }
            events.Add(entry with { HitGroup = events.Count + 1 });
        }
        foreach (var use in Uses(duration, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in UseEvents(use, cancellationToken)) { Add(entry); }
        }
        return [.. events.OrderBy(entry => entry.Time).ThenBy(entry => entry.Kind == SkillDamageKind.Impact)];
    }

    /// <summary>One use's impacts, then its rate's batches in order, then its status's.</summary>
    internal static IEnumerable<SkillDamageEvent> UseEvents(SkillUse use, CancellationToken cancellationToken)
    {
        for (var index = 0d; use.Impact > 0 && index < use.Impacts; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var time = use.Time + index * use.ImpactInterval;
            yield return new(time, use.Impact, use.Slot, SkillDamageKind.Impact, time);
        }
        foreach (var batch in Batches(use, use.WindowEnd, use.Rate, SkillDamageKind.ContinuousWindow, cancellationToken)) { yield return batch; }
        foreach (var batch in Batches(use, use.StatusEnd, use.StatusRate, SkillDamageKind.StatusDot, cancellationToken)) { yield return batch; }
    }

    private static IEnumerable<SkillDamageEvent> Batches(SkillUse use, double end, double rate, SkillDamageKind kind, CancellationToken cancellationToken)
    {
        if (rate == 0) { yield break; }
        var previous = use.Time;
        while (previous < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Split at integer boundaries too, keeping each batch inside a timeline bucket.
            var next = Math.Min(end, Math.Min(previous + 0.1, Math.Floor(previous) + 1));
            if (next <= previous) { throw new SimulationException("time-not-advancing", "Skill tick cannot advance time"); }
            yield return new(next, rate * (next - previous), use.Slot, kind, previous);
            previous = next;
        }
    }

    /// <summary>A run's fights on this schedule, with batches cut at these attempts, if any.</summary>
    public SkillFights Fights(double duration, ImmutableArray<double> boundaries, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(duration) || duration <= 0 || duration > 600) { throw new ArgumentOutOfRangeException(nameof(duration)); }
        CheckBoundaries(boundaries, cancellationToken);
        return new(plans, duration, boundaries, cancellationToken);
    }

    /// <summary>
    /// A work limit a whole fight on this schedule would reach, from its cooldowns alone, so it doesn't wait for the rolls. A stream bounds its
    /// own as it goes, for a state that shortens them.
    /// </summary>
    internal void CheckWork(double duration, ImmutableArray<double> boundaries)
    {
        double deployments = 0, events = 0;
        foreach (var plan in plans)
        {
            var uses = Math.Ceiling(duration / plan.Cycle) + plan.Stock - 1;
            var window = (plan.Rate > 0 ? Math.Min(plan.Window, duration) : 0) + (plan.StatusRate > 0 ? Math.Min(Math.Max(plan.Window, (plan.Impacts - 1) * plan.ImpactInterval) + plan.StatusWindow, duration) : 0);
            deployments += uses;
            events += uses * ((plan.Impact > 0 ? plan.Impacts > 1 ? Math.Min(plan.Impacts, Math.Ceiling(duration / plan.ImpactInterval)) : 1 : 0) + Math.Ceiling(window * 10) + Math.Ceiling(window)) + boundaries.Length * Math.Ceiling(window / plan.Cycle);
        }
        if (deployments > SkillEventStream.UseLimit) { throw new SimulationException("skill-work-limit", "Too many skill deployments"); }
        if (events > SkillEventStream.EventLimit) { throw new SimulationException("skill-work-limit", "Too many skill damage events"); }
    }

    private static void CheckBoundaries(ImmutableArray<double> boundaries, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var previous = -1d;
        foreach (var boundary in boundaries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!double.IsFinite(boundary) || boundary < 0 || boundary <= previous) { throw new ArgumentOutOfRangeException(nameof(boundaries)); }
            previous = boundary;
        }
    }

    /// <summary>Split continuous batches at enemy attempts; elapsed ticks precede same-time instantaneous events.</summary>
    internal static ImmutableArray<SkillDamageEvent> SplitAt(ImmutableArray<SkillDamageEvent> events, ImmutableArray<double> boundaries,
        CancellationToken cancellationToken = default)
    {
        CheckBoundaries(boundaries, cancellationToken);
        var result = ImmutableArray.CreateBuilder<SkillDamageEvent>();
        void Add(SkillDamageEvent entry)
        {
            if (result.Count == SkillEventStream.EventLimit) { throw new SimulationException("skill-work-limit", "Too many split skill events"); }
            result.Add(entry);
        }
        foreach (var entry in events) { Cut(entry, boundaries, Add, cancellationToken); }
        return [.. result.OrderBy(entry => entry.Time).ThenBy(entry => entry.Kind == SkillDamageKind.Impact)];
    }

    /// <summary>One event's pieces, in order, for boundaries already checked.</summary>
    internal static void Cut(SkillDamageEvent entry, ImmutableArray<double> boundaries, Action<SkillDamageEvent> add, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entry.Kind != SkillDamageKind.Impact && !entry.Kind.IsBatch())
        { throw new SimulationException("skill-event-kind-unsupported", "Unknown skill damage kinds are not modeled"); }
        if (!double.IsFinite(entry.Start) || !double.IsFinite(entry.Time) || entry.Start < 0 || entry.Time < entry.Start ||
            entry.Kind.IsBatch() && entry.Time == entry.Start || !double.IsFinite(entry.Damage) || entry.Damage < 0)
        { throw new SimulationException("skill-event-invalid", "Skill events require finite times and nonnegative damage; continuous intervals must be positive"); }
        if (entry.Kind == SkillDamageKind.Impact) { add(entry); return; }
        var index = boundaries.BinarySearch(entry.Start);
        index = index < 0 ? ~index : index + 1;
        var start = entry.Start;
        var remaining = entry.Damage;
        while (index < boundaries.Length && boundaries[index] < entry.Time)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = boundaries[index++];
            var damage = Math.Min(remaining, entry.Damage * ((end - start) / (entry.Time - entry.Start)));
            add(entry with { Start = start, Time = end, Damage = damage });
            remaining -= damage;
            start = end;
        }
        add(entry with { Start = start, Damage = remaining });
    }

    /// <summary>Unlimited-dummy damage bins, integrating each active window exactly over bucket boundaries.</summary>
    internal ImmutableArray<double> DummyTimeline(double duration, CancellationToken cancellationToken = default)
    {
        var uses = Uses(duration, cancellationToken);
        var bins = new double[(int)Math.Ceiling(duration)];
        void Add(int bin, double damage)
        {
            var total = bins[bin] + damage;
            if (!double.IsFinite(total)) { throw new SimulationException("damage-overflow", "Skill damage overflowed"); }
            bins[bin] = total;
        }
        foreach (var use in uses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var index = 0d; index < use.Impacts; index++) { Add((int)(use.Time + index * use.ImpactInterval), use.Impact); }
            for (var bin = (int)use.Time; bin < bins.Length && bin < use.WindowEnd; bin++)
            {
                var overlap = Math.Max(0, Math.Min(bin + 1, use.WindowEnd) - Math.Max(bin, use.Time));
                Add(bin, overlap * use.Rate);
            }
        }
        return [.. bins];
    }

    /// <summary>
    /// Deploy at zero and each cooldown cycle before duration. Multiple impacts are an aggregate
    /// at deployment unless the rule spaces them. Continuous windows may overlap.
    /// </summary>
    internal ImmutableArray<SkillUse> Uses(double duration, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(duration) || duration <= 0 || duration > 600) { throw new ArgumentOutOfRangeException(nameof(duration)); }
        cancellationToken.ThrowIfCancellationRequested();
        var uses = ImmutableArray.CreateBuilder<SkillUse>();
        foreach (var plan in plans)
        {
            // Only the stream keeps charges, so a stocked skill's uses can't be read off its cycle here.
            if (plan.Stock > 1) { throw new SimulationException("skill-output-invalid", $"Skill slot {plan.Slot} holds charges, which only fights schedule"); }
            for (var time = 0d; time < duration;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (uses.Count == SkillEventStream.UseLimit) { throw new SimulationException("skill-work-limit", "Too many skill deployments"); }
                uses.Add(plan.UseAt(time, duration));
                time = plan.After(time);
            }
        }
        return [.. uses.OrderBy(use => use.Time)];
    }
}
