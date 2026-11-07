namespace CharacterPlanner.Simulation.Tests;

public sealed class ShieldStateTests
{
    [Fact]
    public void FractionalRefillSnapsToCapacityAndNeverReportsNegativeRepair()
    {
        const double capacity = 966.5643123171955;
        var shield = new ShieldState(capacity, 258.11142173406057, 3);
        shield.Apply(863.8588099759255, 0);
        shield.Advance(1);
        Assert.True(shield.Advance(10).Repaired > 0);
        Assert.Equal(capacity, shield.Health);
        Assert.True(shield.Full);
        Assert.Equal(0, shield.Advance(11).Repaired);
        Assert.Equal(capacity, shield.Health);
    }

    [Fact]
    public void BreakDiscardsOverflowAndCooldownRaisesAtExactTime()
    {
        var shield = new ShieldState(20, 0, 2);
        shield.Advance(1);
        Assert.Equal(new ShieldHit(20, true), shield.Apply(100, 1));
        Assert.False(shield.IsUp);
        Assert.Null(shield.Advance(2.999).RaisedAt);
        Assert.Equal(3, shield.Advance(3).RaisedAt);
        Assert.True(shield.Full);
        Assert.Equal(new ShieldHit(5, false), shield.Apply(5, 3));
    }

    [Fact]
    public void ActiveRegenerationAndQueuedRepairCapWithoutBanking()
    {
        var shield = new ShieldState(20, 2, 3);
        shield.Advance(1);
        shield.Apply(10, 1);
        Assert.Equal(6, shield.Advance(2, 4).Repaired);
        Assert.Equal(4, shield.Advance(3, 100).Repaired);
        Assert.Equal(0, shield.Advance(4, 100).Repaired);
        shield.Apply(20, 4);
        Assert.Equal(0, shield.Advance(6, 100).Repaired);
        Assert.Equal(new ShieldRecovery(0, 7), shield.Advance(8, 100));
    }

    [Fact]
    public void InvalidHitsAndRatesLeaveShieldAndClockUnchanged()
    {
        var shield = new ShieldState(20, 2, 3);
        shield.Advance(1);
        shield.Apply(10, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => shield.Apply(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => shield.Apply(1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => shield.Advance(2, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => shield.Advance(0));
        Assert.Equal(1, shield.Advance(1.5).Repaired);
        Assert.Equal(11, shield.Health);
    }

    [Fact]
    public void HugeFiniteRegenerationCapsBeforeMultiplication()
    {
        var shield = new ShieldState(double.MaxValue, double.MaxValue, 3);
        shield.Apply(double.MaxValue / 2, 0);
        Assert.True(shield.Advance(10).Repaired > 0);
        Assert.True(shield.Full);
        Assert.Equal("repair-overflow", Assert.Throws<SimulationException>(() => shield.Advance(11, double.MaxValue)).Code);
        Assert.True(shield.Full);
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(1, -1, 1)]
    [InlineData(1, 0, 0)]
    [InlineData(double.NaN, 0, 1)]
    public void InvalidProfilesAreRefused(double health, double regeneration, double cooldown) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShieldState(health, regeneration, cooldown));

    [Fact]
    public void RepairCapsAtCapacityAndSkipsABrokenShield()
    {
        var shield = new ShieldState(100, 0, 5);
        Assert.Equal(100, shield.Capacity);
        shield.Advance(1);
        shield.Apply(30, 1);
        Assert.Equal(10, shield.Repair(10, 1));
        Assert.Equal(20, shield.Repair(50, 1));
        Assert.True(shield.Full);
        // Repairing needs the shield advanced to the same time.
        Assert.Throws<ArgumentOutOfRangeException>(() => shield.Repair(10, 2));
        shield.Advance(2);
        shield.Apply(500, 2);
        Assert.False(shield.IsUp);
        Assert.Equal(0, shield.Repair(10, 2));
    }
}
