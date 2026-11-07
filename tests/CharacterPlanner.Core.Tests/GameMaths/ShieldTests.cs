using System.Collections.Immutable;
using CharacterPlanner.Core.Engine;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// Shield health and regeneration (F25), and Effective HP with Shield (F33), ported from Go's
/// shield_regeneration_g221_test.go and shield_ehp_g424_test.go. Go read the regeneration references from
/// data/sources/division-2-shield-regeneration.json, which isn't part of this repository; the base values and
/// Overcharge bonuses it used are copied here verbatim.
/// </summary>
public sealed class ShieldTests
{
    private static readonly (string Name, (string Id, double Base, double OverchargeBonus)[] Outputs)[] RegenerationProfiles =
    [
        ("Bulwark Shield", [("active_regeneration_reference", 79635, 5.0), ("holstered_regeneration_reference", 132725, 0.4)]),
        ("Crusader Shield", [("active_regeneration_reference", 39817, 5.0), ("holstered_regeneration_reference", 66362, 0.4)]),
        ("Deflector Shield", [("active_regeneration_reference", 63708, 0.6), ("holstered_regeneration_reference", 106180, 0.4)]),
        ("Striker Shield", [("holstered_regeneration_reference", 66362, 1.0), ("active_regeneration_reference", 39817, 5.0)]),
    ];

    /// <summary>
    /// R29: shield skill mods add their roll inside the ordinary tier bracket of the matching regeneration
    /// reference (here at tier 0, so Base x (1 + roll)) and leave the Overcharge references unchanged.
    /// </summary>
    [Fact]
    public void R29ShieldModsRaiseOrdinaryRegenerationOnly()
    {
        var checkedOutputs = 0;
        foreach (var profile in RegenerationProfiles)
        {
            var skill = Assert.Single(Compiled.Pack.ActiveSkills, skill => skill.Name == profile.Name);
            foreach (var (modSlot, choice, output) in new[] { ("hard_coating", "hard_coating_active_regeneration", "active_regeneration_reference"),
                ("gyro", "gyro_holstered_regeneration", "holstered_regeneration_reference") })
            {
                var channel = profile.Outputs.Single(channel => channel.Id == output);
                var b = Build().WithActiveSkillSelection("skill_slot_1", skill.ID).WithSkillMod("skill_slot_1", skill.ID, modSlot, new(choice, .05));
                var r = Resolve(b, Context(("overcharge_utility_preview", "1")));
                Assert.DoesNotContain(r.Diagnostics, diagnostic => diagnostic.Severity == "error");
                var outputs = r.ActiveSkillPreviews["skill_slot_1"].Outputs.ToDictionary(value => value.Id, StringComparer.Ordinal);
                CloseTo(outputs[output].Value, channel.Base * 1.05, 1e-6);
                CloseTo(outputs["overcharge_" + output].Value, channel.Base * (1 + channel.OverchargeBonus), 1e-6);
                var health = Resolve(b.WithSkillMod("skill_slot_1", skill.ID, "circuit_board", new("circuit_board_shield_health", .05)));
                var plain = Resolve(Build().WithActiveSkillSelection("skill_slot_1", skill.ID));
                Assert.True(Value(health, "skill_health") > Value(plain, "skill_health"), $"{profile.Name} health did not rise");
                checkedOutputs++;
            }
        }
        Assert.Equal(8, checkedOutputs);

        static double Value(ResolvedBuild resolved, string id) =>
            resolved.ActiveSkillPreviews["skill_slot_1"].Outputs.Single(output => output.Id == id).Value;
    }

