using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// Repair budgets and rates (F34), ported from Go's repair_talents_g403_test.go, repair_events_g118_test.go,
/// umbra_repair_g149_test.go, support_repair_g146_test.go, ground_control_repair_g136_test.go and
/// clutch_repair_g124_test.go.
/// </summary>
public sealed class RepairTests
{
    /// <summary>
    /// G403: repair talents fill repair budgets and rates: Caduceus from landed critical hits, Symbiosis from firing
    /// DPS, Doctor Home from the kill rate, Bleeding Edge as armor regeneration and Bob and Weave per cover move.
    /// </summary>
    [Fact]
    public void G403RepairTalents()
    {
        var primary = Division.Weapon("primary_weapon");
        static BuildSnapshot With(string slot, string item, string talent) => Build()
            .WithEquipment("gloves", "gloves_n_5_11_tactical_gloves")
            .WithAttributeRoll("gloves", "minor_1", new("critical_hit_chance_gear_minor", 0.06))
            .WithEquipment(slot, item)
            .WithTalentSelection(slot, "talent", talent);

        var c = Stats(With("primary_weapon", "primary_weapon_caduceus", "weapon_talent_caduceus"), primary);
        var crit = c.Of("effective_critical_hit_chance");
        var critShot = 0.75 * c.Of("equipped_weapon_critical_damage") + 0.25 * c.Of("equipped_weapon_critical_headshot_damage");
        CloseTo(c.Of("estimated_crit_repair_per_second"), 0.03 * 0.7 * crit * critShot * c.Of("equipped_weapon_rpm") / 60, 1e-6);
        Assert.True(c.Of("estimated_crit_repair_per_second") > 0, "Caduceus repairs from critical hits");

        var o = Stats(With("primary_weapon", "primary_weapon_oxpecker", "weapon_talent_symbiosis"), primary);
        CloseTo(o.Of("estimated_shield_repair_per_second"), 0.25 * o.Of("equipped_weapon_practical_firing_dps"), 1e-6);

        var d = Stats(With("primary_weapon", "primary_weapon_doctor_home", "weapon_talent_doctor_home"), primary);
        // R43 candidate 3: Doctor Home's 50% is its kit's own preview stat, not generic repair per kill, which the kit rule
        // would otherwise repeat in fights.
        CloseTo(d.Of("doctor_home_kit_repair_percent"), 0.5, 1e-12);
        Assert.Equal((0.0, 0.0), (d.Of("estimated_armor_repair_per_kill"), d.Of("estimated_kill_repair_per_second")));

        var edge = With("chest", "chest_ridgeway_s_pride", "gear_talent_bleeding_edge");
        var plain = Stats(edge).Of("armor_regeneration_percent");
        foreach (var (enemies, want) in new[] { ("1", 0.03), ("3", 0.12), ("5", 0.48) })
        {
            var got = Stats(edge, Context(("bleeding_edge_bleeding_enemies", enemies))).Of("armor_regeneration_percent");
            CloseTo(got - plain, want, 1e-12);
        }

        var bob = Stats(With("holster", "holster_nimble_holster", "gear_talent_bob_and_weave"), Context(("bob_and_weave_meters", "4")));
        CloseTo(bob.Of("estimated_armor_repair_per_cover_move"), 0.2 * bob.Of("character_armor"), 1e-6);
    }

