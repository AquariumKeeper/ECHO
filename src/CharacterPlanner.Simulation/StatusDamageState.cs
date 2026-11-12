using System.Collections.Immutable;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation;

/// <summary>
/// Damage-over-time ticks for the statuses on the current target. An application to a target without the status
/// starts a tick schedule at the application (with tickOnApply) or one period later, and fixes its tick amount. Reapplying
/// while the status is still active extends its deadline and keeps the amount and the cadence (R61), even across a tick
/// that fell due after the old deadline. Ticks land strictly before the current deadline.
/// Talent debuffs (Toxic Delivery) tick here too: their event starts one on the current target that spreads its total over
/// one tick per period, the last at the end of its duration. A rule's event starts another only while fewer than its
/// <see cref="SimTalentDamageDefinition.MaxStacks"/> run on the target (one by default: a running debuff is neither
/// restarted nor stacked); each runs on its own schedule. Ties tick statuses first, then by rule order, then in the order they started.
/// </summary>
public sealed class StatusDamageState
{
    internal static readonly ImmutableHashSet<string> Damaging = ImmutableHashSet.Create(StringComparer.Ordinal, "burning", "bleeding");
    private readonly ImmutableArray<SimStatusDamageDefinition> rules;
    // The schedule's next tick; NaN while the status is not on the target. It may lie at or past the deadline.
    private readonly double[] next;
    private readonly double[] until;
    private readonly double[] amount;
    private readonly double[] applied;
    private int due = -1;
    private readonly ImmutableArray<SimTalentDamageDefinition> talents;
    // The talent debuffs running on the target, in the order they started: each one's rule, start, ticks taken and per-tick damage.
    private readonly List<Debuff> running = [];

    private sealed class Debuff(int rule, double start, double amount)
    {
        public int Rule { get; } = rule;
        public double Start { get; } = start;
        public double Amount { get; } = amount;
        public int Taken { get; set; }
    }

    public StatusDamageState(IEnumerable<SimStatusDamageDefinition> rules, IEnumerable<SimTalentDamageDefinition>? talents = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        this.rules = [.. rules];
        this.talents = [.. talents ?? []];
        if (this.talents.Any(rule => !double.IsFinite(rule.Period) || rule.Period <= 0 || !double.IsFinite(rule.Duration) || rule.Duration < rule.Period ||
            Math.Abs(rule.Duration / rule.Period - Math.Round(rule.Duration / rule.Period)) > 1e-9 || rule.On.Length == 0 || rule.Total.Length == 0 || rule.MaxStacks < 1))
        { throw new SimulationException("talent-damage-invalid", "Talent damage needs an event, a total, a period that divides its duration and at least one stack"); }
        if (this.rules.Any(rule => !Damaging.Contains(rule.Status) || !double.IsFinite(rule.Period) || rule.Period <= 0) ||
            this.rules.Select(rule => rule.Status).Distinct(StringComparer.Ordinal).Count() != this.rules.Length)
        { throw new SimulationException("status-damage-invalid", "Status damage needs one rule per burning or bleeding status and a positive period"); }
        next = Enumerable.Repeat(double.NaN, this.rules.Length).ToArray();
        until = new double[this.rules.Length];
        amount = new double[this.rules.Length];
        applied = Enumerable.Repeat(double.NaN, this.rules.Length).ToArray();
    }

    public double? NextTick => due < 0 ? null : Next(due);

    /// <summary>The total stats the talent debuffs read when they start.</summary>
    public IEnumerable<string> TalentTotals => talents.Select(rule => rule.Total);

    private int Ticks(int talent) => (int)Math.Round(talents[talent].Duration / talents[talent].Period);

    // A status's scheduled tick (index below the rules' count), or a running debuff's next tick computed from its start and
    // ticks taken so it doesn't drift.
    private double Next(int index) => index < rules.Length ? next[index] : Next(running[index - rules.Length]);

    private double Next(Debuff debuff) => debuff.Start + (debuff.Taken + 1) * talents[debuff.Rule].Period;

    /// <summary>
    /// An event for these talents starts each matching talent debuff that has room for another stack on the target, taking
    /// its total from the resolved stats; returns whether any started. Without a living target nothing starts.
    /// </summary>
    public bool StartTalents(string eventName, double time, IReadOnlySet<string>? eligibleTalents, IReadOnlyDictionary<string, double> totals, bool targetPresent)
    {
        ArgumentNullException.ThrowIfNull(totals);
        var started = false;
        for (var index = 0; index < talents.Length; index++)
        {
            var rule = talents[index];
            if (rule.On != eventName || !targetPresent || eligibleTalents is not null && !eligibleTalents.Contains(rule.Talent) ||
                running.Count(debuff => debuff.Rule == index) >= rule.MaxStacks) { continue; }
            var total = totals[rule.Total];
            if (!double.IsFinite(total) || total < 0) { throw new SimulationException("talent-damage-invalid", $"Invalid {rule.Total} total damage"); }
            running.Add(new(index, time, total / Ticks(index)));
            started = true;
        }
        if (started) { FindDue(); }
        return started;
    }

