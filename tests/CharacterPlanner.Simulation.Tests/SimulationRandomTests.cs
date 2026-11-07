namespace CharacterPlanner.Simulation.Tests;

public sealed class SimulationRandomTests
{
    [Fact]
    public void VersionOneHasPinnedIntegerVectors()
    {
        var random = new SimulationRandom(1);
        Assert.Equal(new ulong[] { 6791897765849424158UL, 17405687883870564846UL, 834844254806117752UL, 14341179868655528873UL },
            Enumerable.Range(0, 4).Select(_ => random.NextUInt64()));
    }

    [Fact]
    public void FightStreamsCanBeRunInAnyOrder()
    {
        var expected = new SimulationRandom(7, 3).NextUInt64();
        for (ulong i = 0; i < 3; i++) { _ = new SimulationRandom(7, i).NextUInt64(); }
        Assert.Equal(expected, new SimulationRandom(7, 3).NextUInt64());
        Assert.NotEqual(expected, new SimulationRandom(7, 4).NextUInt64());
    }

    [Fact]
    public void UniformValuesAreAlwaysInTheHalfOpenInterval()
    {
        var random = new SimulationRandom(ulong.MaxValue, ulong.MaxValue);
        for (var i = 0; i < 10000; i++) { var value = random.NextDouble(); Assert.True(value >= 0 && value < 1); }
    }
}
