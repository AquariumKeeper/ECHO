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
    private readonly Dictionary<(int Slot, double Time, double Window, double Rate, double Impact, double StatusRate, double StatusWindow, double Impacts, double ImpactInterval, string ControlStatus, double Delay, double Repair, double RepairDelay, double RepairEvery), SkillUseEvent[]> made = [];
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
        var key = (index, time, plan.Window, plan.Rate, plan.Impact, plan.StatusRate, plan.StatusWindow, plan.Impacts, plan.ImpactInterval, plan.ControlStatus, plan.Delay, plan.ImpactRepair, plan.RepairDelay, plan.RepairEvery);
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

    // The events a running use adds when a charge lengthens it (ADR 0031), cut and numbered as a use's own are.
    internal SkillUseEvent[] Lengthened(int index, double time, SkillUse use, double from, double to, double statusFrom, double statusTo)
    {
        var builder = new List<SkillUseEvent>();
        foreach (var whole in SkillDamageSchedule.Lengthened(use, from, to, statusFrom, statusTo, cancellationToken))
        {
            if (builder.Count >= SkillEventStream.EventLimit) { throw new SimulationException("skill-work-limit", "Too many skill damage events"); }
            var entry = whole with { HitGroup = ++hitGroups };
            SkillDamageSchedule.Cut(entry, boundaries, piece => builder.Add(new(piece, entry.Time, time, index, builder.Count)), cancellationToken);
        }
        builder.Sort(SkillUseEvent.Compare);
        return [.. builder];
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
        // A repair landing at a moment comes before anything else then, so a repair skill's check sees it (ADR 0030).
        if (order == 0) { order = (second.Event.Kind == SkillDamageKind.Repair).CompareTo(first.Event.Kind == SkillDamageKind.Repair); }
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
        // A use a charge lengthens (ADR 0031) has its ends moved on every part of it still running.
        public SkillUse Use { get; set; } = use;
        public double Until => plan.StatusRate > 0 ? Use.StatusEnd : Use.WindowEnd;
        public double WindowEnd => Use.ApplyingEnd;
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
    // ADR 0030: when a slot's last use ended early, its repairs spent; NaN while it runs to its window.
    // ADR 0031: or when its cooldown starts, its window lengthened by charges; whether its count was spent; and where its window now ends.
    private readonly double[] endedEarly;
    private readonly bool[] spent;
    private readonly double[] lengthenedTo;
    // ADR 0037: a ready slot put off while the player is down: it's ready, its cooldown over, until it deploys.
    private readonly bool[] postponed;
    // Repairs and cloud ticks scheduled as the fight goes, numbered for their order.
    private int scheduled;

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
        endedEarly = Enumerable.Repeat(double.NaN, nextUse.Length).ToArray();
        spent = new bool[nextUse.Length];
        postponed = new bool[nextUse.Length];
        lengthenedTo = Enumerable.Repeat(double.NaN, nextUse.Length).ToArray();
    }

    /// <summary>
    /// Adds events a use makes as the fight goes (ADR 0030: a repair skill's repair landing later, or its cloud's ticks), none before
    /// <paramref name="at"/>.
    /// </summary>
    internal void Schedule(int index, SkillDamageSchedule.Plan plan, double useTime, double at, IReadOnlyList<SkillDamageEvent> entries)
    {
        if (entries.Count == 0) { return; }
        events += entries.Count;
        if (events > EventLimit) { throw new SimulationException("skill-work-limit", "Too many skill damage events"); }
        var made = new SkillUseEvent[entries.Count];
        for (var i = 0; i < made.Length; i++)
        {
            var entry = entries[i];
            if (!double.IsFinite(entry.Time) || entry.Time < at) { throw new SimulationException("skill-event-invalid", "A scheduled skill event can't come before now"); }
            made[i] = new(entry with { HitGroup = 0 }, entry.Time, useTime, index, ++scheduled);
        }
        Array.Sort(made, SkillUseEvent.Compare);
        running.Add(new(made, plan, plan.UseAt(useTime, fights.Duration)));
        settled = false;
    }

    /// <summary>
    /// Ends a slot's use made at <paramref name="useTime"/> at <paramref name="end"/>, its repairs spent (ADR 0030): its cooldown starts
    /// then, keeping any cut it has had, sooner than its window's end, or later when the last repair lands after it. A later use changes
    /// nothing.
    /// </summary>
    internal void EndEarly(int index, double useTime, double end)
    {
        if (lastUse[index] != useTime || lastPlan[index] is null) { return; }
        spent[index] = true;
        // A stocked skill's cooldowns run from its uses, so only its running use ends.
        if (stocks[index] > 1) { return; }
        MoveCooldownStart(index, end);
    }

    // A slot's cooldown after its last use starts at this time instead, its next use moving with it.
    private void MoveCooldownStart(int index, double end)
    {
        if (lastPlan[index] is not { } plan || end == CooldownStart(index, plan)) { return; }
        var remaining = nextUse[index] - CooldownStart(index, plan);
        endedEarly[index] = end;
        if (double.IsFinite(nextUse[index])) { nextUse[index] = end + remaining; }
        deployment = null;
        settled = false;
    }

    /// <summary>The plan index of a skill slot, or -1 when the build has no damaging skill there.</summary>
    internal int IndexOf(string slot) => fights.IndexOf(slot);

    /// <summary>A decoy's use ended by its health spent (ADR 0034): its cooldown, from its end, starts then.</summary>
    internal void EndNow(int index, double at)
    {
        if (lastPlan[index] is null) { return; }
        EndEarly(index, lastUse[index], at);
    }

    /// <summary>Puts off the deployment that came up as the next event to <paramref name="at"/> (ADR 0037: a downed player uses no skills).</summary>
    internal void Postpone(double at)
    {
        Settle();
        if (deployment is null) { throw new InvalidOperationException("No skill deployment is due"); }
        if (!(at > deployment.Time)) { throw new SimulationException("time-not-advancing", "A skill deployment can only be put off to later"); }
        nextUse[deploying] = at;
        postponed[deploying] = true;
        deployment = null;
        settled = false;
    }

    /// <summary>The plan a slot's last use deployed with, in the fight's state then; null before its first.</summary>
    internal SkillDamageSchedule.Plan? LastPlan(int index) => lastPlan[index];

    /// <summary>The fight's plans, by index.</summary>
    internal ImmutableArray<SkillDamageSchedule.Plan> Plans => fights.Plans;

    /// <summary>The plan index of the slot the last deployment made a use for; -1 before any, or when the last one was held.</summary>
    internal int LastDeployed { get; private set; } = -1;

    /// <summary>When a slot was last used; NaN before its first use.</summary>
    internal double LastUse(int index) => lastUse[index];

    /// <summary>Whether a slot's skill is out at <paramref name="at"/>: from its last use until its cooldown starts (ADR 0031 stage 2c).</summary>
    internal bool Out(int index, double at) => lastPlan[index] is { } plan && lastUse[index] <= at && at < CooldownStart(index, plan);

    /// <summary>
    /// Where a slot's last use's window ends, if it runs at <paramref name="at"/> (ADR 0031): from its effects' start to its window's end,
    /// lengthened by any charges, and not one whose count was spent. NaN for none.
    /// </summary>
    internal double RunningUntil(int index, double at)
    {
        if (lastPlan[index] is not { } plan || spent[index]) { return double.NaN; }
        var use = plan.UseAt(lastUse[index], fights.Duration);
        var end = double.IsNaN(lengthenedTo[index]) ? use.WindowEnd : lengthenedTo[index];
        return use.Time <= at && at < end ? end : double.NaN;
    }

    /// <summary>
    /// Lengthens a slot's running use by <paramref name="seconds"/> at <paramref name="at"/> (ADR 0031: an Artificer's refresh): its window
    /// and its status run that much longer, up to the fight's end, and a cooldown that runs from its end starts that much later. A use
    /// not running then, or one with charges, is untouched.
    /// </summary>
    internal void Lengthen(int index, double at, double seconds)
    {
        if (!(seconds > 0) || stocks[index] > 1 || double.IsNaN(RunningUntil(index, at)) || lastPlan[index] is not { } plan) { return; }
        var use = plan.UseAt(lastUse[index], fights.Duration);
        var end = double.IsNaN(lengthenedTo[index]) ? use.WindowEnd : lengthenedTo[index];
        var statusEnd = use.StatusEnd + (end - use.WindowEnd);
        var to = Math.Min(fights.Duration, end + seconds);
        var statusTo = Math.Min(fights.Duration, statusEnd + seconds);
        if (!double.IsFinite(end + seconds)) { throw new SimulationException("time-not-advancing", "A lengthened skill use cannot end"); }
        lengthenedTo[index] = to;
        var lengthened = use with { WindowEnd = to, StatusEnd = statusTo };
        // What's left of the use's events applies its status and area to its new ends.
        foreach (var part in running.Where(part => part.Plan == plan && part.Use.Slot == use.Slot && part.Use.Time == use.Time)) { part.Use = lengthened; }
        var made = fights.Lengthened(index, lastUse[index], use, end, to, statusEnd, statusTo);
        events += made.Length;
        if (events > EventLimit) { throw new SimulationException("skill-work-limit", "Too many skill damage events"); }
        if (made.Length > 0) { running.Add(new(made, plan, lengthened)); }
        // A cooldown from the use's end starts as much later as the window now ends, past the fight's end or not; one from the use
        // doesn't move.
        if (plan.CooldownFromEnd) { MoveCooldownStart(index, CooldownStart(index, plan) + seconds); }
        deployment = null;
        settled = false;
    }

    /// <summary>Each plan used only once the player's armor is below a share of its maximum (ADR 0030), by index, with that share.</summary>
    internal Dictionary<int, double> ArmorWaits()
    {
        var waits = new Dictionary<int, double>();
        for (var index = 0; index < fights.Plans.Length; index++) { if (fights.Plans[index].UseArmorBelow > 0) { waits[index] = fights.Plans[index].UseArmorBelow; } }
        return waits;
    }

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
            if (lastPlan[index] is not { } plan || lastUse[index] >= at || !double.IsNaN(heldSince[index]) || postponed[index]) { continue; }
            var amount = share * plan.Cooldown + seconds;
            var earliest = Math.Max(at, CooldownStart(index, plan));
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
    // A use that ended early, its repairs spent (ADR 0030), starts it then.
    private double CooldownStart(int index, SkillDamageSchedule.Plan plan) => !double.IsNaN(endedEarly[index]) ? endedEarly[index] : lastUse[index] + (plan.Cycle - plan.Cooldown);

    /// <summary>
    /// Whether any skill's cooldown is running at this time: one that started before now and hasn't ended, or a stocked skill's
    /// charge coming back. A cooldown starting at this very moment, and one waiting for its use to end, aren't running yet.
    /// </summary>
    internal bool CooldownRunning(double at)
    {
        for (var index = 0; index < nextUse.Length; index++)
        {
            if (lastPlan[index] is not { } plan || !double.IsNaN(heldSince[index]) || postponed[index]) { continue; }
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
        LastDeployed = -1;
        postponed[deploying] = false;
        if (MayDeploy is not null && !MayDeploy(deploying, deployment.Time))
        {
            heldSince[deploying] = deployment.Time;
            nextUse[deploying] = double.PositiveInfinity;
            deployment = null;
            settled = false;
            return;
        }
        Make(deploying, Read!(deployment.Slot, deployment.Time));
        LastDeployed = deploying;
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
            // A tick ending at a deployment's time still comes first, as it sorts ahead of any impact there. So does a repair landing
            // then (ADR 0030), so a skill waiting for armor sees it.
            if (soonest?.Head.Event is { } tick && (tick.Kind.IsBatch() || tick.Kind == SkillDamageKind.Repair) && tick.Time == nextUse[plan]) { break; }
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
        endedEarly[index] = double.NaN;
        spent[index] = false;
        lengthenedTo[index] = double.NaN;
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
