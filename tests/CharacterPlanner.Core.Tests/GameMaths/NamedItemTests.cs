using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// Named and exotic items with their fixed stats and exceptions (F11): Ninjabike's set credit, Memento, recovery
/// pools, Mantis, Bullet King and Clutch, ported from Go's ninjabike_g106_test.go, memento_cores_g114_test.go,
/// recovery_g117_test.go, mantis_headshots_g121_test.go, bullet_king_g122_test.go and clutch_window_g125_test.go.
/// </summary>
public sealed class NamedItemTests
{
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

    private static ResolvedBuild ResolveIn(CompiledPackExpressions compiled, BuildSnapshot build, ImmutableDictionary<string, string>? context = null) =>
        BuildResolver.Resolve(compiled, build, context ?? ImmutableDictionary<string, string>.Empty);

    private static string Describe(IEnumerable<ActiveSet> sets) => string.Join("; ", sets.Select(set => $"{set.SetId} pieces {set.Pieces} bonus {set.BonusPieces} applied [{string.Join(", ", set.AppliedBonuses)}]"));

    /// <summary>G106: Resourceful credits one piece to each represented set, only while selected on the Ninjabike backpack.</summary>
    [Fact]
    public void G106ResourcefulCreditsOnlyRepresentedSets()
    {
        var b = BuildSnapshot.Create(Compiled.Pack, "Resourceful")
            .WithEquipment("backpack", "backpack_ninjabike_messenger_backpack")
            .WithEquipment("mask", "mask_providence_defense_mask")
            .WithEquipment("chest", "chest_walker_harris_co_chest");
        var sets = Resolve(b).ActiveSets;
        Assert.True(sets.All(set => set.Pieces == 1), $"unselected credit: {Describe(sets)}");
        b = b.WithTalentSelection("backpack", "talent", "gear_talent_resourceful");
        sets = Resolve(b).ActiveSets;
        Assert.True(sets.Length == 2, $"represented sets: {Describe(sets)}");
        Assert.True(sets.All(set => set.Pieces == 2 && set.BonusPieces == 1 && set.AppliedBonuses.Length == 2), $"missing credit: {Describe(sets)}");
        b = b.WithEquipment("mask", "");
        Assert.True(Resolve(b).ActiveSets.Length == 1, "credit must not create an absent set");
        b = b.WithEquipment("backpack", "");
        sets = Resolve(b).ActiveSets;
        Assert.True(sets.All(set => set.Pieces == 1 && set.BonusPieces == 0), $"removed host retains credit: {Describe(sets)}");
    }

    /// <summary>G106: Resourceful's credit unlocks a four-piece bonus without a set upgrade, and an illegal selection earns nothing.</summary>
    [Fact]
    public void G106ResourcefulUnlocksFourPiecesWithoutSetUpgrade()
    {
        var b = BuildSnapshot.Create(Compiled.Pack, "Resourceful")
            .WithEquipment("backpack", "backpack_ninjabike_messenger_backpack")
            .WithTalentSelection("backpack", "talent", "gear_talent_resourceful");
        foreach (var slot in new[] { "mask", "chest", "gloves" })
        {
            b = b.WithEquipment(slot, slot + "_striker_s_battlegear_" + slot);
        }
        var r = Resolve(b, Context(("striker_stacks", "10")));
        CloseTo(r.Calculation.Stats.Of("weapon_damage_multiplier"), 1.065, 1e-12);
        Assert.True(r.ActiveSets.All(set => set.Pieces == 4 && set.BonusPieces == 1), $"bad threshold: {Describe(r.ActiveSets)}");
        b = b.WithEquipment("backpack", "backpack_providence_defense_backpack").WithTalentSelection("backpack", "talent", "gear_talent_resourceful");
        var sets = Resolve(b).ActiveSets;
        Assert.True(sets.All(set => set.BonusPieces == 0), $"illegal selection earns credit: {Describe(sets)}");
    }

