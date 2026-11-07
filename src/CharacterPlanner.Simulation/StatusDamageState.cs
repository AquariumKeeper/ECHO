using System.Collections.Immutable;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation;

/// <summary>
/// Damage-over-time ticks for the statuses on the current target. An application to a target without the status
/// starts a tick schedule at the application (with tickOnApply) or one period later, and fixes its tick amount. Reapplying
/// while the status is still active extends its deadline and keeps the amount and the cadence (R61), even across a tick
/// that fell due after the old deadline. Ticks land strictly before the current deadline.
/// Talent debuffs (Toxic Delivery) tick here too: their event starts one on the current target that spreads its total over
/// one tick per period, the last at the end of its duration; while it runs the event neither restarts nor stacks it.
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
    // Each talent debuff's start (NaN while it isn't on the target), ticks taken and per-tick damage.
    private readonly double[] talentStart;
    private readonly int[] talentTaken;
    private readonly double[] talentAmount;

    public StatusDamageState(IEnumerable<SimStatusDamageDefinition> rules, IEnumerable<SimTalentDamageDefinition>? talents = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        this.rules = [.. rules];
        this.talents = [.. talents ?? []];
        if (this.talents.Any(rule => !double.IsFinite(rule.Period) || rule.Period <= 0 || !double.IsFinite(rule.Duration) || rule.Duration < rule.Period ||
            Math.Abs(rule.Duration / rule.Period - Math.Round(rule.Duration / rule.Period)) > 1e-9 || rule.On.Length == 0 || rule.Total.Length == 0))
        { throw new SimulationException("talent-damage-invalid", "Talent damage needs an event, a total and a period that divides its duration"); }
        talentStart = Enumerable.Repeat(double.NaN, this.talents.Length).ToArray();
        talentTaken = new int[this.talents.Length];
        talentAmount = new double[this.talents.Length];
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

    // A status's scheduled tick, or a talent debuff's next tick computed from its start and index so it doesn't drift.
    private double Next(int index) => index < rules.Length ? next[index] :
        double.IsNaN(talentStart[index - rules.Length]) ? double.NaN :
        talentStart[index - rules.Length] + (talentTaken[index - rules.Length] + 1) * talents[index - rules.Length].Period;

    /// <summary>
    /// An event for these talents starts each matching talent debuff not already on the target, taking its total from the
    /// resolved stats; returns whether any started. Without a living target nothing starts.
    /// </summary>
    public bool StartTalents(string eventName, double time, IReadOnlySet<string>? eligibleTalents, IReadOnlyDictionary<string, double> totals, bool targetPresent)
    {
        ArgumentNullException.ThrowIfNull(totals);
        var started = false;
        for (var index = 0; index < talents.Length; index++)
        {
            var rule = talents[index];
            if (rule.On != eventName || !targetPresent || !double.IsNaN(talentStart[index]) || eligibleTalents is not null && !eligibleTalents.Contains(rule.Talent)) { continue; }
            var total = totals[rule.Total];
            if (!double.IsFinite(total) || total < 0) { throw new SimulationException("talent-damage-invalid", $"Invalid {rule.Total} total damage"); }
            talentStart[index] = time;
            talentTaken[index] = 0;
            talentAmount[index] = total / Ticks(index);
            started = true;
        }
        if (started) { FindDue(); }
        return started;
    }

    /// <summary>A dead or replaced target takes its talent debuffs with it.</summary>
    public void ResetTalents()
    {
        Array.Fill(talentStart, double.NaN);
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
            var talent = due - rules.Length;
            // The last tick lands at the end of the duration; the debuff then ends.
            if (++talentTaken[talent] >= Ticks(talent)) { talentStart[talent] = double.NaN; }
            var damage = talentAmount[talent];
            FindDue();
            return (talents[talent].Talent, damage, true);
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
        for (var talent = 0; talent < talents.Length; talent++)
        {
            var at = Next(rules.Length + talent);
            if (!double.IsNaN(at) && (due < 0 || at < Next(due))) { due = rules.Length + talent; }
        }
    }
}
