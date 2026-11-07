using CharacterPlanner.Application.Commands;

namespace CharacterPlanner.Application.Tests.Commands;

public sealed class EquippedWeaponSlotsTests
{
    [Fact]
    public void ChoicesRetainPackOrderAndExcludeGearAndEmptyWeapons()
    {
        Assert.Empty(EquippedWeaponSlots.Read(Division.Rules, Division.Empty()));
        var build = Division.Empty().WithEquipment("sidearm", "sidearm_d50").WithEquipment("mask", "mask_striker_s_battlegear_mask")
            .WithEquipment("primary_weapon", "primary_weapon_acr");
        Assert.Equal(new[] { "primary_weapon", "sidearm" }, EquippedWeaponSlots.Read(Division.Rules, build).Select(slot => slot.ID));
    }
}
