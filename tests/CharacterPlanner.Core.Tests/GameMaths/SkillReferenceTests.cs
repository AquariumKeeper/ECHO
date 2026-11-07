using static CharacterPlanner.Core.Tests.GameMaths.SkillProperties;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// Reviewed skill reference outputs and recovery boundaries (F20 active skills and previews): Pulse effects, Hive
/// buffs, refills and revives, Chem Launcher ammo recovery, shield cooldowns, Riot Foam and Striker Shield
/// references, ported from Go's skill_property_g181_test.go through skill_property_g187_test.go. Each property test
/// gives every active skill the same flat modifiers (skill tier swept from -1 to 8, +20% skill haste, health and
/// duration, +10% skill efficiency and fixed others) and checks a preview output in either slot; the boundary tests
/// sweep skill haste instead, down to a non-positive recovery speed.
/// </summary>
public sealed class SkillReferenceTests
{
    private static readonly double[] None = [0, 0, 0, 0, 0, 0, 0];
    private static readonly double[] Tens = [0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6];

    private static double Scaled(SkillProperty tc, double tier) => tc.Base * (1 + tc.At(tier));

    /// <summary>The recovery-speed boundary expectation: the base over 1 + haste + 10% efficiency, 0 once that isn't positive.</summary>
    private static double Recovery(SkillProperty tc, double haste) => 1 + haste + .1 > 0 ? tc.Base / (1 + haste + .1) : 0;

    /// <summary>G181: Pulse effect durations.</summary>
    [Fact]
    public void G181Reviewed0Properties() =>
        Check(TierSweep, Full, [new("skill_scanner_pulse", 8, [0, 0.15, 0.3, 0.45, 0.6, 0.75, 0.9]), new("skill_banshee_pulse", 10, None), new("skill_achilles_pulse", 10, Tens)],
            "effect_duration_seconds", (tc, tier) => tc.Base * (1 + tc.At(tier)) * 1.3);

    /// <summary>G181: the Achilles Pulse's zone health reference, with Skill Health (R54).</summary>
    [Fact]
    public void G181Reviewed1Properties() => Check(TierSweep, Full, [new("skill_achilles_pulse", 398174, Tens)], "zone_health_reference", (tc, tier) => tc.Base * (1 + tc.At(tier)) * 1.3);

    /// <summary>G181: The Howl's mark takes no bonus (R54).</summary>
    [Fact]
    public void G181TheHowlsMarkIsFixed() => Check(TierSweep, Full, [new("skill_the_howl", 20, None)], "mark_duration_seconds", Scaled);

    /// <summary>G181: The Howl's shock follows the tier alone (R54).</summary>
    [Fact]
    public void G181TheHowlsShockTakesNoBonus() => Check(TierSweep, Full, [new("skill_the_howl", 3, [0, 0.6, 0.7, 0.8, 0.9, 1, 1.5])], "shock_duration_seconds", Scaled);

    /// <summary>G181: the Jammer Pulse's charge time reference.</summary>
    [Fact]
    public void G181Reviewed2Properties() => Check(TierSweep, Full, [new("skill_jammer_pulse", 2, None)], "charge_time_reference_seconds", Scaled);

    /// <summary>G182: the Booster and Artificer Hives' buff amount references.</summary>
    [Fact]
    public void G182Reviewed0Properties() =>
        Check(TierSweep, Full, [new("skill_booster_hive", 0.2, [0, 0.33, 0.66, 1, 1.33, 1.66, 2]), new("skill_artificer_hive", 0.1, [0, 1, 2, 3, 4, 5, 6])],
            "buff_amount_reference", Scaled);

    /// <summary>G182: the Artificer Hive's buff duration reference.</summary>
    [Fact]
    public void G182Reviewed1Properties() => Check(TierSweep, Full, [new("skill_artificer_hive", 10, None)], "buff_duration_reference_seconds", Scaled);

    /// <summary>G182: the Artificer Hive's skill repair reference.</summary>
    [Fact]
    public void G182Reviewed2Properties() => Check(TierSweep, Full, [new("skill_artificer_hive", 0.1, None)], "skill_repair_reference", Scaled);

    /// <summary>G182: the Artificer Hive's skill refresh reference adds the tier's seconds.</summary>
    [Fact]
    public void G182Reviewed3Properties() =>
        Check(TierSweep, Full, [new("skill_artificer_hive", 3, [0, 2, 4, 6, 8, 10, 12])], "skill_refresh_reference_seconds", (tc, tier) => tc.Base + tc.At(tier));