    /// <summary>G118: repair-event amounts are fractions of maximum armor, and a negative maximum repairs nothing.</summary>
    [Fact]
    public void G118RepairAmountsAndMaximumGuard()
    {
        foreach (var maximum in new[] { 1000000.0, -100 })
        {
            var compiled = WithFormulas(("character_armor", maximum), ("armor_repair_on_depletion_percent", .95), ("armor_repair_on_headshot_percent", .2),
                ("armor_repair_on_kill_over_time_percent", .3), ("incoming_repairs", .5), ("repair_skills", .5), ("skill_efficiency", .5));
            var r = BuildResolver.Resolve(compiled, BuildSnapshot.Create(compiled.Pack, "Repair budgets")).Calculation.Stats;
            foreach (var (repairEvent, amount) in new[] { ("depletion", 950000.0), ("headshot", 200000), ("kill_over_time", 300000) })
            {
                CloseTo(r.Of("estimated_armor_repair_on_" + repairEvent), maximum < 0 ? 0 : amount, 1e-8);
            }
            CloseTo(r.Of("estimated_armor_on_kill"), 0, 1e-8);
        }
    }

    /// <summary>G118: Unbreakable, Entrench and Preservation repair only when selected, ready and on the active weapon.</summary>
    [Fact]
    public void G118SelectedTalentsAndWeaponOwnership()
    {
        var b = Build().WithEquipment("chest", "chest_belstone_armory_chest")
            .WithAttributeRoll("chest", "core_1", new("armor_gear_core", 170000));
        foreach (var (talent, flag, repairEvent, amount) in new[] { ("unbreakable", "unbreakable_ready", "depletion", 311600.0), ("entrench", "entrench_ready", "headshot", 65600) })
        {
            var selected = b.WithTalentSelection("chest", "talent", "gear_talent_" + talent);
            var id = "estimated_armor_repair_on_" + repairEvent;
            CloseTo(Stats(selected).Of(id), 0, 1e-8);
            CloseTo(Stats(selected, Context((flag, "true"))).Of(id), amount, 1e-8);
            CloseTo(Stats(selected.WithTalentSelection("chest", "talent", ""), Context((flag, "true"))).Of(id), 0, 1e-8);
        }
        b = b.WithEquipment("primary_weapon", "primary_weapon_acr")
            .WithEquipment("secondary_weapon", "secondary_weapon_acr")
            .WithTalentSelection("primary_weapon", "talent", "weapon_talent_preservation");
        foreach (var (kill, amount) in new[] { ("body", 32800.0), ("headshot", 65600) })
        {
            var r = Stats(b, Context((GamePack.WeaponSlotSelector, "primary_weapon"), ("preservation_kill_type", kill)));
            CloseTo(r.Of("estimated_armor_repair_on_kill_over_time"), amount, 1e-8);
            CloseTo(r.Of("estimated_armor_on_kill"), 0, 1e-8);
            CloseTo(Stats(b, Context((GamePack.WeaponSlotSelector, "secondary_weapon"), ("preservation_kill_type", kill))).Of("estimated_armor_repair_on_kill_over_time"), 0, 1e-8);
        }
    }

