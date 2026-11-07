using static CharacterPlanner.Core.Tests.GameMaths.Division;
using static CharacterPlanner.Core.Tests.GameMaths.SkillProperties;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// Reviewed properties of one skill family per file (F20 active skills and previews): Smart Cover, Decoy, Pulse,
/// Traps, Sticky Bombs, Hives, Fireflies and Seeker Mines, ported from Go's skill_property_g167_test.go through
/// skill_property_g174_test.go. Each test gives every active skill the same flat modifiers (skill tier swept from -1
/// to 8, +20% skill haste, health and duration, +10% skill efficiency and fixed others) and checks a preview output in
/// either slot against the reviewed base and Tier 0-6 bonuses. The bases Go marks "corrected in game (G286)" come
/// from Go's data/sources/division-2-reviewed-corrections.json, which this repository doesn't carry; the values the
/// tests use are written into the cases verbatim, as in Go.
/// </summary>
public sealed class SkillFamilyPropertyTests
{
    private static readonly double[] None = [0, 0, 0, 0, 0, 0, 0];
    private static readonly double[] Tens = [0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6];
    private static readonly double[] Fifteens = [0, 0.15, 0.3, 0.45, 0.6, 0.75, 0.9];
    private static readonly double[] Ones = [0, 1, 2, 3, 4, 5, 6];

    private static double Cooldown(SkillProperty tc, double tier) => tc.Base / (1.3 + tc.At(tier));
    // Burn and Explosive Sticky Bomb and Achilles Pulse divide by tier haste and Skill Haste separately, as their source formulas do (R11).
    private static double SeparateCooldown(SkillProperty tc, double tier) => tc.Base / ((1 + tc.At(tier)) * 1.3);
    private static double Lifetime(SkillProperty tc, double tier) => tc.Base * (1 + tc.At(tier)) * 1.3;
    private static double Scaled(SkillProperty tc, double tier) => tc.Base * (1 + tc.At(tier));
    private static double Count(SkillProperty tc, double tier) => Math.Floor(tc.Base + tc.At(tier));

    /// <summary>G167: Smart Cover's cooldown divides by the skill haste and the tier's cooldown bonus.</summary>
    [Fact]
    public void G167Reviewed0Properties() =>
        Check(TierSweep, Hasted, [new("skill_precision_smart_cover", 45, None), new("skill_fortified_smart_cover", 45, None)], "cooldown_seconds", Cooldown);

    /// <summary>G167: Smart Cover's lifetime, from the in-game bases corrected in G286.</summary>
    [Fact]
    public void G167Reviewed1Properties() =>
        Check(TierSweep, Hasted, [new("skill_precision_smart_cover", 20, None), new("skill_fortified_smart_cover", 30, None)], "duration_seconds", Lifetime);

    /// <summary>G167: Smart Cover's health, with Skill Health in the tier's bracket (R54).</summary>
    [Fact]
    public void G167Reviewed2Properties()
    {
        double[] tiers = [0, 0.7, 1.4, 2.1, 2.8, 3.5, 4.2];
        Check(TierSweep, Hasted, [new("skill_precision_smart_cover", 663623, tiers), new("skill_fortified_smart_cover", 663623, tiers)], "skill_health", (tc, tier) => tc.Base * (1 + tc.At(tier) + .3));
    }

    /// <summary>G167: Smart Cover's radius, from the in-game bases corrected in G286.</summary>
    [Fact]
    public void G167Reviewed3Properties() =>
        Check(TierSweep, Hasted, [new("skill_precision_smart_cover", 6, Tens), new("skill_fortified_smart_cover", 8, Tens)], "radius_meters", Scaled);

    /// <summary>G168: the Decoy's cooldown.</summary>
    [Fact]
    public void G168Reviewed0Properties() => Check(TierSweep, Hasted, [new("skill_decoy", 25, None)], "cooldown_seconds", Cooldown);

    /// <summary>G168: the Decoy's lifetime.</summary>
    [Fact]
    public void G168Reviewed1Properties() => Check(TierSweep, Hasted, [new("skill_decoy", 15, Fifteens)], "duration_seconds", Lifetime);

    /// <summary>G168: the Decoy's health.</summary>
    [Fact]
    public void G168Reviewed2Properties() => Check(TierSweep, Hasted, [new("skill_decoy", 929072, [0, 0.5, 1, 1.5, 2, 2.5, 3])], "skill_health", Lifetime);

