using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// Weapon figures: rate of fire, reload, magazine, handling, ranges and card readings (F30, with F13's upgrade ranks
/// in G316), ported from Go's weapon_rpm_g99_test.go, weapon_reload_g100_test.go, weapon_magazine_g101_test.go,
/// weapon_magazine_g351_test.go, weapon_handling_g116_test.go, weapon_range_g119_test.go,
/// weapon_ranges_reload_g288_test.go and weapon_card_readings_g316_test.go.
/// </summary>
public sealed class WeaponStatTests
{
    private static readonly string[] WeaponSlots = ["primary_weapon", "secondary_weapon", "sidearm"];

    /// <summary>The committed pack with stat formulas replaced by constants, as Go's edits of Pack.Stats and ValidatePack.</summary>
    private static CompiledPackExpressions WithFormulas(params (string Stat, string Formula)[] formulas)
    {
        var replaced = formulas.ToDictionary(value => value.Stat, value => value.Formula, StringComparer.Ordinal);
        var pack = Compiled.Pack with
        {
            Stats = [.. Compiled.Pack.Stats.Select(stat => replaced.TryGetValue(stat.ID, out var formula) ? stat with { Formula = formula } : stat)],
        };
        var result = PackValidator.Validate(pack);
        Assert.True(result.Compiled is not null, $"invalid fixture: {string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message))}");
        return result.Compiled;
    }

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static ImmutableDictionary<string, double> StatsIn(CompiledPackExpressions compiled, BuildSnapshot build, ImmutableDictionary<string, string>? context = null) =>
        BuildResolver.Resolve(compiled, build, context ?? ImmutableDictionary<string, string>.Empty).Calculation.Stats;

    /// <summary>Go's g48WeaponBuild: the first item in each weapon slot, then the first host in the slot that allows the talent, with it selected.</summary>
    private static BuildSnapshot WeaponBuild(string talent, string slot)
    {
        var b = Build();
        foreach (var weaponSlot in WeaponSlots)
        {
            var first = Compiled.Pack.Items.FirstOrDefault(item => item.Slot == weaponSlot);
            if (first is not null) { b = b.WithEquipment(weaponSlot, first.ID); }
        }
        foreach (var item in Compiled.Pack.Items.Where(item => item.Slot == slot))
        {
            foreach (var talentSlot in item.TalentSlots.Where(talentSlot => talentSlot.Choices.Contains(talent)))
            {
                return b.WithEquipment(slot, item.ID).WithTalentSelection(slot, talentSlot.ID, talent);
            }
        }
        Assert.Fail($"no {slot} host allows {talent}");
        return b;
    }

    /// <summary>The first item in a slot that offers a talent, equipped with it selected, as Go's goto-equipped loops.</summary>
    private static BuildSnapshot WithHost(BuildSnapshot b, string slot, string talentName)
    {
        var talent = Compiled.Pack.Talents.FirstOrDefault(talent => talent.Name == talentName) ?? throw new InvalidOperationException($"missing talent {talentName}");
        foreach (var item in Compiled.Pack.Items.Where(item => item.Slot == slot))
        {
            foreach (var talentSlot in item.TalentSlots.Where(talentSlot => talentSlot.Choices.Contains(talent.ID)))
            {
                return b.WithEquipment(slot, item.ID).WithTalentSelection(slot, talentSlot.ID, talent.ID);
            }
        }
        Assert.Fail($"missing {talentName} host");
        return b;
    }

    /// <summary>Go's g53Build("Counter"): the first item in each weapon slot, an ACR and a Tempest, and Centurion's Scabbard with Counter.</summary>
    private static BuildSnapshot CounterBuild()
    {
        var b = Build();
        foreach (var weaponSlot in WeaponSlots)
        {
            var first = Compiled.Pack.Items.FirstOrDefault(item => item.Slot == weaponSlot);
            if (first is not null) { b = b.WithEquipment(weaponSlot, first.ID); }
        }
        b = b.WithEquipment("primary_weapon", "primary_weapon_acr").WithEquipment("sidearm", "sidearm_tempest");
        var counter = Compiled.Pack.Talents.Single(talent => talent.Name == "Counter").ID;
        var talentSlot = Compiled.Pack.Items.Single(item => item.ID == "holster_centurion_scabbard").TalentSlots.First(talentSlot => talentSlot.Choices.Contains(counter));
        return b.WithEquipment("holster", "holster_centurion_scabbard").WithTalentSelection("holster", talentSlot.ID, counter);
    }

