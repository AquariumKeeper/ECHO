using System.Collections.Immutable;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// Overcharge comparison previews (F24, Ordinary and Overcharge previews), ported from Go's overcharge_g199_test.go,
/// overcharge_healing_g201_test.go, overcharge_geometry_g203_test.go, overcharge_capacity_g205_test.go,
/// overcharge_lifetime_g207_test.go, overcharge_status_g209_test.go, overcharge_health_g211_test.go,
/// overcharge_utility_g213_test.go, overcharge_recovery_g215_test.go and overcharge_support_g217_test.go. The Go
/// tests read the community source caches under data/sources, which this repository doesn't carry, so their profiles
/// are transcribed in OverchargeTests.Sources.cs. Go's character-stat comparison covers Stats and Breakdowns, as
/// here; Go's primary stats and scenarios are removed features (ADR 0002) and never took part.
/// </summary>
public sealed partial class OverchargeTests
{
    private static readonly string[] Slots = ["skill_slot_1", "skill_slot_2"];
    private static readonly long[] Ranks = [0, 20];
    private static readonly string[] OffAndInvalid = ["0", "0.5", "2", "NaN"];

    /// <summary>The Airburst Seeker Mine's reviewed Tier 7 bonuses, K217 and K218, in place of the source's.</summary>
    private static readonly Dictionary<string, double> ReviewedAirburstOverchargeBonuses = new(StringComparer.Ordinal) { ["damage"] = 1, ["burn_damage"] = 1 };

    /// <summary>The Explosive Sticky Bomb's reviewed Tier 7 recovery haste, K38, in place of the source's.</summary>
    private const double ReviewedExplosiveStickyRecoveryHaste = .9;

    /// <summary>
    /// G199: the Overcharge damage comparison adds a comparison output per reviewed damage channel, for both slots
    /// and both upgrade grades, without changing character stats or ordinary outputs; it leaves utility skills alone,
    /// and an off or invalid control enables nothing, an invalid one with a diagnostic.
    /// </summary>
    [Fact]
    public void G199OverchargeDamageChannelsAndIsolation()
    {
        var checkedChannels = CheckChannels("overcharge_damage_preview", DamageSource, (channel, _, rank, outputs) =>
        {
            var grade = 0.0;
            if (outputs.GetValueOrDefault(channel.Id)?.Formula.Contains("skill_upgrade_rank", StringComparison.Ordinal) == true) { grade = rank * .01; }
            var reviewedBonus = channel.Bonus;
            if (channel.Skill == "Airburst Seeker Mine" && channel.Cell is "K217" or "K218" && ReviewedAirburstOverchargeBonuses.TryGetValue(channel.Id, out var value))
            { reviewedBonus = value; }
            CloseTo(outputs.GetValueOrDefault("overcharge_" + channel.Id)?.Value ?? 0, channel.Base * (1 + reviewedBonus + grade), 1e-6);
        });
        Assert.True(checkedChannels == 100, $"checked {checkedChannels}, want 100 channels/slots/grades");

        var b = Build().WithActiveSkillSelection("skill_slot_1", "skill_assault_turret").WithActiveSkillSelection("skill_slot_2", "skill_restorer_hive");
        var before = Resolve(b);
        var after = Resolve(b, Context(("overcharge_damage_preview", "1")));
        Assert.True(Same(before.ActiveSkillPreviews.GetValueOrDefault("skill_slot_2"), after.ActiveSkillPreviews.GetValueOrDefault("skill_slot_2")), "utility changed");
        foreach (var value in OffAndInvalid)
        {
            var r = Resolve(b, Context(("overcharge_damage_preview", value)));
            foreach (var o in r.ActiveSkillPreviews.GetValueOrDefault("skill_slot_1")?.Outputs ?? [])
            {
                Assert.False(o.Id.StartsWith("overcharge_", StringComparison.Ordinal), "invalid/off input enabled comparison");
            }
            if (value != "0")
            {
                Assert.True(r.Diagnostics.Any(d => d.Code == "context-value-invalid" && d.Subject == "overcharge_damage_preview"), "invalid context undiagnosed");
            }
        }
    }