    /// <summary>G168: the Decoy's threat multiplier.</summary>
    [Fact]
    public void G168Reviewed3Properties() => Check(TierSweep, Hasted, [new("skill_decoy", 1, Fifteens)], "threat_multiplier", Scaled);

    /// <summary>G169: Pulse cooldowns, and the cooldown factor that produces them.</summary>
    [Fact]
    public void G169Reviewed0Properties() =>
        Check(TierSweep, Full,
        [
            new("skill_scanner_pulse", 20, None), new("skill_remote_pulse", 20, None), new("skill_jammer_pulse", 30, None), new("skill_banshee_pulse", 30, None),
        ], "cooldown_seconds", Cooldown, factor: "cooldown_factor");

    /// <summary>G169: Achilles Pulse's cooldown, on separate brackets from 2026-11-04 (R11).</summary>
    [Fact]
    public void G169AchillesPulseCooldown() => Check(TierSweep, Full, [new("skill_achilles_pulse", 40, Tens)], "cooldown_seconds", SeparateCooldown);

    /// <summary>G169: Pulse radii. Banshee's follows the tier's cone size (R54).</summary>
    [Fact]
    public void G169Reviewed1Properties() =>
        Check(TierSweep, Full,
            [new("skill_scanner_pulse", 100, None), new("skill_remote_pulse", 15, Fifteens), new("skill_jammer_pulse", 20, Tens), new("skill_banshee_pulse", 30, Tens)],
            "radius_meters", Scaled);

    /// <summary>G169: the Remote Pulse's lifetime.</summary>
    [Fact]
    public void G169Reviewed2Properties() => Check(TierSweep, Full, [new("skill_remote_pulse", 45, Fifteens)], "duration_seconds", Lifetime);

    /// <summary>G169: the Achilles Pulse's zone capacity.</summary>
    [Fact]
    public void G169Reviewed3Properties() => Check(TierSweep, Full, [new("skill_achilles_pulse", 1, [0, 0, 0, 1, 1, 1, 2])], "zone_capacity", Count);

    /// <summary>G170: Trap cooldowns, and the cooldown factor that produces them.</summary>
    [Fact]
    public void G170Reviewed0Properties() =>
        Check(TierSweep, Full, [new("skill_shock_trap", 60, None), new("skill_repair_trap", 40, None)], "cooldown_seconds", Cooldown, factor: "cooldown_factor");

    /// <summary>G170: Trap lifetimes.</summary>
    [Fact]
    public void G170Reviewed1Properties() =>
        Check(TierSweep, Full, [new("skill_shock_trap", 60, None), new("skill_repair_trap", 60, None), new("skill_shrapnel_trap", 60, None)], "duration_seconds", Lifetime);

    /// <summary>G170: Trap deployable capacities.</summary>
    [Fact]
    public void G170Reviewed2Properties() =>
        Check(TierSweep, Full, [new("skill_shock_trap", 6, [0, 2, 2, 4, 6, 8, 10]), new("skill_repair_trap", 2, Ones), new("skill_shrapnel_trap", 9, Ones)], "deployable_capacity", Count);

    /// <summary>G170: the Shock Trap's radius.</summary>
    [Fact]
    public void G170Reviewed3Properties() => Check(TierSweep, Full, [new("skill_shock_trap", 2.5, Tens)], "radius_meters", Scaled);

    /// <summary>
    /// G170: Ortiz Reficere's Nanite duration reduction shortens the Repair Trap's lifetime and leaves the Assault
    /// Turret's alone.
    /// </summary>
    [Fact]
    public void G170NaniteDurationThresholdAndHost() => NaniteDuration("skill_repair_trap", 60);

    /// <summary>G171: EMP Sticky Bomb's cooldown, on one bracket as it has no tier haste.</summary>
    [Fact]
    public void G171Reviewed0Properties() =>
        Check(TierSweep, Full, [new("skill_emp_sticky_bomb", 30, None)], "cooldown_seconds", Cooldown);

    /// <summary>G171: Burn and Explosive Sticky Bomb's cooldowns, on separate brackets from 2026-11-04 (R11).</summary>
    [Fact]
    public void G171SeparateCooldownBrackets() =>
        Check(TierSweep, Full, [new("skill_burn_sticky_bomb", 60, Fifteens), new("skill_explosive_sticky_bomb", 60, Fifteens)], "cooldown_seconds", SeparateCooldown, factor: "cooldown_factor");

