using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// The Assault Turret's burst cadence and the Stinger Hive's sting rate (F31, Skill DPS), ported from Go's
/// assault_turret_cadence_g345_test.go and stinger_cadence_g347_test.go. Go read the shared cases from
/// tests/fixtures/assault-turret-cadence.json and tests/fixtures/stinger-cadence.json, which aren't part of this
/// repository; their assumptions and expected factors are copied here verbatim.
/// </summary>
public sealed class SkillCadenceTests
{
    private static readonly string[] Slots = ["skill_slot_1", "skill_slot_2"];

    /// <summary>The cases of Go's tests/fixtures/assault-turret-cadence.json: the assumptions and each estimate's factor of the shot damage.</summary>
    private static readonly (string Name, (string Key, string Value)[] Context, (string Id, double Factor)[]? Expected)[] TurretCases =
    [
        ("defaults (always shown)", [], [("assumed_burst_damage", 20), ("assumed_firing_dps", 4), ("assumed_burst_cycle_dps", 3.3333333333333335)]),
        ("zero pause", [("assault_turret_pause_seconds", "0")], [("assumed_burst_damage", 20), ("assumed_firing_dps", 4), ("assumed_burst_cycle_dps", 4)]),
        ("edited cadence", [("assault_turret_burst_shots", "10"), ("assault_turret_burst_seconds", "4"), ("assault_turret_pause_seconds", "2")],
            [("assumed_burst_damage", 10), ("assumed_firing_dps", 2.5), ("assumed_burst_cycle_dps", 1.6666666666666667)]),
        ("single shot", [("assault_turret_burst_shots", "1"), ("assault_turret_burst_seconds", "0.5"), ("assault_turret_pause_seconds", "0.5")],
            [("assumed_burst_damage", 1), ("assumed_firing_dps", 2), ("assumed_burst_cycle_dps", 1)]),
        ("long pause", [("assault_turret_pause_seconds", "5")], [("assumed_burst_damage", 20), ("assumed_firing_dps", 4), ("assumed_burst_cycle_dps", 2)]),
    ];

    /// <summary>The cases of Go's tests/fixtures/stinger-cadence.json: the assumptions and the expected stings per second.</summary>
    private static readonly (string Name, (string Key, string Value)[] Context, double? Factor)[] StingerCases =
    [
        ("defaults (always shown)", [], 1),
        ("minimum interval", [("stinger_repeat_seconds", "0.1")], 10),
        ("edited interval", [("stinger_repeat_seconds", "2")], 0.5),
        ("maximum interval", [("stinger_repeat_seconds", "60")], 0.016666666666666666),
    ];

    private static double PreviewValue(ResolvedBuild resolved, string slot, string output)
    {
        var found = resolved.ActiveSkillPreviews.TryGetValue(slot, out var preview) ? preview.Outputs.FirstOrDefault(candidate => candidate.Id == output) : null;
        Assert.True(found is not null, $"missing preview output {slot}/{output}");
        return found.Value;
    }

    /// <summary>A preview output's every field, its inputs in order, so outputs compare by value as Go's reflect.DeepEqual.</summary>
    private static string Describe(SkillPreviewOutput output) =>
        string.Join("|", output.Id, output.Name, output.Description, output.Format, output.Formula, output.When, output.Kind, output.Value.ToString("R", CultureInfo.InvariantCulture),
            string.Join(",", output.Inputs.OrderBy(input => input.Key, StringComparer.Ordinal).Select(input => $"{input.Key}={input.Value.ToString("R", CultureInfo.InvariantCulture)}")));

    /// <summary>Go's withoutAssumedOutputs: the outputs other than the skill DPS rates, which derive from the damage channels.</summary>
    private static string[] WithoutAssumedOutputs(IEnumerable<SkillPreviewOutput> outputs) =>
        [.. outputs.Where(output => !output.Id.StartsWith("assumed_", StringComparison.Ordinal)).Select(Describe)];

