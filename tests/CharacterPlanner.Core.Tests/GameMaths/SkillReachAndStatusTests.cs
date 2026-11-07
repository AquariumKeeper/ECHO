using static CharacterPlanner.Core.Tests.GameMaths.Division;
using static CharacterPlanner.Core.Tests.GameMaths.SkillProperties;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// Reviewed skill reach and status properties (F20 active skills and previews): healing and effect ranges, cloud
/// lifetimes, and the shock, disrupt, disorient, ensnare, blind, burn and bleed durations, ported from Go's
/// skill_property_g175_test.go through skill_property_g180_test.go, with the Nanite range and duration reductions on
/// healing skills. Each property test gives every active skill the same flat modifiers (skill tier swept from -1 to 8,
/// +20% skill haste, health and duration, +10% skill efficiency and fixed others) and checks a preview output in
/// either slot. The bases Go marks "corrected in game (G286)" come from Go's
/// data/sources/division-2-reviewed-corrections.json, which this repository doesn't carry; the values the tests use
/// are written into the cases verbatim, as in Go.
/// </summary>
public sealed class SkillReachAndStatusTests
{
    private static readonly double[] None = [0, 0, 0, 0, 0, 0, 0];
    private static readonly double[] Tens = [0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6];
    private static readonly double[] Fives = [0, 0.05, 0.1, 0.15, 0.2, 0.25, 0.3];

    private static double Scaled(SkillProperty tc, double tier) => tc.Base * (1 + tc.At(tier));

    /// <summary>A status duration: the tier-scaled base by Go's factor 4.1 for the flat skill modifiers.</summary>
    private static double Status(SkillProperty tc, double tier) => tc.Base * (1 + tc.At(tier)) * 4.1;

    /// <summary>G180's flat modifiers: burn and bleed duration bonuses ahead of the full set.</summary>
    private static (string Stat, double Value)[] Burning(double tier) =>
        [("burn_duration", .2), ("trauma_bleed_duration_bonus", .5), ("combustion_debuff_duration_bonus", 3), .. Full(tier)];

    private static readonly SkillProperty[] HealingRanges =
        [new("skill_repair_trap", 0.75, None), new("skill_restorer_hive", 8, Tens), new("skill_reinforcer_chem_launcher", 3, Tens), new("skill_mender_seeker_mine", 3, Fives)];

    /// <summary>G175: healing ranges.</summary>
    [Fact]
    public void G175Reviewed0Properties() => Check(TierSweep, Full, HealingRanges, "healing_range_meters", Scaled);

    /// <summary>
    /// G175: Ortiz Reficere's Nanite range reduction (90% from four pieces, 25% with Improved Dampeners on its
    /// backpack) shortens healing ranges, and leaves the Shock Trap's radius alone.
    /// </summary>
    [Fact]
    public void G175NaniteThresholdAndHost()
    {
        var cases = HealingRanges.Select(tc => (tc, Output: "healing_range_meters", Healing: true)).Append((new("skill_shock_trap", 2.5, Tens), "radius_meters", false)).ToArray();
        foreach (var pieces in new[] { 3, 4, 5 })
        {
            foreach (var (b, upgraded) in NaniteBuilds(pieces))
            {
                foreach (var (tc, output, healing) in cases)
                {
                    foreach (var slot in Slots)
                    {
                        var r = Resolve(b.WithActiveSkillSelection(slot, tc.Id));
                        var s = r.Calculation.Stats;
                        var reduction = NaniteReduction(pieces, upgraded);
                        CloseTo(s.Of("nanite_range_reduction"), reduction, 1e-12);
                        var want = tc.Base * (1 + tc.At(s.Of("skill_tier"))) * 1;
                        if (healing) { want *= 1 - reduction; }
                        CloseTo(Preview(r, slot, output), want, 1e-8);
                    }
                }
            }
        }
    }

    /// <summary>G176: Hive ranges, from the in-game bases corrected in G286.</summary>
    [Fact]
    public void G176Reviewed0Properties() =>
        Check(TierSweep, Full,
            [new("skill_stinger_hive", 8, Fives), new("skill_reviver_hive", 8, Fives), new("skill_booster_hive", 12, Fives), new("skill_artificer_hive", 8, Tens)],
            "range_meters", Scaled);

    /// <summary>G177: the Incinerator Turret's flame range.</summary>
    [Fact]
    public void G177Reviewed0Properties() => Check(TierSweep, Full, [new("skill_incinerator_turret", 15, None)], "range_meters", Scaled);

    /// <summary>G177: the Tactician Drone's scan range adds the tier's meters.</summary>
    [Fact]
    public void G177Reviewed1Properties() =>
        Check(TierSweep, Full, [new("skill_tactician_drone", 50, [0, 5, 10, 15, 20, 25, 30])], "scan_range_meters", (tc, tier) => tc.Base + tc.At(tier));

