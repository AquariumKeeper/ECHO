using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// Practical and theoretical weapon DPS, including shotgun pellets and expected-value talents (F30), ported from Go's
/// critical_average_g109_test.go, bodyshot_dps_g110_test.go, reload_cycle_g111_test.go, practical_dps_g361_test.go,
/// out_of_cover_g374_test.go, shell_reload_g378_test.go and expected_talents_g399_test.go.
/// </summary>
public sealed class WeaponDpsTests
{
    /// <summary>The committed pack with one stat's formula replaced, as Go's edit of c.Pack.Stats and ValidatePack.</summary>
    private static CompiledPackExpressions WithFormula(string stat, string formula)
    {
        var pack = Compiled.Pack with { Stats = Compiled.Pack.Stats.Select(definition => definition.ID == stat ? definition with { Formula = formula } : definition).ToImmutableArray() };
        var changed = PackValidator.Validate(pack);
        Assert.True(changed.Compiled is not null, $"invalid fixture: {string.Join("; ", changed.Diagnostics.Select(diagnostic => diagnostic.Message))}");
        return changed.Compiled;
    }

    private static ImmutableDictionary<string, double> StatsIn(CompiledPackExpressions compiled, BuildSnapshot build, ImmutableDictionary<string, string> context) =>
        BuildResolver.Resolve(compiled, build, context).Calculation.Stats;

    private static BuildSnapshot Acr() => Build().WithEquipment("primary_weapon", "primary_weapon_acr");

    /// <summary>G109: the raw critical hit chance total is kept, its probability is bounded to [0, 0.6], and the average bodyshot uses the probability.</summary>
    [Fact]
    public void G109CriticalChanceBoundsAndRawTotals()
    {
        var b = Acr();
        foreach (var (raw, probability) in new[] { (-1.0, 0.0), (0, 0), (.35, .35), (.6, .6), (1, .6) })
        {
            var s = StatsIn(WithFormula("critical_hit_chance", raw.ToString("R", CultureInfo.InvariantCulture)), b, Division.Weapon("primary_weapon"));
            CloseTo(s.Of("critical_hit_chance"), raw, 1e-12);
            CloseTo(s.Of("effective_critical_hit_chance"), probability, 1e-12);
            CloseTo(s.Of("equipped_weapon_average_bodyshot_damage"), 58896.5 * (1 + probability * .25), 1e-8);
        }
    }

    /// <summary>G109: weapon mods apply to their own weapon only, and Perfect Boiling Point's phases set the critical chance.</summary>
    [Fact]
    public void G109ModsOwnerScopeAndBoilingPointPhases()
    {
        var b = Acr().WithEquipment("secondary_weapon", "secondary_weapon_acr")
            .WithModSelection("primary_weapon", "optics", "weapon_mod_optics_c79_scope_3_4x");
        var r = Stats(b, Division.Weapon("primary_weapon"));
        CloseTo(r.Of("effective_critical_hit_chance"), .05, 1e-12);
        CloseTo(r.Of("equipped_weapon_average_bodyshot_damage"), 58896.5 * 1.0125, 1e-8);
        CloseTo(Stats(b, Division.Weapon("secondary_weapon")).Of("equipped_weapon_average_bodyshot_damage"), 58896.5, 1e-8);
        CloseTo(Stats(b).Of("equipped_weapon_average_bodyshot_damage"), 0, 1e-12);
        b = b.WithModSelection("primary_weapon", "optics", "weapon_mod_optics_exps3_holo_sight");
        CloseTo(Stats(b, Division.Weapon("primary_weapon")).Of("equipped_weapon_average_bodyshot_damage"), 58896.5, 1e-8);
        b = b.WithEquipment("primary_weapon", "primary_weapon_steamer")
            .WithTalentSelection("primary_weapon", "talent", "weapon_talent_perfect_boiling_point");
        foreach (var phase in new[] { "early", "late" })
        {
            r = Stats(b, Context(("weapon_slot", "primary_weapon"), ("boiling_point_phase", phase)));
            var probability = phase == "late" ? .6 : 0;
            CloseTo(r.Of("effective_critical_hit_chance"), probability, 1e-12);
            CloseTo(r.Of("equipped_weapon_average_bodyshot_damage"), r.Of("equipped_weapon_damage") * (1 + probability * .25), 1e-8);
        }
    }

