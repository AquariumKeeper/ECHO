using System.Globalization;
using CharacterPlanner.Core.Engine;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>Skill DPS and Build DPS (F31), ported from Go's tick_dps_g346_test.go and build_outputs_g363_test.go.</summary>
public sealed class SkillDpsTests
{
    private static double Preview(ResolvedBuild resolved, string slot, string output)
    {
        var found = resolved.ActiveSkillPreviews.TryGetValue(slot, out var preview) ? preview.Outputs.FirstOrDefault(candidate => candidate.Id == output) : null;
        Assert.True(found is not null, $"missing preview output {slot}/{output}");
        return found.Value;
    }

    /// <summary>A preview output's every field, its inputs in order, so outputs compare by value as Go's reflect.DeepEqual.</summary>
    private static string Describe(SkillPreviewOutput output) =>
        string.Join("|", output.Id, output.Name, output.Description, output.Format, output.Formula, output.When, output.Kind, output.Value.ToString("R", CultureInfo.InvariantCulture),
            string.Join(",", output.Inputs.OrderBy(input => input.Key, StringComparer.Ordinal).Select(input => $"{input.Key}={input.Value.ToString("R", CultureInfo.InvariantCulture)}")));

    private static string[] Ordinary(IEnumerable<SkillPreviewOutput> outputs) =>
        outputs.Where(output => !output.Id.StartsWith("assumed_", StringComparison.Ordinal)).Select(Describe).ToArray();

    /// <summary>The shared tick cases from Go's tests/fixtures/skill-tick-cadence.json: the assumptions and the expected ticks per second.</summary>
    private static readonly (string Name, (string Key, string Value)[] Context, double? Factor)[] TickCases =
    [
        ("defaults (always shown)", [], 2),
        ("minimum interval", [("tick_dps_interval_seconds", "0.1")], 10),
        ("one second", [("tick_dps_interval_seconds", "1")], 1),
        ("maximum interval", [("tick_dps_interval_seconds", "10")], 0.1),
    ];

    /// <summary>
    /// G346: every skill with a burn, bleed or cloud channel shows its ticking DPS at the assumed tick interval, in
    /// either skill slot, and leaves its ordinary outputs alone.
    /// </summary>
    [Fact]
    public void G346TickDps()
    {
        Assert.Equal(4, TickCases.Length);
        var count = 0;
        foreach (var skill in Compiled.Pack.ActiveSkills)
        {
            var channel = "";
            foreach (var output in skill.PreviewOutputs)
            {
                if (output.ID is "burn_damage" or "bleed_damage" or "cloud_damage") { channel = output.ID; }
            }
            if (channel.Length == 0) { continue; }
            count++;
            foreach (var slot in new[] { "skill_slot_1", "skill_slot_2" })
            {
                var b = Build().WithActiveSkillSelection(slot, skill.ID).WithSkillUpgrade(slot, skill.ID, 30);
                var baseline = Resolve(b);
                var damage = Preview(baseline, slot, channel);
                foreach (var (name, context, factor) in TickCases)
                {
                    var result = Resolve(b, Context(context));
                    var ticking = result.ActiveSkillPreviews[slot].Outputs.Where(output => output.Id == "assumed_ticking_dps").ToArray();
                    foreach (var output in ticking)
                    {
                        Assert.True(factor is not null, $"{skill.ID} {name}: disabled ticking output visible");
                        CloseTo(output.Value, damage * factor.Value, 1e-7);
                    }
                    Assert.True(ticking.Length > 0 == factor is not null, $"{skill.ID} {name}: missing ticking rate");
                    Assert.Equal(Ordinary(baseline.ActiveSkillPreviews[slot].Outputs), Ordinary(result.ActiveSkillPreviews[slot].Outputs));
                }
            }
        }
        Assert.True(count == 8, $"expected eight tick channels, got {count}");
    }

    private static double Output(ResolvedBuild resolved, string id)
    {
        var output = resolved.BuildOutputs.FirstOrDefault(candidate => candidate.Id == id);
        Assert.True(output is not null, $"build output {id} missing: {string.Join("; ", resolved.Diagnostics.Select(diagnostic => diagnostic.Message))}");
        return output.Value;
    }

    /// <summary>G363: build outputs evaluate after skill previews.</summary>
    [Fact]
    public void G363BuildOutputs()
    {
        var empty = Resolve(Build(), Context());
        Assert.True(Output(empty, "build_dps") == 0 && Output(empty, "build_time_to_kill") == 0, "empty build outputs aren't 0");

        var b = Build().WithEquipment("primary_weapon", "primary_weapon_acr").WithActiveSkillSelection("skill_slot_1", "skill_assault_turret");
        var resolved = Resolve(b, Division.Weapon("primary_weapon"));
        var turret = 0.0;
        foreach (var output in resolved.ActiveSkillPreviews["skill_slot_1"].Outputs)
        {
            if (output.Id == "assumed_cycle_dps") { turret = output.Value; } // the headline since G409
        }
        var s = resolved.Calculation.Stats;
        var weapon = s.Of("equipped_weapon_practical_reload_cycle_dps");
        Assert.True(weapon > 0 && turret > 0, $"weapon {weapon}, turret {turret}");
        CloseTo(Output(resolved, "build_dps"), weapon + turret, 1e-9);
        var versusArmor = (weapon * (1 + s.Of("damage_to_armor"))) + turret;
        var versusHealth = (weapon * (1 + s.Of("health_damage"))) + turret;
        CloseTo(Output(resolved, "build_time_to_kill"), (s.Of("enemy_target_armor") / versusArmor) + (s.Of("enemy_target_health") / versusHealth), 1e-9);
    }
}
