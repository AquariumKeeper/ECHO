using System.Collections.Immutable;
using System.Text.Json.Nodes;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// Conditional preview outputs, Rigger's cancellation reset and the Smart Cover and Defender Drone support references
/// (F20, Active skills and skill previews), ported from Go's conditional_skill_preview_g158_test.go,
/// smart_cover_support_g219_test.go and defender_protection_g225_test.go. Go's G158 conditional fixture replaced the
/// skills of its Ashen Vale reference pack, a Go-era v1 file the engine doesn't load (ADR 0001); it's added to the
/// committed Division 2 pack here, and reads that pack's skill_damage_multiplier (1) where Go read Ashen Vale's attack_power
/// (both positive on an empty build). Go's G219 and G225 read data/sources/division-2-smart-cover-support.json and
/// data/sources/division-2-defender-protection.json, which aren't part of this repository; the profiles' output
/// ids, bases and Overcharge bonuses they used are copied here verbatim.
/// </summary>
public sealed class SkillPreviewTests
{
    private static readonly string[] Slots = ["skill_slot_1", "skill_slot_2"];
    private static readonly long[] Ranks = [0, 20];
    private const string Positive = "skill_damage_multiplier";

    /// <summary>As Go's assertNoErrorDiagnostics.</summary>
    private static void NoErrors(ResolvedBuild result)
    {
        var errors = result.Diagnostics.Where(diagnostic => diagnostic.Severity == "error").ToArray();
        Assert.True(errors.Length == 0, $"error diagnostics: {string.Join("; ", errors.Select(diagnostic => diagnostic.ToString()))}");
    }

    /// <summary>
    /// The committed pack with Go's G158 fixture, one conditional skill in its own slot, added. (Go replaced the pack's
    /// skills and slots; the Division 2 pack's build outputs, presentation and simulation name its own skills, so the
    /// fixture is added beside them.)
    /// </summary>
    private static GamePack ConditionalPack(params ActiveSkillPreviewOutputDefinition[] outputs) => Compiled.Pack with
    {
        ActiveSkills = Compiled.Pack.ActiveSkills.Add(new() { ID = "conditional", Name = "Conditional", PreviewOutputs = [.. outputs] }),
        ActiveSkillSlots = Compiled.Pack.ActiveSkillSlots.Add(new() { ID = "slot", Name = "Slot", Choices = ["conditional"] }),
    };

    /// <summary>
    /// G158: a preview output's when condition shows it when non-zero, hides it at 0, diagnoses a non-finite one,
    /// carries its inputs and text, keeps a hidden output's formula unevaluated, and must be compiled; an unknown
    /// identifier, function or curve, or a malformed when in the pack file, is rejected.
    /// </summary>
    [Fact]
    public void G158ConditionalPreviewSemantics()
    {
        foreach (var when in new[] { "", "0", "1", "-1", Positive + " > 0", "sqrt(-1)" })
        {
            ActiveSkillPreviewOutputDefinition baseOutput = new() { ID = "base", Name = "Base", Formula = Positive }, gated = new() { ID = "gated", Name = "Gated", Formula = "0", When = when };
            var p = ConditionalPack(baseOutput, gated);
            var validated = PackValidator.Validate(p);
            Assert.True(validated.Compiled is not null, $"invalid fixture {when}: {string.Join("; ", validated.Diagnostics.Select(diagnostic => diagnostic.Message))}");
            var c = validated.Compiled;
            var b = BuildSnapshot.Create(p).WithActiveSkillSelection("slot", "conditional");
            var r = BuildResolver.Resolve(c, b);
            var want = when is "0" or "sqrt(-1)" ? 1 : 2;
            Assert.True(r.ActiveSkillPreviews["slot"].Outputs.Length == want, $"{when}: outputs={string.Join(", ", r.ActiveSkillPreviews["slot"].Outputs.Select(output => output.Id))}");
            var errors = 0;
            foreach (var d in r.Diagnostics.Where(d => d.Code == "active-skill-preview-eval"))
            {
                errors++;
                Assert.True(d.Subject == "slot.gated", d.ToString());
            }
            Assert.True(errors == (when == "sqrt(-1)" ? 1 : 0), when == "sqrt(-1)" ? "non-finite condition missing diagnostic" : $"unexpected diagnostics for {when}");
            if (when == Positive + " > 0")
            {
                var o = r.ActiveSkillPreviews["slot"].Outputs[1];
                CloseTo(o.Inputs.GetValueOrDefault(Positive), r.Calculation.Stats.Of(Positive), 1e-12);
                Assert.True(o.When == when, "condition metadata missing");
            }
            if (when == "0")
            {
                p = ConditionalPack(baseOutput, gated with { Formula = "sqrt(-1)" });
                c = PackValidator.Validate(p).Compiled;
                Assert.True(c is not null, "invalid inactive-formula fixture");
                r = BuildResolver.Resolve(c, b);
                Assert.DoesNotContain(r.Diagnostics, d => d.Code == "active-skill-preview-eval"); // inactive formula evaluated
                c = c with { SkillPreviewExpressions = c.SkillPreviewExpressions.Remove("activeSkills.conditional.previewOutputs.gated.when") };
                r = BuildResolver.Resolve(c, b);
                Assert.True(r.Diagnostics.Any(d => d.Code == "active-skill-preview-eval"), "missing compiled condition accepted");
            }
        }
        foreach (var when in new[] { "unknown_stat", "unknown_function(1)", "curve('missing_curve',1)" })
        {
            var p = ConditionalPack(new ActiveSkillPreviewOutputDefinition { ID = "gated", Name = "Gated", Formula = "0", When = when });
            Assert.True(PackValidator.Validate(p).Compiled is null, $"accepted {when}");
        }
        // Go unmarshalled each raw preview output; here each goes into the purpose-built test pack's rules file, which
        // loads with the same output and no when.
        static ImmutableSortedDictionary<string, ReadOnlyMemory<byte>> WithOutput(string raw) =>
            TestPacks.Edit(PackFiles.Rules, node => node["skills"]![0]!["previewOutputs"]!.AsArray().Add(JsonNode.Parse(raw)));
        var control = PackValidator.Load(WithOutput("""{"id":"out","name":"Out","formula":"0"}"""));
        Assert.True(control.Compiled is not null, $"control rejected: {string.Join("; ", control.Diagnostics.Select(diagnostic => diagnostic.Message))}");
        foreach (var raw in new[] { """{"id":"out","name":"Out","formula":"0","when":null}""", """{"id":"out","name":"Out","formula":"0","when":7}""",
            """{"id":"out","name":"Out","formula":"0","when":""}""", """{"id":"out","name":"Out","formula":"0","when":" "}""" })
        {
            Assert.True(PackValidator.Load(WithOutput(raw)).Compiled is null, $"accepted {raw}");
        }
    }

