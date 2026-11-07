using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// Skill Expertise grades (F22) and their interaction with skill mods (F23, Eligible, reviewed skill mods), with the
/// reviewed Overcharge corrections they share a file with, ported from Go's skill_expertise_g191_test.go,
/// skill_readings_g325_test.go and restorer_duration_source_g330_test.go. Go's share-code checks of its own
/// versioned tuple (that a selected-skill build still encodes as v6, a graded one as v10) are dropped: those are
/// Go-era share formats, read here only by CharacterPlanner.Legacy (ADR 0001), and this app's ShareCode has a
/// single version; the round trip through ShareCode is kept.
/// </summary>
public sealed class SkillExpertiseTests
{
    private static readonly string[] Slots = ["skill_slot_1", "skill_slot_2"];
    // The Y8S3.3 skills' per-grade values have no reviewed source: the grade can be set, and no output shows or uses it.
    private static readonly string[] NoExpertiseSource = ["skill_leech_hive", "skill_the_howl", "skill_blackout_seeker", "skill_corrosive_trap", "skill_gas_bomber_drone", "skill_trophy_system_drone"];
    private const string RestorerDurationSourceOutput = "restorer_duration_source_seconds";

    /// <summary>The committed pack with the chosen active skills' modifiers replaced, as Go's edit of pack.ActiveSkills[i].Modifiers and ValidatePack.</summary>
    private static CompiledPackExpressions WithSkillModifiers(Func<ActiveSkillDefinition, bool> which, params (string Stat, double Value)[] modifiers)
    {
        ImmutableArray<StatModifier> replaced = [.. modifiers.Select(modifier => new StatModifier { Stat = modifier.Stat, Op = ModifierOps.Flat, Value = new(modifier.Value) })];
        var pack = Compiled.Pack with { ActiveSkills = [.. Compiled.Pack.ActiveSkills.Select(skill => which(skill) ? skill with { Modifiers = replaced } : skill)] };
        var result = PackValidator.Validate(pack);
        Assert.True(result.Compiled is not null, $"invalid fixture: {string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message))}");
        return result.Compiled;
    }

    /// <summary>As Go's assertNoErrorDiagnostics.</summary>
    private static void NoErrors(ResolvedBuild result)
    {
        var errors = result.Diagnostics.Where(diagnostic => diagnostic.Severity == "error").ToArray();
        Assert.True(errors.Length == 0, $"error diagnostics: {string.Join("; ", errors.Select(diagnostic => diagnostic.ToString()))}");
    }

    /// <summary>As Go's division2SkillPreviewValue: the output must exist.</summary>
    private static double PreviewValue(ResolvedBuild resolved, string slot, string output)
    {
        var found = SkillOutput(resolved, slot, output);
        Assert.True(found is not null, $"missing preview output {slot}/{output}");
        return found.Value;
    }

    /// <summary>As Go's g330SkillOutput: the output, or null.</summary>
    private static double? SkillOutput(ResolvedBuild resolved, string slot, string output) =>
        resolved.ActiveSkillPreviews.TryGetValue(slot, out var preview) ? preview.Outputs.FirstOrDefault(candidate => candidate.Id == output)?.Value : null;

    /// <summary>As Go's hasDiagnostic.</summary>
    private static bool HasDiagnostic(ResolvedBuild resolved, string code, string subject) =>
        resolved.Diagnostics.Any(diagnostic => diagnostic.Code == code && diagnostic.Subject == subject);