    /// <summary>
    /// A talent's running debuffs leave this enemy, to pass on (ADR 0013): each keeps its rule, start, ticks taken and amount, so its
    /// schedule goes on where it lands. Every enemy's state is made from the same rules, so they mean the same rule there.
    /// </summary>
    internal (int Rule, double Start, double Amount, int Taken)[] TakeTalent(string talent)
    {
        var taken = running.Where(debuff => talents[debuff.Rule].Talent == talent).Select(debuff => (debuff.Rule, debuff.Start, debuff.Amount, debuff.Taken)).ToArray();
        if (taken.Length == 0) { return taken; }
        running.RemoveAll(debuff => talents[debuff.Rule].Talent == talent);
        FindDue();
        return taken;
    }

    /// <summary>
    /// Debuffs passed on from another enemy start running here, the latest-started first, up to each rule's stack cap with what's
    /// already here; the rest are lost. Returns how many landed.
    /// </summary>
    internal int Receive(IEnumerable<(int Rule, double Start, double Amount, int Taken)> debuffs)
    {
        var landed = 0;
        foreach (var debuff in debuffs.OrderByDescending(entry => entry.Start))
        {
            if (running.Count(entry => entry.Rule == debuff.Rule) >= talents[debuff.Rule].MaxStacks) { continue; }
            running.Add(new(debuff.Rule, debuff.Start, debuff.Amount) { Taken = debuff.Taken });
            landed++;
        }
        if (landed == 0) { return 0; }
        // Back in start order, which ticks due together are taken in; a stable sort keeps this enemy's own order among equal starts.
        var ordered = running.OrderBy(debuff => debuff.Start).ToArray();
        running.Clear();
        running.AddRange(ordered);
        FindDue();
        return landed;
    }

    /// <summary>A dead or replaced target takes its talent debuffs with it.</summary>
    public void ResetTalents()
    {
        running.Clear();
        FindDue();
    }

    /// <summary>Reads the statuses' latest applications; ticks holds the current per-tick amount of each status.</summary>
    public void Sync(StatusTriggerState statuses, IReadOnlyDictionary<string, double> ticks)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        ArgumentNullException.ThrowIfNull(ticks);
        for (var index = 0; index < rules.Length; index++)
        {
            var rule = rules[index];
            if (statuses.Application(rule.Status) is not { } application)
            {
                next[index] = double.NaN;
                applied[index] = double.NaN;
                continue;
            }
            if (application.AppliedAt != applied[index])
            {
                // A status that had expired, or was never on, starts over with the amount now; one still active keeps its amount and cadence.
                var active = !double.IsNaN(applied[index]) && application.AppliedAt < until[index];
                if (!active)
                {
                    var tick = ticks[rule.Status];
                    if (!double.IsFinite(tick) || tick < 0) { throw new SimulationException("status-damage-invalid", $"Invalid {rule.Status} tick damage"); }
                    next[index] = application.AppliedAt + (rule.TickOnApply ? 0 : rule.Period);
                    amount[index] = tick;
                }
                applied[index] = application.AppliedAt;
            }
            until[index] = application.Until;
        }
        FindDue();
    }

    /// <summary>Takes the tick due at time and advances that status's (or talent debuff's) schedule by one period.</summary>
    public (string Source, double Damage, bool Talent) Take(double time)
    {
        if (due < 0 || Next(due) != time) { throw new InvalidOperationException("No status tick is due at that time"); }
        if (due >= rules.Length)
        {
            var debuff = running[due - rules.Length];
            // The last tick lands at the end of the duration; the debuff then ends.
            if (++debuff.Taken >= Ticks(debuff.Rule)) { running.RemoveAt(due - rules.Length); }
            FindDue();
            return (talents[debuff.Rule].Talent, debuff.Amount, true);
        }
        var index = due;
        var following = time + rules[index].Period;
        if (!double.IsFinite(following) || following <= time) { throw new SimulationException("time-not-advancing", "The status tick period cannot advance time"); }
        next[index] = following;
        FindDue();
        return (rules[index].Status, amount[index], false);
    }

    private void FindDue()
    {
        due = -1;
        for (var index = 0; index < rules.Length; index++)
        {
            if (next[index] < until[index] && (due < 0 || next[index] < Next(due))) { due = index; }
        }
        for (var position = 0; position < running.Count; position++)
        {
            // Ties go to a status first, then to the talent rule listed first, then to the debuff started first.
            var at = Next(running[position]);
            if (due < 0 || at < Next(due) || at == Next(due) && due >= rules.Length && running[position].Rule < running[due - rules.Length].Rule)
            { due = rules.Length + position; }
        }
    }
}