    /// <summary>G99: Measured's phases scale the estimated RPM of its own weapon only, and an empty or unselected weapon has none.</summary>
    [Fact]
    public void G99EstimatedWeaponRPMPhasesAndOwnerScope()
    {
        var b = WeaponBuild("weapon_talent_measured", "primary_weapon");
        var baseline = Stats(b, Weapon("primary_weapon"));
        Assert.True(baseline.Of("weapon_rpm") > 0, "missing base RPM");
        foreach (var (phase, factor) in new[] { ("top", 1.25), ("bottom", .82) })
        {
            var result = Stats(b, Context(("weapon_slot", "primary_weapon"), ("measured_phase", phase)));
            CloseTo(result.Of("equipped_weapon_rpm"), baseline.Of("weapon_rpm") * factor, 1e-10);
            foreach (var id in new[] { "weapon_rpm", "weapon_reload_time", "weapon_magazine_size", "weapon_optimal_range" })
            {
                CloseTo(result.Of(id), baseline.Of(id), 1e-12);
            }
        }
        CloseTo(baseline.Of("equipped_weapon_rpm"), baseline.Of("weapon_rpm"), 1e-12);
        foreach (var slot in new[] { "secondary_weapon", "sidearm" })
        {
            var result = Stats(b, Context(("weapon_slot", slot), ("measured_phase", "top")));
            CloseTo(result.Of("rate_of_fire"), 0, 1e-12);
            CloseTo(result.Of("equipped_weapon_rpm"), result.Of("weapon_rpm"), 1e-12);
        }
        CloseTo(Stats(b, Context(("measured_phase", "top"))).Of("equipped_weapon_rpm"), 0, 1e-12);
        b = b.WithEquipment("primary_weapon", "");
        CloseTo(Stats(b, Context(("weapon_slot", "primary_weapon"), ("measured_phase", "top"))).Of("equipped_weapon_rpm"), 0, 1e-12);
    }

    /// <summary>G99: gear and weapon rate-of-fire bonuses add before the estimate (Counter's offense buff and Measured's top phase).</summary>
    [Fact]
    public void G99EstimatedWeaponRPMAddsGearAndWeaponBonuses()
    {
        var b = WithHost(CounterBuild(), "primary_weapon", "Measured");
        var result = Stats(b, Context(("weapon_slot", "primary_weapon"), ("measured_phase", "top"), ("counter_buff_group", "offense")));
        CloseTo(result.Of("rate_of_fire"), .45, 1e-12);
        CloseTo(result.Of("equipped_weapon_rpm"), result.Of("weapon_rpm") * 1.45, 1e-10);
    }

    /// <summary>G99: a negative estimate floors at 0.</summary>
    [Fact]
    public void G99EstimatedWeaponRPMFloorsNegativeEstimate()
    {
        var b = WeaponBuild("weapon_talent_measured", "primary_weapon");
        var result = StatsIn(WithFormulas(("rate_of_fire", "-2")), b, Weapon("primary_weapon"));
        Assert.True(result.Of("weapon_rpm") > 0, "missing base RPM");
        CloseTo(result.Of("equipped_weapon_rpm"), 0, 1e-12);
    }

    /// <summary>G100: reload time divides by 1 + reload speed, for Fast Hands' own weapon only.</summary>
    [Fact]
    public void G100ReloadInverseSpeedAndOwnerScope()
    {
        var b = WeaponBuild("weapon_talent_fast_hands", "primary_weapon");
        var baseline = Stats(b, Weapon("primary_weapon"));
        Assert.True(baseline.Of("weapon_reload_time") > 0, "missing base reload time");
        CloseTo(baseline.Of("equipped_weapon_reload_time"), baseline.Of("weapon_reload_time"), 1e-12);
        foreach (var stacks in new[] { 10, 40 })
        {
            var result = Stats(b, Context(("weapon_slot", "primary_weapon"), ("fast_hands_stacks", stacks.ToString(CultureInfo.InvariantCulture))));
            CloseTo(result.Of("effective_reload_speed"), stacks * .03, 1e-12);
            CloseTo(result.Of("equipped_weapon_reload_time"), baseline.Of("weapon_reload_time") / (1 + (stacks * .03)), 1e-12);
            foreach (var id in new[] { "weapon_reload_time", "weapon_rpm", "equipped_weapon_rpm", "weapon_magazine_size", "equipped_weapon_damage" })
            {
                CloseTo(result.Of(id), baseline.Of(id), 1e-12);
            }
        }
        foreach (var slot in new[] { "secondary_weapon", "sidearm" })
        {
            var result = Stats(b, Context(("weapon_slot", slot), ("fast_hands_stacks", "40")));
            CloseTo(result.Of("reload_speed"), 0, 1e-12);
            CloseTo(result.Of("equipped_weapon_reload_time"), result.Of("weapon_reload_time"), 1e-12);
        }
        CloseTo(Stats(b).Of("equipped_weapon_reload_time"), 0, 1e-12);
        b = b.WithEquipment("primary_weapon", "");
        CloseTo(Stats(b, Weapon("primary_weapon")).Of("equipped_weapon_reload_time"), 0, 1e-12);
    }