    /// <summary>G110: bodyshot DPS converts shot damage by the weapon's cadence, only for the weapon in hand and its own mods; an unavailable cadence gives 0.</summary>
    [Fact]
    public void G110BodyshotDpsConversionAndOwnerScope()
    {
        var b = Acr().WithEquipment("secondary_weapon", "secondary_weapon_acr")
            .WithModSelection("primary_weapon", "optics", "weapon_mod_optics_c79_scope_3_4x");
        var primary = Division.Weapon("primary_weapon");
        var r = Stats(b, primary);
        CloseTo(r.Of("weapon_rpm"), 650, 1e-12);
        CloseTo(r.Of("equipped_weapon_bodyshot_dps"), 58896.5 * 1.0125 * 650 / 60, 1e-8);
        CloseTo(Stats(b, Division.Weapon("secondary_weapon")).Of("equipped_weapon_bodyshot_dps"), 58896.5 * 650 / 60, 1e-8);
        CloseTo(Stats(b).Of("equipped_weapon_bodyshot_dps"), 0, 1e-12);
        CloseTo(Stats(b.WithEquipment("primary_weapon", ""), primary).Of("equipped_weapon_bodyshot_dps"), 0, 1e-12);
        b = b.WithModSelection("primary_weapon", "magazine", "weapon_mod_magazine_sturdy_extended_5_56_mag");
        var modded = Stats(b, primary);
        Assert.True(modded.Of("equipped_weapon_magazine_size") > r.Of("equipped_weapon_magazine_size") && modded.Of("equipped_weapon_reload_time") > r.Of("equipped_weapon_reload_time"),
            "magazine mod did not change capacity and reload time");
        CloseTo(modded.Of("equipped_weapon_bodyshot_dps"), r.Of("equipped_weapon_bodyshot_dps"), 1e-8);

        r = StatsIn(WithFormula("rate_of_fire", "-2"), b, primary);
        Assert.True(r.Of("equipped_weapon_average_bodyshot_damage") > 0, "missing shot damage");
        CloseTo(r.Of("equipped_weapon_bodyshot_dps"), 0, 1e-12);
    }

    /// <summary>G110: Measured's phases change both damage and rate of fire.</summary>
    [Fact]
    public void G110MeasuredPhasesUseBothDamageAndCadence()
    {
        var b = Acr().WithTalentSelection("primary_weapon", "talent", "weapon_talent_measured");
        foreach (var (phase, damage, rate) in new[] { ("top", .75, 1.25), ("bottom", 1.3, .82) })
        {
            var r = Stats(b, Context(("weapon_slot", "primary_weapon"), ("measured_phase", phase)));
            CloseTo(r.Of("equipped_weapon_bodyshot_dps"), 58896.5 * damage * 650 * rate / 60, 1e-8);
        }
    }