    /// <summary>G114: Memento counts configured cores by type and combines its short and trophy buffs.</summary>
    [Fact]
    public void G114MementoCountsConfiguredCoresAndCombinesBuffs()
    {
        var b = BuildSnapshot.Create(Compiled.Pack, "Memento")
            .WithEquipment("backpack", "backpack_memento")
            .WithTalentSelection("backpack", "talent", "gear_talent_kill_confirmed");
        foreach (var (slot, attribute, value) in new[] { ("core_1", "weapon_damage_gear_core", .15), ("core_2", "armor_gear_core", 170000), ("core_3", "skill_tier_gear_core", 1) })
        {
            b = b.WithAttributeRoll("backpack", slot, new(attribute, value));
        }
        var baseStats = Stats(b);
        var ctx = Context(("memento_short_buff_active", "true"), ("memento_trophy_stacks", "10"));
        var r = Stats(b, ctx);
        foreach (var id in new[] { "weapon_core_count", "armor_core_count", "skill_core_count" })
        {
            CloseTo(r.Of(id), 1, 1e-12);
        }
        CloseTo(r.Of("weapon_damage") - baseStats.Of("weapon_damage"), .15, 1e-12);
        CloseTo(r.Of("skill_efficiency"), .15, 1e-12);
        CloseTo(r.Of("bonus_armor_percent"), .1, 1e-12);
        CloseTo(r.Of("armor_regeneration_percent"), .01, 1e-12);
        CloseTo(Stats(b).Of("bonus_armor_percent"), 0, 1e-12);
        CloseTo(Stats(b.WithTalentSelection("backpack", "talent", ""), ctx).Of("bonus_armor_percent"), 0, 1e-12);
        CloseTo(Stats(b.WithEquipment("backpack", ""), ctx).Of("skill_efficiency"), 0, 1e-12);
        b = b.WithEquipment("mask", "mask_n_5_11_tactical_mask");
        CloseTo(Stats(b, ctx).Of("weapon_core_count"), 1, 1e-12);
        b = b.WithAttributeRoll("mask", "core_1", new("weapon_damage_gear_core", .15));
        CloseTo(Stats(b, ctx).Of("weapon_core_count"), 2, 1e-12);
        b = b.WithAttributeRoll("mask", "core_1", new("skill_tier_gear_core", 1));
        CloseTo(Stats(b, ctx).Of("weapon_core_count"), 1, 1e-12);
        CloseTo(Stats(b, ctx).Of("skill_core_count"), 2, 1e-12);
        b = b.WithAttributeRoll("mask", "core_1", new("skill_tier_gear_core", 9));
        CloseTo(Stats(b, ctx).Of("skill_core_count"), 1, 1e-12);
        var changed = WithFormulas(("skill_tier", 5));
        var t = ResolveIn(changed, b, Context(("memento_short_buff_active", "true"))).Calculation.Stats;
        CloseTo(t.Of("skill_tier"), 6, 1e-12);
        CloseTo(t.Of("skill_efficiency"), .05, 1e-12);
        foreach (var slot in new[] { "primary_weapon", "secondary_weapon", "" })
        {
            CloseTo(ResolveIn(changed, b, Context(("weapon_slot", slot), ("memento_short_buff_active", "true"))).Calculation.Stats.Of("bonus_armor_percent"), .1, 1e-12);
        }
    }

    private static readonly string[] RecoveryEstimates = ["estimated_bonus_armor", "estimated_armor_regeneration", "estimated_armor_on_kill", "estimated_health_on_kill"];

    /// <summary>G117: recovery estimates are flat amounts plus shares of the armor and health pools, and negative pools give 0.</summary>
    [Fact]
    public void G117RecoveryArithmetic()
    {
        var values = new (string, double)[]
        {
            ("character_armor", 1000000), ("character_health", 300000), ("armor_regeneration", 4900), ("armor_regeneration_percent", .03), ("armor_on_kill", 17025),
            ("armor_on_kill_percent", .1), ("health_on_kill", .3), ("bonus_armor_percent", .2), ("incoming_repairs", .5), ("repair_skills", .5), ("skill_efficiency", .5),
        };
        var compiled = WithFormulas(values);
        var r = ResolveIn(compiled, BuildSnapshot.Create(compiled.Pack, "Recovery")).Calculation.Stats;
        foreach (var (id, value) in values)
        {
            CloseTo(r.Of(id), value, 1e-12);
        }
        foreach (var (id, value) in new[] { ("estimated_bonus_armor", 200000.0), ("estimated_armor_regeneration", 34900), ("estimated_armor_on_kill", 117025), ("estimated_health_on_kill", 90000) })
        {
            CloseTo(r.Of(id), value, 1e-8);
        }
        // Go edited the same pack again, so the repair inputs above stay in place.
        var negative = values.Select(pair => pair.Item1 switch
        {
            "character_armor" or "character_health" or "armor_regeneration" or "armor_on_kill" => (pair.Item1, -100.0),
            "bonus_armor_percent" or "armor_regeneration_percent" or "armor_on_kill_percent" or "health_on_kill" => (pair.Item1, -1.0),
            _ => pair,
        }).ToArray();
        compiled = WithFormulas(negative);
        r = ResolveIn(compiled, BuildSnapshot.Create(compiled.Pack, "Negative pools")).Calculation.Stats;
        foreach (var id in RecoveryEstimates)
        {
            CloseTo(r.Of(id), 0, 1e-12);
        }
    }

