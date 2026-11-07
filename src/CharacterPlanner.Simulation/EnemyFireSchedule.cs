using System.Collections.Immutable;
using CharacterPlanner.Core.Engine;

namespace CharacterPlanner.Simulation;

/// <summary>Static Core-backed enemy attempts. Sampling hit rolls and merging combat events belong to the fight scheduler.</summary>
public sealed class EnemyFireSchedule
{
    private const int ShotLimit = 1000000;
    /// <summary>The pack's numeric input for the share of enemy shots that hit.</summary>
    internal const string HitRateInput = "enemy_hit_rate";
    private readonly double armor;
    private readonly double bonusArmor;
    private readonly double health;
    public double DamagePerHit { get; }
    public double RawDamagePerHit { get; }
    public double HitProbability { get; }
    public double? Interval { get; }
    public int Attackers { get; }

    private EnemyFireSchedule(double armor, double bonusArmor, double health, double damagePerHit, double rawDamagePerHit, double hitProbability, double? interval, int attackers)
    {
        this.armor = armor;
        this.bonusArmor = bonusArmor;
        this.health = health;
        DamagePerHit = damagePerHit;
        RawDamagePerHit = rawDamagePerHit;
        HitProbability = hitProbability;
        Interval = interval;
        Attackers = attackers;
    }

    public static EnemyFireSchedule Create(ResolvedBuild resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        if (resolved.Diagnostics.Any(diagnostic => diagnostic.Severity == "error"))
        { throw new SimulationException("resolve-failed", "Incoming fire requires a valid Core resolution"); }
        double Read(string id)
        {
            if (!resolved.Calculation.Stats.TryGetValue(id, out var value) || !double.IsFinite(value) || value < 0)
            { throw new SimulationException("incoming-stat-invalid", $"Missing, negative or nonfinite {id}"); }
            return value;
        }
        var armor = Read("character_armor");
        var bonus = Read("estimated_bonus_armor");
        var health = Read("character_health");
        if (health <= 0) { throw new SimulationException("incoming-stat-invalid", "Incoming fire requires positive character health"); }
        if (!resolved.Assumptions.Numeric.TryGetValue(HitRateInput, out var hitRate) || !double.IsFinite(hitRate) || hitRate is < 0 or > 100)
        { throw new SimulationException("incoming-stat-invalid", "Incoming fire requires a resolved enemy hit rate from zero through 100"); }
        // The pack has no effective-hit-chance stat: cadence is recovered as DPS / damage / hit rate. A modifier
        // on incoming DPS (a hit-chance or fire-rate effect) cannot be split between the two, so it is refused
        // rather than silently turned into a slower cadence.
        if (resolved.Calculation.Breakdowns.TryGetValue("enemy_target_incoming_dps", out var breakdown) && !breakdown.Contributions.IsEmpty)
        { throw new SimulationException("incoming-dps-modifier-unsupported", "Effects on incoming DPS cannot be split into enemy cadence and hit chance"); }
        var probability = hitRate / 100;
        var attackers = resolved.Assumptions.Numeric.GetValueOrDefault(DummyFightOptions.AttackersInput, 1);
        if (!double.IsFinite(attackers) || attackers < 1 || attackers > int.MaxValue || attackers != Math.Floor(attackers))
        { throw new SimulationException("incoming-stat-invalid", "Incoming attacker count must be a positive whole number"); }
        var raw = Read("enemy_target_hit_damage");
        var dps = Read("enemy_target_incoming_dps");
        var damage = raw * Read("enemy_damage_taken_factor");
        if (!double.IsFinite(damage)) { throw new SimulationException("incoming-stat-invalid", "Mitigated enemy hit damage overflowed"); }
        double? interval = null;
        if (dps > 0)
        {
            if (raw == 0 || probability == 0)
            { throw new SimulationException("incoming-stat-invalid", "Positive incoming DPS requires positive hit damage and hit probability"); }
            var rate = dps / raw / probability;
            var seconds = 1 / rate;
            if (!double.IsFinite(rate) || rate <= 0 || !double.IsFinite(seconds) || seconds <= 0)
            { throw new SimulationException("incoming-stat-invalid", "Enemy cadence must be finite and positive"); }
            interval = seconds;
        }
        return new(armor, bonus, health, damage, raw, probability, interval, (int)attackers);
    }

    public IncomingDamageState CreateState() => new(armor, bonusArmor, health);

    // Rescaling hit damage rescales Core's incoming DPS too, so the derived interval can move by an ulp
    // while the cadence is unchanged. The base schedule is kept, so only a real change is refused.
    internal bool SameClockAndPools(EnemyFireSchedule other, bool anyHitChance = false) => SamePlayer(other) && (anyHitChance || HitProbability == other.HitProbability) && (Interval, other.Interval) switch
    {
        (null, null) => true,
        ({ } mine, { } theirs) => Math.Abs(mine - theirs) <= 1e-9 * Math.Max(mine, theirs),
        _ => false
    };

    /// <summary>The same player and attackers, whatever the fire: a target sequence's profiles may differ only in the fire itself.</summary>
    internal bool SamePlayer(EnemyFireSchedule other) => armor == other.armor && bonusArmor == other.bonusArmor && health == other.health && Attackers == other.Attackers;

    /// <summary>Refuses a cadence too fast for a fight of this length with this many enemies firing it.</summary>
    internal void CheckAttempts(double duration, int enemies)
    {
        if (Interval is { } interval && Math.Ceiling(duration / interval) * enemies > ShotLimit)
        { throw new SimulationException("incoming-work-limit", "Too many incoming shot attempts"); }
    }

    /// <summary>All attempt times for one fight, computed once: the cadence never changes during a run.</summary>
    public ImmutableArray<double> ShotTimeArray(double duration, CancellationToken cancellationToken = default) =>
        [.. ShotTimes(duration, cancellationToken)];

    /// <summary>Attempts start after one interval and occupy [0, duration); no damage or RNG is consumed here.</summary>
    public IEnumerable<double> ShotTimes(double duration, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(duration) || duration <= 0 || duration > 600) { throw new ArgumentOutOfRangeException(nameof(duration)); }
        cancellationToken.ThrowIfCancellationRequested();
        if (Interval is not { } interval) { yield break; }
        var previous = 0d;
        for (var shot = 1; ; shot++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var at = shot * interval;
            if (at >= duration) { yield break; }
            if (shot > ShotLimit) { throw new SimulationException("incoming-work-limit", "Too many incoming shot attempts"); }
            if (!double.IsFinite(at) || at <= previous)
            { throw new SimulationException("time-not-advancing", "Enemy cadence cannot advance time"); }
            yield return at;
            previous = at;
        }
    }
}