    /// <summary>
    /// G201: the Overcharge healing comparison adds a comparison output per reviewed healing channel without changing
    /// stats or ordinary outputs, leaves damage slots alone, and an off or invalid control changes nothing.
    /// </summary>
    [Fact]
    public void G201OverchargeHealingChannelsAndIsolation()
    {
        var checkedChannels = CheckChannels("overcharge_healing_preview", HealingSource, (channel, _, _, outputs) =>
            CloseTo(Comparison(outputs, channel.Id).Value, channel.Base * (1 + channel.Bonus), 1e-6));
        Assert.True(checkedChannels == 24, $"incomplete coverage {checkedChannels}");
        CheckIsolation("overcharge_healing_preview", "skill_restorer_hive", "skill_assault_turret", "damage slot changed");
    }

    /// <summary>
    /// G203: the Overcharge geometry comparison scales radius, range and cone-angle references by the reviewed bonus,
    /// or adds meters, from the reviewed in-game base where one corrects the source; an ambiguous variant stays off.
    /// </summary>
    [Fact]
    public void G203OverchargeGeometryChannelsAndIsolation()
    {
        var checkedChannels = CheckChannels("overcharge_geometry_preview", GeometrySource, (channel, skill, _, outputs) =>
        {
            var o = Comparison(outputs, channel.Id);
            var @base = ReviewedSkillBase(skill.ID, "overcharge_" + channel.Id, channel.Base);
            var want = @base * (1 + ReviewedSkillBonus(skill.ID, channel.Id, channel.Bonus));
            if (channel.Mode == "add_meters") { want = @base + channel.Bonus; }
            CloseTo(o.Value, want, 1e-6);
        });
        Assert.True(checkedChannels == 120, $"incomplete coverage {checkedChannels}");
        CheckIsolation("overcharge_geometry_preview", "skill_tactician_drone", "skill_bombardier_drone", "ambiguous geometry enabled");
    }

    /// <summary>G205: the Overcharge capacity comparison adds the reviewed extra count to the source capacity.</summary>
    [Fact]
    public void G205OverchargeCapacityChannelsAndIsolation()
    {
        // The capacity source's extraCount is carried in Channel.Bonus.
        var checkedChannels = CheckChannels("overcharge_capacity_preview", CapacitySource, (channel, _, _, outputs) =>
            CloseTo(Comparison(outputs, channel.Id).Value, channel.Base + channel.Bonus, 1e-6));
        Assert.True(checkedChannels == 80, $"incomplete coverage {checkedChannels}");
        CheckIsolation("overcharge_capacity_preview", "skill_sniper_turret", "skill_assault_turret", "unsupported capacity enabled");
    }

    /// <summary>G207: the Overcharge lifetime comparison scales the reviewed base duration by the reviewed bonus.</summary>
    [Fact]
    public void G207OverchargeLifetimeChannelsAndIsolation()
    {
        var checkedChannels = CheckChannels("overcharge_lifetime_preview", LifetimeSource, (channel, skill, _, outputs) =>
            CloseTo(Comparison(outputs, channel.Id).Value, ReviewedSkillBase(skill.ID, "overcharge_" + channel.Id, channel.Base) * (1 + channel.Bonus), 1e-6));
        Assert.True(checkedChannels == 116, $"incomplete coverage {checkedChannels}");
        CheckIsolation("overcharge_lifetime_preview", "skill_scanner_pulse", "skill_blinder_firefly", "unsupported lifetime enabled");
    }