    /// <summary>
    /// G149: Umbra Initiative's Shadow stacks give a repair budget, delivery time and rate, capped by From the Shadows
    /// and Into the Light, rejecting invalid stack counts, and changing no other stat or skill preview.
    /// </summary>
    [Fact]
    public void G149UmbraShadowRepairEstimates()
    {
        const string key = "umbra_shadow_stacks_to_consume";
        static void Check(BuildSnapshot b, string input, double stacks)
        {
            var r = Stats(b, Context((key, input)));
            CloseTo(r.Of("umbra_shadow_repair_budget"), r.Of("character_armor") * .008 * stacks, 1e-8);
            CloseTo(r.Of("umbra_shadow_delivery_time"), stacks / 10, 1e-8);
            CloseTo(r.Of("umbra_shadow_average_repair_rate"), stacks > 0 ? r.Of("character_armor") * .08 : 0, 1e-8);
        }
        foreach (var pieces in new[] { 3, 4, 5 })
        {
            var setBuild = SetBuild("gear_set_umbra_initiative", pieces);
            Check(setBuild, "25", pieces >= 4 ? 25 : 0);
            Check(setBuild, "0", 0);
            CloseTo(Stats(setBuild).Of("umbra_shadow_repair_budget"), 0, 1e-12);
        }
        var b = SetBuild("gear_set_umbra_initiative", 4);
        foreach (var chest in new[] { false, true })
        {
            foreach (var backpack in new[] { false, true })
            {
                var selected = b;
                var cap = 50.0;
                if (chest) { selected = SelectUpgrade(selected, "chest", "gear_talent_from_the_shadows"); }
                if (backpack)
                {
                    selected = SelectUpgrade(selected, "backpack", "gear_talent_into_the_light");
                    cap = 100;
                }
                foreach (var stacks in new[] { 0, 1, 9, 10, 25, 49, 50, 51, 75, 100 })
                {
                    Check(selected, stacks.ToString(CultureInfo.InvariantCulture), Math.Min(stacks, cap));
                }
            }
        }
        foreach (var input in new[] { "-1", "101", "NaN", "1.5", "Infinity", "bad" })
        {
            Check(b, input, 0);
        }
        Check(b.WithTalentSelection("chest", "talent", "gear_talent_into_the_light"), "100", 50);
        var ninja = b.WithEquipment("backpack", "backpack_ninjabike_messenger_backpack")
            .WithTalentSelection("backpack", "talent", "gear_talent_resourceful");
        Check(ninja, "100", 50);
        Check(ninja.WithTalentSelection("backpack", "talent", ""), "100", 0);
        var armored = b.WithAttributeRoll("mask", "core_1", new("armor_gear_core", 170000))
            .WithAccountUpgrade("watch_total_armor", 50)
            .WithEquipmentUpgrade("mask", "mask_umbra_initiative_mask", 10);
        Check(armored, "25", 25);
        foreach (var skill in Compiled.Pack.ActiveSkills)
        {
            var selected = b.WithActiveSkillSelection("skill_slot_1", skill.ID);
            ResolvedBuild off = Resolve(selected), on = Resolve(selected, Context((key, "50")));
            Assert.True(SamePreviews(off.ActiveSkillPreviews, on.ActiveSkillPreviews), $"changed skill {skill.ID}");
            foreach (var id in new[] { "character_armor", "bonus_armor_percent", "estimated_armor_regeneration", "effective_repair_skills", "equipped_weapon_damage", "umbra_shadow_full_stack_repair_capacity", "critical_hit_damage", "rate_of_fire" })
            {
                CloseTo(on.Calculation.Stats.Of(id), off.Calculation.Stats.Of(id), 1e-12);
            }
        }
    }

