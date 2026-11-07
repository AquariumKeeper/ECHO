using CharacterPlanner.Core.Builds;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>Talent copy rules (F14), ported from Go's abridged_g404_test.go.</summary>
public sealed class TalentCopyTests
{
    /// <summary>
    /// G404: Abridged applies the secondary weapon's talent while the primary is active, only for two non-named,
    /// non-exotic weapons of the same type.
    /// </summary>
    [Fact]
    public void G404AbridgedCopiesTheSecondaryTalent()
    {
        static BuildSnapshot Loadout(string primaryWeapon, string secondary, bool abridged)
        {
            var b = Build().WithEquipment("primary_weapon", primaryWeapon)
                .WithEquipment("secondary_weapon", secondary)
                .WithTalentSelection("secondary_weapon", "talent", "weapon_talent_near_sighted")
                .WithEquipment("mask", "mask_tinkerer");
            return abridged ? b.WithTalentSelection("mask", "talent", "gear_talent_abridged") : b;
        }
        var primary = Division.Weapon("primary_weapon");
        var without = Stats(Loadout("primary_weapon_acr", "secondary_weapon_acr", false), primary).Of("stability");
        var with = Stats(Loadout("primary_weapon_acr", "secondary_weapon_acr", true), primary).Of("stability");
        CloseTo(with - without, 0.8, 1e-12);
        var resolved = Resolve(Loadout("primary_weapon_acr", "secondary_weapon_acr", true), primary);
        Assert.True(resolved.Calculation.Breakdowns.TryGetValue("stability", out var breakdown)
            && breakdown.Contributions.Any(contribution => contribution.Source.Name == "Near Sighted (Abridged)"), "the breakdown names the copied talent and Abridged");
        var secondaryWeapon = Division.Weapon("secondary_weapon");
        CloseTo(Stats(Loadout("primary_weapon_acr", "secondary_weapon_acr", true), secondaryWeapon).Of("stability"),
            Stats(Loadout("primary_weapon_acr", "secondary_weapon_acr", false), secondaryWeapon).Of("stability"), 1e-12);
        CloseTo(Stats(Loadout("primary_weapon_military_l86_lsw", "secondary_weapon_acr", true), primary).Of("stability"),
            Stats(Loadout("primary_weapon_military_l86_lsw", "secondary_weapon_acr", false), primary).Of("stability"), 1e-12);
    }
}