    /// <summary>G117: Memento's recovery applies only while selected, and an empty build recovers nothing.</summary>
    [Fact]
    public void G117MementoRecoveryAndEmptyBonuses()
    {
        var b = BuildSnapshot.Create(Compiled.Pack, "Recovery");
        var r = Stats(b);
        foreach (var id in RecoveryEstimates)
        {
            CloseTo(r.Of(id), 0, 1e-12);
        }
        b = b.WithEquipment("backpack", "backpack_memento")
            .WithTalentSelection("backpack", "talent", "gear_talent_kill_confirmed")
            .WithAttributeRoll("backpack", "core_2", new("armor_gear_core", 170000));
        var ctx = Context(("memento_short_buff_active", "true"), ("memento_trophy_stacks", "10"));
        r = Stats(b, ctx);
        CloseTo(r.Of("character_armor"), 301000, 1e-8);
        CloseTo(r.Of("estimated_bonus_armor"), 30100, 1e-8);
        CloseTo(r.Of("estimated_armor_regeneration"), 3010, 1e-8);
        r = Stats(b.WithTalentSelection("backpack", "talent", ""), ctx);
        CloseTo(r.Of("estimated_bonus_armor"), 0, 1e-8);
        CloseTo(r.Of("estimated_armor_regeneration"), 0, 1e-8);
        CloseTo(r.Of("character_armor"), 301000, 1e-8);
    }

    /// <summary>G117: Belstone's permanent regeneration and a flat regeneration roll add up.</summary>
    [Fact]
    public void G117PermanentBrandAndFlatRegeneration()
    {
        var b = BuildSnapshot.Create(Compiled.Pack, "Permanent recovery")
            .WithEquipment("mask", "mask_belstone_armory_mask")
            .WithAttributeRoll("mask", "core_1", new("armor_gear_core", 170000))
            .WithAttributeRoll("mask", "minor_1", new("armor_regeneration_gear_minor", 4925));
        var r = Stats(b);
        CloseTo(r.Of("character_armor"), 250000, 1e-8);
        CloseTo(r.Of("armor_regeneration_percent"), .01, 1e-12);
        CloseTo(r.Of("estimated_armor_regeneration"), 7425, 1e-8);
        CloseTo(Stats(b.WithEquipment("mask", "")).Of("estimated_armor_regeneration"), 0, 1e-8);
    }

    /// <summary>G121: In Plain Sight amplifies Mantis's headshots only while active and in hand, and leaves everything else alone.</summary>
    [Fact]
    public void G121MantisHeadshotsAndOwnerScope()
    {
        foreach (var owner in new[] { "primary_weapon", "secondary_weapon" })
        {
            var other = owner == "primary_weapon" ? "secondary_weapon" : "primary_weapon";
            var b = BuildSnapshot.Create(Compiled.Pack, "Mantis")
                .WithEquipment(owner, owner + "_mantis")
                .WithEquipment(other, other + "_acr")
                .WithActiveSkillSelection("skill_slot_1", "skill_assault_turret");
            var baseline = Resolve(b, Weapon(owner));
            var bs = baseline.Calculation.Stats;
            b = b.WithTalentSelection(owner, "talent", "weapon_talent_in_plain_sight");
            CloseTo(Stats(b, Weapon(owner)).Of("equipped_weapon_headshot_damage"), bs.Of("equipped_weapon_headshot_damage"), 1e-8);
            var active = Resolve(b, Context(("weapon_slot", owner), ("mantis_target_not_targeting_you", "true")));
            var a = active.Calculation.Stats;
            CloseTo(a.Of("headshot_damage_multiplier"), 1.65, 1e-12); // Y8S3.2: 65%
            foreach (var id in new[] { "equipped_weapon_headshot_damage", "equipped_weapon_critical_headshot_damage" })
            {
                CloseTo(a.Of(id), bs.Of(id) * 1.65, 1e-8);
            }
            foreach (var id in new[] { "equipped_weapon_damage", "equipped_weapon_critical_damage", "equipped_weapon_average_bodyshot_damage", "equipped_weapon_bodyshot_dps", "equipped_weapon_reload_cycle_dps", "headshot_damage", "weapon_total_headshot_damage" })
            {
                CloseTo(a.Of(id), bs.Of(id), 1e-8);
            }
            var outputs = active.ActiveSkillPreviews["skill_slot_1"].Outputs;
            for (var index = 0; index < outputs.Length; index++)
            {
                CloseTo(outputs[index].Value, baseline.ActiveSkillPreviews["skill_slot_1"].Outputs[index].Value, 1e-8);
            }
            foreach (var slot in new[] { other, "" })
            {
                CloseTo(Stats(b, Context(("weapon_slot", slot), ("mantis_target_not_targeting_you", "true"))).Of("headshot_damage_multiplier"), 1, 1e-12);
            }
            b = b.WithTalentSelection(owner, "talent", "");
            CloseTo(Stats(b, Context(("weapon_slot", owner), ("mantis_target_not_targeting_you", "true"))).Of("equipped_weapon_headshot_damage"), bs.Of("equipped_weapon_headshot_damage"), 1e-8);
        }
    }