    /// <summary>
    /// G146: Refactor's Return to Sender and Future Initiative's Ground Control budgets need four pieces and their
    /// input, double or more with the chest or backpack upgrade, reject invalid inputs and change no skill preview.
    /// </summary>
    [Fact]
    public void G146SupportRepairBudgetsThresholdUpgradesAndIsolation()
    {
        var cases = new[]
        {
            (Set: "refactor", Key: "return_to_sender_skill_damage", Slot: "chest", Talent: "increased_interest",
                Base: new Dictionary<string, double> { ["return_to_sender_self_budget"] = 10000, ["return_to_sender_ally_budget"] = 20000 },
                Upgraded: new Dictionary<string, double> { ["return_to_sender_self_budget"] = 25000, ["return_to_sender_ally_budget"] = 35000 }),
            (Set: "future_initiative", Key: "ground_control_eligible_ally_repair", Slot: "backpack", Talent: "advanced_combat_tactics",
                Base: new Dictionary<string, double> { ["ground_control_proximity_repair_budget"] = 60000 },
                Upgraded: new Dictionary<string, double> { ["ground_control_proximity_repair_budget"] = 120000 }),
        };
        foreach (var tc in cases)
        {
            foreach (var pieces in new[] { 3, 4, 5 })
            {
                var setBuild = SetBuild("gear_set_" + tc.Set, pieces);
                foreach (var (id, budget) in tc.Base)
                {
                    CloseTo(Stats(setBuild, Context((tc.Key, "100000"))).Of(id), pieces < 4 ? 0 : budget, 1e-10);
                    CloseTo(Stats(setBuild).Of(id), 0, 1e-12);
                }
            }
            var b = SetBuild("gear_set_" + tc.Set, 4);
            var upgraded = SelectUpgrade(b, tc.Slot, "gear_talent_" + tc.Talent);
            foreach (var (id, budget) in tc.Base)
            {
                var r = Resolve(upgraded, Context((tc.Key, "100000")));
                CloseTo(r.Calculation.Stats.Of(id), tc.Upgraded[id], 1e-10);
                ExpectSetValue(r, id, "gear_set_" + tc.Set, tc.Upgraded[id]);
                foreach (var full in new[] { "true", "false" })
                {
                    CloseTo(Stats(b, Context((tc.Key, "100000"), ("full_armor", full))).Of(id), budget, 1e-10);
                }
                foreach (var value in new[] { "-1", "10000001", "NaN", "1.5", "Infinity" })
                {
                    CloseTo(Stats(b, Context((tc.Key, value))).Of(id), 0, 1e-12);
                }
                CloseTo(Stats(b, Context((tc.Key, "10000000"))).Of(id), budget * 100, 1e-9);
            }
            foreach (var skill in Compiled.Pack.ActiveSkills)
            {
                var selected = b.WithActiveSkillSelection("skill_slot_1", skill.ID);
                ResolvedBuild off = Resolve(selected), on = Resolve(selected, Context((tc.Key, "100000")));
                Assert.True(SamePreviews(off.ActiveSkillPreviews, on.ActiveSkillPreviews), $"changed skill {skill.ID}");
                foreach (var id in new[] { "character_armor", "estimated_armor_regeneration", "bonus_armor_percent", "effective_repair_skills", "total_weapon_damage", "total_skill_damage" })
                {
                    CloseTo(on.Calculation.Stats.Of(id), off.Calculation.Stats.Of(id), 1e-12);
                }
            }
        }
    }

    /// <summary>
    /// G136: Ground Control's proximity repair needs four Future Initiative pieces and doubles only with Advanced
    /// Combat Tactics selected on a Future Initiative backpack.
    /// </summary>
    [Fact]
    public void G136GroundControlRepairSelectionAndHosts()
    {
        const string id = "ground_control_proximity_repair_percent";
        foreach (var pieces in new[] { 3, 4, 5 })
        {
            var b = SetBuild("gear_set_future_initiative", pieces);
            double threshold = pieces >= 4 ? .6 : 0, upgraded = pieces >= 4 ? 1.2 : 0;
            CloseTo(Stats(b).Of(id), threshold, 1e-12);
            b = SelectUpgrade(b, "backpack", "gear_talent_advanced_combat_tactics");
            CloseTo(Stats(b).Of(id), upgraded, 1e-12);
            b = b.WithTalentSelection("backpack", "talent", "");
            CloseTo(Stats(b).Of(id), threshold, 1e-12);
        }
        var other = SelectUpgrade(SetBuild("gear_set_future_initiative", 5), "backpack", "gear_talent_advanced_combat_tactics")
            .WithEquipment("backpack", "backpack_belstone_armory_backpack");
        CloseTo(Stats(other).Of(id), .6, 1e-12);
        var chest = SetBuild("gear_set_future_initiative", 4).WithTalentSelection("chest", "talent", "gear_talent_advanced_combat_tactics");
        CloseTo(Stats(chest).Of(id), .6, 1e-12);
    }