    /// <summary>
    /// G345: the Assault Turret's burst damage, firing DPS and burst-cycle DPS follow the cadence assumptions from its
    /// shot damage, in either slot and with tier, grade and mod bonuses, without changing ordinary outputs, the
    /// other skill or the share code.
    /// </summary>
    [Fact]
    public void G345AssaultTurretCadenceSharedFixtures()
    {
        Assert.True(TurretCases.Length == 5, "missing shared cadence cases");
        foreach (var slot in Slots)
        {
            foreach (var boosted in new[] { false, true })
            {
                var compiled = Compiled;
                if (boosted)
                {
                    ImmutableArray<StatModifier> modifiers =
                    [
                        new() { Stat = "skill_tier", Op = ModifierOps.Flat, Value = new(6) }, new() { Stat = "skill_damage", Op = ModifierOps.Flat, Value = new(.2) },
                        new() { Stat = "skill_efficiency", Op = ModifierOps.Flat, Value = new(.1) }, new() { Stat = "total_skill_damage", Op = ModifierOps.Flat, Value = new(.3) },
                    ];
                    var pack = Compiled.Pack with { ActiveSkills = [.. Compiled.Pack.ActiveSkills.Select(skill => skill.ID == "skill_assault_turret" ? skill with { Modifiers = modifiers } : skill)] };
                    var result = PackValidator.Validate(pack);
                    Assert.True(result.Compiled is not null, "cadence pack did not validate");
                    compiled = result.Compiled;
                }
                var other = slot == "skill_slot_1" ? "skill_slot_2" : "skill_slot_1";
                var b = BuildSnapshot.Create(compiled.Pack).WithActiveSkillSelection(slot, "skill_assault_turret").WithActiveSkillSelection(other, "skill_striker_drone");
                if (boosted)
                {
                    b = b.WithSkillUpgrade(slot, "skill_assault_turret", 30).WithSkillMod(slot, "skill_assault_turret", "firing_mechanism", new("magnetic_rail", .05));
                }
                var code = ShareCode.Encode(b);
                var baseline = BuildResolver.Resolve(compiled, b);
                foreach (var (name, context, expected) in TurretCases)
                {
                    var label = $"{slot}/{name} (boosted {boosted})";
                    var got = BuildResolver.Resolve(compiled, b, Context(context));
                    var ordinary = new List<SkillPreviewOutput>();
                    var estimates = new Dictionary<string, double>(StringComparer.Ordinal);
                    foreach (var output in got.ActiveSkillPreviews[slot].Outputs)
                    {
                        if (output.Id == "assumed_cycle_dps") { continue; } // G409's cycle average has its own tests.
                        if (output.Id.StartsWith("assumed_", StringComparison.Ordinal)) { estimates[output.Id] = output.Value; }
                        else { ordinary.Add(output); }
                    }
                    Assert.True(ordinary.Select(Describe).SequenceEqual(WithoutAssumedOutputs(baseline.ActiveSkillPreviews[slot].Outputs)), $"{label}: cadence changed ordinary outputs");
                    Assert.False(got.ActiveSkillPreviews[other].Outputs.Any(output => output.Id.StartsWith("assumed_", StringComparison.Ordinal)), $"{label}: cadence leaked to another skill");
                    if (expected is null)
                    {
                        Assert.True(estimates.Count == 0, $"{label}: disabled preview is visible");
                        continue;
                    }
                    Assert.True(estimates.Count == 3, $"{label}: missing cadence outputs ({string.Join(", ", estimates.Keys)})");
                    var damage = PreviewValue(got, slot, "damage");
                    Assert.True(damage > 0, $"{label}: missing ordinary shot damage");
                    foreach (var (id, factor) in expected) { CloseTo(estimates.GetValueOrDefault(id), damage * factor, 1e-7); }
                    Assert.True(ShareCode.Encode(b) == code, $"{label}: view assumptions changed share code");
                }
            }
        }
    }

    /// <summary>G347: the Stinger Hive's sting DPS is its sting damage at the assumed repeat interval, in either slot.</summary>
    [Fact]
    public void G347StingerDPS()
    {
        Assert.True(StingerCases.Length == 4, "missing shared tick cases");
        var count = 0;
        foreach (var skill in Compiled.Pack.ActiveSkills)
        {
            var channel = skill.ID == "skill_stinger_hive" && skill.PreviewOutputs.Any(output => output.ID == "damage") ? "damage" : "";
            if (channel.Length == 0) { continue; }
            count++;
            foreach (var slot in Slots)
            {
                var b = Build().WithActiveSkillSelection(slot, skill.ID).WithSkillUpgrade(slot, skill.ID, 30);
                var baseline = Resolve(b);
                var damage = PreviewValue(baseline, slot, channel);
                foreach (var (name, context, factor) in StingerCases)
                {
                    var result = Resolve(b, Context(context));
                    var ordinary = new List<SkillPreviewOutput>();
                    var found = false;
                    foreach (var output in result.ActiveSkillPreviews[slot].Outputs)
                    {
                        if (output.Id == "assumed_sting_dps")
                        {
                            found = true;
                            Assert.True(factor is not null, $"{slot}/{name}: disabled ticking output visible");
                            CloseTo(output.Value, damage * factor.Value, 1e-7);
                        }
                        // The window follows the same repeat interval, one sting a charge (R63).
                        else if (!output.Id.StartsWith("assumed_", StringComparison.Ordinal) && output.Id != "sting_window_seconds") { ordinary.Add(output); }
                    }
                    Assert.True(found == factor is not null && ordinary.Select(Describe).SequenceEqual(WithoutAssumedOutputs([.. baseline.ActiveSkillPreviews[slot].Outputs.Where(output => output.Id != "sting_window_seconds")])),
                        $"{slot}/{name}: missing ticking rate or changed ordinary outputs");
                }
            }
        }
        Assert.True(count == 1, $"expected one direct sting channel, got {count}");
    }
}
