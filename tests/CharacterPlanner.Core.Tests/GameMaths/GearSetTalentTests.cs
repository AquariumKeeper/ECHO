using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// Gear sets' four-piece talents and their named chest and backpack upgrades, with Ninjabike's credit (F11), ported
/// from Go's true_patriot_white_g126_test.go, true_patriot_blue_g127_test.go, true_patriot_rotation_g128_test.go,
/// symphony_both_g129_test.go, symphony_threshold_g130_test.go, feedback_cooldown_g131_test.go,
/// apex_properties_g132_test.go, on_point_duration_g133_test.go, crowd_properties_g134_test.go,
/// camaraderie_marks_g135_test.go, aces_shots_g137_test.go, symptom_g142_test.go, heatstroke_g143_test.go,
/// hackstep_g144_test.go, foundry_g145_test.go, hotshot_armor_g150_test.go, outer_core_g151_test.go,
/// core_exercise_g152_test.go, stoic_resistance_g153_test.go, hotshot_headshots_g155_test.go,
/// trauma_bleed_g156_test.go and combustion_burn_g157_test.go.
/// </summary>
public sealed class GearSetTalentTests
{
    private const string Virtuoso = "gear_set_virtuoso";

    /// <summary>The pack with some stat formulas replaced by constants, as Go's tests did by editing Pack.Stats.</summary>
    private static CompiledPackExpressions WithFormulas(params (string Stat, double Value)[] values)
    {
        var replaced = values.ToDictionary(value => value.Stat, value => value.Value.ToString("R", CultureInfo.InvariantCulture), StringComparer.Ordinal);
        var pack = Compiled.Pack with
        {
            Stats = [.. Compiled.Pack.Stats.Select(stat => replaced.TryGetValue(stat.ID, out var formula) ? stat with { Formula = formula } : stat)],
        };
        var result = PackValidator.Validate(pack);
        Assert.True(result.Compiled is not null, $"invalid fixture: {string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message))}");
        return result.Compiled;
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

    /// <summary>Go's g54WeaponSlot: pistols are sidearms.</summary>
    private static string WeaponSlotOf(string tag) => tag == "weapon_type_pistol" ? "sidearm" : "primary_weapon";

    /// <summary>The first weapon with a tag in its slot, as Go's g54SelectWeapon.</summary>
    private static BuildSnapshot SelectWeapon(BuildSnapshot b, string tag)
    {
        var slot = WeaponSlotOf(tag);
        var item = Compiled.Pack.Items.FirstOrDefault(item => item.Slot == slot && item.Tags.Contains(tag))
            ?? throw new InvalidOperationException($"missing {slot} weapon tagged {tag}");
        return b.WithEquipment(slot, item.ID);
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

    /// <summary>Whether a breakdown has a contribution from a source with a value, as Go's provenance loops.</summary>
    private static bool HasContribution(ResolvedBuild result, string stat, string sourceId, double value) =>
        result.Calculation.Breakdowns.TryGetValue(stat, out var breakdown) && breakdown.Contributions.Any(contribution => contribution.Source.Id == sourceId && contribution.Value == value);

    /// <summary>Every stat of the baseline unchanged except the named ones, as Go's loops over base.Stats.</summary>
    private static void SameStatsExcept(ImmutableDictionary<string, double> baseline, ImmutableDictionary<string, double> active, string label, params string[] except)
    {
        foreach (var (id, value) in baseline)
        {
            if (except.Contains(id)) { continue; }
            Assert.True(Math.Abs(active.Of(id) - value) <= 1e-12, $"{label}: {id} = {active.Of(id)}, want {value}");
        }
    }

    [Theory]
    [InlineData(2, false, 1)]
    [InlineData(2, true, 1)]
    [InlineData(3, true, 1)]
    [InlineData(4, false, 1)]
    [InlineData(4, true, 1.2)]
    public void VeerOverrideNeedsTheSetsFourPiecesAndItsTalent(int pieces, bool selected, double expected)
    {
        // R48: the source requires the set's 4-piece talent, so its bonus moved to the set's top row, under has_talent.
        var build = SetBuild("gear_set_apex_protocol", pieces);
        if (selected) { build = SelectUpgrade(build, "chest", "gear_talent_veer_override"); }
        var stats = Stats(build, Context(("veer_override_mark_active", "true"), ("apex_engine_bonus_stacks", "3")));
        CloseTo(stats.Of("weapon_damage_multiplier"), expected, 1e-12);
        CloseTo(stats.Of("skill_damage_multiplier"), expected, 1e-12);
    }

    [Theory]
    [InlineData(3, 0, 0)]
    [InlineData(4, 0.3, 0.15)]
    public void VeerFailsafeNeedsTheSetsFourPiecesAndItsTalent(int pieces, double skillDamage, double armorOnKill)
    {
        // Each stack gives 10% skill damage and 5% armor on kill, only with the talent on a full set; the same build without the
        // talent is the baseline. It's gated apart from Veer Override, which is on the chest.
        var stacks = Context(("apex_engine_bonus_stacks", "3"));
        var without = Stats(SetBuild("gear_set_apex_protocol", pieces), stacks);
        var with = Stats(SelectUpgrade(SetBuild("gear_set_apex_protocol", pieces), "backpack", "gear_talent_veer_failsafe"), stacks);
        CloseTo(with.Of("skill_damage") - without.Of("skill_damage"), skillDamage, 1e-12);
        CloseTo(with.Of("armor_on_kill_percent") - without.Of("armor_on_kill_percent"), armorOnKill, 1e-12);
    }

    /// <summary>Go's isDamagePreviewChannel.</summary>
    private static bool IsDamageChannel(string id) => id is "damage" or "average_damage" or "cloud_damage" or "burn_damage" or "bleed_damage" or "explosion_damage" or "armor_damage";

    /// <summary>The first skill slot's preview outputs, empty without a preview, as Go's read of a nil map entry.</summary>
    private static ImmutableArray<SkillPreviewOutput> Outputs(ResolvedBuild result) =>
        result.ActiveSkillPreviews.TryGetValue("skill_slot_1", out var preview) ? preview.Outputs : [];

    // Go compared skill previews with reflect.DeepEqual; this compares the same values.
    private static bool SamePreviews(ImmutableDictionary<string, SkillPreview> a, ImmutableDictionary<string, SkillPreview> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && pair.Value.SkillId == other.SkillId
            && pair.Value.Outputs.Length == other.Outputs.Length && pair.Value.Outputs.Zip(other.Outputs).All(outputs => SameOutput(outputs.First, outputs.Second)));

    private static bool SameOutput(SkillPreviewOutput a, SkillPreviewOutput b) =>
        a.Id == b.Id && a.Name == b.Name && a.Description == b.Description && a.Format == b.Format && a.Formula == b.Formula && a.When == b.When
            && a.Value == b.Value && a.Kind == b.Kind && a.Inputs.Count == b.Inputs.Count && a.Inputs.All(pair => b.Inputs.TryGetValue(pair.Key, out var value) && value == pair.Value);

    /// <summary>Go's g126Patriot: the first True Patriot item in each of the first slots of mask, chest, backpack and gloves.</summary>
    private static BuildSnapshot Patriot(int pieces)
    {
        var b = BuildSnapshot.Create(Compiled.Pack, "White repair");
        foreach (var slot in new[] { "mask", "chest", "backpack", "gloves" }.Take(pieces))
        {
            var item = Compiled.Pack.Items.FirstOrDefault(item => item.Slot == slot && item.ID.Contains("true_patriot", StringComparison.Ordinal));
            if (item is not null) { b = b.WithEquipment(item.Slot, item.ID); }
        }
        return b;
    }

    /// <summary>G126: True Patriot's White repair needs four pieces and the ready flag, and Patriotic Boost raises it.</summary>
    [Fact]
    public void G126WhiteRepairThresholdReadyAndUpgrade()
    {
        foreach (var pieces in new[] { 3, 4 })
        {
            var b = Patriot(pieces);
            CloseTo(Stats(b).Of("estimated_true_patriot_white_repair"), 0, 1e-8);
            foreach (var weapon in new[] { "", "primary_weapon", "secondary_weapon" })
            {
                var r = Stats(b, Context(("true_patriot_white_ready", "true"), ("weapon_slot", weapon)));
                var fraction = pieces == 4 ? .02 : 0;
                CloseTo(r.Of("true_patriot_white_repair_percent"), fraction, 1e-12);
                CloseTo(r.Of("estimated_true_patriot_white_repair"), r.Of("character_armor") * fraction, 1e-8);
                CloseTo(r.Of("estimated_armor_regeneration"), 0, 1e-8);
                CloseTo(r.Of("weapon_damage_multiplier"), 1, 1e-8);
            }
        }
        var boosted = Patriot(4).WithTalentSelection("backpack", "talent", "gear_talent_patriotic_boost");
        var ctx = Context(("true_patriot_white_ready", "true"));
        var result = Resolve(boosted, ctx);
        CloseTo(result.Calculation.Stats.Of("true_patriot_white_repair_percent"), .05, 1e-12);
        CloseTo(result.Calculation.Stats.Of("estimated_true_patriot_white_repair"), result.Calculation.Stats.Of("character_armor") * .05, 1e-8);
        Assert.True(HasContribution(result, "true_patriot_white_repair_percent", "gear_set_true_patriot", .05), "missing White set provenance");
        CloseTo(Stats(boosted.WithTalentSelection("backpack", "talent", ""), ctx).Of("true_patriot_white_repair_percent"), .02, 1e-12);
        CloseTo(Stats(boosted.WithEquipment("gloves", ""), ctx).Of("true_patriot_white_repair_percent"), 0, 1e-12);
        CloseTo(Stats(boosted, Context(("true_patriot_red_active", "true"))).Of("estimated_true_patriot_white_repair"), 0, 1e-8);
    }

    /// <summary>G126: a negative maximum armor repairs nothing, and skill repair bonuses don't scale White.</summary>
    [Fact]
    public void G126WhiteRepairMaximumGuardAndSkillExclusion()
    {
        foreach (var maximum in new[] { -100.0, 1000000 })
        {
            var compiled = WithFormulas(("character_armor", maximum), ("incoming_repairs", 5), ("repair_skills", 5), ("skill_efficiency", 5));
            var r = BuildResolver.Resolve(compiled, Patriot(4), Context(("true_patriot_white_ready", "true"))).Calculation.Stats;
            CloseTo(r.Of("estimated_true_patriot_white_repair"), maximum > 0 ? 20000 : 0, 1e-8);
            CloseTo(r.Of("character_armor"), maximum, 1e-8);
        }
    }

    /// <summary>G127: Blue's damage reduction against the target needs four pieces, and Patriotic Boost doubles it without changing other stats.</summary>
    [Fact]
    public void G127BlueThresholdTargetAndUpgrade()
    {
        foreach (var pieces in new[] { 3, 4 })
        {
            var b = Patriot(pieces);
            CloseTo(Stats(b).Of("true_patriot_blue_enemy_damage_factor"), 1, 1e-12);
            foreach (var weapon in new[] { "", "primary_weapon", "secondary_weapon" })
            {
                var r = Stats(b, Context(("true_patriot_blue_target", "true"), ("weapon_slot", weapon)));
                var (fraction, factor) = pieces == 4 ? (.1, .9) : (0.0, 1.0);
                CloseTo(r.Of("true_patriot_blue_reduction"), fraction, 1e-12);
                CloseTo(r.Of("true_patriot_blue_enemy_damage_factor"), factor, 1e-12);
                CloseTo(r.Of("incoming_damage_multiplier"), 1, 1e-12);
            }
        }
        var boosted = Patriot(4).WithTalentSelection("backpack", "talent", "gear_talent_patriotic_boost");
        var baseline = Stats(boosted, Context(("true_patriot_red_active", "true"), ("true_patriot_white_ready", "true")));
        var ctx = Context(("true_patriot_red_active", "true"), ("true_patriot_white_ready", "true"), ("true_patriot_blue_target", "true"));
        var result = Resolve(boosted, ctx);
        CloseTo(result.Calculation.Stats.Of("true_patriot_blue_enemy_damage_factor"), .8, 1e-12);
        SameStatsExcept(baseline, result.Calculation.Stats, "Blue target", "true_patriot_blue_reduction", "true_patriot_blue_enemy_damage_factor");
        Assert.True(HasContribution(result, "true_patriot_blue_reduction", "gear_set_true_patriot", .2), "missing Blue set provenance");
        CloseTo(Stats(boosted.WithTalentSelection("backpack", "talent", ""), ctx).Of("true_patriot_blue_enemy_damage_factor"), .9, 1e-12);
        if (Compiled.Pack.Items.Any(item => item.ID == "holster_true_patriot_holster")) { boosted = boosted.WithEquipment("holster", "holster_true_patriot_holster"); }
        boosted = boosted.WithEquipment("backpack", "backpack_belstone_armory_backpack");
        CloseTo(Stats(boosted, ctx).Of("true_patriot_blue_enemy_damage_factor"), .9, 1e-12);
    }

    /// <summary>G127: Blue's factor is bounded to [0, 1], and incoming damage is left alone.</summary>
    [Fact]
    public void G127BlueBoundsAndIncomingIsolation()
    {
        foreach (var (reduction, factor) in new[] { (-.1, 1.0), (.1, .9), (1.5, 0) })
        {
            var compiled = WithFormulas(("true_patriot_blue_reduction", reduction), ("incoming_damage_multiplier", 1.5));
            var r = BuildResolver.Resolve(compiled, BuildSnapshot.Create(compiled.Pack, "Blue bounds")).Calculation.Stats;
            CloseTo(r.Of("true_patriot_blue_enemy_damage_factor"), factor, 1e-12);
            CloseTo(r.Of("incoming_damage_multiplier"), 1.5, 1e-12);
        }
    }

    /// <summary>G128: the rotation interval needs four pieces, and Waving the Flag halves it only on its chest host.</summary>
    [Fact]
    public void G128RotationThresholdAndSelectedHost()
    {
        foreach (var pieces in new[] { 3, 4 })
        {
            var b = Patriot(pieces);
            CloseTo(Stats(b).Of("true_patriot_rotation_interval"), pieces == 4 ? 2 : 0, 1e-12);
            b = b.WithTalentSelection("chest", "talent", "gear_talent_waving_the_flag");
            CloseTo(Stats(b).Of("true_patriot_rotation_interval"), pieces == 4 ? 1 : 0, 1e-12);
        }
        var replaced = Patriot(4).WithTalentSelection("chest", "talent", "gear_talent_waving_the_flag")
            .WithEquipment("holster", "holster_true_patriot_holster")
            .WithEquipment("chest", "chest_belstone_armory_chest");
        CloseTo(Stats(replaced).Of("true_patriot_rotation_interval"), 2, 1e-12);
        CloseTo(Stats(Patriot(4).WithTalentSelection("backpack", "talent", "gear_talent_waving_the_flag")).Of("true_patriot_rotation_interval"), 2, 1e-12);
    }

    /// <summary>G128: the rotation interval doesn't activate or strengthen the debuffs.</summary>
    [Fact]
    public void G128RotationDoesNotActivateOrStrengthenDebuffs()
    {
        var b = Patriot(4).WithTalentSelection("backpack", "talent", "gear_talent_patriotic_boost");
        foreach (var ctx in new[] { Context(), Context(("true_patriot_red_active", "true"), ("true_patriot_white_ready", "true"), ("true_patriot_blue_target", "true"), ("weapon_slot", "secondary_weapon")) })
        {
            var baseline = Stats(b, ctx);
            var selected = Resolve(b.WithTalentSelection("chest", "talent", "gear_talent_waving_the_flag"), ctx);
            CloseTo(selected.Calculation.Stats.Of("true_patriot_rotation_interval"), 1, 1e-12);
            SameStatsExcept(baseline, selected.Calculation.Stats, "Waving the Flag", "true_patriot_rotation_interval");
            Assert.True(HasContribution(selected, "true_patriot_rotation_interval", "gear_set_true_patriot", 1), "missing rotation set provenance");
        }
    }

    /// <summary>G129: Symphony's "both" branch gives both range buffs for every weapon class, and Fortissimo doubles the damage.</summary>
    [Fact]
    public void G129SimultaneousSymphonyClassesAndFortissimo()
    {
        foreach (var tag in new[] { "weapon_type_assault_rifle", "weapon_type_lmg", "weapon_type_rifle", "weapon_type_marksman_rifle", "weapon_type_shotgun", "weapon_type_smg", "weapon_type_pistol" })
        {
            foreach (var upgrade in new[] { false, true })
            {
                var b = SelectWeapon(SetBuild(Virtuoso, 4), tag);
                if (upgrade) { b = SelectUpgrade(b, "chest", "gear_talent_fortissimo"); }
                var baseline = Stats(b, Weapon(WeaponSlotOf(tag)));
                var active = Resolve(b, Context(("weapon_slot", WeaponSlotOf(tag)), ("symphony_range_buff", "both")));
                var a = active.Calculation.Stats;
                var damage = upgrade ? 1.2 : .6;
                CloseTo(a.Of("weapon_damage") - baseline.Of("weapon_damage"), damage, 1e-12);
                CloseTo(a.Of("headshot_damage") - baseline.Of("headshot_damage"), .45, 1e-12);
                CloseTo(a.Of("bonus_armor_percent") - baseline.Of("bonus_armor_percent"), .375, 1e-12);
                CloseTo(a.Of("estimated_bonus_armor"), a.Of("character_armor") * .375, 1e-8);
                ExpectSetValue(active, "weapon_damage", Virtuoso, damage);
            }
        }
    }

    /// <summary>G129: the damage needs four pieces and a weapon in hand, and the range branches are exclusive.</summary>
    [Fact]
    public void G129SimultaneousSymphonyThresholdOwnershipAndExclusiveBranches()
    {
        foreach (var pieces in new[] { 3, 4 })
        {
            var b = SelectWeapon(SetBuild(Virtuoso, pieces), "weapon_type_assault_rifle");
            var baseline = Stats(b);
            foreach (var weapon in new[] { "", "secondary_weapon" })
            {
                var active = Stats(b, Context(("weapon_slot", weapon), ("symphony_range_buff", "both")));
                CloseTo(active.Of("weapon_damage"), baseline.Of("weapon_damage"), 1e-12);
                var (hsd, armor) = pieces == 4 ? (.45, .375) : (0.0, 0.0);
                CloseTo(active.Of("headshot_damage") - baseline.Of("headshot_damage"), hsd, 1e-12);
                CloseTo(active.Of("bonus_armor_percent") - baseline.Of("bonus_armor_percent"), armor, 1e-12);
            }
            foreach (var branch in new[] { "", "invalid", "within", "beyond", "both" })
            {
                var damage = pieces != 4 ? 0 : branch == "both" ? .6 : branch is "within" or "beyond" ? .2 : 0;
                var active = Stats(b, Context(("weapon_slot", "primary_weapon"), ("symphony_range_buff", branch)));
                CloseTo(active.Of("weapon_damage") - baseline.Of("weapon_damage"), damage, 1e-12);
            }
        }
        var stale = SelectUpgrade(SetBuild(Virtuoso, 5), "chest", "gear_talent_fortissimo").WithEquipment("chest", "chest_belstone_armory_chest");
        stale = SelectWeapon(stale, "weapon_type_assault_rifle");
        ExpectSetValue(Resolve(stale, Context(("weapon_slot", "primary_weapon"), ("symphony_range_buff", "both"))), "weapon_damage", Virtuoso, .6);
    }

    /// <summary>
    /// The threshold-and-host pattern Go repeated per set (G130, G131, G133, G135): a four-piece value, its upgrade
    /// while selected on the named host, and the plain value for a stale host or a wrong slot.
    /// </summary>
    private static void ExpectThresholdAndHosts(string setId, string stat, string slot, string talent, string replacement, double threshold, double upgraded)
    {
        foreach (var pieces in new[] { 3, 4, 5 })
        {
            var b = SetBuild(setId, pieces);
            var (plain, selected) = pieces >= 4 ? (threshold, upgraded) : (0, 0);
            CloseTo(Stats(b).Of(stat), plain, 1e-12);
            b = SelectUpgrade(b, slot, talent);
            CloseTo(Stats(b).Of(stat), selected, 1e-12);
            b = b.WithTalentSelection(slot, "talent", "");
            CloseTo(Stats(b).Of(stat), plain, 1e-12);
        }
        var stale = SelectUpgrade(SetBuild(setId, 5), slot, talent).WithEquipment(slot, replacement);
        CloseTo(Stats(stale).Of(stat), threshold, 1e-12);
        var wrong = SetBuild(setId, 4).WithTalentSelection(slot == "chest" ? "backpack" : "chest", "talent", talent);
        CloseTo(Stats(wrong).Of(stat), threshold, 1e-12);
    }

    /// <summary>G130: Symphony's trigger stacks need four pieces, and Accelerando lowers them only on its backpack host.</summary>
    [Fact]
    public void G130SymphonyThresholdSelectionAndHosts() =>
        ExpectThresholdAndHosts(Virtuoso, "symphony_trigger_stacks", "backpack", "gear_talent_accelerando", "backpack_belstone_armory_backpack", 4, 3);

    /// <summary>G130: Accelerando changes the trigger stacks only, whichever range branch is active.</summary>
    [Fact]
    public void G130SymphonyThresholdIsIndependentOfBuffEffects()
    {
        var b = SelectUpgrade(SelectWeapon(SetBuild(Virtuoso, 4), "weapon_type_assault_rifle"), "chest", "gear_talent_fortissimo");
        foreach (var branch in new[] { "", "within", "beyond", "both" })
        {
            var ctx = Context(("weapon_slot", "primary_weapon"), ("symphony_range_buff", branch));
            var baseline = Stats(b, ctx);
            var active = Resolve(SelectUpgrade(b, "backpack", "gear_talent_accelerando"), ctx);
            CloseTo(active.Calculation.Stats.Of("symphony_trigger_stacks"), 3, 1e-12);
            SameStatsExcept(baseline, active.Calculation.Stats, $"branch \"{branch}\"", "symphony_trigger_stacks");
            ExpectSetValue(active, "symphony_trigger_stacks", Virtuoso, 3);
        }
    }

    /// <summary>G131: Feedback Loop's cooldown needs four pieces, and Short Circuit halves it only on its backpack host.</summary>
    [Fact]
    public void G131FeedbackCooldownSelectionAndHosts() =>
        ExpectThresholdAndHosts("gear_set_hard_wired", "feedback_loop_cooldown", "backpack", "gear_talent_short_circuit", "backpack_belstone_armory_backpack", 10, 5);

    /// <summary>G131: Short Circuit changes the cooldown only, whether or not Feedback Loop is active.</summary>
    [Fact]
    public void G131FeedbackCooldownIsIndependentOfBuffEffects()
    {
        var b = SelectUpgrade(SelectWeapon(SetBuild("gear_set_hard_wired", 4), "weapon_type_assault_rifle"), "chest", "gear_talent_positive_reinforcement");
        foreach (var branch in new[] { "", "true", "invalid" })
        {
            var ctx = Context(("weapon_slot", "primary_weapon"), ("feedback_loop_active", branch));
            var baseline = Stats(b, ctx);
            var active = Resolve(SelectUpgrade(b, "backpack", "gear_talent_short_circuit"), ctx);
            CloseTo(active.Calculation.Stats.Of("feedback_loop_cooldown"), 5, 1e-12);
            SameStatsExcept(baseline, active.Calculation.Stats, $"feedback_loop_active \"{branch}\"", "feedback_loop_cooldown");
            ExpectSetValue(active, "feedback_loop_cooldown", "gear_set_hard_wired", 5);
        }
    }

    /// <summary>G132: Apex Predator's stack duration and disorient radius need four pieces and their upgrades only on their hosts, and change nothing else.</summary>
    [Fact]
    public void G132ApexPropertiesThresholdHostsAndIsolation()
    {
        foreach (var (stat, slot, talent, replacement, threshold, upgraded) in new[]
        {
            ("apex_predator_stack_duration", "chest", "gear_talent_endless_hunger", "chest_belstone_armory_chest", 10.0, 30.0),
            ("apex_predator_disorient_radius", "backpack", "gear_talent_overwhelming_force", "backpack_belstone_armory_backpack", 5, 10),
        })
        {
            ExpectThresholdAndHosts("gear_set_hunter_s_fury", stat, slot, talent, replacement, threshold, upgraded);
            var b = SelectWeapon(SetBuild("gear_set_hunter_s_fury", 4), "weapon_type_assault_rifle");
            foreach (var flag in new[] { "", "true", "invalid" })
            {
                var ctx = Context(("weapon_slot", "primary_weapon"), ("hunter_fury_target_within_15m", flag));
                var baseline = Stats(b, ctx);
                var active = Resolve(SelectUpgrade(b, slot, talent), ctx);
                SameStatsExcept(baseline, active.Calculation.Stats, $"{talent}, flag \"{flag}\"", stat);
                ExpectSetValue(active, stat, "gear_set_hunter_s_fury", upgraded);
            }
        }
    }

    /// <summary>G133: On Point's buff duration needs four pieces, and Point of No Return doubles it only on its chest host.</summary>
    [Fact]
    public void G133OnPointDurationThresholdAndHosts() =>
        ExpectThresholdAndHosts("gear_set_breaking_point", "on_point_buff_duration", "chest", "gear_talent_point_of_no_return", "chest_belstone_armory_chest", 20, 40);

    /// <summary>G133: Point of No Return changes the duration only, not the buff amounts.</summary>
    [Fact]
    public void G133OnPointDurationDoesNotChangeBuffAmounts()
    {
        foreach (var tag in new[] { "weapon_type_rifle", "weapon_type_marksman_rifle", "weapon_type_smg" })
        {
            var b = SelectUpgrade(SelectWeapon(SetBuild("gear_set_breaking_point", 4), tag), "backpack", "gear_talent_point_of_honor");
            foreach (var flag in new[] { "", "true" })
            {
                foreach (var stacks in new[] { "0", "10", "200" })
                {
                    var ctx = Context(("weapon_slot", "primary_weapon"), ("on_point_active", flag), ("on_point_stacks", stacks));
                    var baseline = Stats(b, ctx);
                    var active = Resolve(SelectUpgrade(b, "chest", "gear_talent_point_of_no_return"), ctx);
                    SameStatsExcept(baseline, active.Calculation.Stats, $"{tag}, active \"{flag}\", stacks {stacks}", "on_point_buff_duration");
                    ExpectSetValue(active, "on_point_buff_duration", "gear_set_breaking_point", 40);
                }
            }
        }
    }

    /// <summary>G134: Negotiator's Dilemma's mark limit and transfer share need four pieces and their upgrades only on their hosts, and change nothing else.</summary>
    [Fact]
    public void G134CrowdPropertiesThresholdHostsAndIsolation()
    {
        foreach (var (stat, slot, talent, replacement, threshold, upgraded) in new[]
        {
            ("crowd_control_mark_limit", "chest", "gear_talent_target_rich_environment", "chest_belstone_armory_chest", 3.0, 5.0),
            ("crowd_control_transfer_percent", "backpack", "gear_talent_critical_measures", "backpack_belstone_armory_backpack", .6, 1.12),
        })
        {
            ExpectThresholdAndHosts("gear_set_negotiator_s_dilemma", stat, slot, talent, replacement, threshold, upgraded);
            var b = SelectWeapon(SetBuild("gear_set_negotiator_s_dilemma", 4), "weapon_type_assault_rifle");
            foreach (var stacks in new[] { "0", "5", "10" })
            {
                var ctx = Context(("weapon_slot", "primary_weapon"), ("negotiator_chd_stacks", stacks));
                var baseline = Stats(b, ctx);
                var active = Resolve(SelectUpgrade(b, slot, talent), ctx);
                SameStatsExcept(baseline, active.Calculation.Stats, $"{talent}, stacks {stacks}", stat);
                ExpectSetValue(active, stat, "gear_set_negotiator_s_dilemma", upgraded);
            }
        }
    }

    /// <summary>G135: Camaraderie's mark limit needs four pieces, and All for One doubles it only on its chest host.</summary>
    [Fact]
    public void G135CamaraderieCapacityThresholdAndHosts() =>
        ExpectThresholdAndHosts("gear_set_concentrated_company", "camaraderie_mark_limit", "chest", "gear_talent_all_for_one", "chest_belstone_armory_chest", 4, 8);

    /// <summary>G135: All for One changes the mark limit only, not the buff amounts.</summary>
    [Fact]
    public void G135CamaraderieCapacityDoesNotChangeBuffAmounts()
    {
        foreach (var tag in new[] { "weapon_type_rifle", "weapon_type_marksman_rifle", "weapon_type_smg" })
        {
            var b = SelectUpgrade(SelectWeapon(SetBuild("gear_set_concentrated_company", 4), tag), "backpack", "gear_talent_one_for_all");
            foreach (var weapon in new[] { "", "primary_weapon" })
            {
                foreach (var stacks in new[] { "0", "10", "35" })
                {
                    var ctx = Context(("weapon_slot", weapon), ("camaraderie_stacks", stacks));
                    var baseline = Stats(b, ctx);
                    var active = Resolve(SelectUpgrade(b, "chest", "gear_talent_all_for_one"), ctx);
                    SameStatsExcept(baseline, active.Calculation.Stats, $"{tag}, weapon \"{weapon}\", stacks {stacks}", "camaraderie_mark_limit");
                    ExpectSetValue(active, "camaraderie_mark_limit", "gear_set_concentrated_company", 8);
                }
            }
        }
    }

    private static readonly (string Id, double Count)[] AcesCounts = [("aces_eights_four_kind_shots", 4), ("aces_eights_full_house_shots", 3), ("aces_eights_aces_hand_shots", 2)];

    /// <summary>G137: Aces and Eights' shot counts need four pieces, and Ace in the Sleeve adds one only on its backpack host.</summary>
    [Fact]
    public void G137AcesCountsThresholdAndHosts()
    {
        foreach (var pieces in new[] { 3, 4, 5 })
        {
            var b = SetBuild("gear_set_aces_eights", pieces);
            var selected = SelectUpgrade(b, "backpack", "gear_talent_ace_in_the_sleeve");
            foreach (var (id, count) in AcesCounts)
            {
                var (plain, upgraded) = pieces < 4 ? (0, 0) : (count, count + 1);
                CloseTo(Stats(b).Of(id), plain, 1e-12);
                CloseTo(Stats(selected).Of(id), upgraded, 1e-12);
                CloseTo(Stats(selected.WithTalentSelection("backpack", "talent", "")).Of(id), plain, 1e-12);
            }
        }
        var stale = SelectUpgrade(SetBuild("gear_set_aces_eights", 5), "backpack", "gear_talent_ace_in_the_sleeve").WithEquipment("backpack", "backpack_belstone_armory_backpack");
        var wrong = SetBuild("gear_set_aces_eights", 4).WithTalentSelection("chest", "talent", "gear_talent_ace_in_the_sleeve");
        foreach (var (id, count) in AcesCounts)
        {
            CloseTo(Stats(stale).Of(id), count, 1e-12);
            CloseTo(Stats(wrong).Of(id), count, 1e-12);
        }
    }

    /// <summary>G137: Ace in the Sleeve changes the counts only; it doesn't activate the empowered shot or change damage.</summary>
    [Fact]
    public void G137AcesCountsDoNotActivateOrChangeDamage()
    {
        foreach (var tag in new[] { "weapon_type_rifle", "weapon_type_marksman_rifle", "weapon_type_smg" })
        {
            foreach (var noLimit in new[] { false, true })
            {
                var b = SelectWeapon(SetBuild("gear_set_aces_eights", 4), tag);
                if (noLimit) { b = SelectUpgrade(b, "chest", "gear_talent_no_limit"); }
                foreach (var weapon in new[] { "", "primary_weapon" })
                {
                    foreach (var empowered in new[] { "false", "true" })
                    {
                        var ctx = Context(("weapon_slot", weapon), ("aces_eights_empowered_shot", empowered));
                        var baseline = Stats(b, ctx);
                        var active = Resolve(SelectUpgrade(b, "backpack", "gear_talent_ace_in_the_sleeve"), ctx);
                        SameStatsExcept(baseline, active.Calculation.Stats, $"{tag}, No Limit {noLimit}, weapon \"{weapon}\", empowered {empowered}", [.. AcesCounts.Select(pair => pair.Id)]);
                        foreach (var (id, count) in AcesCounts)
                        {
                            ExpectSetValue(active, id, "gear_set_aces_eights", count + 1);
                        }
                    }
                }
            }
        }
    }

    /// <summary>G142: Symptom Aggravator amplifies weapon and every skill damage channel against status-affected targets, with four pieces on its host.</summary>
    [Fact]
    public void G142SymptomThresholdHostAndAllDamageChannels()
    {
        var affected = Context(("target_status_affected", "true"));
        foreach (var pieces in new[] { 3, 4, 5 })
        {
            var b = SelectUpgrade(SetBuild("gear_set_eclipse_protocol", pieces), "backpack", "gear_talent_symptom_aggravator");
            var r = Stats(b, affected);
            var want = pieces >= 4 ? 1.3 : 1;
            CloseTo(r.Of("weapon_damage_multiplier"), want, 1e-12);
            CloseTo(r.Of("skill_damage_multiplier"), want, 1e-12);
            CloseTo(Stats(b).Of("weapon_damage_multiplier"), 1, 1e-12);
            foreach (var invalid in new[] { b.WithTalentSelection("backpack", "talent", ""), b.WithEquipment("backpack", "backpack_belstone_armory_backpack") })
            {
                CloseTo(Stats(invalid, affected).Of("weapon_damage_multiplier"), 1, 1e-12);
            }
        }
        var four = SelectUpgrade(SetBuild("gear_set_eclipse_protocol", 4), "backpack", "gear_talent_symptom_aggravator");
        var channels = 0;
        foreach (var skill in Compiled.Pack.ActiveSkills)
        {
            var selected = four.WithActiveSkillSelection("skill_slot_1", skill.ID);
            var off = Resolve(selected);
            var on = Resolve(selected, affected);
            var offOutputs = Outputs(off);
            for (var i = 0; i < offOutputs.Length; i++)
            {
                var output = offOutputs[i];
                if (output.Id.StartsWith("assumed_", StringComparison.Ordinal)) { continue; } // skill DPS rates derive from the damage channels
                var factor = 1.0;
                // Leech Hive's and Gas Bomber's damage take no skill bonuses (R54).
                if (IsDamageChannel(output.Id) && skill.ID is not ("skill_leech_hive" or "skill_gas_bomber_drone")) { factor = 1.3; channels++; }
                Assert.True(Math.Abs(Outputs(on)[i].Value - (output.Value * factor)) <= 1e-7, $"{skill.ID}/{output.Id}: {Outputs(on)[i].Value}, want {output.Value * factor}");
            }
            foreach (var id in new[] { "skill_damage", "status_effects", "skill_efficiency", "symptom_aggravator_damage_bonus" })
            {
                CloseTo(on.Calculation.Stats.Of(id), off.Calculation.Stats.Of(id), 1e-12);
            }
        }
        Assert.True(channels == 28, $"got {channels} damage channels");
        var stacked = SelectUpgrade(SelectUpgrade(SetBuild("gear_set_eclipse_protocol", 5), "backpack", "gear_talent_symptom_aggravator").WithEquipment("chest", "chest_belstone_armory_chest"), "chest", "gear_talent_glass_cannon");
        var s = Stats(stacked, affected);
        CloseTo(s.Of("weapon_damage_multiplier"), 1.3 * 1.25, 1e-12);
        CloseTo(s.Of("skill_damage_multiplier"), 1.3 * 1.25, 1e-12);
    }

    /// <summary>G143: Heatstroke amplifies weapon damage only, against a target the Prototype ignited, with four pieces on its host.</summary>
    [Fact]
    public void G143HeatstrokeThresholdTargetAndSkillIsolation()
    {
        const string key = "exuro_target_ignited_by_prototype";
        foreach (var pieces in new[] { 3, 4, 5 })
        {
            var b = SelectUpgrade(SetBuild("gear_set_ortiz_exuro", pieces), "backpack", "gear_talent_heatstroke");
            var r = Stats(b, Context((key, "true")));
            CloseTo(r.Of("weapon_damage_multiplier"), pieces >= 4 ? 1.4 : 1, 1e-12);
            CloseTo(r.Of("skill_damage_multiplier"), 1, 1e-12);
            foreach (var ctx in new[] { Context(), Context((key, "false")), Context(("target_burning", "true"), ("target_status_affected", "true")) })
            {
                CloseTo(Stats(b, ctx).Of("weapon_damage_multiplier"), 1, 1e-12);
            }
            foreach (var invalid in new[] { b.WithTalentSelection("backpack", "talent", ""), b.WithEquipment("backpack", "backpack_belstone_armory_backpack") })
            {
                CloseTo(Stats(invalid, Context((key, "true"))).Of("weapon_damage_multiplier"), 1, 1e-12);
            }
        }
        var four = SelectUpgrade(SetBuild("gear_set_ortiz_exuro", 4), "backpack", "gear_talent_heatstroke");
        var channels = 0;
        foreach (var skill in Compiled.Pack.ActiveSkills)
        {
            var selected = four.WithActiveSkillSelection("skill_slot_1", skill.ID);
            var off = Resolve(selected);
            var on = Resolve(selected, Context((key, "true")));
            Assert.True(SamePreviews(off.ActiveSkillPreviews, on.ActiveSkillPreviews), $"Heatstroke changed skill {skill.ID}");
            channels += Outputs(off).Count(output => IsDamageChannel(output.Id));
        }
        Assert.True(channels == 30, $"got {channels} damage channels");
        var armed = SelectWeapon(four, "weapon_type_assault_rifle");
        var weaponOff = Stats(armed, Weapon("primary_weapon"));
        var weaponOn = Resolve(armed, Context(("weapon_slot", "primary_weapon"), (key, "true")));
        Assert.True(weaponOff.Of("equipped_weapon_damage") > 0, "missing weapon damage");
        foreach (var id in new[] { "equipped_weapon_damage", "equipped_weapon_critical_damage", "equipped_weapon_headshot_damage", "equipped_weapon_critical_headshot_damage", "equipped_weapon_bodyshot_dps", "equipped_weapon_reload_cycle_dps" })
        {
            CloseTo(weaponOn.Calculation.Stats.Of(id), weaponOff.Of(id) * 1.4, 1e-7);
        }
        ExpectSetValue(weaponOn, "weapon_damage_multiplier", "gear_set_ortiz_exuro", .4);
    }

    /// <summary>G144: Hackstep converts qualifying bonus armor into total weapon damage in 5% steps, capped at 20%, for weapons only.</summary>
    [Fact]
    public void G144HackstepBoundariesThresholdAndWeaponStacking()
    {
        const string key = "hackstep_qualifying_bonus_armor_percent";
        foreach (var pieces in new[] { 3, 4, 5 })
        {
            var b = SetBuild("gear_set_system_corruption", pieces);
            CloseTo(Stats(b, Context((key, "50"))).Of("total_weapon_damage"), pieces >= 4 ? .2 : 0, 1e-12);
            CloseTo(Stats(b).Of("total_weapon_damage"), 0, 1e-12);
        }
        var four = SetBuild("gear_set_system_corruption", 4);
        foreach (var (value, want) in new[] { ("0", 0.0), ("4", 0), ("5", .02), ("24", .08), ("25", .1), ("49", .18), ("50", .2), ("100", .2), ("-1", 0), ("101", 0), ("NaN", 0), ("1.5", 0) })
        {
            var got = Stats(four, Context((key, value))).Of("total_weapon_damage");
            Assert.True(Math.Abs(got - want) <= 1e-12, $"input \"{value}\": total weapon damage {got}, want {want}");
        }
        foreach (var skill in Compiled.Pack.ActiveSkills)
        {
            var selected = four.WithActiveSkillSelection("skill_slot_1", skill.ID);
            Assert.True(SamePreviews(Resolve(selected).ActiveSkillPreviews, Resolve(selected, Context((key, "100"))).ActiveSkillPreviews), $"changed skill {skill.ID}");
        }
        var armed = SelectWeapon(four, "weapon_type_assault_rifle");
        var off = Stats(armed, Weapon("primary_weapon"));
        var on = Resolve(armed, Context(("weapon_slot", "primary_weapon"), (key, "50")));
        Assert.True(off.Of("equipped_weapon_damage") > 0, "missing weapon estimate");
        foreach (var id in new[] { "equipped_weapon_damage", "equipped_weapon_critical_damage", "equipped_weapon_headshot_damage", "equipped_weapon_critical_headshot_damage", "equipped_weapon_bodyshot_dps", "equipped_weapon_reload_cycle_dps" })
        {
            CloseTo(on.Calculation.Stats.Of(id), off.Of(id) * 1.2, 1e-7);
        }
        foreach (var id in new[] { "estimated_armor", "bonus_armor_percent", "estimated_bonus_armor", "skill_damage_multiplier" })
        {
            CloseTo(on.Calculation.Stats.Of(id), off.Of(id), 1e-12);
        }
        ExpectSetValue(on, "total_weapon_damage", "gear_set_system_corruption", .2);
    }

    /// <summary>G145: Makeshift Repairs' budget and rate need four pieces, both upgrades apply, invalid inputs give 0, and nothing else changes.</summary>
    [Fact]
    public void G145FoundryBudgetThresholdUpgradesAndIsolation()
    {
        const string key = "makeshift_qualifying_damage";
        foreach (var pieces in new[] { 3, 4, 5 })
        {
            var b = SetBuild("gear_set_foundry_bulwark", pieces);
            var want = pieces >= 4 ? 25000 : 0;
            var on = Stats(b, Context((key, "100000")));
            CloseTo(on.Of("makeshift_repair_budget"), want, 1e-10);
            CloseTo(on.Of("makeshift_repair_average_rate"), want / 10.0, 1e-10);
            CloseTo(Stats(b).Of("makeshift_repair_budget"), 0, 1e-12);
        }
        var four = SetBuild("gear_set_foundry_bulwark", 4);
        foreach (var chest in new[] { false, true })
        {
            foreach (var backpack in new[] { false, true })
            {
                var selected = four;
                var (budget, duration) = (25000.0, 10.0);
                if (chest) { selected = SelectUpgrade(selected, "chest", "gear_talent_improved_materials"); duration = 5; }
                if (backpack) { selected = SelectUpgrade(selected, "backpack", "gear_talent_process_refinery"); budget = 35000; }
                var r = Resolve(selected, Context((key, "100000")));
                CloseTo(r.Calculation.Stats.Of("makeshift_repair_budget"), budget, 1e-10);
                CloseTo(r.Calculation.Stats.Of("makeshift_repair_average_rate"), budget / duration, 1e-10);
                ExpectSetValue(r, "makeshift_repair_budget", "gear_set_foundry_bulwark", budget);
            }
        }
        foreach (var value in new[] { "-1", "10000001", "NaN", "1.5", "Infinity" })
        {
            CloseTo(Stats(four, Context((key, value))).Of("makeshift_repair_budget"), 0, 1e-12);
        }
        CloseTo(Stats(four, Context((key, "10000000"))).Of("makeshift_repair_budget"), 2500000, 1e-10);
        foreach (var skill in Compiled.Pack.ActiveSkills)
        {
            var selected = four.WithActiveSkillSelection("skill_slot_1", skill.ID);
            var off = Resolve(selected);
            var on = Resolve(selected, Context((key, "100000")));
            Assert.True(SamePreviews(off.ActiveSkillPreviews, on.ActiveSkillPreviews), $"changed skill {skill.ID}");
            foreach (var id in new[] { "character_armor", "estimated_armor_regeneration", "bonus_armor_percent", "effective_repair_skills", "total_weapon_damage", "equipped_weapon_damage" })
            {
                CloseTo(on.Calculation.Stats.Of(id), off.Calculation.Stats.Of(id), 1e-12);
            }
        }
    }

    /// <summary>G150: Headache's armor repair and bonus armor capacity need four pieces; Stand Tall doubles the capacity only on its host.</summary>
    [Fact]
    public void G150HotshotArmorCapacities()
    {
        const string key = "headache_current_armor";
        foreach (var pieces in new[] { 3, 4, 5 })
        {
            var b = SetBuild("gear_set_hotshot", pieces);
            var baseline = Stats(b);
            var on = Stats(b, Context((key, "100000")));
            var (repair, capacity) = pieces >= 4 ? (baseline.Of("character_armor") * .1, 50000.0) : (0, 0);
            CloseTo(baseline.Of("headache_available_armor_repair"), repair, 1e-8);
            CloseTo(baseline.Of("headache_bonus_armor_capacity"), 0, 1e-12);
            CloseTo(on.Of("headache_bonus_armor_capacity"), capacity, 1e-8);
        }
        var four = SetBuild("gear_set_hotshot", 4);
        foreach (var chest in new[] { false, true })
        {
            foreach (var backpack in new[] { false, true })
            {
                var selected = four;
                var capacity = 50000.0;
                if (chest) { selected = SelectUpgrade(selected, "chest", "gear_talent_stand_tall"); capacity = 100000; }
                if (backpack) { selected = SelectUpgrade(selected, "backpack", "gear_talent_lucky"); }
                foreach (var full in new[] { "true", "false" })
                {
                    var r = Resolve(selected, Context((key, "100000"), ("full_armor", full)));
                    CloseTo(r.Calculation.Stats.Of("headache_bonus_armor_capacity"), capacity, 1e-8);
                    CloseTo(r.Calculation.Stats.Of("headache_available_armor_repair"), r.Calculation.Stats.Of("character_armor") * .1, 1e-8);
                    CloseTo(r.Calculation.Stats.Of("bonus_armor_percent"), 0, 1e-12);
                    ExpectSetValue(r, "headache_bonus_armor_capacity", "gear_set_hotshot", capacity);
                }
            }
        }
        foreach (var input in new[] { "-1", "10000001", "NaN", "1.5", "Infinity", "bad" })
        {
            CloseTo(Stats(four, Context((key, input))).Of("headache_bonus_armor_capacity"), 0, 1e-12);
        }
        CloseTo(Stats(four, Context((key, "10000000"))).Of("headache_bonus_armor_capacity"), 5000000, 1e-8);
        var invalid = four.WithTalentSelection("backpack", "talent", "gear_talent_stand_tall");
        CloseTo(Stats(invalid, Context((key, "100000"))).Of("headache_bonus_armor_capacity"), 50000, 1e-8);
        var ninja = four.WithEquipment("backpack", "backpack_ninjabike_messenger_backpack").WithTalentSelection("backpack", "talent", "gear_talent_resourceful");
        CloseTo(Stats(ninja, Context((key, "100000"))).Of("headache_bonus_armor_capacity"), 50000, 1e-8);
        CloseTo(Stats(ninja.WithTalentSelection("backpack", "talent", ""), Context((key, "100000"))).Of("headache_bonus_armor_capacity"), 0, 1e-12);
        var armored = four.WithAttributeRoll("mask", "core_1", new("armor_gear_core", 170000))
            .WithAccountUpgrade("watch_total_armor", 50)
            .WithEquipmentUpgrade("mask", "mask_hotshot_mask", 10);
        var a = Stats(armored, Context((key, "100000")));
        CloseTo(a.Of("headache_available_armor_repair"), a.Of("character_armor") * .1, 1e-8);
        CloseTo(a.Of("headache_bonus_armor_capacity"), 50000, 1e-8);
        foreach (var skill in Compiled.Pack.ActiveSkills)
        {
            var selected = four.WithActiveSkillSelection("skill_slot_1", skill.ID);
            var off = Resolve(selected);
            var on = Resolve(selected, Context((key, "100000")));
            Assert.True(SamePreviews(off.ActiveSkillPreviews, on.ActiveSkillPreviews), $"changed skill {skill.ID}");
            foreach (var id in new[] { "character_armor", "bonus_armor_percent", "estimated_bonus_armor", "estimated_armor_regeneration", "effective_repair_skills", "equipped_weapon_damage", "headache_available_armor_repair" })
            {
                CloseTo(on.Calculation.Stats.Of(id), off.Calculation.Stats.Of(id), 1e-12);
            }
        }
    }

    private static BuildSnapshot WithTripleCores(BuildSnapshot b)
    {
        foreach (var (slot, attribute, value) in new[] { ("core_1", "weapon_damage_gear_core", .15), ("core_2", "armor_gear_core", 170000), ("core_3", "skill_tier_gear_core", 1) })
        {
            b = b.WithAttributeRoll("backpack", slot, new(attribute, value));
        }
        return b;
    }

    /// <summary>G151: the Core Strength backpack takes one core of each type, counted by type.</summary>
    [Fact]
    public void G151OuterCoreBackpack()
    {
        var b = WithTripleCores(BuildSnapshot.Create(Compiled.Pack, "Outer Core").WithEquipment("backpack", "backpack_core_strength_backpack"));
        var r = Stats(b);
        foreach (var id in new[] { "weapon_core_count", "armor_core_count", "skill_core_count" })
        {
            CloseTo(r.Of(id), 1, 1e-12);
        }
        CloseTo(r.Of("weapon_damage"), .15, 1e-12);
        CloseTo(r.Of("armor"), 170000, 1e-12);
        CloseTo(r.Of("skill_tier"), 1, 1e-12);
        CloseTo(r.Of("core_exercise_conversion_share"), 0, 1e-12);
        b = b.WithAttributeRoll("backpack", "core_2", new("skill_tier_gear_core", 1));
        CloseTo(Stats(b).Of("armor_core_count"), 0, 1e-12);
        b = b.WithEquipment("backpack", "backpack_belstone_armory_backpack");
        CloseTo(Stats(b).Of("skill_core_count"), 0, 1e-12);
    }

    /// <summary>G152: Core Exercise converts a share of each triple-core backpack's other cores automatically, raised by Inner Core.</summary>
    [Fact]
    public void G152CoreExerciseCountsTripleCoresAutomatically()
    {
        var b = WithTripleCores(SetBuild("gear_set_core_strength", 4));
        foreach (var chest in new[] { false, true })
        {
            var selected = chest ? SelectUpgrade(b, "chest", "gear_talent_inner_core") : b;
            var share = chest ? .75 : .4;
            var r = Stats(selected);
            CloseTo(r.Of("weapon_damage"), .2 + (2 * .15 * share), 1e-12);
            CloseTo(r.Of("armor"), 170000 + (2 * 170000 * share), 1e-8);
            CloseTo(r.Of("skill_efficiency"), .05 + (2 * .15 * share), 1e-12);
            CloseTo(r.Of("skill_tier"), 1, 1e-12);
            foreach (var id in new[] { "weapon_core_count", "armor_core_count", "skill_core_count" })
            {
                CloseTo(r.Of(id), 1, 1e-12);
            }
        }
    }

    /// <summary>G152: Core Exercise counts only valid configured gear cores, needs four pieces, and takes Ninjabike's credit.</summary>
    [Fact]
    public void G152CoreExerciseCountsOnlyValidConfiguredGearCores()
    {
        var b = SetBuild("gear_set_core_strength", 4).WithAttributeRoll("mask", "core_1", new("weapon_damage_gear_core", .02));
        var r = Stats(b);
        CloseTo(r.Of("armor"), 68000, 1e-8);
        CloseTo(r.Of("skill_efficiency"), .11, 1e-12);
        foreach (var value in new[] { -.01, .151 })
        {
            CloseTo(Stats(b.WithAttributeRoll("mask", "core_1", new("weapon_damage_gear_core", value))).Of("armor"), 0, 1e-8);
        }
        CloseTo(Stats(b.WithEquipment("gloves", "")).Of("armor"), 0, 1e-8);
        b = b.WithEquipment("backpack", "backpack_ninjabike_messenger_backpack");
        CloseTo(Stats(b).Of("armor"), 0, 1e-8);
        b = b.WithTalentSelection("backpack", "talent", "gear_talent_resourceful");
        CloseTo(Stats(b).Of("armor"), 68000, 1e-8);
    }

    /// <summary>G153: Stoic's assumed resistance needs four pieces; Deceit counts decoys and Polyethylene Plating raises the rate.</summary>
    [Fact]
    public void G153StoicThresholdAndSelectedUpgrades()
    {
        foreach (var pieces in new[] { 3, 4, 5 })
        {
            var b = SetBuild("gear_set_aegis", pieces);
            CloseTo(Stats(b).Of("stoic_assumed_resistance"), 0, 1e-12);
            foreach (var chest in new[] { false, true })
            {
                foreach (var backpack in new[] { false, true })
                {
                    var selected = b;
                    var (targets, rate) = (2.0, .04);
                    if (chest) { selected = SelectUpgrade(selected, "chest", "gear_talent_deceit"); targets = 5; }
                    if (backpack) { selected = SelectUpgrade(selected, "backpack", "gear_talent_polyethylene_plating"); rate = .05; }
                    var want = pieces < 4 ? 0 : targets * rate * 1.4;
                    var r = Stats(selected, Context(("stoic_player_targets", "2"), ("stoic_decoy_targets", "3"), ("stoic_group_agents", "4")));
                    CloseTo(r.Of("stoic_assumed_resistance"), want, 1e-12);
                    CloseTo(r.Of("damage_resistance"), want, 1e-12);
                }
            }
        }
    }

    /// <summary>G153: Stoic's inputs are bounded, it changes nothing else, and it takes Ninjabike's credit.</summary>
    [Fact]
    public void G153StoicBoundsAndNinjaBikeCredit()
    {
        var b = SelectUpgrade(SetBuild("gear_set_aegis", 4), "chest", "gear_talent_deceit");
        var r = Stats(b, Context(("stoic_player_targets", "8"), ("stoic_decoy_targets", "8"), ("stoic_group_agents", "4")));
        CloseTo(r.Of("stoic_assumed_resistance"), .448, 1e-12);
        var baseline = Stats(b);
        foreach (var id in new[] { "equipped_weapon_damage", "character_armor", "character_health", "bonus_armor_percent", "skill_tier" })
        {
            CloseTo(r.Of(id), baseline.Of(id), 1e-12);
        }
        foreach (var value in new[] { "-1", "9", "1.5", "NaN", "Infinity", "bad" })
        {
            CloseTo(Stats(b, Context(("stoic_player_targets", value), ("stoic_decoy_targets", value))).Of("stoic_assumed_resistance"), 0, 1e-12);
        }
        b = b.WithEquipment("backpack", "backpack_ninjabike_messenger_backpack");
        var ctx = Context(("stoic_player_targets", "2"));
        CloseTo(Stats(b, ctx).Of("stoic_assumed_resistance"), 0, 1e-12);
        b = b.WithTalentSelection("backpack", "talent", "gear_talent_resourceful");
        CloseTo(Stats(b, ctx).Of("stoic_assumed_resistance"), .088, 1e-12);
    }

    /// <summary>G155: Headache's empowered headshot multiplies an eligible marksman rifle's headshots only, with four pieces.</summary>
    [Fact]
    public void G155EligibleMMRHeadshots()
    {
        foreach (var pieces in new[] { 3, 4, 5 })
        {
            foreach (var owner in new[] { "primary_weapon", "secondary_weapon" })
            {
                var other = owner == "primary_weapon" ? "secondary_weapon" : "primary_weapon";
                var b = SetBuild("gear_set_hotshot", pieces).WithEquipment(owner, owner + "_m700_carbon").WithEquipment(other, other + "_acr");
                b = SelectUpgrade(SelectUpgrade(b, "chest", "gear_talent_stand_tall"), "backpack", "gear_talent_lucky");
                var off = Stats(b, Weapon(owner));
                var on = Stats(b, Context(("weapon_slot", owner), ("headache_empowered_headshot", "true")));
                var factor = pieces >= 4 ? 1.8 : 1;
                CloseTo(on.Of("headshot_damage_multiplier"), factor, 1e-12);
                Assert.True(off.Of("equipped_weapon_headshot_damage") > 0, "MMR baseline missing");
                foreach (var id in new[] { "equipped_weapon_headshot_damage", "equipped_weapon_critical_headshot_damage" })
                {
                    CloseTo(on.Of(id), off.Of(id) * factor, 1e-8);
                }
                foreach (var id in new[] { "headshot_damage", "weapon_total_headshot_damage", "equipped_weapon_damage", "equipped_weapon_critical_damage", "equipped_weapon_bodyshot_dps", "equipped_weapon_reload_cycle_dps", "character_armor" })
                {
                    CloseTo(on.Of(id), off.Of(id), 1e-8);
                }
                foreach (var selected in new[] { other, "sidearm" })
                {
                    CloseTo(Stats(b, Context(("weapon_slot", selected), ("headache_empowered_headshot", "true"))).Of("headshot_damage_multiplier"), 1, 1e-12);
                }
                foreach (var flag in new[] { "", "false", "bad" })
                {
                    CloseTo(Stats(b, Context(("weapon_slot", owner), ("headache_empowered_headshot", flag), ("full_armor", "true"))).Of("headshot_damage_multiplier"), 1, 1e-12);
                }
                foreach (var skill in Compiled.Pack.ActiveSkills)
                {
                    var skilled = b.WithActiveSkillSelection("skill_slot_1", skill.ID);
                    var a = Outputs(Resolve(skilled, Weapon(owner)));
                    var z = Outputs(Resolve(skilled, Context(("weapon_slot", owner), ("headache_empowered_headshot", "true"))));
                    for (var i = 0; i < a.Length; i++)
                    {
                        CloseTo(z[i].Value, a[i].Value, 1e-8);
                    }
                }
            }
        }
        var mantis = SetBuild("gear_set_hotshot", 4).WithEquipment("backpack", "backpack_ninjabike_messenger_backpack").WithEquipment("primary_weapon", "primary_weapon_mantis");
        CloseTo(Stats(mantis, Context(("weapon_slot", "primary_weapon"), ("headache_empowered_headshot", "true"))).Of("headshot_damage_multiplier"), 1, 1e-12);
        mantis = mantis.WithTalentSelection("backpack", "talent", "gear_talent_resourceful").WithTalentSelection("primary_weapon", "talent", "weapon_talent_in_plain_sight");
        var both = Stats(mantis, Context(("weapon_slot", "primary_weapon"), ("headache_empowered_headshot", "true"), ("mantis_target_not_targeting_you", "true")));
        CloseTo(both.Of("headshot_damage_multiplier"), 1.8 * 1.65, 1e-12); // In Plain Sight is 65% since Y8S3.2
    }

    private static bool IsCountedDamage(string id) => id is "damage" or "bleed_damage" or "burn_damage" or "cloud_damage" or "explosion_damage" or "average_damage";

    /// <summary>G156: Trauma Specialist doubles a selected skill's bleed damage and lengthens its bleed by half, with four pieces on its host.</summary>
    [Fact]
    public void G156SelectedTraumaSkillBleed()
    {
        int bleed = 0, damage = 0;
        foreach (var pieces in new[] { 3, 4, 5 })
        {
            foreach (var skill in Compiled.Pack.ActiveSkills)
            {
                var b = SetBuild("gear_set_ongoing_directive", pieces).WithActiveSkillSelection("skill_slot_1", skill.ID);
                var on = SelectUpgrade(b, "backpack", "gear_talent_trauma_specialist");
                var off = Resolve(b);
                var r = Resolve(on);
                var outputs = Outputs(r);
                for (var i = 0; i < outputs.Length; i++)
                {
                    var o = outputs[i];
                    if (o.Id.StartsWith("assumed_", StringComparison.Ordinal)) { continue; } // skill DPS rates derive from the bleed channel
                    var factor = pieces >= 4 && o.Id == "bleed_damage" ? 2 : pieces >= 4 && o.Id == "bleed_duration_seconds" ? 1.5 : 1;
                    var want = Outputs(off)[i].Value * factor;
                    Assert.True(Math.Abs(o.Value - want) <= 1e-8, $"{pieces} pieces, {skill.ID}/{o.Id}: {o.Value}, want {want}");
                    if (pieces == 4)
                    {
                        if (o.Id == "bleed_damage") { bleed++; }
                        if (IsCountedDamage(o.Id)) { damage++; }
                    }
                }
                foreach (var id in new[] { "status_effects", "skill_damage", "skill_damage_multiplier", "total_skill_damage", "equipped_weapon_damage" })
                {
                    CloseTo(r.Calculation.Stats.Of(id), off.Calculation.Stats.Of(id), 1e-12);
                }
                var cleared = on.WithTalentSelection("backpack", "talent", "");
                var wrong = cleared.WithTalentSelection("chest", "talent", "gear_talent_trauma_specialist");
                var stale = on.WithEquipment("backpack", "backpack_belstone_armory_backpack");
                foreach (var invalid in new[] { cleared, wrong, stale })
                {
                    CloseTo(Stats(invalid).Of("trauma_bleed_damage_bonus"), 0, 1e-12);
                }
            }
        }
        Assert.True(bleed == 3 && damage == 29, $"channels: bleed={bleed} damage={damage}");
    }

    /// <summary>G157: Combustion's initial burn debuff amplifies skill burn damage only, with four pieces, and takes Ninjabike's credit.</summary>
    [Fact]
    public void G157EligibleBurnChannels()
    {
        int burn = 0, damage = 0;
        var debuff = Context(("combustion_initial_burn_debuff", "true"));
        foreach (var pieces in new[] { 3, 4, 5 })
        {
            foreach (var skill in Compiled.Pack.ActiveSkills)
            {
                var b = SetBuild("gear_set_ember_engine", pieces).WithActiveSkillSelection("skill_slot_1", skill.ID);
                b = SelectUpgrade(SelectUpgrade(b, "chest", "gear_talent_flashpoint"), "backpack", "gear_talent_white_hot");
                var off = Resolve(b);
                var on = Resolve(b, debuff);
                var factor = pieces >= 4 ? 1.25 : 1;
                CloseTo(on.Calculation.Stats.Of("combustion_burn_damage_factor"), factor, 1e-12);
                var outputs = Outputs(on);
                for (var i = 0; i < outputs.Length; i++)
                {
                    var o = outputs[i];
                    if (o.Id.StartsWith("assumed_", StringComparison.Ordinal)) { continue; } // skill DPS rates derive from the burn channel
                    var want = Outputs(off)[i].Value * (o.Id == "burn_damage" ? factor : 1);
                    Assert.True(Math.Abs(o.Value - want) <= 1e-8, $"{pieces} pieces, {skill.ID}/{o.Id}: {o.Value}, want {want}");
                    if (pieces == 4)
                    {
                        if (o.Id == "burn_damage") { burn++; }
                        if (IsCountedDamage(o.Id)) { damage++; }
                    }
                }
                foreach (var id in new[] { "skill_damage", "status_effects", "total_skill_damage", "skill_damage_multiplier", "equipped_weapon_damage", "character_armor" })
                {
                    CloseTo(on.Calculation.Stats.Of(id), off.Calculation.Stats.Of(id), 1e-12);
                }
                foreach (var flag in new[] { "", "false", "bad" })
                {
                    CloseTo(Stats(b, Context(("combustion_initial_burn_debuff", flag), ("target_burning", "true"), ("target_status_affected", "true"))).Of("combustion_burn_damage_factor"), 1, 1e-12);
                }
            }
        }
        Assert.True(burn == 4 && damage == 29, $"channels burn={burn} damage={damage}");
        var ninja = SetBuild("gear_set_ember_engine", 4).WithEquipment("backpack", "backpack_ninjabike_messenger_backpack");
        CloseTo(Stats(ninja, debuff).Of("combustion_burn_damage_factor"), 1, 1e-12);
        ninja = ninja.WithTalentSelection("backpack", "talent", "gear_talent_resourceful");
        CloseTo(Stats(ninja, debuff).Of("combustion_burn_damage_factor"), 1.25, 1e-12);
    }
}
