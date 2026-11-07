using System.Collections.Immutable;

namespace CharacterPlanner.Simulation;

/// <summary>
/// The enemies firing (ADR 0008): each is a target in the fight's order, with its own attempt clock. A silent group, whose
/// schedules have no interval, never fires. One enemy at a time is a group of one, whose place passes down the fight's order.
/// </summary>
internal sealed class EnemyGroup
{
    // Attempts count from an anchor, so a steady cadence doesn't drift and stays the fixed schedule's shot * interval.
    private sealed class Enemy(int target, double interval, double start)
    {
        public int Target { get; set; } = target;
        public double Interval { get; set; } = interval;
        public double Start { get; set; } = start;
        public int Attempts { get; set; } = 1;
        public double Next => Start + Attempts * Interval;
    }

    private readonly int size;
    private readonly Func<int, double?> interval;
    private readonly int? total;
    private readonly List<Enemy> enemies;
    // Who is alive and in the group, firing or silent.
    private readonly List<int> alive;
    // The next target in the fight's order that hasn't joined.
    private int reserve;
    private Enemy? soonest;

    /// <param name="size">How many enemies fire at once: the first targets in the fight's order.</param>
    /// <param name="interval">Each target's attempt interval, by its place in the fight's order.</param>
    /// <param name="total">How many enemies there are in all, or null when the next is always there to join.</param>
    public EnemyGroup(int size, Func<int, double?> interval, int? total = null)
    {
        this.size = size;
        this.interval = interval;
        this.total = total;
        alive = [.. Enumerable.Range(0, size)];
        reserve = size;
        if (interval(0) is null) { enemies = []; return; }
        var seconds = Enumerable.Range(0, size).Select(target => interval(target)!.Value).ToArray();
        // Evenly staggered across the shortest interval, so every enemy fires within its own first interval and mixed cadences
        // don't start as a volley.
        var shortest = seconds.Min();
        enemies = [.. seconds.Select((own, target) => new Enemy(target, own, shortest * (target + 1) / size - own))];
        FindSoonest();
    }

    // The earlier place wins a tie, so a fight's attempts have one order.
    private void FindSoonest()
    {
        soonest = enemies.Count == 0 ? null : enemies[0];
        foreach (var enemy in enemies) { if (enemy.Next < soonest!.Next) { soonest = enemy; } }
    }

    /// <summary>The targets alive in the group, by their places in the fight's order.</summary>
    public IReadOnlyList<int> Alive => alive;

    /// <summary>
    /// Who the player goes to after this target: the next living one in turn, or with nobody else the one who'd join, or null.
    /// </summary>
    public int? Following(int place)
    {
        var at = alive.IndexOf(place);
        if (alive.Count > 1) { return alive[(at + 1) % alive.Count]; }
        return reserve >= total ? null : reserve;
    }

    /// <summary>When the next attempt comes.</summary>
    public double Next => soonest?.Next ?? double.PositiveInfinity;

    /// <summary>The target that makes the next attempt, by its place in the fight's order.</summary>
    public int Firing => soonest?.Target ?? -1;

    /// <summary>The attempt at <paramref name="at"/> has been made: its enemy's clock moves on one interval.</summary>
    public void Advance(double at)
    {
        var enemy = soonest ?? throw new InvalidOperationException("A silent group made an attempt");
        enemy.Attempts++;
        if (enemy.Next <= at) { throw new SimulationException("time-not-advancing", "Enemy cadence cannot advance time"); }
        FindSoonest();
    }

    /// <summary>
    /// A dead enemy's place goes to the target behind the group, if there is one left. Its wait keeps its share of an interval at
    /// the new cadence, so the group stays staggered and a slow enemy's pending attempt doesn't hold back a fast one's fire.
    /// </summary>
    public void Replace(int dead, double at)
    {
        var joining = reserve;
        var leaves = joining >= total;
        if (leaves) { alive.Remove(dead); }
        else
        {
            alive[alive.IndexOf(dead)] = joining;
            reserve++;
        }
        if (enemies.Count == 0) { return; }
        var enemy = enemies.Single(candidate => candidate.Target == dead);
        if (leaves)
        {
            enemies.Remove(enemy);
            FindSoonest();
            return;
        }
        enemy.Target = joining;
        var seconds = interval(enemy.Target)!.Value;
        if (seconds == enemy.Interval) { return; }
        var next = at + Math.Max(0, enemy.Next - at) * seconds / enemy.Interval;
        enemy.Interval = seconds;
        enemy.Start = next - seconds;
        enemy.Attempts = 1;
        FindSoonest();
    }

    /// <summary>Attempt times of a group sharing one cadence: a kill never moves them, so they're known at setup.</summary>
    public static ImmutableArray<double> SteadyTimes(int size, double interval, double duration)
    {
        var group = new EnemyGroup(size, _ => interval);
        var times = ImmutableArray.CreateBuilder<double>();
        while (group.Next < duration)
        {
            var at = group.Next;
            times.Add(at);
            group.Advance(at);
        }
        return times.ToImmutable();
    }
}
