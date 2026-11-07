using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Legacy.Tests;

public sealed class LoadoutResolverTests
{
    private static GamePack Pack => new()
    {
        Level = 1,
        Stats = [new() { ID = "strength", Name = "Strength", Formula = "10" }],
        Slots = [new() { ID = "main", Name = "Main Hand", Weapon = true }, new() { ID = "off", Name = "Off Hand" }],
        Items = [new() { ID = "shield", Name = "Shield", Slot = "off" }, new() { ID = "sword", Name = "Sword", Slot = "main" }]
    };

    [Fact]
    public void UnknownAndWrongSlotItemsAreDiagnosedAndUnknownBuildSlotsAreIgnored()
    {
        var pack = Pack;
        var build = BuildSnapshot.Create(pack).WithEquipment("main", "missing").WithEquipment("off", "sword").WithEquipment("stale", "missing");
        var result = LoadoutResolver.Resolve(pack, build);
        Assert.Empty(result.Equipped);
        Assert.Equal(("error", "unknown-item", "main"), (result.Diagnostics[0].Severity, result.Diagnostics[0].Code, result.Diagnostics[0].Subject));
        Assert.Equal(("error", "wrong-slot", "off"), (result.Diagnostics[1].Severity, result.Diagnostics[1].Code, result.Diagnostics[1].Subject));
        Assert.Equal(2, result.Diagnostics.Length);
    }

    [Fact]
    public void EquippedItemsFollowAuthoredSlotOrderAndOnlyWeaponsDependOnTheActiveWeapon()
    {
        var pack = Pack with { Slots = [Pack.Slots[1], Pack.Slots[0]] };
        var build = BuildSnapshot.Create(pack).WithEquipment("main", "sword").WithEquipment("off", "shield");
        var result = LoadoutResolver.Resolve(pack, build);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(["shield", "sword"], result.Equipped.Select(entry => entry.Item.ID));
        var (shield, sword) = (result.Equipped[0], result.Equipped[1]);
        var none = ImmutableDictionary<string, string>.Empty;
        var main = none.Add(GamePack.WeaponSlotSelector, "main");
        Assert.True(shield.EffectsActive(none));
        Assert.False(sword.EffectsActive(none));
        Assert.True(sword.EffectsActive(main));
        Assert.False(sword.EffectsActive(none.Add(GamePack.WeaponSlotSelector, "off")));
    }
}