    /// <summary>
    /// G221: the Overcharge preview switch adds the shield regeneration references and their Overcharge values to
    /// the skill previews, and changes no stat, breakdown or ordinary output; invalid or off values change nothing.
    /// </summary>
    [Fact]
    public void G221ShieldRegenerationReferenceIsolation()
    {
        var checkedOutputs = 0;
        foreach (var profile in RegenerationProfiles)
        {
            foreach (var skill in Compiled.Pack.ActiveSkills.Where(skill => skill.Name == profile.Name))
            {
                foreach (var slot in new[] { "skill_slot_1", "skill_slot_2" })
                {
                    foreach (var rank in new long[] { 0, 20 })
                    {
                        var b = Build().WithActiveSkillSelection(slot, skill.ID).WithSkillUpgrade(slot, skill.ID, rank);
                        var before = Resolve(b);
                        var after = Resolve(b, Context(("overcharge_utility_preview", "1")));
                        Assert.DoesNotContain(after.Diagnostics, diagnostic => diagnostic.Severity == "error");
                        Assert.True(SameStats(before.Calculation.Stats, after.Calculation.Stats) && SameBreakdowns(before.Calculation.Breakdowns, after.Calculation.Breakdowns), "stats changed");
                        var outputs = after.ActiveSkillPreviews[slot].Outputs.ToDictionary(output => output.Id, StringComparer.Ordinal);
                        foreach (var output in before.ActiveSkillPreviews[slot].Outputs)
                        {
                            Assert.True(outputs.TryGetValue(output.Id, out var same) && SameOutput(output, same), "ordinary output changed");
                        }
                        foreach (var channel in profile.Outputs)
                        {
                            foreach (var (id, want) in new[] { (channel.Id, channel.Base), ("overcharge_" + channel.Id, channel.Base * (1 + channel.OverchargeBonus)) })
                            {
                                Assert.True(outputs.TryGetValue(id, out var output), $"missing reference {id}");
                                CloseTo(output!.Value, want, 1e-6);
                                checkedOutputs++;
                            }
                        }
                        foreach (var value in new[] { "0", ".5", "2", "NaN" })
                        {
                            var r = Resolve(b, Context(("overcharge_utility_preview", value)));
                            Assert.True(SamePreviews(before.ActiveSkillPreviews, r.ActiveSkillPreviews), "invalid or off switch changed outputs");
                        }
                    }
                }
            }
        }
        Assert.Equal(64, checkedOutputs);
    }

    /// <summary>G424: Effective HP with Shield adds a shield's health to Effective HP.</summary>
    [Fact]
    public void G424ShieldEffectiveHP()
    {
        Assert.True(Output(Resolve(Build(), Context()), "shield_effective_hp") == 0, "Effective HP with Shield without a shield");
        var resolved = Resolve(Build().WithActiveSkillSelection("skill_slot_2", "skill_bulwark_shield"), Context());
        var shield = resolved.ActiveSkillPreviews.TryGetValue("skill_slot_2", out var preview)
            ? preview.Outputs.FirstOrDefault(output => output.Id == "skill_health")?.Value : null;
        Assert.True(shield > 0, $"Bulwark health {shield}");
        var want = resolved.Calculation.Stats.Of("effective_hp") + shield!.Value;
        var got = Output(resolved, "shield_effective_hp");
        Assert.True(Math.Abs(got - want) <= 1e-6 * want, $"Effective HP with Shield = {got}, want {want}");
    }

    /// <summary>A build output's value, as Go's g363Output.</summary>
    private static double Output(ResolvedBuild resolved, string id) =>
        (resolved.BuildOutputs.FirstOrDefault(output => output.Id == id) ?? throw new InvalidOperationException($"build output {id} missing")).Value;

    // Go compared with reflect.DeepEqual; these compare the same values (doubles with ==, so NaN is never equal).
    private static bool SameStats(ImmutableDictionary<string, double> a, ImmutableDictionary<string, double> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static bool SameBreakdowns(ImmutableDictionary<string, AggregatedStat> a, ImmutableDictionary<string, AggregatedStat> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && pair.Value.Base == other.Base && pair.Value.Final == other.Final
            && pair.Value.Clamped == other.Clamped && pair.Value.Contributions.SequenceEqual(other.Contributions));

    private static bool SameOutput(SkillPreviewOutput a, SkillPreviewOutput b) =>
        a.Id == b.Id && a.Name == b.Name && a.Description == b.Description && a.Format == b.Format && a.Formula == b.Formula
            && a.When == b.When && a.Value == b.Value && a.Kind == b.Kind && SameStats(a.Inputs, b.Inputs);

    private static bool SamePreviews(ImmutableDictionary<string, SkillPreview> a, ImmutableDictionary<string, SkillPreview> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && pair.Value.SkillId == other.SkillId
            && pair.Value.Outputs.Length == other.Outputs.Length && pair.Value.Outputs.Zip(other.Outputs).All(outputs => SameOutput(outputs.First, outputs.Second)));
}
