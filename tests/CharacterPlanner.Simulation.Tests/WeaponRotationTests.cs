using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

public sealed class WeaponRotationTests
{
    private static WeaponRotation Rotation(WeaponSwapMode mode = WeaponSwapMode.WhenEmpty)
    {
        var pack = new GamePack
        {
            ID = "rotation",
            Name = "Rotation",
            Version = "1",
            Level = 40,
            Slots = [new() { ID = "a", Name = "A", Weapon = true }, new() { ID = "b", Name = "B", Weapon = true }],
            Items = [new() { ID = "one", Name = "One", Slot = "a" }, new() { ID = "two", Name = "Two", Slot = "b" }],
            Stats = [new() { ID = "equipped_weapon_magazine_size", Formula = "1" },
                new() { ID = "equipped_weapon_rpm", Formula = "60" }, new() { ID = "equipped_weapon_reload_time", Formula = "2" }]
        };
        var compiled = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack).Compiled);
        var build = BuildSnapshot.Create(pack).WithEquipment("a", "one").WithEquipment("b", "two");
        return WeaponRotation.Create(compiled, build, ImmutableDictionary<string, string>.Empty, "a", "b", mode);
    }

    [Fact]
    public void ExternalKillsWaitForReloadAndDoNotConsumeAmmo()
    {
        var rotation = Rotation(WeaponSwapMode.AfterKill);
        rotation.Fire(0);
        Assert.Throws<ArgumentOutOfRangeException>(() => rotation.SwapAfterExternalKill(1));
        Assert.Equal("a", rotation.ActiveSlot);
        var swap = rotation.SwapAfterExternalKill(3);
        Assert.NotNull(swap);
        Assert.Equal(3, swap.StartedAt);
        Assert.Equal(4, swap.ReadyAt);
        Assert.Equal(1, rotation.Rounds("a"));
        Assert.Equal(1, rotation.Rounds("b"));
    }

    [Fact]
    public void ExternalKillsDoNotOverrideEmptyMagazinePolicy()
    {
        var rotation = Rotation();
        Assert.Null(rotation.SwapAfterExternalKill(0));
        Assert.Equal("a", rotation.ActiveSlot);
    }

    [Fact]
    public void EmptyMagazinesKeepAlternatingAndReloadOnArrivalWithoutRefillingHolsteredWeapon()
    {
        var rotation = Rotation();
        Assert.Equal(new WeaponAction(WeaponActionKind.Swap, "a", "b", 1, 2) { SwapCompletedAt = 2 }, rotation.Fire(0));
        Assert.Equal(0, rotation.Rounds("a"));
        Assert.Equal(1, rotation.Rounds("b"));
        Assert.Equal(new WeaponAction(WeaponActionKind.Swap, "b", "a", 3, 6) { ReloadedDestination = true, SwapCompletedAt = 4 }, rotation.Fire(2));
        Assert.Equal(1, rotation.Rounds("a"));
        Assert.Equal(0, rotation.Rounds("b"));
        Assert.Equal("a", rotation.ActiveSlot);
        // Both weapons stay in the rotation: the empty holstered weapon is reloaded when it is drawn again.
        Assert.Equal(new WeaponAction(WeaponActionKind.Swap, "a", "b", 7, 10) { ReloadedDestination = true, SwapCompletedAt = 8 }, rotation.Fire(6));
        Assert.Equal(0, rotation.Rounds("a"));
        Assert.Equal(1, rotation.Rounds("b"));
        Assert.Equal("b", rotation.ActiveSlot);
    }

    [Fact]
    public void KillRotationOnlySwapsOnKill()
    {
        var rotation = Rotation(WeaponSwapMode.AfterKill);
        Assert.Equal(WeaponActionKind.Reload, rotation.Fire(0).Kind);
        Assert.Equal(WeaponActionKind.Swap, rotation.Fire(3, killed: true).Kind);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidShotTimeLeavesStateUnchanged(double time)
    {
        var rotation = Rotation();
        Assert.Throws<ArgumentOutOfRangeException>(() => rotation.Fire(time));
        Assert.Equal("a", rotation.ActiveSlot);
        Assert.Equal(1, rotation.Rounds("a"));
        Assert.Equal(0, rotation.ReadyAt);
    }

    [Fact]
    public void CannotFireDuringSwap()
    {
        var rotation = Rotation();
        rotation.Fire(0);
        Assert.Throws<ArgumentOutOfRangeException>(() => rotation.Fire(1));
        Assert.Equal(1, rotation.Rounds("b"));
    }

    [Fact]
    public void SnapshotsResolveEachWeaponAsActive()
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(p => Path.GetFileName(p), p => (ReadOnlyMemory<byte>)File.ReadAllBytes(p));
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr")
            .WithEquipment("secondary_weapon", "secondary_weapon_police_m4");
        var rotation = WeaponRotation.Create(pack, build, ImmutableDictionary<string, string>.Empty,
            "primary_weapon", "secondary_weapon", WeaponSwapMode.AfterKill);
        var first = rotation.ActiveResolution.Calculation.Stats["equipped_weapon_rpm"];
        rotation.Fire(0, killed: true);
        Assert.Equal("secondary_weapon", rotation.ActiveSlot);
        Assert.NotEqual(first, rotation.ActiveResolution.Calculation.Stats["equipped_weapon_rpm"]);
    }
}