    /// <summary>G209: the Overcharge status comparison scales the reviewed base status duration by the reviewed bonus.</summary>
    [Fact]
    public void G209OverchargeStatusChannelsAndIsolation()
    {
        var checkedChannels = CheckChannels("overcharge_status_preview", StatusSource, (channel, skill, _, outputs) =>
            CloseTo(Comparison(outputs, channel.Id).Value, ReviewedSkillBase(skill.ID, "overcharge_" + channel.Id, channel.Base) * (1 + channel.Bonus), 1e-6));
        Assert.True(checkedChannels == 44, $"incomplete coverage {checkedChannels}");
        CheckIsolation("overcharge_status_preview", "skill_shock_trap", "skill_fixer_drone", "unsupported status enabled");
    }

    /// <summary>G211: the Overcharge health comparison scales the source health by the reviewed bonus.</summary>
    [Fact]
    public void G211OverchargeHealthChannelsAndIsolation()
    {
        var checkedChannels = CheckChannels("overcharge_health_preview", HealthSource, (channel, skill, _, outputs) =>
            CloseTo(Comparison(outputs, channel.Id).Value, channel.Base * (1 + ReviewedSkillBonus(skill.ID, channel.Id, channel.Bonus)), 1e-6));
        Assert.True(checkedChannels == 120, $"incomplete coverage {checkedChannels}");
        CheckIsolation("overcharge_health_preview", "skill_assault_turret", "skill_shock_trap", "unsupported health enabled");
    }

    /// <summary>G213: the Overcharge utility comparison scales the source by the reviewed bonus, or adds seconds.</summary>
    [Fact]
    public void G213OverchargeUtilityChannelsAndIsolation()
    {
        var checkedChannels = CheckChannels("overcharge_utility_preview", UtilitySource, (channel, _, _, outputs) =>
        {
            var want = channel.Base * (1 + channel.Bonus);
            if (channel.Mode == "add_seconds") { want = channel.Base + channel.Bonus; }
            CloseTo(Comparison(outputs, channel.Id).Value, want, 1e-6);
        });
        Assert.True(checkedChannels == 36, $"incomplete coverage {checkedChannels}");
        CheckIsolation("overcharge_utility_preview", "skill_artificer_hive", "skill_shock_trap", "unsupported utility enabled");
    }

    /// <summary>
    /// G215: the Overcharge recovery comparison divides the source recovery time by one plus the reviewed haste, the
    /// Explosive Sticky Bomb's from the in-game reading.
    /// </summary>
    [Fact]
    public void G215OverchargeRecoveryChannelsAndIsolation()
    {
        var checkedChannels = CheckChannels("overcharge_recovery_preview", RecoverySource, (channel, skill, _, outputs) =>
        {
            var o = Comparison(outputs, channel.Id);
            var bonus = ReviewedSkillBonus(skill.ID, channel.Id, channel.Bonus);
            if (channel.Skill == "Explosive Sticky Bomb" && channel.Cell == "K38") { bonus = ReviewedExplosiveStickyRecoveryHaste; }
            CloseTo(o.Value, channel.Base / (1 + bonus), 1e-6);
        });
        Assert.True(checkedChannels == 124, $"incomplete coverage {checkedChannels}");
        CheckIsolation("overcharge_recovery_preview", "skill_burn_sticky_bomb", "skill_banshee_pulse", "unsupported recovery enabled");
    }

    /// <summary>
    /// G217: the support comparisons, under the utility control, show the raw Hive haste and Reviver armor bonuses
    /// and the Jammer charging-time reference; the Hive refill baseline is reported as its haste reference.
    /// </summary>
    [Fact]
    public void G217OverchargeSupportChannelsAndIsolation()
    {
        var checkedChannels = CheckChannels("overcharge_utility_preview", SupportSource, (channel, _, _, outputs) =>
        {
            var id = channel.Id == "refill_baseline_seconds" ? "refill_haste_reference" : channel.Id;
            var o = Comparison(outputs, id);
            var want = channel.Bonus;
            if (channel.Mode == "time_reference") { want = channel.Base * (1 + channel.Bonus); }
            CloseTo(o.Value, want, 1e-6);
        });
        Assert.True(checkedChannels == 28, $"incomplete coverage {checkedChannels}");
        CheckIsolation("overcharge_utility_preview", "skill_reviver_hive", "skill_shock_trap", "unsupported utility enabled");
    }