    /// <summary>G177: the Banshee Pulse's cone angle.</summary>
    [Fact]
    public void G177Reviewed2Properties() => Check(TierSweep, Full, [new("skill_banshee_pulse", 20, Tens)], "cone_angle_degrees", Scaled);

    private static readonly (SkillProperty Property, bool Healing)[] Clouds =
    [
        (new("skill_reinforcer_chem_launcher", 5, None), true), (new("skill_firestarter_chem_launcher", 20, None), false),
        (new("skill_oxidizer_chem_launcher", 5, [0, 0.2, 0.4, 0.6, 0.8, 1, 1.2]), false), (new("skill_mender_seeker_mine", 8, None), true),
    ];

    /// <summary>G178: cloud lifetimes.</summary>
    [Fact]
    public void G178Reviewed0Properties() =>
        Check(TierSweep, Full, [.. Clouds.Select(cloud => cloud.Property)], "cloud_duration_seconds", (tc, tier) => tc.Base * (1 + tc.At(tier)) * 1.3);

    /// <summary>G178: Ortiz Reficere's Nanite duration reduction shortens healing clouds only.</summary>
    [Fact]
    public void G178NaniteThresholdAndHost()
    {
        foreach (var pieces in new[] { 3, 4, 5 })
        {
            foreach (var (b, upgraded) in NaniteBuilds(pieces))
            {
                foreach (var (tc, healing) in Clouds)
                {
                    foreach (var slot in Slots)
                    {
                        var r = Resolve(b.WithActiveSkillSelection(slot, tc.Id));
                        var s = r.Calculation.Stats;
                        var reduction = NaniteReduction(pieces, upgraded);
                        CloseTo(s.Of("nanite_duration_reduction"), reduction, 1e-12);
                        var want = tc.Base * (1 + tc.At(s.Of("skill_tier"))) * (1 + s.Of("effective_skill_duration"));
                        if (healing) { want *= 1 - reduction; }
                        CloseTo(Preview(r, slot, "cloud_duration_seconds"), want, 1e-8);
                    }
                }
            }
        }
    }

    /// <summary>G179: the Shock Trap's shock duration.</summary>
    [Fact]
    public void G179Reviewed0Properties() => Check(TierSweep, Full, [new("skill_shock_trap", 3, Tens)], "shock_duration_seconds", Status);

    /// <summary>G179: the Jammer Pulse's disrupt duration.</summary>
    [Fact]
    public void G179Reviewed1Properties() => Check(TierSweep, Full, [new("skill_jammer_pulse", 3, Tens)], "disrupt_duration_seconds", Status);

    /// <summary>G179: the Banshee Pulse's disorient duration.</summary>
    [Fact]
    public void G179Reviewed2Properties() => Check(TierSweep, Full, [new("skill_banshee_pulse", 4, Tens)], "disorient_duration_seconds", Status);

    /// <summary>G179: the Riot Foam Chem Launcher's ensnare duration.</summary>
    [Fact]
    public void G179Reviewed3Properties() => Check(TierSweep, Full, [new("skill_riot_foam_chem_launcher", 6, Tens)], "ensnare_duration_seconds", Status);

    /// <summary>G179: the Blinder Firefly's blind duration.</summary>
    [Fact]
    public void G179Reviewed4Properties() => Check(TierSweep, Full, [new("skill_blinder_firefly", 5, Tens)], "blind_duration_seconds", Status);

    /// <summary>G180: burn durations, from the in-game bases corrected in G286, with the burn duration bonus.</summary>
    [Fact]
    public void G180Reviewed0Properties() =>
        Check(TierSweep, Burning,
        [
            new("skill_burn_sticky_bomb", 4, [0, 0.35, 0.4, 0.6, 0.8, 1, 1.2]), new("skill_firestarter_chem_launcher", 4, Tens),
            new("skill_airburst_seeker_mine", 4, [0, 0.5, 1, 1.5, 2, 2.5, 3]),
        ], "burn_duration_seconds", (tc, tier) => tc.Base * (1 + tc.At(tier)) * 4.3);

    /// <summary>G180: bleed durations, with the trauma bleed duration bonus.</summary>
    [Fact]
    public void G180Reviewed1Properties() =>
        Check(TierSweep, Burning, [new("skill_artillery_turret", 7, None), new("skill_stinger_hive", 7, None), new("skill_explosive_seeker_mine", 7, None)],
            "bleed_duration_seconds", (tc, tier) => tc.Base * (1 + tc.At(tier)) * 4.1 * 1.5);
}