    /// <summary>
    /// G191: an Expertise grade adds 1% a grade to the reviewed damage channels' tier bonus, shows the grade as a
    /// reference from grade 1, and leaves other outputs and character stats alone, at Skill Tiers 0, 3 and 6.
    /// </summary>
    [Fact]
    public void G191SkillExpertiseDamageAndReferenceIsolation()
    {
        foreach (var tier in new[] { 0, 3, 6 })
        {
            var compiled = WithSkillModifiers(_ => true, ("skill_tier", tier), ("skill_damage", .3), ("skill_efficiency", .1));
            var p = compiled.Pack;
            var channels = 0;
            foreach (var skill in p.ActiveSkills)
            {
                Assert.True(skill.Upgrade is { MaxRank: 30 }, $"missing rank descriptor on {skill.ID}");
                foreach (var slot in Slots)
                {
                    var @base = BuildSnapshot.Create(p).WithActiveSkillSelection(slot, skill.ID);
                    var before = BuildResolver.Resolve(compiled, @base);
                    foreach (var rank in new[] { 0, 20, 30 })
                    {
                        var after = BuildResolver.Resolve(compiled, @base.WithSkillUpgrade(slot, skill.ID, rank));
                        NoErrors(after);
                        var indexed = after.ActiveSkillPreviews[slot].Outputs.ToDictionary(o => o.Id, StringComparer.Ordinal);
                        foreach (var o in before.ActiveSkillPreviews[slot].Outputs)
                        {
                            if (o.Id.StartsWith("assumed_", StringComparison.Ordinal)) { continue; } // skill DPS rates derive from the damage channels checked here
                            var expected = o.Value;
                            Assert.True(indexed.TryGetValue(o.Id, out var graded), $"{skill.ID} {o.Id} missing at grade {rank}");
                            if (o.Formula.Contains("skill_upgrade_rank", StringComparison.Ordinal))
                            {
                                if (rank == 0 && slot == "skill_slot_1") { channels++; }
                                var curve = $"{skill.ID}_{o.Id}_tiers";
                                Assert.True(p.Curves.ContainsKey(curve), $"missing curve {curve}");
                                var bonus = p.Curves[curve][tier].Y;
                                expected *= (1 + bonus + (rank * .01)) / (1 + bonus);
                                Assert.True(graded.Inputs.GetValueOrDefault("skill_upgrade_rank") == rank, $"missing grade input on {skill.ID} {o.Id}");
                            }
                            CloseTo(graded.Value, expected, 1e-6);
                        }
                        if (rank == 0 || NoExpertiseSource.Contains(skill.ID))
                        {
                            Assert.False(indexed.ContainsKey("expertise_bonus_reference"), $"reference visible on {skill.ID} at grade {rank}");
                        }
                        else
                        {
                            Assert.True(indexed.TryGetValue("expertise_bonus_reference", out var reference), $"missing expertise reference on {skill.ID}");
                            CloseTo(reference.Value, rank * .01, 1e-12);
                        }
                        Assert.True(after.Calculation.Stats.Of("skill_damage") == before.Calculation.Stats.Of("skill_damage"), "grade leaked to stats");
                    }
                }
            }
            Assert.True(channels == 17, $"reviewed damage channels={channels} at tier {tier}");
        }
    }

    /// <summary>
    /// G325: the Restorer Hive's grade shows as its Expertise reference (1% of the 180 s base a grade) without
    /// changing its estimated duration, other outputs, the other skill or player stats.
    /// </summary>
    [Fact]
    public void G325RestorerExpertiseSourceReferenceIsolation()
    {
        const string skillId = "skill_restorer_hive";
        foreach (var slot in Slots)
        {
            var other = slot == "skill_slot_1" ? "skill_slot_2" : "skill_slot_1";
            var @base = Build().WithActiveSkillSelection(slot, skillId).WithActiveSkillSelection(other, "skill_assault_turret");
            var baseline = Resolve(@base);
            foreach (var rank in new[] { 1, 30 })
            {
                var resolved = Resolve(@base.WithSkillUpgrade(slot, skillId, rank));
                NoErrors(resolved);
                var reference = PreviewValue(resolved, slot, "expertise_bonus_reference");
                CloseTo(reference, rank * .01, 1e-12);
                // Source reference: one grade is 1% of the 180 s base, or 1.8 s.
                // The game/source does not establish its aggregation with other duration factors.
                CloseTo(180 * reference, rank == 30 ? 54 : 1.8, 1e-12);
                Assert.True(PreviewValue(resolved, slot, "duration_seconds") == PreviewValue(baseline, slot, "duration_seconds"), $"grade {rank} changed estimated duration");
                foreach (var output in baseline.ActiveSkillPreviews[slot].Outputs.Where(output => output.Id != "expertise_bonus_reference"))
                {
                    var got = PreviewValue(resolved, slot, output.Id);
                    Assert.True(got == output.Value, $"grade {rank} changed unrelated {output.Id}: {got} != {output.Value}");
                }
                Assert.True(SamePreview(resolved.ActiveSkillPreviews[other], baseline.ActiveSkillPreviews[other]), "grade leaked to the other skill slot");
                Assert.True(SameStats(resolved.Calculation.Stats, baseline.Calculation.Stats), "grade leaked into player stats");
            }
        }
    }