    /// <summary>G171: Sticky Bomb lifetimes.</summary>
    [Fact]
    public void G171Reviewed1Properties() =>
        Check(TierSweep, Full, [new("skill_burn_sticky_bomb", 10, None), new("skill_emp_sticky_bomb", 8, Tens), new("skill_explosive_sticky_bomb", 10, None)], "duration_seconds", Lifetime);

    /// <summary>G171: the EMP Sticky Bomb's radius.</summary>
    [Fact]
    public void G171Reviewed2Properties() => Check(TierSweep, Full, [new("skill_emp_sticky_bomb", 4, Tens)], "radius_meters", Scaled);

    /// <summary>G172: Hive lifetimes.</summary>
    [Fact]
    public void G172Reviewed0Properties() =>
        Check(TierSweep, Full,
        [
            new("skill_restorer_hive", 180, None), new("skill_stinger_hive", 180, None), new("skill_reviver_hive", 180, None), new("skill_booster_hive", 180, None),
            new("skill_artificer_hive", 180, None),
        ], "duration_seconds", Lifetime);

    /// <summary>
    /// G172: Ortiz Reficere's Nanite duration reduction shortens the Restorer Hive's lifetime and leaves the Assault
    /// Turret's alone.
    /// </summary>
    [Fact]
    public void G172NaniteDurationThresholdAndHost() => NaniteDuration("skill_restorer_hive", 180);

    /// <summary>G173: the Blinder Firefly's cooldown, and the cooldown factor that produces it.</summary>
    [Fact]
    public void G173Reviewed0Properties() => Check(TierSweep, Full, [new("skill_blinder_firefly", 50, None)], "cooldown_seconds", Cooldown, factor: "cooldown_factor");

    /// <summary>G173: Firefly target capacities.</summary>
    [Fact]
    public void G173Reviewed1Properties() =>
        Check(TierSweep, Full, [new("skill_blinder_firefly", 3, Ones), new("skill_burster_firefly", 3, Ones), new("skill_demolisher_firefly", 3, Ones)], "target_capacity", Count);

    /// <summary>G173: Firefly speed multipliers.</summary>
    [Fact]
    public void G173Reviewed2Properties()
    {
        double[] tiers = [0, 0.35, 0.7, 1.05, 1.4, 1.75, 2.1];
        Check(TierSweep, Full, [new("skill_blinder_firefly", 1, tiers), new("skill_burster_firefly", 1, tiers), new("skill_demolisher_firefly", 1, tiers)], "speed_multiplier", Scaled);
    }

    /// <summary>G174: Seeker Mine health.</summary>
    [Fact]
    public void G174Reviewed0Properties() =>
        Check(TierSweep, Full,
        [
            new("skill_explosive_seeker_mine", 199087, None), new("skill_airburst_seeker_mine", 199087, None), new("skill_cluster_seeker_mine", 199087, None),
            new("skill_mender_seeker_mine", 199087, None),
        ], "skill_health", Lifetime);

    /// <summary>G174: the Cluster Seeker Mine's and the Bombardier Drone's deployable capacities.</summary>
    [Fact]
    public void G174Reviewed1Properties() =>
        Check(TierSweep, Full, [new("skill_cluster_seeker_mine", 3, Ones), new("skill_bombardier_drone", 3, Ones)], "deployable_capacity", Count);

    /// <summary>Go's G170 and G172 Nanite checks: the host skill's lifetime shortens by the reduction, the Assault Turret's doesn't.</summary>
    private static void NaniteDuration(string host, double hostBase)
    {
        foreach (var pieces in new[] { 3, 4, 5 })
        {
            foreach (var (b, upgraded) in NaniteBuilds(pieces))
            {
                foreach (var id in new[] { host, "skill_assault_turret" })
                {
                    var r = Resolve(b.WithActiveSkillSelection("skill_slot_1", id));
                    var reduction = NaniteReduction(pieces, upgraded);
                    CloseTo(r.Calculation.Stats.Of("nanite_duration_reduction"), reduction, 1e-12);
                    var want = (id == host ? hostBase : 180) * (1 + r.Calculation.Stats.Of("effective_skill_duration"));
                    if (id == host) { want *= 1 - reduction; }
                    CloseTo(Preview(r, "skill_slot_1", "duration_seconds"), want, 1e-8);
                }
            }
        }
    }
}