    /// <summary>G100: reload speed combines direct reload speed with weapon handling (Braced in cover).</summary>
    [Fact]
    public void G100ReloadCombinesGearHandlingAndDirectSpeed()
    {
        var b = WithHost(WeaponBuild("weapon_talent_fast_hands", "primary_weapon"), "chest", "Braced");
        var result = Stats(b, Context(("weapon_slot", "primary_weapon"), ("fast_hands_stacks", "10"), ("in_cover", "true")));
        CloseTo(result.Of("reload_speed"), .3, 1e-12);
        CloseTo(result.Of("weapon_handling"), .45, 1e-12);
        CloseTo(result.Of("effective_reload_speed"), .75, 1e-12);
        CloseTo(result.Of("equipped_weapon_reload_time"), result.Of("weapon_reload_time") / 1.75, 1e-12);
        var other = Stats(b, Context(("weapon_slot", "secondary_weapon"), ("fast_hands_stacks", "10"), ("in_cover", "true")));
        CloseTo(other.Of("effective_reload_speed"), .45, 1e-12);
        CloseTo(other.Of("equipped_weapon_reload_time"), other.Of("weapon_reload_time") / 1.45, 1e-12);
    }

    /// <summary>G100: a negative reload speed lengthens the reload, and a factor of 0 or less gives 0.</summary>
    [Fact]
    public void G100ReloadNegativeAndInvalidSpeedFactors()
    {
        var b = WeaponBuild("weapon_talent_fast_hands", "primary_weapon");
        foreach (var bonus in new[] { -.25, -1, -2 })
        {
            var result = StatsIn(WithFormulas(("reload_speed", Number(bonus))), b, Weapon("primary_weapon"));
            var want = bonus > -1 ? result.Of("weapon_reload_time") / (1 + bonus) : 0;
            CloseTo(result.Of("equipped_weapon_reload_time"), want, 1e-12);
        }
    }

    /// <summary>G101: flat magazine mods add after the percentage, and Overflowing's bonus applies to the base magazine of its own weapon.</summary>
    [Fact]
    public void G101MagazineFlatModsAndBaseOnlyBonuses()
    {
        var b = WeaponBuild("weapon_talent_overflowing", "primary_weapon")
            .WithEquipment("primary_weapon", "primary_weapon_acr")
            .WithTalentSelection("primary_weapon", "talent", "weapon_talent_overflowing")
            .WithModSelection("primary_weapon", "magazine", "weapon_mod_magazine_sturdy_extended_5_56_mag");
        var baseline = Stats(b, Weapon("primary_weapon"));
        CloseTo(baseline.Of("equipped_weapon_magazine_size"), 50, 1e-12);
        var active = Stats(b, Context(("weapon_slot", "primary_weapon"), ("overflowing_active", "true")));
        CloseTo(active.Of("equipped_weapon_magazine_size"), 80, 1e-12);
        CloseTo(active.Of("base_magazine_size_percent"), 1, 1e-12);
        CloseTo(active.Of("magazine_size_percent"), 1, 1e-12);
        CloseTo(active.Of("weapon_magazine_size"), 30, 1e-12);
        var other = Stats(b, Context(("weapon_slot", "secondary_weapon"), ("overflowing_active", "true")));
        CloseTo(other.Of("magazine_size"), 0, 1e-12);
        CloseTo(other.Of("base_magazine_size_percent"), 0, 1e-12);
        CloseTo(other.Of("equipped_weapon_magazine_size"), other.Of("weapon_magazine_size"), 1e-12);
        CloseTo(Stats(b).Of("equipped_weapon_magazine_size"), 0, 1e-12);
        b = b.WithEquipment("primary_weapon", "");
        CloseTo(Stats(b, Weapon("primary_weapon")).Of("equipped_weapon_magazine_size"), 0, 1e-12);
    }