    /// <summary>G325: out-of-range grades are diagnosed and change nothing, and a graded build survives its share code.</summary>
    [Fact]
    public void G325RestorerExpertiseRejectsInvalidRanksAndPreservesShare()
    {
        var @base = Build().WithActiveSkillSelection("skill_slot_1", "skill_restorer_hive");
        var baseline = Resolve(@base);
        foreach (var rank in new[] { -1, 31 })
        {
            var resolved = Resolve(@base.WithSkillUpgrade("skill_slot_1", "skill_restorer_hive", rank));
            Assert.True(HasDiagnostic(resolved, "invalid-skill-upgrade", "skill_slot_1"), $"invalid rank {rank} was accepted");
            Assert.True(PreviewValue(resolved, "skill_slot_1", "duration_seconds") == PreviewValue(baseline, "skill_slot_1", "duration_seconds"), $"invalid rank {rank} changed duration");
        }

        @base = @base.WithActiveSkillSelection("skill_slot_2", "skill_assault_turret");
        var configured = @base.WithSkillUpgrade("skill_slot_1", "skill_restorer_hive", 20);
        var decoded = ShareCode.Decode(ShareCode.Encode(configured));
        Assert.True(decoded.SkillUpgrades.Count == configured.SkillUpgrades.Count && configured.SkillUpgrades.All(pair => decoded.SkillUpgrades.GetValueOrDefault(pair.Key) == pair.Value),
            "share lost installed grade");
        var beforeShare = Resolve(configured);
        var afterShare = Resolve(decoded);
        Assert.True(SameResolved(beforeShare, afterShare), "grade share changed the resolved Restorer reference");
        CloseTo(PreviewValue(afterShare, "skill_slot_1", "expertise_bonus_reference"), .2, 1e-12);
        Assert.True(SamePreview(afterShare.ActiveSkillPreviews["skill_slot_2"], beforeShare.ActiveSkillPreviews["skill_slot_2"]), "grade share leaked into the other skill");
    }

    /// <summary>G325: the Airburst Seeker Mine's corrected Tier 7 Overcharge doubles its damage and burn, in both slots.</summary>
    [Fact]
    public void G325CorrectedAirburstOverchargeDamageInBothSlots()
    {
        foreach (var slot in Slots)
        {
            var compiled = WithSkillModifiers(_ => true, ("skill_tier", 6));
            var @base = BuildSnapshot.Create(compiled.Pack).WithActiveSkillSelection(slot, "skill_airburst_seeker_mine");
            var resolved = BuildResolver.Resolve(compiled, @base, Context(("overcharge_damage_preview", "1")));
            NoErrors(resolved);
            var s = resolved.Calculation.Stats;
            var damageWant = 331811 * 2 * Math.Max(0, 1 + s.Of("effective_skill_damage")) * Math.Max(0, 1 + s.Of("total_skill_damage")) * s.Of("skill_damage_multiplier");
            var burnWant = 79635 * 2 * Math.Max(0, 1 + s.Of("effective_skill_damage") + s.Of("effective_status_effects")) * Math.Max(0, 1 + s.Of("total_skill_damage"))
                * s.Of("skill_damage_multiplier") * Math.Max(0, s.Of("combustion_burn_damage_factor"));
            CloseTo(PreviewValue(resolved, slot, "overcharge_damage"), damageWant, 1e-7);
            CloseTo(PreviewValue(resolved, slot, "overcharge_burn_damage"), burnWant, 1e-7);
        }
    }

    /// <summary>G325: the Explosive Sticky Bomb's corrected Overcharge recovery adds 90% haste, in both slots.</summary>
    [Fact]
    public void G325CorrectedExplosiveStickyOverchargeRecoveryInBothSlots()
    {
        foreach (var slot in Slots)
        {
            var resolved = Resolve(Build().WithActiveSkillSelection(slot, "skill_explosive_sticky_bomb"), Context(("overcharge_recovery_preview", "1")));
            NoErrors(resolved);
            var want = 60 / (1 + resolved.Calculation.Stats.Of("effective_skill_haste") + .9);
            CloseTo(PreviewValue(resolved, slot, "overcharge_cooldown_seconds"), want, 1e-12);
        }
    }