    private static readonly SkillProperty[] ChemLaunchers =
    [
        new("skill_reinforcer_chem_launcher", 30, None), new("skill_firestarter_chem_launcher", 30, None), new("skill_riot_foam_chem_launcher", 30, None),
        new("skill_oxidizer_chem_launcher", 25, None),
    ];

    /// <summary>G183: Chem Launcher ammo recovery divides by the recovery speed and not by the tier.</summary>
    [Fact]
    public void G183Reviewed0Properties() => Check(TierSweep, Full, ChemLaunchers, "ammo_recovery_seconds", (tc, _) => tc.Base / 1.3);

    /// <summary>G183: Chem Launcher ammo recovery at the skill-haste boundaries, 0 once the recovery speed isn't positive.</summary>
    [Fact]
    public void G183RecoverySpeedBoundaries() => Check(HasteSweep, Boundary, ChemLaunchers, "ammo_recovery_seconds", Recovery, 1e-10);

    /// <summary>G184: Hive refill baselines.</summary>
    [Fact]
    public void G184Reviewed0Properties() =>
        Check(TierSweep, Full,
        [
            new("skill_restorer_hive", 11, None), new("skill_stinger_hive", 10, None), new("skill_reviver_hive", 180, None), new("skill_booster_hive", 11.3, None),
            new("skill_artificer_hive", 11.3, None),
        ], "refill_baseline_seconds", Scaled);

    /// <summary>G185: the Reviver Hive's revive charges add the tier's charges.</summary>
    [Fact]
    public void G185Reviewed0Properties() => Check(TierSweep, Full, [new("skill_reviver_hive", 1, [0, 1, 2, 3, 4, 5, 6])], "revive_charges", (tc, tier) => tc.Base + tc.At(tier));

    /// <summary>G185: the Reviver Hive's revive armor baseline reference doesn't change with the tier.</summary>
    [Fact]
    public void G185Reviewed1Properties() => Check(TierSweep, Full, [new("skill_reviver_hive", 0.25, Tens)], "revive_armor_baseline_reference", (tc, _) => tc.Base);

    /// <summary>G185: the Reviver Hive's revive armor tier bonus reference is the tier's bonus alone.</summary>
    [Fact]
    public void G185Reviewed2Properties() => Check(TierSweep, Full, [new("skill_reviver_hive", 0.25, Tens)], "revive_armor_tier_bonus_reference", (tc, tier) => tc.At(tier));

    private static readonly SkillProperty[] Shields =
        [new("skill_bulwark_shield", 20, None), new("skill_crusader_shield", 20, None), new("skill_deflector_shield", 20, None), new("skill_striker_shield", 20, None)];

    /// <summary>G186: shield cooldowns divide by the recovery speed and not by the tier.</summary>
    [Fact]
    public void G186Reviewed0Properties() => Check(TierSweep, Full, Shields, "cooldown_seconds", (tc, _) => tc.Base / 1.3);

    /// <summary>G186: shield cooldowns at the skill-haste boundaries, 0 once the recovery speed isn't positive.</summary>
    [Fact]
    public void G186RecoverySpeedBoundaries() => Check(HasteSweep, Boundary, Shields, "cooldown_seconds", Recovery, 1e-10);

    /// <summary>G187: the Riot Foam Chem Launcher's foam health reference, with Skill Health (R54).</summary>
    [Fact]
    public void G187Reviewed0Properties() => Check(TierSweep, Full, [new("skill_riot_foam_chem_launcher", 452470, [0, 0.5, 1, 1.5, 2, 2.5, 3])], "foam_health_reference", (tc, tier) => tc.Base * (1 + tc.At(tier)) * 1.3);

    /// <summary>G187: the Striker Shield's buff angle reference.</summary>
    [Fact]
    public void G187Reviewed1Properties() => Check(TierSweep, Full, [new("skill_striker_shield", 45, None)], "buff_angle_reference_degrees", Scaled);

    /// <summary>G187: the Striker Shield's buff range reference.</summary>
    [Fact]
    public void G187Reviewed2Properties() => Check(TierSweep, Full, [new("skill_striker_shield", 10, None)], "buff_range_reference_meters", Scaled);
}