    /// <summary>G101: Pakhan's stacks scale the base magazine before its flat mod, and ordinary percentages round down.</summary>
    [Fact]
    public void G101MagazinePakhanAndOrdinaryPercentage()
    {
        var b = WeaponBuild("weapon_talent_pakhan", "primary_weapon")
            .WithModSelection("primary_weapon", "magazine", "weapon_mod_magazine_pakhan_magazine");
        foreach (var stacks in new[] { 0, 1, 4 })
        {
            var r = Stats(b, Context(("weapon_slot", "primary_weapon"), ("pakhan_stacks", stacks.ToString(CultureInfo.InvariantCulture))));
            CloseTo(r.Of("magazine_size"), 20, 1e-12);
            CloseTo(r.Of("equipped_weapon_magazine_size"), Math.Floor((r.Of("weapon_magazine_size") * (1 + (.75 * stacks))) + 20), 1e-12);
        }
        b = WeaponBuild("weapon_talent_lucky_shot", "primary_weapon")
            .WithEquipment("primary_weapon", "primary_weapon_classic_m1a")
            .WithTalentSelection("primary_weapon", "talent", "weapon_talent_lucky_shot")
            .WithModSelection("primary_weapon", "magazine", "weapon_mod_magazine_tightly_packed_marksman_mag");
        CloseTo(Stats(b, Weapon("primary_weapon")).Of("equipped_weapon_magazine_size"), 18, 1e-12);
        foreach (var bonus in new[] { "0.03", "-2" })
        {
            CloseTo(StatsIn(WithFormulas(("magazine_size_percent", bonus)), b, Weapon("primary_weapon")).Of("equipped_weapon_magazine_size"), bonus == "-2" ? 0 : 18, 1e-12);
        }
    }

    private static BuildSnapshot G351Weapon(string item, double magazineRoll)
    {
        var b = Build().WithEquipment("primary_weapon", item);
        return magazineRoll != 0 ? b.WithAttributeRoll("primary_weapon", "minor_1", new("magazine_size_weapon_minor", magazineRoll)) : b;
    }

    private static double G351Magazine(CompiledPackExpressions compiled, BuildSnapshot b) =>
        StatsIn(compiled, b, Weapon("primary_weapon")).Of("equipped_weapon_magazine_size");

    /// <summary>G351: magazine capacity follows the game's ClipSizeFinal; a positive percentage adds at least one round.</summary>
    [Fact]
    public void G351PositivePercentAddsAtLeastOneRound()
    {
        CloseTo(G351Magazine(Compiled, G351Weapon("primary_weapon_thorn", 0.08)), 9, 1e-12);
        var dread = G351Weapon("primary_weapon_dread_edict", 0.09).WithModSelection("primary_weapon", "magazine", "weapon_mod_magazine_tuned_tension_spring");
        CloseTo(G351Magazine(Compiled, dread), 16, 1e-12);
    }

    /// <summary>G351: flat exotic magazine mods stay exact.</summary>
    [Fact]
    public void G351FlatExoticMagazineModsStayExact()
    {
        var vindicator = G351Weapon("primary_weapon_vindicator", 0).WithModSelection("primary_weapon", "magazine", "weapon_mod_magazine_simulation_magazine");
        CloseTo(G351Magazine(Compiled, vindicator), 40, 1e-12);
        var ironLung = G351Weapon("primary_weapon_iron_lung", 0).WithModSelection("primary_weapon", "magazine", "weapon_mod_magazine_bellows");
        CloseTo(G351Magazine(Compiled, ironLung), 85, 1e-12);
    }