    /// <summary>G121: the headshot multiplier amplifies after the additive bonuses, and a negative multiplier gives 0.</summary>
    [Fact]
    public void G121HeadshotAmplificationAfterAdditiveBonuses()
    {
        foreach (var (multiplier, expected) in new[] { (1.5, 525.0), (-1, 0) })
        {
            var compiled = WithFormulas(("equipped_weapon_damage", 100), ("weapon_total_critical_hit_damage", .5), ("weapon_total_headshot_damage", 2), ("headshot_damage_multiplier", multiplier));
            var r = ResolveIn(compiled, BuildSnapshot.Create(compiled.Pack, "Headshots")).Calculation.Stats;
            CloseTo(r.Of("equipped_weapon_critical_headshot_damage"), expected, 1e-8);
            CloseTo(r.Of("equipped_weapon_damage"), 100, 1e-8);
        }
    }

    /// <summary>G122: Bullet Hell removes Bullet King's reload for its own weapon only, and its removal restores the ordinary figures.</summary>
    [Fact]
    public void G122BulletKingOwnerAndTalentRemoval()
    {
        foreach (var owner in new[] { "primary_weapon", "secondary_weapon" })
        {
            var other = owner == "primary_weapon" ? "secondary_weapon" : "primary_weapon";
            var b = BuildSnapshot.Create(Compiled.Pack, "Bullet King").WithEquipment(owner, owner + "_bullet_king").WithEquipment(other, other + "_acr");
            var ctx = Weapon(owner);
            var baseline = Stats(b, ctx);
            CloseTo(baseline.Of("equipped_weapon_reload_time"), 0, 1e-12);
            CloseTo(baseline.Of("equipped_weapon_reload_cycle_dps"), 0, 1e-12);
            b = b.WithTalentSelection(owner, "talent", "weapon_talent_bullet_hell");
            var active = Stats(b, ctx);
            CloseTo(active.Of("weapon_no_reload"), 1, 1e-12);
            CloseTo(active.Of("equipped_weapon_reload_cycle_dps"), active.Of("equipped_weapon_bodyshot_dps"), 1e-8);
            Assert.True(active.Of("equipped_weapon_reload_cycle_dps") > 0, $"{owner}: missing no-reload DPS");
            foreach (var id in new[] { "weapon_reload_time", "equipped_weapon_reload_time", "equipped_weapon_damage", "equipped_weapon_average_bodyshot_damage", "equipped_weapon_bodyshot_dps", "equipped_weapon_magazine_size", "equipped_weapon_magazine_damage", "equipped_weapon_firing_duration" })
            {
                CloseTo(active.Of(id), baseline.Of(id), 1e-8);
            }
            var normal = Stats(b, Weapon(other));
            CloseTo(normal.Of("weapon_no_reload"), 0, 1e-12);
            Assert.True(normal.Of("equipped_weapon_reload_cycle_dps") > 0 && normal.Of("equipped_weapon_reload_cycle_dps") < normal.Of("equipped_weapon_bodyshot_dps"),
                $"{owner}: ordinary reload penalty changed ({normal.Of("equipped_weapon_reload_cycle_dps")} against {normal.Of("equipped_weapon_bodyshot_dps")})");
            CloseTo(Stats(b).Of("weapon_no_reload"), 0, 1e-12);
            CloseTo(Stats(b).Of("equipped_weapon_reload_cycle_dps"), 0, 1e-12);
            b = b.WithTalentSelection(owner, "talent", "");
            CloseTo(Stats(b, ctx).Of("equipped_weapon_reload_cycle_dps"), 0, 1e-12);
        }
    }