    /// <summary>The first pieces of a set, each in its own slot, as Go's g54SetBuild.</summary>
    private static BuildSnapshot SetBuild(string setId, int pieces)
    {
        var set = Compiled.Pack.Sets.FirstOrDefault(set => set.ID == setId) ?? throw new InvalidOperationException($"missing set {setId}");
        var b = BuildSnapshot.Create(Compiled.Pack, "G54 set talent regression: " + setId);
        foreach (var itemId in set.Items.Take(pieces))
        {
            var item = Compiled.Pack.Items.FirstOrDefault(item => item.ID == itemId);
            if (item is not null) { b = b.WithEquipment(item.Slot, item.ID); }
        }
        return b;
    }

    /// <summary>A talent selected on the equipped item's talent slot that offers it, as Go's g54SelectUpgrade.</summary>
    private static BuildSnapshot SelectUpgrade(BuildSnapshot b, string slot, string talentId)
    {
        var itemId = b.Equipment.GetValueOrDefault(slot, "");
        var talentSlot = Compiled.Pack.Items.Where(item => item.ID == itemId).SelectMany(item => item.TalentSlots).FirstOrDefault(talentSlot => talentSlot.Choices.Contains(talentId))
            ?? throw new InvalidOperationException($"{talentId} is not selectable on equipped {slot} item {itemId}");
        return b.WithTalentSelection(slot, talentSlot.ID, talentId);
    }

    /// <summary>
    /// G158: Rigger's Complete Uptime, on its own backpack with four or more pieces, sets the cancellation cooldown of
    /// the five platform skills to 0, changing no other output or stat; another host or none grants nothing.
    /// </summary>
    [Fact]
    public void G158RiggerPlatformPreviewAndIsolation()
    {
        var eligible = 0;
        foreach (var pieces in new[] { 3, 4, 5 })
        {
            foreach (var skill in Compiled.Pack.ActiveSkills)
            {
                var b = SetBuild("gear_set_rigger", pieces).WithActiveSkillSelection("skill_slot_1", skill.ID);
                var on = SelectUpgrade(b, "backpack", "gear_talent_complete_uptime");
                var @base = Resolve(b);
                var r = Resolve(on);
                var platform = skill.PreviewOutputs.Any(o => o.ID == "cooldown_after_cancel");
                var reset = false;
                var j = 0;
                foreach (var o in r.ActiveSkillPreviews["skill_slot_1"].Outputs)
                {
                    if (o.Id == "cooldown_after_cancel")
                    {
                        reset = true;
                        CloseTo(o.Value, 0, 1e-12);
                        CloseTo(o.Inputs.GetValueOrDefault("rigger_cancel_reset_available"), 1, 1e-12);
                        if (pieces == 4) { eligible++; }
                    }
                    else
                    {
                        CloseTo(o.Value, @base.ActiveSkillPreviews["skill_slot_1"].Outputs[j].Value, 1e-8);
                        j++;
                    }
                }
                Assert.True(reset == (platform && pieces >= 4), $"pieces={pieces} skill={skill.ID} reset={reset}");
                foreach (var id in new[] { "skill_haste", "effective_skill_haste", "skill_damage", "skill_damage_multiplier", "equipped_weapon_damage" })
                {
                    CloseTo(r.Calculation.Stats.Of(id), @base.Calculation.Stats.Of(id), 1e-12);
                }
                foreach (var invalid in new[]
                {
                    on.WithTalentSelection("backpack", "talent", ""), on.WithEquipment("backpack", "backpack_belstone_armory_backpack"),
                    on.WithTalentSelection("backpack", "talent", "").WithTalentSelection("chest", "talent", "gear_talent_complete_uptime"),
                })
                {
                    Assert.False(Resolve(invalid).ActiveSkillPreviews["skill_slot_1"].Outputs.Any(o => o.Id == "cooldown_after_cancel"), $"invalid host granted cancellation on {skill.ID}");
                }
            }
        }
        Assert.True(eligible == 5, $"platform count={eligible}");
    }

