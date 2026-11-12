using System.Collections.Immutable;

namespace CharacterPlanner.Simulation;

/// <summary>
/// What's shared by a run's fights: the same use at the same time makes the same events, so each is made once, with batches cut at
/// the attempts known in advance, if any.
/// </summary>
public sealed class SkillFights
{
    private readonly ImmutableArray<SkillDamageSchedule.Plan> plans;
    private readonly double duration;
    private readonly ImmutableArray<double> boundaries;
    private readonly CancellationToken cancellationToken;
    // Fights whose cooldowns follow the state deploy at different times, so what's kept for reuse is bounded.
    private const int MadeLimit = 4096;
    private readonly Dictionary<(int Slot, double Time, double Window, double Rate, double Impact, double StatusRate, double StatusWindow, double Impacts, double ImpactInterval), SkillUseEvent[]> made = [];
    private int hitGroups;

    internal SkillFights(ImmutableArray<SkillDamageSchedule.Plan> plans, double duration, ImmutableArray<double> boundaries, CancellationToken cancellationToken) =>
        (this.plans, this.duration, this.boundaries, this.cancellationToken) = (plans, duration, boundaries, cancellationToken);

    internal ImmutableArray<SkillDamageSchedule.Plan> Plans => plans;

    /// <summary>The plan index of a skill slot, or -1 when the build has no damaging skill there.</summary>
    internal int IndexOf(string slot)
    {
        for (var index = 0; index < plans.Length; index++) { if (plans[index].Slot == slot) { return index; } }
        return -1;
    }
    internal double Duration => duration;

    public SkillEventStream NewFight() => new(this);

    // A use's impact and batches, in order. A batch's pieces carry the time the whole batch ends.
    internal SkillUseEvent[] Use(int index, double time, SkillDamageSchedule.Plan plan)
    {
        var key = (index, time, plan.Window, plan.Rate, plan.Impact, plan.StatusRate, plan.StatusWindow, plan.Impacts, plan.ImpactInterval);
        if (made.TryGetValue(key, out var events)) { return events; }
        if (made.Count == MadeLimit) { made.Clear(); }
        var builder = new List<SkillUseEvent>();
        foreach (var whole in SkillDamageSchedule.UseEvents(plan.UseAt(time, duration), cancellationToken))
        {
            // A use's impacts aren't bounded by the fight's length alone, so this can't wait for the stream's count.
            if (builder.Count >= SkillEventStream.EventLimit) { throw new SimulationException("skill-work-limit", "Too many skill damage events"); }
            var entry = whole with { HitGroup = ++hitGroups };
            SkillDamageSchedule.Cut(entry, boundaries, piece => builder.Add(new(piece, entry.Time, time, index, builder.Count)), cancellationToken);
        }
        // A rate, a status and spaced impacts run side by side, so a stream walking the use needs them merged.
        builder.Sort(SkillUseEvent.Compare);
        return made[key] = [.. builder];
    }
}

/// <summary>
/// An event and its place in the order a list of the whole fight would hold: by time, then ticks before impacts, then which batch ended
/// first, which use came first and which slot.
/// </summary>
internal sealed class SkillUseEvent(SkillDamageEvent entry, double batch, double use, int plan, int index)
{
    public SkillDamageEvent Event { get; } = entry;
    private double Batch { get; } = batch;
    private double Use { get; } = use;
    private int Plan { get; } = plan;
    private int Index { get; } = index;
    private bool IsImpact => Event.Kind == SkillDamageKind.Impact;

    public static int Compare(SkillUseEvent first, SkillUseEvent second)
    {
        var order = first.Event.Time.CompareTo(second.Event.Time);
        if (order == 0) { order = first.IsImpact.CompareTo(second.IsImpact); }
        if (order == 0) { order = first.Batch.CompareTo(second.Batch); }
        if (order == 0) { order = first.Use.CompareTo(second.Use); }
        if (order == 0) { order = first.Plan.CompareTo(second.Plan); }
        return order != 0 ? order : first.Index.CompareTo(second.Index);
    }
}

/// <summary>
/// One fight's skill damage, made as the fight reaches it. Only a few uses run at once, so this walks each one's events side by side
/// rather than sorting them all.
/// </summary>
public sealed class SkillEventStream
{
    internal const int UseLimit = 100000;
    internal const int EventLimit = 1000000;