    /// <summary>
    /// For each source profile's skill in each slot at upgrade ranks 0 and 20: turning the comparison on leaves stats,
    /// breakdowns and ordinary outputs unchanged and raises no error, then each channel is checked. Returns the count.
    /// </summary>
    private static int CheckChannels(string input, Channel[] source,
        Action<Channel, ActiveSkillDefinition, long, IReadOnlyDictionary<string, SkillPreviewOutput>> check)
    {
        var checkedChannels = 0;
        foreach (var profile in source.GroupBy(channel => channel.Skill, StringComparer.Ordinal))
        {
            foreach (var skill in Compiled.Pack.ActiveSkills.Where(skill => skill.Name == profile.Key))
            {
                foreach (var slot in Slots)
                {
                    foreach (var rank in Ranks)
                    {
                        var b = Build().WithActiveSkillSelection(slot, skill.ID).WithSkillUpgrade(slot, skill.ID, rank);
                        var before = Resolve(b);
                        var after = Resolve(b, Context((input, "1")));
                        var errors = after.Diagnostics.Where(d => d.Severity == "error").ToArray();
                        Assert.True(errors.Length == 0, $"{skill.ID} in {slot} at {rank} produced an error diagnostic: {string.Join("; ", errors.Select(d => d.ToString()))}");
                        Assert.True(Same(before.Calculation, after.Calculation), "comparison changed character stats");
                        var outputs = (after.ActiveSkillPreviews.GetValueOrDefault(slot)?.Outputs ?? []).ToDictionary(o => o.Id, StringComparer.Ordinal);
                        foreach (var o in before.ActiveSkillPreviews.GetValueOrDefault(slot)?.Outputs ?? [])
                        {
                            Assert.True(Same(o, outputs.GetValueOrDefault(o.Id)), $"ordinary preview changed: {o.Id}");
                        }
                        foreach (var channel in profile)
                        {
                            check(channel, skill, rank, outputs);
                            checkedChannels++;
                        }
                    }
                }
            }
        }
        return checkedChannels;
    }

    /// <summary>The comparison output for a channel, which must exist.</summary>
    private static SkillPreviewOutput Comparison(IReadOnlyDictionary<string, SkillPreviewOutput> outputs, string channelId) =>
        outputs.GetValueOrDefault("overcharge_" + channelId) ?? throw new Xunit.Sdk.XunitException($"missing comparison overcharge_{channelId}");

    /// <summary>An unsupported skill in the second slot is unchanged, and an off or invalid control changes no preview.</summary>
    private static void CheckIsolation(string input, string first, string second, string unsupportedChanged)
    {
        var b = Build().WithActiveSkillSelection("skill_slot_1", first).WithActiveSkillSelection("skill_slot_2", second);
        var before = Resolve(b);
        var after = Resolve(b, Context((input, "1")));
        Assert.True(Same(before.ActiveSkillPreviews.GetValueOrDefault("skill_slot_2"), after.ActiveSkillPreviews.GetValueOrDefault("skill_slot_2")), unsupportedChanged);
        foreach (var value in OffAndInvalid)
        {
            var r = Resolve(b, Context((input, value)));
            Assert.True(Same(before.ActiveSkillPreviews, r.ActiveSkillPreviews), $"off or invalid control {value} changed outputs");
        }
    }