    /// <summary>G111: magazine damage, firing duration and reload-cycle DPS follow magazine mods, reload speed and the weapon in hand.</summary>
    [Fact]
    public void G111MagazineAndRepeatingReloadCycle()
    {
        var primary = Division.Weapon("primary_weapon");
        var b = Acr();
        var r = Stats(b, primary);
        CloseTo(r.Of("equipped_weapon_magazine_damage"), 58896.5 * 30, 1e-8);
        CloseTo(r.Of("equipped_weapon_firing_duration"), 30.0 * 60 / 650, 1e-12);
        CloseTo(r.Of("equipped_weapon_reload_cycle_dps"), 58896.5 * 30 / ((30.0 * 60 / 650) + r.Of("weapon_reload_time")), 1e-8);
        b = b.WithModSelection("primary_weapon", "magazine", "weapon_mod_magazine_sturdy_extended_5_56_mag");
        r = Stats(b, primary);
        CloseTo(r.Of("equipped_weapon_magazine_damage"), 58896.5 * 50, 1e-8);
        CloseTo(r.Of("equipped_weapon_reload_cycle_dps"), 58896.5 * 50 / ((50.0 * 60 / 650) + r.Of("equipped_weapon_reload_time")), 1e-8);
        b = b.WithModSelection("primary_weapon", "magazine", "").WithTalentSelection("primary_weapon", "talent", "weapon_talent_fast_hands");
        var plain = Stats(b, primary);
        var stacked = Stats(b, Context(("weapon_slot", "primary_weapon"), ("fast_hands_stacks", "10")));
        Assert.True(stacked.Of("equipped_weapon_reload_cycle_dps") > plain.Of("equipped_weapon_reload_cycle_dps"), "reload speed did not improve cycle DPS");
        CloseTo(stacked.Of("equipped_weapon_magazine_damage"), plain.Of("equipped_weapon_magazine_damage"), 1e-8);
        b = b.WithModSelection("primary_weapon", "optics", "weapon_mod_optics_c79_scope_3_4x").WithEquipment("secondary_weapon", "secondary_weapon_acr");
        CloseTo(Stats(b, primary).Of("equipped_weapon_reload_cycle_dps"), Stats(b, Division.Weapon("secondary_weapon")).Of("equipped_weapon_reload_cycle_dps") * 1.0125, 1e-8);
        CloseTo(Stats(b).Of("equipped_weapon_reload_cycle_dps"), 0, 1e-12);
        b = b.WithEquipment("primary_weapon", "");
        CloseTo(Stats(b, primary).Of("equipped_weapon_reload_cycle_dps"), 0, 1e-12);
    }

    /// <summary>G111: an unavailable rate of fire, magazine size or reload speed gives a reload-cycle DPS of 0 without an error.</summary>
    [Fact]
    public void G111UnavailableCycleInputsReturnZero()
    {
        foreach (var id in new[] { "rate_of_fire", "magazine_size_percent", "reload_speed" })
        {
            var r = BuildResolver.Resolve(WithFormula(id, "-2"), Acr(), Division.Weapon("primary_weapon"));
            CloseTo(r.Calculation.Stats.Of("equipped_weapon_reload_cycle_dps"), 0, 1e-12);
            Assert.DoesNotContain(r.Diagnostics, diagnostic => diagnostic.Severity == "error");
        }
    }

    private static ImmutableDictionary<string, double> G361Stats(string item, params (string Key, string Value)[] context) =>
        Stats(Build().WithEquipment("primary_weapon", item), Context([("weapon_slot", "primary_weapon"), .. context]));

    /// <summary>G361: practical weapon DPS as pack stats.</summary>
    [Fact]
    public void G361PracticalDpsFormula()
    {
        var s = G361Stats("primary_weapon_acr");
        var crit = Math.Min(1, s.Of("effective_critical_hit_chance"));
        var head = (s.Of("equipped_weapon_headshot_damage") * (1 - crit)) + (s.Of("equipped_weapon_critical_headshot_damage") * crit);
        var shot = 0.7 * ((0.75 * s.Of("equipped_weapon_average_bodyshot_damage")) + (0.25 * head));
        CloseTo(s.Of("equipped_weapon_practical_shot_damage"), shot, 1e-9);
        CloseTo(s.Of("equipped_weapon_practical_firing_dps"), shot * s.Of("equipped_weapon_rpm") / 60, 1e-9);
        CloseTo(s.Of("equipped_weapon_practical_reload_cycle_dps"),
            shot * s.Of("equipped_weapon_magazine_size") / (s.Of("equipped_weapon_firing_duration") + s.Of("equipped_weapon_reload_time")), 1e-9);
    }