    /// <summary>The Smart Cover support profiles of Go's data/sources/division-2-smart-cover-support.json: skill name, output id, base and Overcharge bonus.</summary>
    private static readonly (string Name, string Id, double Base, double OverchargeBonus)[] SmartCoverSupport =
    [
        ("Precision Smart Cover", "weapon_handling_reference", 0.15, 1.0),
        ("Precision Smart Cover", "out_of_cover_damage_reference", 0.1, 0.35),
        ("Fortified Smart Cover", "bonus_armor_reference", 0.5, 1.0),
        ("Fortified Smart Cover", "explosive_resistance_reference", 0.05, 0.6),
        ("Fortified Smart Cover", "pulse_resistance_reference", 0.05, 0.45),
    ];

    /// <summary>The Defender Drone protection profile of Go's data/sources/division-2-defender-protection.json.</summary>
    private static readonly (string Name, string Id, double Base, double OverchargeBonus)[] DefenderProtection =
    [
        ("Defender Drone", "damage_reduction_reference", 0.2, 0.6),
    ];

    /// <summary>
    /// G219: the Overcharge utility switch adds the Smart Cover support references (base, tier bonus and Overcharge
    /// bonus) without changing stats or ordinary outputs; an off or invalid switch changes nothing.
    /// </summary>
    [Fact]
    public void G219SmartCoverSupportReferenceIsolation() => Assert.True(CheckSupportReferences(SmartCoverSupport) == 60, "incomplete coverage");

    /// <summary>
    /// G225: the Overcharge utility switch adds the Defender Drone protection references (base, tier bonus and
    /// Overcharge bonus) without changing stats or ordinary outputs; an off or invalid switch changes nothing.
    /// </summary>
    [Fact]
    public void G225DefenderProtectionReferenceIsolation() => Assert.True(CheckSupportReferences(DefenderProtection) == 12, "incomplete coverage");

    /// <summary>Go's shared G219 and G225 body: checks each profile's skill in both slots and grades, and returns the references checked.</summary>
    private static int CheckSupportReferences((string Name, string Id, double Base, double OverchargeBonus)[] source)
    {
        var checkedReferences = 0;
        foreach (var profile in source.GroupBy(channel => channel.Name, StringComparer.Ordinal))
        {
            foreach (var skill in Compiled.Pack.ActiveSkills.Where(skill => skill.Name == profile.Key))
            {
                foreach (var slot in Slots)
                {
                    foreach (var rank in Ranks)
                    {
                        var b = Build().WithActiveSkillSelection(slot, skill.ID).WithSkillUpgrade(slot, skill.ID, rank);
                        var before = Resolve(b);
                        var after = Resolve(b, Context(("overcharge_utility_preview", "1")));
                        NoErrors(after);
                        Assert.True(SameStats(before.Calculation.Stats, after.Calculation.Stats) && SameBreakdowns(before.Calculation.Breakdowns, after.Calculation.Breakdowns), "stats changed");
                        var outputs = after.ActiveSkillPreviews[slot].Outputs.ToDictionary(output => output.Id, StringComparer.Ordinal);
                        foreach (var output in before.ActiveSkillPreviews[slot].Outputs)
                        {
                            Assert.True(outputs.TryGetValue(output.Id, out var same) && SameOutput(output, same), $"ordinary output changed: {output.Id}");
                        }
                        foreach (var channel in profile)
                        {
                            foreach (var (id, want) in new[] { (channel.Id + "_base", channel.Base), (channel.Id + "_tier_bonus", 0), ("overcharge_" + channel.Id, channel.OverchargeBonus) })
                            {
                                Assert.True(outputs.TryGetValue(id, out var output), $"missing reference {id} on {skill.ID}");
                                CloseTo(output!.Value, want, 1e-6);
                                checkedReferences++;
                            }
                        }
                        foreach (var value in new[] { "0", ".5", "2", "NaN" })
                        {
                            var r = Resolve(b, Context(("overcharge_utility_preview", value)));
                            Assert.True(SamePreviews(before.ActiveSkillPreviews, r.ActiveSkillPreviews), $"invalid or off switch {value} changed outputs");
                        }
                    }
                }
            }
        }
        return checkedReferences;
    }

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