    /// <summary>G136: Advanced Combat Tactics changes Ground Control's proximity repair and no other stat.</summary>
    [Fact]
    public void G136GroundControlRepairIsIndependentOfBuffEffects()
    {
        const string id = "ground_control_proximity_repair_percent";
        var b = SelectWeapon(SetBuild("gear_set_future_initiative", 4), "weapon_type_assault_rifle");
        b = SelectUpgrade(b, "chest", "gear_talent_tactical_superiority");
        foreach (var branch in new[] { "", "true", "invalid" })
        {
            var ctx = Context((GamePack.WeaponSlotSelector, "primary_weapon"), ("full_armor", branch));
            var baseline = Resolve(b, ctx);
            var active = Resolve(SelectUpgrade(b, "backpack", "gear_talent_advanced_combat_tactics"), ctx);
            CloseTo(active.Calculation.Stats.Of(id), 1.2, 1e-12);
            foreach (var (stat, value) in baseline.Calculation.Stats)
            {
                if (stat != id) { CloseTo(active.Calculation.Stats.Of(stat), value, 1e-12); }
            }
            ExpectSetValue(active, id, "gear_set_future_initiative", 1.2);
        }
    }

    /// <summary>
    /// G124: Clutch and Perfect Clutch convert critical hits into a share of missing armor in either eligible state,
    /// only with both the state and the missing armor given, and the assumptions don't change the build.
    /// </summary>
    [Fact]
    public void G124ClutchEligibleMissingArmorConversion()
    {
        foreach (var talent in new[] { "clutch", "perfect_clutch" })
        {
            var host = talent == "perfect_clutch" ? "backpack_devil_s_due" : "backpack_belstone_armory_backpack";
            var b = BuildSnapshot.Create(Compiled.Pack, "Clutch").WithEquipment("backpack", host)
                .WithAttributeRoll("backpack", "core_1", new("armor_gear_core", 170000))
                .WithTalentSelection("backpack", "talent", "gear_talent_" + talent);
            var code = ShareCode.Encode(b);
            var maximum = Stats(b).Of("character_armor");
            foreach (var state in new[] { "low_armor", "post_kill" })
            {
                foreach (var missing in new[] { 0, 50, 90, 100 })
                {
                    var r = Resolve(b, Context(("clutch_repair_state", state), ("clutch_missing_armor_percent", missing.ToString(CultureInfo.InvariantCulture))));
                    CloseTo(r.Calculation.Stats.Of("estimated_armor_repair_on_crit"), maximum * missing / 100 * .025, 1e-8);
                    CloseTo(r.Calculation.Stats.Of("missing_armor_repair_on_crit_percent"), .025, 1e-12);
                    CloseTo(r.Calculation.Stats.Of("character_armor"), maximum, 1e-8);
                    Assert.DoesNotContain(r.Diagnostics, diagnostic => diagnostic.Severity == "error");
                }
            }
            foreach (var ctx in new[] { Context(("clutch_repair_state", "post_kill")), Context(("clutch_missing_armor_percent", "90")), Context(("clutch_repair_state", "low_armor,post_kill"), ("clutch_missing_armor_percent", "90")) })
            {
                CloseTo(Stats(b, ctx).Of("estimated_armor_repair_on_crit"), 0, 1e-8);
            }
            var cleared = b.WithTalentSelection("backpack", "talent", "");
            CloseTo(Stats(cleared, Context(("clutch_repair_state", "post_kill"), ("clutch_missing_armor_percent", "90"))).Of("estimated_armor_repair_on_crit"), 0, 1e-8);
            Assert.True(code == ShareCode.Encode(b), "view assumption changed build");
        }
    }

