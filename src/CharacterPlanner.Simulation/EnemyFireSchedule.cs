using System.Collections.Immutable;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation;

/// <summary>Static Core-backed enemy attempts. Sampling hit rolls and merging combat events belong to the fight scheduler.</summary>
public sealed class EnemyFireSchedule
{
    private const int ShotLimit = 1000000;
    /// <summary>The pack's numeric input for the share of enemy shots that hit.</summary>
    internal const string HitRateInput = "enemy_hit_rate";
    /// <summary>
    /// The pack's stat with the share of the enemy's landing hits removed before they reach the player (ADR 0034: the Trophy System's
    /// reflections), from 0 to 1; a pack without it removes none.
    /// </summary>
    internal const string RemovedShareStat = "enemy_hit_removed_share";
    /// <summary>The pack's stat with the share of a hit's raw damage a decoy takes (ADR 0034); a pack without it gives the whole hit.</summary>
    internal const string DecoyShareStat = "decoy_damage_share";
    /// <summary>
    /// The pack's stat saying whether disrupt stops this enemy's fire (ADR 0035: machines stop, humans don't), 0 or 1, read per firing
    /// enemy's profile; a pack without it stops none.
    /// </summary>
    internal const string DisruptStopsFireStat = "enemy_disrupt_stops_fire";
    private readonly double armor;
    private readonly double bonusArmor;
    private readonly double health;
    public double DamagePerHit { get; }
    public double RawDamagePerHit { get; }
    public double HitProbability { get; }
    public double? Interval { get; }
    public int Attackers { get; }
    /// <summary>The share of landing hits removed before they reach the player (ADR 0034), 0 to 1.</summary>
    public double RemovedShare { get; private init; }
    /// <summary>The share of a hit's raw damage a decoy drawing fire takes (ADR 0034).</summary>
    public double DecoyShare { get; private init; } = 1;
    /// <summary>Whether disrupt stops this enemy's fire, as shock does (ADR 0035).</summary>
    public bool DisruptStopsFire { get; private init; }

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
        // ADR 0034: a share of landing hits removed, read in the fight's state and held to 0 to 1, so no state a fight happens to reach is
        // refused; a pack without the stat removes none, and so does a fight that drives no keys (static), where nothing out holds it.
        var removed = resolved.Calculation.Stats.TryGetValue(RemovedShareStat, out var share) ? share : 0;
        if (!double.IsFinite(removed)) { throw new SimulationException("incoming-stat-invalid", $"{RemovedShareStat} must be finite"); }
        removed = resolved.Assumptions.Effective.GetValueOrDefault(GamePack.FightContextSelector) == GamePack.FightContextFight ? Math.Clamp(removed, 0, 1) : 0;
        var decoy = resolved.Calculation.Stats.TryGetValue(DecoyShareStat, out var decoyShare) ? decoyShare : 1;
        if (!double.IsFinite(decoy) || decoy < 0) { throw new SimulationException("incoming-stat-invalid", $"{DecoyShareStat} must be finite and not negative"); }
        var stops = resolved.Calculation.Stats.TryGetValue(DisruptStopsFireStat, out var stopping) ? stopping : 0;
        if (stops is not (0 or 1)) { throw new SimulationException("incoming-stat-invalid", $"{DisruptStopsFireStat} must be 0 or 1"); }
        return new(armor, bonus, health, damage, raw, probability, interval, (int)attackers) { RemovedShare = removed, DecoyShare = decoy, DisruptStopsFire = stops == 1 };
    }

    /// <summary>The player's bonus armor in this state: armor times the bonus armor share, plus the party's.</summary>
    public double BonusArmor => bonusArmor;

    public IncomingDamageState CreateState() => new(armor, bonusArmor, health);

    // Rescaling hit damage rescales Core's incoming DPS too, so the derived interval can move by an ulp
    // while the cadence is unchanged. The base schedule is kept, so only a real change is refused. A fight's state may change the
    // player's bonus armor, which the pools follow (ADR 0020).
    internal bool SameClockAndPools(EnemyFireSchedule other, bool anyHitChance = false, bool anyBonusArmor = false) => SamePlayer(other, anyBonusArmor) && (anyHitChance || HitProbability == other.HitProbability) && (Interval, other.Interval) switch
    {
        (null, null) => true,
        ({ } mine, { } theirs) => Math.Abs(mine - theirs) <= 1e-9 * Math.Max(mine, theirs),
        _ => false
    };

    /// <summary>The same player and attackers, whatever the fire: a target sequence's profiles may differ only in the fire itself.</summary>
    internal bool SamePlayer(EnemyFireSchedule other, bool anyBonusArmor = false) =>
        armor == other.armor && (anyBonusArmor || bonusArmor == other.bonusArmor) && health == other.health && Attackers == other.Attackers;

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
