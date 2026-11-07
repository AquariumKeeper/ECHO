using static CharacterPlanner.Core.Tests.GameMaths.Division;
using static CharacterPlanner.Core.Tests.GameMaths.SkillProperties;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// Reviewed skill properties shared across families: skill health, lifetime, charges and radius against the skill
/// tier (F20 active skills and previews), ported from Go's skill_property_g162_test.go through
/// skill_property_g166_test.go. Each test gives every active skill the same flat modifiers (skill tier swept from -1
/// to 8, +20% skill health and duration, +10% skill efficiency and fixed others) and checks a preview output in
/// either slot against the reviewed base and Tier 0-6 bonuses.
/// </summary>
public sealed class SkillTierTableTests
{
    private static readonly double[] Turret = [0, 0.7, 1.4, 2.1, 2.8, 3.5, 4.2];
    private static readonly double[] None = [0, 0, 0, 0, 0, 0, 0];

    /// <summary>G162: turret and drone health scales with the tier's bonus and +30% skill health.</summary>
    [Fact]
    public void G162ReviewedProperties() =>
        Check(TierSweep, Tiered,
        [
            new("skill_assault_turret", 1061796, Turret), new("skill_incinerator_turret", 1061796, Turret), new("skill_sniper_turret", 1061796, Turret),
            new("skill_artillery_turret", 1061796, Turret), new("skill_striker_drone", 530898, Turret), new("skill_defender_drone", 1061796, Turret),
            new("skill_bombardier_drone", 1061796, Turret), new("skill_fixer_drone", 1061796, Turret), new("skill_tactician_drone", 1061796, Turret),
        ], "skill_health", (tc, tier) => tc.Base * (1 + tc.At(tier)) * 1.3);

    /// <summary>G163: hive health scales with the tier, and takes Skill Health twice (R54); firefly health has no tier bonus.</summary>
    [Fact]
    public void G163ReviewedProperties()
    {
        Check(TierSweep, Tiered,
        [
            new("skill_restorer_hive", 796347, Turret), new("skill_stinger_hive", 796347, Turret), new("skill_reviver_hive", 796347, Turret),
            new("skill_booster_hive", 796347, Turret), new("skill_artificer_hive", 796347, Turret),
        ], "skill_health", (tc, tier) => tc.Base * (1 + tc.At(tier) + .3) * 1.3);
        Check(TierSweep, Tiered,
            [new("skill_blinder_firefly", 663623, None), new("skill_burster_firefly", 663623, None), new("skill_demolisher_firefly", 663623, None)],
            "skill_health", (tc, tier) => tc.Base * (1 + tc.At(tier)) * 1.3);
    }

    /// <summary>G164: turret and drone lifetimes; only the Incinerator Turret's grows with the tier.</summary>
    [Fact]
    public void G164ReviewedProperties() =>
        Check(TierSweep, Tiered,
        [
            new("skill_assault_turret", 180, None), new("skill_incinerator_turret", 15, [0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6]), new("skill_sniper_turret", 300, None),
            new("skill_striker_drone", 180, None), new("skill_defender_drone", 20, None), new("skill_fixer_drone", 180, None), new("skill_tactician_drone", 300, None),
        ], "duration_seconds", (tc, tier) => tc.Base * (1 + tc.At(tier)) * 1.3);

    /// <summary>
    /// G164: Ortiz Reficere's Nanite duration reduction (90% from four pieces, 25% with Improved Dampeners on its
    /// backpack) shortens the Fixer Drone's lifetime and leaves the Assault Turret's alone.
    /// </summary>
    [Fact]
    public void G164NaniteDurationThresholdAndHost()
    {
        foreach (var pieces in new[] { 3, 4, 5 })
        {
            foreach (var (b, upgraded) in NaniteBuilds(pieces))
            {
                foreach (var id in new[] { "skill_fixer_drone", "skill_assault_turret" })
                {
                    var r = Resolve(b.WithActiveSkillSelection("skill_slot_1", id));
                    var reduction = NaniteReduction(pieces, upgraded);
                    CloseTo(r.Calculation.Stats.Of("nanite_duration_reduction"), reduction, 1e-12);
                    var want = 180 * (1 + r.Calculation.Stats.Of("effective_skill_duration"));
                    if (id == "skill_fixer_drone") { want *= 1 - reduction; }
                    CloseTo(Preview(r, "skill_slot_1", "duration_seconds"), want, 1e-8);
                }
            }
        }
    }

    /// <summary>G165: hive, chem launcher and turret charges add the tier's whole charges.</summary>
    [Fact]
    public void G165ReviewedProperties()
    {
        double[] fours = [0, 4, 8, 12, 16, 20, 24], ones = [0, 1, 2, 3, 4, 5, 6];
        Check(TierSweep, Tiered,
        [
            new("skill_restorer_hive", 8, fours), new("skill_stinger_hive", 8, ones), new("skill_booster_hive", 8, fours), new("skill_artificer_hive", 8, fours),
            new("skill_reinforcer_chem_launcher", 2, ones), new("skill_firestarter_chem_launcher", 1, ones), new("skill_riot_foam_chem_launcher", 1, ones),
            new("skill_oxidizer_chem_launcher", 1, ones), new("skill_sniper_turret", 3, [0, 2, 4, 6, 8, 10, 12]), new("skill_artillery_turret", 3, ones),
        ], "charge_capacity", (tc, tier) => Math.Floor(tc.Base + tc.At(tier)));
    }

    /// <summary>G166: explosion and cloud radii scale with the tier and not with other skill stats.</summary>
    [Fact]
    public void G166ReviewedProperties()
    {
        double[] fives = [0, 0.05, 0.1, 0.15, 0.2, 0.25, 0.3];
        Check(TierSweep, Tiered,
        [
            new("skill_explosive_sticky_bomb", 3, [0, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7]), new("skill_shrapnel_trap", 2, None),
            new("skill_artillery_turret", 4.5, [0, 0.08, 0.16, 0.24, 0.32, 0.4, 0.5]), new("skill_firestarter_chem_launcher", 3, fives),
            new("skill_oxidizer_chem_launcher", 3.5, [0, 0.02, 0.04, 0.06, 0.08, 0.1, 0.12]), new("skill_riot_foam_chem_launcher", 3, fives),
            new("skill_explosive_seeker_mine", 4, fives), new("skill_airburst_seeker_mine", 6, None), new("skill_cluster_seeker_mine", 2, None),
            new("skill_bombardier_drone", 4.5, None),
        ], "radius_meters", (tc, tier) => tc.Base * (1 + tc.At(tier)));
    }
}