    /// <summary>G361: the hit-rate assumption scales practical DPS, out-of-range inputs fall back to the default, and shotguns count by pellet.</summary>
    [Fact]
    public void G361PracticalDpsAssumptionsAndShotguns()
    {
        var plain = G361Stats("primary_weapon_acr").Of("equipped_weapon_practical_firing_dps");
        CloseTo(G361Stats("primary_weapon_acr", ("practical_hit_rate", "35")).Of("equipped_weapon_practical_firing_dps"), plain / 2, 1e-9);
        CloseTo(G361Stats("primary_weapon_acr", ("practical_hit_rate", "0")).Of("equipped_weapon_practical_reload_cycle_dps"), 0, 1e-12);
        // Out-of-range inputs fall back to the default.
        CloseTo(G361Stats("primary_weapon_acr", ("practical_hit_rate", "250")).Of("equipped_weapon_practical_firing_dps"), plain, 1e-9);
        var shotgun = Compiled.Pack.Items.FirstOrDefault(item => item.Slot == "primary_weapon" && item.Tags.Contains("weapon_type_shotgun"));
        Assert.True(shotgun is not null, "no primary shotgun");
        // G414: shotguns count, with the assumptions as shares of their 8 pellets, so the same expectation holds.
        var s = G361Stats(shotgun.ID);
        var crit = Math.Min(1, s.Of("effective_critical_hit_chance"));
        var head = (s.Of("equipped_weapon_headshot_damage") * (1 - crit)) + (s.Of("equipped_weapon_critical_headshot_damage") * crit);
        CloseTo(s.Of("equipped_weapon_practical_shot_damage"), 0.7 * ((0.75 * s.Of("equipped_weapon_average_bodyshot_damage")) + (0.25 * head)), 1e-9);
        Assert.True(s.Of("equipped_weapon_pellets") == 8 && s.Of("equipped_weapon_practical_reload_cycle_dps") > 0,
            $"{shotgun.ID}: {s.Of("equipped_weapon_pellets")} pellets, practical {s.Of("equipped_weapon_practical_reload_cycle_dps")}");
        Assert.Equal(1, G361Stats("primary_weapon_acr").Of("equipped_weapon_pellets"));
    }

    /// <summary>
    /// G374: Damage to Targets Out of Cover applies to weapon damage in the target estimates only while Target Out of
    /// Cover is on.
    /// </summary>
    [Fact]
    public void G374OutOfCoverDamage()
    {
        var b = Build().WithEquipment("primary_weapon", "primary_weapon_stoner_lamg")
            .WithAttributeRoll("primary_weapon", "core_2", new("dtoc_weapon_core", 0.12));
        var off = Stats(b, Division.Weapon("primary_weapon"));
        var on = Stats(b, Context(("weapon_slot", "primary_weapon"), ("enemy_out_of_cover", "1")));
        Assert.True(off.Of("out_of_cover_damage_factor") == 1, $"factor while off = {off.Of("out_of_cover_damage_factor")}");
        CloseTo(on.Of("out_of_cover_damage_factor"), 1 + on.Of("dtoc"), 1e-12);
        Assert.True(on.Of("dtoc") >= 0.12 && on.Of("equipped_weapon_target_bodyshots") < off.Of("equipped_weapon_target_bodyshots"),
            $"dtoc {on.Of("dtoc")}; bodyshots on {on.Of("equipped_weapon_target_bodyshots")}, off {off.Of("equipped_weapon_target_bodyshots")}");
        Assert.True(on.Of("equipped_weapon_practical_reload_cycle_dps") == off.Of("equipped_weapon_practical_reload_cycle_dps"), "weapon DPS previews must not change with cover state");
    }

