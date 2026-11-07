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
    private int uses;
    private int events;

    internal SkillEventStream(SkillFights fights)
    {
        this.fights = fights;
        nextUse = new double[fights.Plans.Length];
        stocks = Enumerable.Repeat(double.NaN, nextUse.Length).ToArray();
        held = new double[nextUse.Length];
        recharges = Enumerable.Repeat(double.PositiveInfinity, nextUse.Length).ToArray();
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