    /// <summary>G122: no reload still needs a rate of fire.</summary>
    [Fact]
    public void G122NoReloadStillRequiresCadence()
    {
        var compiled = WithFormulas(("rate_of_fire", -2));
        var b = BuildSnapshot.Create(compiled.Pack, "No cadence")
            .WithEquipment("primary_weapon", "primary_weapon_bullet_king")
            .WithTalentSelection("primary_weapon", "talent", "weapon_talent_bullet_hell");
        var r = ResolveIn(compiled, b, Weapon("primary_weapon")).Calculation.Stats;
        CloseTo(r.Of("weapon_no_reload"), 1, 1e-12);
        CloseTo(r.Of("equipped_weapon_reload_cycle_dps"), 0, 1e-12);
    }

    /// <summary>G125: Clutch's post-kill window is 4 seconds plus one per configured weapon damage core, and only while selected.</summary>
    [Fact]
    public void G125ClutchWindowUsesConfiguredCores()
    {
        foreach (var talent in new[] { "clutch", "perfect_clutch" })
        {
            var host = talent == "perfect_clutch" ? "backpack_devil_s_due" : "backpack_belstone_armory_backpack";
            var b = BuildSnapshot.Create(Compiled.Pack, "Clutch window").WithEquipment("backpack", host).WithTalentSelection("backpack", "talent", "gear_talent_" + talent);
            CloseTo(Stats(b).Of("clutch_post_kill_window"), 4, 1e-8);
            var slots = new[] { "mask", "chest", "holster", "gloves", "knees", "backpack" };
            for (var i = 0; i < slots.Length; i++)
            {
                var slot = slots[i];
                if (slot != "backpack")
                {
                    var item = Compiled.Pack.Items.FirstOrDefault(item => item.Slot == slot && item.Tags.Contains("gear") && !item.Tags.Contains("named") && !item.Tags.Contains("exotic"));
                    if (item is not null) { b = b.WithEquipment(item.Slot, item.ID); }
                }
                b = b.WithAttributeRoll(slot, "core_1", new("weapon_damage_gear_core", .15));
                var r = Stats(b);
                CloseTo(r.Of("weapon_core_count"), i + 1, 1e-8);
                CloseTo(r.Of("clutch_post_kill_window"), i + 5, 1e-8);
                CloseTo(r.Of("missing_armor_repair_on_crit_percent"), 0, 1e-8);
                CloseTo(r.Of("estimated_armor_repair_on_crit"), 0, 1e-8);
            }
            b = b.WithAttributeRoll("mask", "core_1", new("armor_gear_core", 170000));
            CloseTo(Stats(b).Of("clutch_post_kill_window"), 9, 1e-8);
            b = b.WithAttributeRoll("chest", "core_1", new("weapon_damage_gear_core", 9));
            CloseTo(Stats(b).Of("clutch_post_kill_window"), 8, 1e-8);
            CloseTo(Stats(b.WithTalentSelection("backpack", "talent", "")).Of("clutch_post_kill_window"), 0, 1e-8);
        }
    }

    /// <summary>G125: the window is bounded to 4–10 seconds, independent of Clutch's repair state, with the talent as its source.</summary>
    [Fact]
    public void G125ClutchWindowBoundsAndEventIndependence()
    {
        foreach (var (cores, seconds) in new[] { (-1.0, 4.0), (0, 4), (1.9, 5), (6, 10), (20, 10) })
        {
            var compiled = WithFormulas(("weapon_core_count", cores), ("weapon_damage", 5));
            var b = BuildSnapshot.Create(compiled.Pack, "Clutch window").WithEquipment("backpack", "backpack_belstone_armory_backpack").WithTalentSelection("backpack", "talent", "gear_talent_clutch");
            foreach (var state in new[] { "", "low_armor", "post_kill" })
            {
                var r = ResolveIn(compiled, b, Context(("clutch_repair_state", state), ("weapon_slot", "secondary_weapon"), ("clutch_missing_armor_percent", "90")));
                CloseTo(r.Calculation.Stats.Of("clutch_post_kill_window"), seconds, 1e-8);
                Assert.True(r.Calculation.Breakdowns["clutch_post_kill_window"].Contributions.Any(contribution => contribution.Source.Id == "gear_talent_clutch" && contribution.Value == seconds),
                    $"cores {cores}, state \"{state}\": missing Clutch window provenance");
            }
        }
    }
}