    /// <summary>G351: small bases and negative percentages.</summary>
    [Fact]
    public void G351SmallBasesAndNegativePercent()
    {
        CloseTo(G351Magazine(WithFormulas(("magazine_size_percent", "0.5")), G351Weapon("primary_weapon_double_barrel_shotgun", 0)), 2, 1e-12);
        var acr = G351Weapon("primary_weapon_acr", 0).WithModSelection("primary_weapon", "magazine", "weapon_mod_magazine_sturdy_extended_5_56_mag");
        CloseTo(G351Magazine(WithFormulas(("magazine_size_percent", "-0.1")), acr), 47, 1e-12);
        CloseTo(G351Magazine(WithFormulas(("magazine_size_percent", "-0.01")), acr), 49, 1e-12);
    }

    /// <summary>G116: each effective handling component is its own bonus plus weapon handling.</summary>
    [Fact]
    public void G116CombinedHandlingComponents()
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal) { ["accuracy"] = .3, ["stability"] = -.2, ["swap_speed"] = .1, ["reload_speed"] = .4, ["weapon_handling"] = .5 };
        var compiled = WithFormulas([.. values.Select(pair => (pair.Key, Number(pair.Value)))]);
        var r = StatsIn(compiled, BuildSnapshot.Create(compiled.Pack, "Handling"));
        foreach (var id in new[] { "accuracy", "stability", "swap_speed", "reload_speed" })
        {
            CloseTo(r.Of(id), values[id], 1e-12);
            CloseTo(r.Of("effective_" + id), values[id] + .5, 1e-12);
        }
    }

    /// <summary>G116: On Empty's handling buff applies to its own weapon's effective handling only, and not to damage.</summary>
    [Fact]
    public void G116HandlingBuffOwnerScope()
    {
        var b = WeaponBuild("weapon_talent_on_empty", "primary_weapon");
        var baseline = Stats(b, Weapon("primary_weapon"));
        var active = Stats(b, Context(("weapon_slot", "primary_weapon"), ("on_empty_active", "true")));
        var other = Stats(b, Context(("weapon_slot", "secondary_weapon"), ("on_empty_active", "true")));
        foreach (var id in new[] { "accuracy", "stability", "swap_speed", "reload_speed" })
        {
            CloseTo(active.Of("effective_" + id) - baseline.Of("effective_" + id), .6, 1e-12);
            CloseTo(active.Of(id), baseline.Of(id), 1e-12);
            CloseTo(other.Of("effective_" + id), other.Of(id), 1e-12);
        }
        CloseTo(active.Of("equipped_weapon_damage"), baseline.Of("equipped_weapon_damage"), 1e-12);
    }

    /// <summary>G119: optimal range is the base times 1 + bonus, and 0 for a non-positive base or factor.</summary>
    [Fact]
    public void G119RangeArithmeticAndGuards()
    {
        foreach (var (rangeBase, bonus, expected) in new[] { (32.0, .75, 56.0), (32, -.35, 20.8), (32, -2, 0), (0, 2, 0), (-32, 1, 0) })
        {
            var compiled = WithFormulas(("weapon_optimal_range", Number(rangeBase)), ("optimal_range", Number(bonus)));
            var r = StatsIn(compiled, BuildSnapshot.Create(compiled.Pack, "Range"));
            CloseTo(r.Of("equipped_weapon_optimal_range"), expected, 1e-8);
            CloseTo(r.Of("weapon_optimal_range"), rangeBase, 1e-12);
            CloseTo(r.Of("optimal_range"), bonus, 1e-12);
        }
    }

    /// <summary>G119: Near Sighted and Outsider change their own weapon's optimal range only.</summary>
    [Fact]
    public void G119RangeTalentOwnerAndBuffScope()
    {
        var b = Build().WithEquipment("primary_weapon", "primary_weapon_acr").WithEquipment("secondary_weapon", "secondary_weapon_acr");
        var baseline = Stats(b, Weapon("primary_weapon"));
        b = b.WithTalentSelection("primary_weapon", "talent", "weapon_talent_near_sighted");
        var r = Stats(b, Weapon("primary_weapon"));
        CloseTo(r.Of("equipped_weapon_optimal_range"), baseline.Of("weapon_optimal_range") * .65, 1e-8);
        CloseTo(r.Of("equipped_weapon_damage"), baseline.Of("equipped_weapon_damage"), 1e-8);
        var other = Stats(b, Weapon("secondary_weapon"));
        CloseTo(other.Of("equipped_weapon_optimal_range"), other.Of("weapon_optimal_range"), 1e-8);
        CloseTo(Stats(b).Of("equipped_weapon_optimal_range"), 0, 1e-8);
        b = b.WithEquipment("primary_weapon", "primary_weapon_converted_smg_9_a2").WithTalentSelection("primary_weapon", "talent", "weapon_talent_outsider");
        var ctx = Context(("weapon_slot", "primary_weapon"), ("outsider_active", "true"));
        r = Stats(b, ctx);
        CloseTo(r.Of("equipped_weapon_optimal_range"), r.Of("weapon_optimal_range") * 2, 1e-8);
        r = Stats(b.WithTalentSelection("primary_weapon", "talent", ""), ctx);
        CloseTo(r.Of("equipped_weapon_optimal_range"), r.Of("weapon_optimal_range"), 1e-8);
    }

    /// <summary>G288: optimal and long range follow range bonuses (maximum range doesn't), and tactical and full reloads follow reload speed.</summary>
    [Fact]
    public void G288RangesAndTacticalReloadFollowBonuses()
    {
        foreach (var slot in new[] { "primary_weapon", "secondary_weapon" })
        {
            var b = Build().WithEquipment(slot, slot + "_aug_a3_cqc");
            var ctx = Weapon(slot);
            var baseStats = Stats(b, ctx);
            CloseTo(baseStats.Of("equipped_weapon_reload_time"), 2.16, 1e-12);
            CloseTo(baseStats.Of("equipped_weapon_tactical_reload_time"), 1.86, 1e-12);
            CloseTo(baseStats.Of("equipped_weapon_optimal_range"), 26, 1e-12);
            CloseTo(baseStats.Of("equipped_weapon_long_range"), 105, 1e-12);
            CloseTo(baseStats.Of("equipped_weapon_max_range"), 105, 1e-12);

            b = b.WithAttributeRoll(slot, "minor_1", new("optimal_range_weapon_minor", 0.2));
            var ranged = Stats(b, ctx);
            CloseTo(ranged.Of("equipped_weapon_optimal_range"), 26 * 1.2, 1e-12);
            CloseTo(ranged.Of("equipped_weapon_long_range"), 105 * 1.2, 1e-12);
            CloseTo(ranged.Of("equipped_weapon_max_range"), 105, 1e-12);

            b = b.WithAttributeRoll(slot, "minor_1", new("reload_speed_weapon_minor", 0.1));
            var reloading = Stats(b, ctx);
            CloseTo(reloading.Of("equipped_weapon_tactical_reload_time"), 1.86 / 1.1, 1e-12);
            CloseTo(reloading.Of("equipped_weapon_reload_time"), 2.16 / 1.1, 1e-12);

            var unselected = Stats(b, Weapon(slot == "primary_weapon" ? "secondary_weapon" : "primary_weapon"));
            foreach (var id in new[] { "equipped_weapon_long_range", "equipped_weapon_max_range", "equipped_weapon_tactical_reload_time" })
            {
                Assert.True(Math.Abs(unselected.Of(id)) <= 1e-12, $"{slot}: unselected {id} = {unselected.Of(id)}");
            }
        }
    }

    /// <summary>G288: channels a weapon doesn't have stay 0, and community reload estimates remain.</summary>
    [Fact]
    public void G288UnavailableChannelsStayZero()
    {
        var shotgun = Stats(Build().WithEquipment("primary_weapon", "primary_weapon_custom_m870_mcs"), Weapon("primary_weapon"));
        CloseTo(shotgun.Of("equipped_weapon_long_range"), 40, 1e-12);
        CloseTo(shotgun.Of("equipped_weapon_max_range"), 75, 1e-12);
        CloseTo(shotgun.Of("equipped_weapon_tactical_reload_time"), 0, 1e-12);
        foreach (var (slot, item) in new[] { ("primary_weapon", "primary_weapon_police_ump_45"), ("sidearm", "sidearm_police_686_magnum") })
        {
            var stats = Stats(Build().WithEquipment(slot, item), Weapon(slot));
            foreach (var id in new[] { "equipped_weapon_long_range", "equipped_weapon_max_range", "equipped_weapon_tactical_reload_time" })
            {
                Assert.True(Math.Abs(stats.Of(id)) <= 1e-12, $"{item}: {id} = {stats.Of(id)}");
            }
            Assert.True(stats.Of("equipped_weapon_reload_time") > 0, $"{item} lost its community reload estimate");
        }
    }

    private static BuildSnapshot G316WeaponBuild(string slot, string name) =>
        BuildSnapshot.Create(Compiled.Pack, "G316 weapon reading").WithEquipment(slot, slot + "_" + name.Replace(" ", "_", StringComparison.Ordinal).ToLowerInvariant());

    /// <summary>G316: Bullet King keeps its 100 rounds with Bullet Hell, and its no-reload cycle DPS is its firing DPS.</summary>
    [Fact]
    public void G316BulletKingKeepsOneHundredRoundsWithBulletHell()
    {
        foreach (var slot in new[] { "primary_weapon", "secondary_weapon" })
        {
            var other = slot == "primary_weapon" ? "secondary_weapon" : "primary_weapon";
            var b = G316WeaponBuild(slot, "Bullet King").WithEquipment(other, other + "_acr");
            var ctx = Weapon(slot);
            var baseline = Stats(b, ctx);
            CloseTo(baseline.Of("weapon_magazine_size"), 100, 1e-12);
            CloseTo(baseline.Of("equipped_weapon_magazine_size"), 100, 1e-12);

            b = b.WithTalentSelection(slot, "talent", "weapon_talent_bullet_hell");
            var active = Stats(b, ctx);
            CloseTo(active.Of("weapon_no_reload"), 1, 1e-12);
            CloseTo(active.Of("weapon_magazine_size"), 100, 1e-12);
            CloseTo(active.Of("equipped_weapon_magazine_size"), 100, 1e-12);
            CloseTo(active.Of("equipped_weapon_magazine_damage"), active.Of("equipped_weapon_average_bodyshot_damage") * 100, 1e-8);
            CloseTo(active.Of("equipped_weapon_reload_cycle_dps"), active.Of("equipped_weapon_bodyshot_dps"), 1e-8);
            Assert.True(active.Of("equipped_weapon_reload_cycle_dps") > 0, $"{slot}: Bullet Hell must retain positive firing DPS");
        }
    }

    /// <summary>G316: Oh Carol's base damage applies once, with attribute, SHD Watch and equipment upgrade bonuses added (F13).</summary>
    [Fact]
    public void G316OhCarolBaseIsAppliedOnceWithDamageBonuses()
    {
        foreach (var slot in new[] { "primary_weapon", "secondary_weapon" })
        {
            var ctx = Weapon(slot);
            var clean = Stats(G316WeaponBuild(slot, "Oh Carol"), ctx);
            CloseTo(clean.Of("weapon_base_damage"), 433563.5, 1e-12);
            CloseTo(clean.Of("equipped_weapon_damage"), 433563.5, 1e-8);

            var b = G316WeaponBuild(slot, "Oh Carol")
                .WithAttributeRoll(slot, "core_1", new("marksman_rifle_damage_weapon_core", .15))
                .WithAccountUpgrade("watch_weapon_damage", 30)
                .WithEquipmentUpgrade(slot, slot + "_oh_carol", 20);
            var active = Stats(b, ctx);
            CloseTo(active.Of("weapon_base_damage"), 433563.5, 1e-12);
            CloseTo(active.Of("weapon_damage"), .26, 1e-12);
            CloseTo(active.Of("marksman_rifle_damage"), .15, 1e-12);
            CloseTo(active.Of("equipped_weapon_damage"), 433563.5 * 1.41, 1e-8);
        }
    }

    /// <summary>G316: magazine readings for Froth and the Vector SBR, Froth's damage pending, and controls unchanged.</summary>
    [Fact]
    public void G316MagazineReadingsKeepFrothDamagePendingAndControlsUnchanged()
    {
        foreach (var slot in new[] { "primary_weapon", "secondary_weapon" })
        {
            var froth = Stats(G316WeaponBuild(slot, "Froth"), Weapon(slot));
            CloseTo(froth.Of("weapon_base_damage"), 39306.5, 1e-12);
            CloseTo(froth.Of("weapon_magazine_size"), 19, 1e-12);

            var vector = Stats(G316WeaponBuild(slot, "Vector SBR 9mm"), Weapon(slot));
            CloseTo(vector.Of("weapon_base_damage"), 39306.5, 1e-12);
            CloseTo(vector.Of("weapon_magazine_size"), 33, 1e-12);

            var m44 = Stats(G316WeaponBuild(slot, "Classic M44 Carbine"), Weapon(slot));
            CloseTo(m44.Of("weapon_base_damage"), 433563.5, 1e-12);
        }
    }
}