    /// <summary>
    /// G330: the Restorer duration source comparison, only with its flag, adds the grade to the skill-mod pool (not
    /// multiplicatively) over the 180 s base with Skill Duration, and changes no ordinary or Overcharge duration,
    /// other skill or stat.
    /// </summary>
    [Fact]
    public void G330RestorerDurationSourcePoolAndVisibility()
    {
        var context = Context(("restorer_duration_source_preview", "1"), ("overcharge_lifetime_preview", "1"));
        foreach (var slot in Slots)
        {
            var compiled = WithSkillModifiers(skill => skill.ID == "skill_restorer_hive", ("skill_duration", .2), ("skill_efficiency", .1));
            var other = slot == "skill_slot_1" ? "skill_slot_2" : "skill_slot_1";
            var @base = BuildSnapshot.Create(compiled.Pack).WithActiveSkillSelection(slot, "skill_restorer_hive").WithActiveSkillSelection(other, "skill_assault_turret");
            var modded = @base.WithSkillMod(slot, "skill_restorer_hive", "system", new("polycarbonate_housing", .042));
            var baseline = BuildResolver.Resolve(compiled, modded);
            Assert.True(SkillOutput(baseline, slot, RestorerDurationSourceOutput) is null, $"{slot}: source comparison is visible without its context flag");

            var gradeZeroSource = 0.0;
            foreach (var rank in new[] { 0, 1, 20, 30 })
            {
                var label = $"{slot} grade {rank}";
                var resolved = BuildResolver.Resolve(compiled, modded.WithSkillUpgrade(slot, "skill_restorer_hive", rank), context);
                var got = SkillOutput(resolved, slot, RestorerDurationSourceOutput);
                Assert.True(got is not null, $"{label}: source comparison missing");
                CloseTo(got.Value, 180 * (1 + .042 + (rank * .01)) * 1.3, 1e-10);
                if (rank == 0)
                {
                    gradeZeroSource = got.Value;
                    // Zero installed grade contributes zero; the comparison is still the fixed-mod estimate.
                    CloseTo(got.Value, PreviewValue(baseline, slot, "duration_seconds"), 1e-10);
                }
                if (rank == 1) { CloseTo(got.Value - gradeZeroSource, 1.8 * 1.3, 1e-10); }
                Assert.False(rank > 0 && Math.Abs(got.Value - (180 * (1 + .042) * (1 + (rank * .01)) * 1.3)) < 1e-5, $"{label}: appears multiplicative inside the skill-mod pool: {got}");
                CloseTo(resolved.Calculation.Stats.Of("effective_skill_duration"), .3, 1e-12);
                Assert.True(PreviewValue(resolved, slot, "duration_seconds") == PreviewValue(baseline, slot, "duration_seconds"), $"{label}: changed ordinary duration");
                var baselineOvercharge = BuildResolver.Resolve(compiled, modded, Context(("overcharge_lifetime_preview", "1")));
                Assert.True(PreviewValue(resolved, slot, "overcharge_duration_seconds") == PreviewValue(baselineOvercharge, slot, "overcharge_duration_seconds"), $"{label}: changed Overcharge duration");
                Assert.True(SkillOutput(resolved, other, RestorerDurationSourceOutput) is null, $"{label}: Restorer comparison leaked to the other skill slot");
                Assert.True(SameStats(resolved.Calculation.Stats, baseline.Calculation.Stats), $"{label}: Restorer comparison changed player stats");
                NoErrors(resolved);
            }
        }
    }

    /// <summary>
    /// G330: with four Ortiz Reficere pieces, the source comparison ignores the nanite duration reduction that the ordinary
    /// duration applies, and the comparison flag doesn't change the ordinary duration.
    /// </summary>
    [Fact]
    public void G330RestorerDurationSourceIgnoresOrtizNaniteComparison()
    {
        foreach (var slot in Slots)
        {
            var other = slot == "skill_slot_1" ? "skill_slot_2" : "skill_slot_1";
            foreach (var (name, reduction, talent) in new[] { ("90_percent", .9, false), ("25_percent", .25, true) })
            {
                var label = $"{slot}/{name}";
                var configured = Build();
                foreach (var gearSlot in new[] { "mask", "chest", "backpack", "gloves" }) { configured = configured.WithEquipment(gearSlot, gearSlot + "_ortiz_reficere_" + gearSlot); }
                if (talent) { configured = configured.WithTalentSelection("backpack", "talent", "gear_talent_improved_dampeners"); }
                configured = configured.WithActiveSkillSelection(slot, "skill_restorer_hive").WithActiveSkillSelection(other, "skill_assault_turret")
                    .WithSkillUpgrade(slot, "skill_restorer_hive", 30);

                var ordinary = Resolve(configured);
                var comparison = Resolve(configured, Context(("restorer_duration_source_preview", "1")));
                Assert.True(SkillOutput(ordinary, slot, RestorerDurationSourceOutput) is null, $"{label}: source comparison is visible without its context flag");
                CloseTo(comparison.Calculation.Stats.Of("nanite_duration_reduction"), reduction, 1e-12);
                CloseTo(comparison.Calculation.Stats.Of("effective_skill_duration"), .08, 1e-12);
                CloseTo(PreviewValue(comparison, slot, RestorerDurationSourceOutput), 180 * 1.3 * 1.08, 1e-12);
                CloseTo(PreviewValue(comparison, slot, "duration_seconds"), 180 * 1.08 * (1 - reduction), 1e-10);
                Assert.True(PreviewValue(comparison, slot, "duration_seconds") == PreviewValue(ordinary, slot, "duration_seconds"), $"{label}: comparison context changed ordinary Ortiz duration");
            }
        }
    }