    private sealed class Running(SkillUseEvent[] events, SkillDamageSchedule.Plan plan, SkillUse use)
    {
        public SkillDamageSchedule.Plan Plan => plan;
        public double Until => plan.StatusRate > 0 ? use.StatusEnd : use.WindowEnd;
        public double WindowEnd => use.ApplyingEnd;
        public SkillUseEvent Head => events[At];
        public int At { get; set; }
        public bool Done => At >= events.Length;
    }

    private readonly SkillFights fights;
    private readonly double[] nextUse;
    private readonly List<Running> running = [];
    private Running? soonest;
    private bool settled;
    private SkillDamageEvent? deployment;
    private int deploying;
    // Each stocked skill's charges, NaN before its first use, and when the next comes back, infinite when it's full.
    private readonly double[] stocks;
    private readonly double[] held;
    private readonly double[] recharges;
    // Each slot's last use and the plan it deployed with, for shortening its cooldown; NaN and null before its first.
    private readonly double[] lastUse;
    private readonly SkillDamageSchedule.Plan?[] lastPlan;
    private int uses;
    private int events;
    // ADR 0014: each slot's uses this fight, whether it's held waiting for its condition, since when, and its held time so far.
    private readonly int[] slotUses;
    private readonly double[] heldSince;
    private readonly double[] heldSeconds;

    internal SkillEventStream(SkillFights fights)
    {
        this.fights = fights;
        nextUse = new double[fights.Plans.Length];
        stocks = Enumerable.Repeat(double.NaN, nextUse.Length).ToArray();
        held = new double[nextUse.Length];
        recharges = Enumerable.Repeat(double.PositiveInfinity, nextUse.Length).ToArray();
        lastUse = Enumerable.Repeat(double.NaN, nextUse.Length).ToArray();
        lastPlan = new SkillDamageSchedule.Plan?[nextUse.Length];
        slotUses = new int[nextUse.Length];
        heldSince = Enumerable.Repeat(double.NaN, nextUse.Length).ToArray();
        heldSeconds = new double[nextUse.Length];
    }

    /// <summary>The plan index of a skill slot, or -1 when the build has no damaging skill there.</summary>
    internal int IndexOf(string slot) => fights.IndexOf(slot);

    /// <summary>A slot's first use waits until <paramref name="at"/> (ADR 0014's <c>afterSeconds</c>); later ones follow its cooldown.</summary>
    internal void DelayFirst(int index, double at)
    {
        if (uses > 0) { throw new InvalidOperationException("A first use is delayed before the fight starts"); }
        // Held from when it would be ready until then, or to the fight's end if that's sooner.
        heldSeconds[index] += Math.Max(0, Math.Min(at, fights.Duration) - nextUse[index]);
        nextUse[index] = Math.Max(nextUse[index], at);
        deployment = null;
        settled = false;
    }

    /// <summary>
    /// Whether a ready slot may deploy now (ADR 0014's conditions). A slot that may not is held until <see cref="Release"/> finds it may;
    /// null lets every slot deploy when ready. Only a fight that reads its skills as they deploy can hold one.
    /// </summary>
    internal Func<int, double, bool>? MayDeploy { get; set; }

    /// <summary>Lets each held slot whose condition now holds deploy at <paramref name="at"/>.</summary>
    internal void Release(double at)
    {
        if (MayDeploy is null) { return; }
        for (var index = 0; index < nextUse.Length; index++)
        {
            if (double.IsNaN(heldSince[index]) || at >= fights.Duration || !MayDeploy(index, at)) { continue; }
            heldSeconds[index] += Math.Max(0, at - heldSince[index]);
            heldSince[index] = double.NaN;
            nextUse[index] = at;
            deployment = null;
            settled = false;
        }
    }

    /// <summary>A slot's uses this fight.</summary>
    internal int UsesOf(int index) => slotUses[index];

    /// <summary>The time a slot was ready but held this fight, a hold still running counted to <paramref name="end"/>.</summary>
    internal double HeldFor(int index, double end) => heldSeconds[index] + (double.IsNaN(heldSince[index]) ? 0 : Math.Max(0, end - heldSince[index]));

