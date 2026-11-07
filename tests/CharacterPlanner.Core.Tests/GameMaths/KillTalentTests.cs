using CharacterPlanner.Core.Builds;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>Kill-triggered talents (F35), ported from Go's kill_talents_g400_test.go.</summary>
public sealed class KillTalentTests
{
    private static BuildSnapshot Weapon(string slot, string item, string talent = "")
    {
        var build = Build()
            .WithEquipment("gloves", "gloves_n_5_11_tactical_gloves")
            .WithAttributeRoll("gloves", "minor_1", new("critical_hit_chance_gear_minor", 0.06))
            .WithEquipment("chest", "chest_n_5_11_tactical_chest")
            .WithEquipment(slot, item);
        return talent.Length > 0 ? build.WithTalentSelection(slot, "talent", talent) : build;
    }

    /// <summary>
    /// G400: the expected kill rate against the selected enemy drives Salvage's refills and Headhunter's and
    /// Determined's empowered shots, on practical DPS only, and talent bonuses don't change the kill rate itself.
    /// </summary>
    [Fact]
    public void G400KillTriggeredTalents()
    {
        var sidearm = Division.Weapon("sidearm");
        var s = Stats(Weapon("sidearm", "sidearm_m1911"), sidearm);
        Assert.True(s.Of("practical_shots_to_kill") > 1, $"shots to kill the default target = {s.Of("practical_shots_to_kill")}");
        var wantShots = s.Of("enemy_target_armor") / (s.Of("practical_base_shot_damage") * s.Of("out_of_cover_damage_factor") * (1 + s.Of("damage_to_armor")))
            + s.Of("enemy_target_health") / (s.Of("practical_base_shot_damage") * s.Of("out_of_cover_damage_factor") * (1 + s.Of("health_damage")));
        CloseTo(s.Of("practical_shots_to_kill"), wantShots, 1e-9);
        CloseTo(s.Of("practical_kills_per_shot"), 1 / wantShots, 1e-12);

        var salvage = Stats(Weapon("sidearm", "sidearm_m1911", "weapon_talent_salvage"), sidearm);
        CloseTo(salvage.Of("practical_rounds_returned_per_shot"), 0.7 * s.Of("practical_kills_per_shot") * s.Of("equipped_weapon_magazine_size") / 2, 1e-12);
        Assert.True(salvage.Of("equipped_weapon_practical_reload_cycle_dps") > s.Of("equipped_weapon_practical_reload_cycle_dps"), "Salvage's refills raise practical reload-cycle DPS");

        var primary = Division.Weapon("primary_weapon");
        var rifle = Stats(Weapon("primary_weapon", "primary_weapon_acr_ss"), primary);
        var crit = rifle.Of("effective_critical_hit_chance");
        var head = rifle.Of("equipped_weapon_headshot_damage") * (1 - crit) + rifle.Of("equipped_weapon_critical_headshot_damage") * crit;
        var headshotKills = rifle.Of("practical_kills_per_shot") * 0.25;

        var h = Stats(Weapon("primary_weapon", "primary_weapon_acr_ss").WithTalentSelection("chest", "talent", "gear_talent_headhunter"), primary);
        var wantBonus = headshotKills * Math.Min(1.25 * head, 8 * rifle.Of("equipped_weapon_damage"));
        CloseTo(h.Of("practical_shot_bonus_damage"), wantBonus, 1e-6);
        CloseTo(h.Of("equipped_weapon_practical_shot_damage"), rifle.Of("equipped_weapon_practical_shot_damage") + wantBonus, 1e-6);
        CloseTo(h.Of("practical_shots_to_kill"), rifle.Of("practical_shots_to_kill"), 1e-9);

        var determined = Stats(Weapon("primary_weapon", "primary_weapon_acr_ss", "weapon_talent_determined"), primary);
        CloseTo(determined.Of("practical_shot_bonus_damage"), headshotKills * 0.75 * (head - rifle.Of("equipped_weapon_average_bodyshot_damage")), 1e-6);
        Assert.True(determined.Of("equipped_weapon_average_bodyshot_damage") == rifle.Of("equipped_weapon_average_bodyshot_damage"), "kill talents leave the theoretical figures alone");
    }
}