    /// <summary>
    /// Go's reviewedSkillBase: the in-game-corrected leading base of a skill preview output, or the source base when
    /// no reviewed correction applies; the correction's source base must match. The rows are the leading-base
    /// Overcharge corrections of Go's data/sources/division-2-reviewed-corrections.json.
    /// </summary>
    private static double ReviewedSkillBase(string skillId, string outputId, double sourceBase)
    {
        if (!ReviewedBases.TryGetValue((skillId, outputId), out var row)) { return sourceBase; }
        Assert.True(row.Source == sourceBase, $"{skillId}-{outputId}: source base {row.Source} differs from {sourceBase}");
        return row.Corrected;
    }

    /// <summary>A tier 7 bonus, with the corrections R54 made to the workbook's from the game files.</summary>
    private static double ReviewedSkillBonus(string skillId, string outputId, double sourceBonus)
    {
        if (!ReviewedBonuses.TryGetValue((skillId, outputId), out var row)) { return sourceBonus; }
        Assert.True(row.Source == sourceBonus, $"{skillId}-{outputId}: source bonus {row.Source} differs from {sourceBonus}");
        return row.Corrected;
    }

    private static readonly Dictionary<(string Skill, string Output), (double Source, double Corrected)> ReviewedBonuses = new()
    {
        [("skill_airburst_seeker_mine", "cooldown_seconds")] = (0, 3),
        [("skill_cluster_seeker_mine", "radius_meters")] = (0, 1),
        [("skill_banshee_pulse", "radius_meters")] = (0, 1.5),
        [("skill_tactician_drone", "skill_health")] = (5.6, 4.2),
    };

    private static readonly Dictionary<(string Skill, string Output), (double Source, double Corrected)> ReviewedBases = new()
    {
        [("skill_precision_smart_cover", "overcharge_duration_seconds")] = (24, 20),
        [("skill_fortified_smart_cover", "overcharge_duration_seconds")] = (36, 30),
        [("skill_fortified_smart_cover", "overcharge_radius_meters")] = (6, 8),
        [("skill_booster_hive", "overcharge_range_meters")] = (8, 12),
        [("skill_burn_sticky_bomb", "overcharge_burn_duration_seconds")] = (4.5, 4),
        [("skill_airburst_seeker_mine", "overcharge_damage")] = (331811, 331811),
        [("skill_airburst_seeker_mine", "overcharge_burn_damage")] = (79635, 79635),
    };

    // Go's reflect.DeepEqual over the engine's records, whose immutable collections compare by reference.
    private static bool Same(StatResolution a, StatResolution b) =>
        SameMap(a.Stats, b.Stats, (x, y) => x.Equals(y))
        && SameMap(a.Breakdowns, b.Breakdowns, (x, y) => x.Base.Equals(y.Base) && x.Final.Equals(y.Final) && x.Clamped == y.Clamped && x.Contributions.SequenceEqual(y.Contributions));

    private static bool Same(SkillPreviewOutput? a, SkillPreviewOutput? b) =>
        a is null || b is null ? a == b
            : a with { Inputs = ImmutableDictionary<string, double>.Empty } == b with { Inputs = ImmutableDictionary<string, double>.Empty } && SameMap(a.Inputs, b.Inputs, (x, y) => x.Equals(y));

    private static bool Same(SkillPreview? a, SkillPreview? b) =>
        a is null || b is null ? a == b
            : a.SkillId == b.SkillId && a.Outputs.Length == b.Outputs.Length && a.Outputs.Zip(b.Outputs).All(pair => Same(pair.First, pair.Second));

    private static bool Same(IReadOnlyDictionary<string, SkillPreview> a, IReadOnlyDictionary<string, SkillPreview> b) => SameMap(a, b, (x, y) => Same(x, y));

    private static bool SameMap<T>(IReadOnlyDictionary<string, T> a, IReadOnlyDictionary<string, T> b, Func<T, T, bool> same) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && same(pair.Value, other));

    /// <summary>A source profile's channel: the skill name, output id, source base and bonus, mode and source cell.</summary>
    private sealed record Channel(string Skill, string Id, double Base, double Bonus, string Mode, string Cell);
}