    /// <summary>
    /// Takes time off every skill cooldown running at this time: <paramref name="share"/> of each skill's own cooldown plus
    /// <paramref name="seconds"/> (a share of 1 is a reset). A cooldown that runs from a use's end can't end before that use
    /// does, and none ends before now. A stocked skill's next charge comes back sooner, and charges back by now are held. A
    /// skill not yet used, ready now, or used at this very moment is untouched. Only a stream that reads each use as it deploys has its next uses still to
    /// make; a fight that drives buffs, the only kind that grants this, always does.
    /// </summary>
    internal void Shorten(double at, double share, double seconds)
    {
        if (Read is null) { throw new InvalidOperationException("Cooldowns can be cut only in a fight that reads its skills as they deploy"); }
        if (!double.IsFinite(at) || !double.IsFinite(share) || share < 0 || !double.IsFinite(seconds) || seconds < 0)
        { throw new SimulationException("skill-cooldown-invalid", "A cooldown reduction must be finite and not negative"); }
        for (var index = 0; index < nextUse.Length; index++)
        {
            // A cooldown that starts at this very moment isn't cut: a use whose own impact resets it (Skilled on a skill kill)
            // would otherwise deploy again at once, without end.
            // A held skill is ready, waiting only for its condition (ADR 0014), so there's nothing to cut.
            if (lastPlan[index] is not { } plan || lastUse[index] >= at || !double.IsNaN(heldSince[index])) { continue; }
            var amount = share * plan.Cooldown + seconds;
            var earliest = Math.Max(at, lastUse[index] + (plan.Cycle - plan.Cooldown));
            if (stocks[index] > 1)
            {
                if (double.IsFinite(recharges[index]) && recharges[index] > at) { recharges[index] = Math.Max(earliest, recharges[index] - amount); }
                while (recharges[index] <= at)
                {
                    held[index]++;
                    recharges[index] = held[index] < stocks[index] ? recharges[index] + plan.Cycle : double.PositiveInfinity;
                }
                if (nextUse[index] > at) { nextUse[index] = Math.Max(lastUse[index] + plan.StockInterval, held[index] >= 1 ? at : recharges[index]); }
            }
            else if (nextUse[index] > at) { nextUse[index] = Math.Max(earliest, nextUse[index] - amount); }
        }
        // A deployment already announced may now come sooner.
        deployment = null;
        settled = false;
    }

    // When a slot's cooldown after its last use starts: at the use, or at its end for one that runs from the end.
    private double CooldownStart(int index, SkillDamageSchedule.Plan plan) => lastUse[index] + (plan.Cycle - plan.Cooldown);

    /// <summary>
    /// Whether any skill's cooldown is running at this time: one that started before now and hasn't ended, or a stocked skill's
    /// charge coming back. A cooldown starting at this very moment, and one waiting for its use to end, aren't running yet.
    /// </summary>
    internal bool CooldownRunning(double at)
    {
        for (var index = 0; index < nextUse.Length; index++)
        {
            if (lastPlan[index] is not { } plan || !double.IsNaN(heldSince[index])) { continue; }
            if (stocks[index] > 1 ? lastUse[index] < at && double.IsFinite(recharges[index]) && recharges[index] > at
                : CooldownStart(index, plan) < at && nextUse[index] > at) { return true; }
        }
        return false;
    }

    /// <summary>
    /// The first skill cooldown start at or after <paramref name="since"/> among the uses made so far, or infinity with none: a
    /// skill still out starts its own at its end, and a use made since starts one (for a stocked skill, at the use).
    /// </summary>
    internal double CooldownStartSince(double since)
    {
        var first = double.PositiveInfinity;
        for (var index = 0; index < nextUse.Length; index++)
        {
            if (lastPlan[index] is not { } plan) { continue; }
            var start = stocks[index] > 1 ? lastUse[index] : CooldownStart(index, plan);
            if (start >= since && start < first) { first = start; }
        }
        return first;
    }

    /// <summary>
    /// Reads a slot's skill as it deploys, from the fight's state then. Set for a fight whose skills can change, and each deployment then
    /// comes up as an event of its own, for the fight to take with <see cref="Deploy"/> when it has reached that time.
    /// </summary>
    internal Func<string, double, SkillDamageSchedule.Plan>? Read { get; set; }

    /// <summary>The plan of the use the last taken event came from.</summary>
    internal SkillDamageSchedule.Plan? Taken { get; private set; }

    /// <summary>When that use's status ends, which for one with no status of its own is when its window does.</summary>
    internal double TakenUntil { get; private set; }

    /// <summary>When that use stops applying its status.</summary>
    internal double TakenWindowEnd { get; private set; }