    /// <summary>
    /// G330: an invalid grade, or a grade on another skill, leaves the source comparison at the 180 s base; the
    /// comparison stays on the Restorer, and a selected-skill build survives its share code.
    /// </summary>
    [Fact]
    public void G330RestorerDurationSourceInvalidOwnershipAndLegacyShare()
    {
        var @base = Build().WithActiveSkillSelection("skill_slot_1", "skill_restorer_hive").WithActiveSkillSelection("skill_slot_2", "skill_assault_turret");
        var context = Context(("restorer_duration_source_preview", "1"));
        foreach (var rank in new[] { -1, 31 })
        {
            var resolved = Resolve(@base.WithSkillUpgrade("skill_slot_1", "skill_restorer_hive", rank), context);
            Assert.True(HasDiagnostic(resolved, "invalid-skill-upgrade", "skill_slot_1"), $"invalid grade {rank} was accepted");
            CloseTo(PreviewValue(resolved, "skill_slot_1", RestorerDurationSourceOutput), 180, 1e-12);
        }

        var foreign = Resolve(@base.WithSkillUpgrade("skill_slot_2", "skill_assault_turret", 30), context);
        CloseTo(PreviewValue(foreign, "skill_slot_1", RestorerDurationSourceOutput), 180, 1e-12);
        Assert.True(SkillOutput(foreign, "skill_slot_2", RestorerDurationSourceOutput) is null, "Restorer comparison appeared on a different skill");

        // Go also checked that this build still encodes as its legacy v6 tuple; that's a Go-era share format (see the class summary).
        var decoded = ShareCode.Decode(ShareCode.Encode(@base));
        Assert.True(SameResolved(Resolve(decoded, context), Resolve(@base, context)), "share changed the context-only Restorer comparison");
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

    private static bool SamePreview(SkillPreview? a, SkillPreview? b) =>
        a is null || b is null ? a == b
            : a.SkillId == b.SkillId && a.Outputs.Length == b.Outputs.Length && a.Outputs.Zip(b.Outputs).All(outputs => SameOutput(outputs.First, outputs.Second));

    /// <summary>Two resolutions alike in every result Go's ResolvedCharacter carried: stats, breakdowns, sets, previews, build outputs and diagnostics.</summary>
    private static bool SameResolved(ResolvedBuild a, ResolvedBuild b) =>
        SameStats(a.Calculation.Stats, b.Calculation.Stats) && SameBreakdowns(a.Calculation.Breakdowns, b.Calculation.Breakdowns)
            && a.ActiveSets.Length == b.ActiveSets.Length && a.ActiveSets.Zip(b.ActiveSets).All(sets => sets.First with { AppliedBonuses = [] } == sets.Second with { AppliedBonuses = [] }
                && sets.First.AppliedBonuses.SequenceEqual(sets.Second.AppliedBonuses))
            && a.ActiveSkillPreviews.Count == b.ActiveSkillPreviews.Count && a.ActiveSkillPreviews.All(pair => SamePreview(pair.Value, b.ActiveSkillPreviews.GetValueOrDefault(pair.Key)))
            && a.BuildOutputs.SequenceEqual(b.BuildOutputs) && a.Diagnostics.SequenceEqual(b.Diagnostics)
            && a.Tags.SetEquals(b.Tags) && a.SelectedTags.SetEquals(b.SelectedTags);
}