    /// <summary>G378: a shell-by-shell reload grows with the final magazine.</summary>
    [Fact]
    public void G378ShellReloads()
    {
        static ImmutableDictionary<string, double> Of(string item) => Stats(Build().WithEquipment("primary_weapon", item), Division.Weapon("primary_weapon"));
        foreach (var item in new[] { "primary_weapon_thorn", "primary_weapon_spas_12" })
        {
            var s = Of(item);
            CloseTo(s.Of("equipped_weapon_reload_time"), 5.5, 1e-9);
            CloseTo(s.Of("equipped_weapon_reload_time"),
                (s.Of("weapon_reload_first_shell") + (s.Of("weapon_reload_per_shell") * (s.Of("equipped_weapon_magazine_size") - 1))) / (1 + s.Of("effective_reload_speed")), 1e-12);
        }
        var acr = Of("primary_weapon_acr");
        Assert.True(acr.Of("weapon_reload_per_shell") == 0, "magazine weapons must not get a per-shell reload");
        CloseTo(acr.Of("equipped_weapon_reload_time"), acr.Of("weapon_reload_time") / (1 + acr.Of("effective_reload_speed")), 1e-12);
    }

    private static BuildSnapshot G399Weapon(string weapon) => Build()
        .WithEquipment("primary_weapon", weapon)
        .WithEquipment("gloves", "gloves_n_5_11_tactical_gloves")
        .WithAttributeRoll("gloves", "minor_1", new("critical_hit_chance_gear_minor", 0.06));

    /// <summary>
    /// G399: Boomerang returns rounds and empowers the next shot in expectation, and a scoped First Blood adds one
    /// headshot per magazine, on practical DPS only.
    /// </summary>
    [Fact]
    public void G399ExpectedValueTalents()
    {
        var primary = Division.Weapon("primary_weapon");
        var b = G399Weapon("primary_weapon_m16a2");
        var plain = Stats(b, primary);
        var crit = plain.Of("effective_critical_hit_chance");
        Assert.True(crit > 0, $"the test build needs critical hit chance, got {crit}");

        var s = Stats(b.WithTalentSelection("primary_weapon", "talent", "weapon_talent_boomerang"), primary);
        var returned = 0.5 * 0.7 * crit;
        CloseTo(s.Of("practical_rounds_returned_per_shot"), returned, 1e-12);
        CloseTo(s.Of("equipped_weapon_practical_shot_damage"), plain.Of("equipped_weapon_practical_shot_damage") * (1 + (0.4 * returned)), 1e-6);
        var stretch = 1 / (1 - returned);
        CloseTo(s.Of("equipped_weapon_practical_reload_cycle_dps"),
            s.Of("equipped_weapon_practical_shot_damage") * s.Of("equipped_weapon_magazine_size") * stretch / ((s.Of("equipped_weapon_firing_duration") * stretch) + s.Of("equipped_weapon_reload_time")), 1e-6);
        Assert.True(s.Of("equipped_weapon_average_bodyshot_damage") == plain.Of("equipped_weapon_average_bodyshot_damage"), "Boomerang leaves the theoretical figures alone");

        var rifle = G399Weapon("primary_weapon_m700_carbon");
        plain = Stats(rifle, primary);
        var firstBlood = rifle.WithTalentSelection("primary_weapon", "talent", "weapon_talent_first_blood");
        Assert.True(Stats(firstBlood, primary).Of("practical_magazine_bonus_damage") == 0, "First Blood needs the scoped assumption");
        var p = Stats(firstBlood, Context(("weapon_slot", "primary_weapon"), ("first_blood_scoped", "true")));
        var head = (p.Of("equipped_weapon_headshot_damage") * (1 - crit)) + (p.Of("equipped_weapon_critical_headshot_damage") * crit);
        var gain = 0.7 * (1 - 0.25) * (head - p.Of("equipped_weapon_average_bodyshot_damage"));
        CloseTo(p.Of("practical_magazine_bonus_damage"), gain, 1e-6);
        CloseTo(p.Of("equipped_weapon_practical_reload_cycle_dps") - plain.Of("equipped_weapon_practical_reload_cycle_dps"),
            gain / (p.Of("equipped_weapon_firing_duration") + p.Of("equipped_weapon_reload_time")), 1e-6);
    }
}