    /// <summary>The next event, or a deployment, or null with none left.</summary>
    public SkillDamageEvent? Next
    {
        get
        {
            Settle();
            return deployment ?? soonest?.Head.Event;
        }
    }

    /// <summary>Makes the deployment that came up as the next event.</summary>
    internal void Deploy()
    {
        Settle();
        if (deployment is null) { throw new InvalidOperationException("No skill deployment is due"); }
        // A ready slot whose condition doesn't hold as the fight reaches it waits, out of the schedule, until a decision releases it.
        if (MayDeploy is not null && !MayDeploy(deploying, deployment.Time))
        {
            heldSince[deploying] = deployment.Time;
            nextUse[deploying] = double.PositiveInfinity;
            deployment = null;
            settled = false;
            return;
        }
        Make(deploying, Read!(deployment.Slot, deployment.Time));
        deployment = null;
        settled = false;
    }

    /// <summary>Takes the next event.</summary>
    public SkillDamageEvent Take()
    {
        Settle();
        if (deployment is not null) { throw new InvalidOperationException("A skill deployment is due first"); }
        var taken = soonest ?? throw new InvalidOperationException("No skill event is left");
        Taken = taken.Plan;
        TakenUntil = taken.Until;
        TakenWindowEnd = taken.WindowEnd;
        var entry = taken.Head.Event;
        taken.At++;
        if (taken.Done) { running.Remove(taken); }
        settled = false;
        return entry;
    }

    // Every use at or before the next event's time is made first, since its events can come ahead of it.
    private void Settle()
    {
        if (settled) { return; }
        while (true)
        {
            soonest = null;
            foreach (var use in running)
            { if (soonest is null || SkillUseEvent.Compare(use.Head, soonest.Head) < 0) { soonest = use; } }
            var plan = -1;
            for (var index = 0; index < nextUse.Length; index++)
            { if (nextUse[index] < fights.Duration && (plan < 0 || nextUse[index] < nextUse[plan])) { plan = index; } }
            if (plan < 0 || soonest?.Head.Event.Time < nextUse[plan]) { break; }
            if (Read is null) { Make(plan, fights.Plans[plan]); continue; }
            // A tick ending at a deployment's time still comes first, as it sorts ahead of any impact there.
            if (soonest?.Head.Event is { } tick && tick.Kind.IsBatch() && tick.Time == nextUse[plan]) { break; }
            deploying = plan;
            deployment = new(nextUse[plan], 0, fights.Plans[plan].Slot, SkillDamageKind.Deployment, nextUse[plan]);
            break;
        }
        settled = true;
    }

    // A use spends a held charge, and one comes back each cooldown while any are missing. Uses stay at least the stock's interval apart,
    // and with none held the next waits for one to come back. A skill's stock is the one it deployed first with, for the whole fight.
    private double AfterStocked(int index, SkillDamageSchedule.Plan plan, double time)
    {
        var stock = stocks[index];
        while (recharges[index] <= time)
        {
            held[index]++;
            recharges[index] = held[index] < stock ? recharges[index] + plan.Cycle : double.PositiveInfinity;
        }
        held[index]--;
        if (double.IsPositiveInfinity(recharges[index])) { recharges[index] = time + plan.Cycle; }
        var next = Math.Max(time + plan.StockInterval, held[index] >= 1 ? 0 : recharges[index]);
        if (!double.IsFinite(next) || next <= time) { throw new SimulationException("time-not-advancing", "Skill stock cannot advance time"); }
        return next;
    }

    private void Make(int index, SkillDamageSchedule.Plan plan)
    {
        if (++uses > UseLimit) { throw new SimulationException("skill-work-limit", "Too many skill deployments"); }
        var time = nextUse[index];
        (lastUse[index], lastPlan[index]) = (time, plan);
        slotUses[index]++;
        if (double.IsNaN(stocks[index]))
        {
            stocks[index] = plan.Stock;
            held[index] = plan.Stock;
        }
        nextUse[index] = stocks[index] > 1 ? AfterStocked(index, plan, time) : plan.After(time);
        var made = fights.Use(index, time, plan);
        events += made.Length;
        if (events > EventLimit) { throw new SimulationException("skill-work-limit", "Too many skill damage events"); }
        if (made.Length > 0) { running.Add(new(made, plan, plan.UseAt(time, fights.Duration))); }
    }
}