    /// <summary>G124: Clutch rejects invalid missing-armor inputs with a diagnostic, and skill repair doesn't scale it.</summary>
    [Fact]
    public void G124ClutchRejectsInvalidInputsAndExcludesSkillRepair()
    {
        var compiled = WithFormulas(("incoming_repairs", 5), ("repair_skills", 5), ("skill_efficiency", 5));
        var b = BuildSnapshot.Create(compiled.Pack, "Clutch").WithEquipment("backpack", "backpack_belstone_armory_backpack")
            .WithTalentSelection("backpack", "talent", "gear_talent_clutch");
        var r = BuildResolver.Resolve(compiled, b, Context(("clutch_repair_state", "post_kill"), ("clutch_missing_armor_percent", "50")));
        CloseTo(r.Calculation.Stats.Of("estimated_armor_repair_on_crit"), r.Calculation.Stats.Of("character_armor") * .0125, 1e-8);
        foreach (var value in new[] { "-1", "101", "50.5", "NaN", "" })
        {
            var invalid = BuildResolver.Resolve(compiled, b, Context(("clutch_repair_state", "post_kill"), ("clutch_missing_armor_percent", value)));
            CloseTo(invalid.Calculation.Stats.Of("estimated_armor_repair_on_crit"), 0, 1e-8);
            Assert.True(invalid.Diagnostics.Any(diagnostic => diagnostic.Code == "context-value-invalid" && diagnostic.Subject == "clutch_missing_armor_percent"),
                $"missing diagnostic for \"{value}\": {string.Join("; ", invalid.Diagnostics)}");
        }
    }

    /// <summary>The pack with some stat formulas replaced by constants, as Go's tests did by editing Pack.Stats.</summary>
    private static CompiledPackExpressions WithFormulas(params (string Stat, double Value)[] values)
    {
        var replaced = values.ToDictionary(value => value.Stat, value => value.Value.ToString("R", CultureInfo.InvariantCulture), StringComparer.Ordinal);
        var pack = Compiled.Pack with
        {
            Stats = [.. Compiled.Pack.Stats.Select(stat => replaced.TryGetValue(stat.ID, out var formula) ? stat with { Formula = formula } : stat)],
        };
        var result = PackValidator.Validate(pack);
        return result.Compiled ?? throw new InvalidOperationException($"invalid fixture: {string.Join("; ", result.Diagnostics)}");
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

    /// <summary>The first primary weapon with a tag, as Go's g54SelectWeapon (no pistol tag is used here).</summary>
    private static BuildSnapshot SelectWeapon(BuildSnapshot b, string tag)
    {
        var item = Compiled.Pack.Items.FirstOrDefault(item => item.Slot == "primary_weapon" && item.Tags.Contains(tag))
            ?? throw new InvalidOperationException($"missing primary_weapon weapon tagged {tag}");
        return b.WithEquipment("primary_weapon", item.ID);
    }

    /// <summary>A stat's four-piece contribution from a set, as Go's g54ExpectSetValue.</summary>
    private static void ExpectSetValue(ResolvedBuild result, string stat, string setId, double want)
    {
        var contribution = result.Calculation.Breakdowns.TryGetValue(stat, out var breakdown)
            ? breakdown.Contributions.FirstOrDefault(contribution => contribution.Source.Kind == "set" && contribution.Source.Id == setId && contribution.Source.Name.EndsWith("(4pc)", StringComparison.Ordinal))
            : null;
        Assert.True(contribution is not null, $"{stat} has no four-piece contribution from {setId}");
        CloseTo(contribution.Value, want, 1e-12);
    }

    // Go compared skill previews with reflect.DeepEqual; this compares the same values.
    private static bool SamePreviews(ImmutableDictionary<string, SkillPreview> a, ImmutableDictionary<string, SkillPreview> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && pair.Value.SkillId == other.SkillId
            && pair.Value.Outputs.Length == other.Outputs.Length && pair.Value.Outputs.Zip(other.Outputs).All(outputs => SameOutput(outputs.First, outputs.Second)));

    private static bool SameOutput(SkillPreviewOutput a, SkillPreviewOutput b) =>
        a.Id == b.Id && a.Name == b.Name && a.Description == b.Description && a.Format == b.Format && a.Formula == b.Formula && a.When == b.When
            && a.Value == b.Value && a.Kind == b.Kind && a.Inputs.Count == b.Inputs.Count && a.Inputs.All(pair => b.Inputs.TryGetValue(pair.Key, out var value) && value == pair.Value);
}
