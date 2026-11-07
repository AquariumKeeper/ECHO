using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

/// <summary>
/// Weapon falloff at the fight distance (R7's shape; the floor stat is R42's): full damage to optimal range, linear to long
/// range, then the floor. It applies only when the pack declares the floor.
/// </summary>
public sealed partial class DummyWeaponSimulatorTests
{
    private static readonly (string, double)[] Ranges = [("equipped_weapon_optimal_range", 20), ("equipped_weapon_long_range", 40)];

    private static double[] ShotDamage(CompiledPackExpressions pack, double? distance) =>
        [.. Run(pack, Build(pack), Context, new() { Duration = 5, Mode = SimulationMode.Random, Iterations = 1, DriveSimpleBuffs = true, CaptureFirstFightEvents = true, Distance = distance })
            .FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.Shot).Select(entry => entry.Damage)];

    [Theory]
    [InlineData(0, 10)]
    [InlineData(20, 10)]
    [InlineData(25, 8.75)]
    [InlineData(30, 7.5)]
    [InlineData(40, 5)]
    [InlineData(900, 5)]
    public void ShotDamageFallsOffFromOptimalToLongRangeThenHoldsTheFloor(double distance, double damage)
    {
        var pack = Tiny([.. Ranges, ("weapon_falloff_floor", 0.5)]);
        var shots = ShotDamage(pack, distance);
        Assert.NotEmpty(shots);
        Assert.All(shots, shot => Assert.Equal(damage, shot, 9));
        var result = Run(pack, Build(pack), Context, new() { Duration = 5, Mode = SimulationMode.Random, Iterations = 1, DriveSimpleBuffs = true, Distance = distance });
        Assert.Contains(result.Limitations, note => note.StartsWith("Weapon damage falls off at the fight distance", StringComparison.Ordinal));
        Assert.StartsWith("d2p-simple-buffs-v61/", result.Contract, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(50, 5)]
    [InlineData(60, 5)]
    [InlineData(61, 0)]
    public void NoDamageLandsBeyondMaximumRange(double distance, double damage)
    {
        // R42: the floor holds out to maximum range and nothing lands beyond it, even before long range.
        var pack = Tiny([.. Ranges, ("weapon_falloff_floor", 0.5), ("equipped_weapon_max_range", 60)]);
        Assert.All(ShotDamage(pack, distance), shot => Assert.Equal(damage, shot, 9));
        var early = Tiny([.. Ranges, ("weapon_falloff_floor", 0.5), ("equipped_weapon_max_range", 30)]);
        Assert.All(ShotDamage(early, 31), shot => Assert.Equal(0, shot));
    }

    [Fact]
    public void AFloorOfOneNeedsNoRanges()
    {
        // The ten weapons without source data (and Nemesis) have floor 1: no falloff, with or without range data.
        var unknown = Tiny(("weapon_falloff_floor", 1));
        Assert.All(ShotDamage(unknown, 500), shot => Assert.Equal(10, shot));
    }

    [Fact]
    public void WithoutADeclaredFloorOrADistanceDamageDoesNotFallOff()
    {
        // The committed pack declares no floor yet (R42), so a distance changes nothing and says so.
        var plain = Tiny(Ranges);
        Assert.All(ShotDamage(plain, 900), shot => Assert.Equal(10, shot));
        Assert.Contains(Run(plain, Build(plain), Context, new() { Duration = 5, Mode = SimulationMode.Random, Iterations = 1, DriveSimpleBuffs = true, Distance = 900 }).Limitations,
            note => note == "The fight distance doesn't change weapon damage: the pack declares no falloff floor");
        // Without a distance neither note appears, and a declared floor is unused.
        var floored = Tiny([.. Ranges, ("weapon_falloff_floor", 0.5)]);
        Assert.All(ShotDamage(floored, null), shot => Assert.Equal(10, shot));
        Assert.DoesNotContain(Run(floored, Build(floored), Context, new() { Duration = 5, Mode = SimulationMode.Random, Iterations = 1, DriveSimpleBuffs = true }).Limitations,
            note => note.Contains("fight distance", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, 20, 40)]
    [InlineData(1.5, 20, 40)]
    [InlineData(0.5, 0, 40)]
    [InlineData(0.5, 30, 20)]
    public void AnInvalidFloorOrRangeIsRefusedAtADistance(double floor, double optimal, double longRange)
    {
        var pack = Tiny(("weapon_falloff_floor", floor), ("equipped_weapon_optimal_range", optimal), ("equipped_weapon_long_range", longRange));
        Assert.Equal("weapon-stat-invalid", Assert.Throws<SimulationException>(() => ShotDamage(pack, 25)).Code);
        Assert.NotEmpty(ShotDamage(pack, null));
    }
}
