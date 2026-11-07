namespace CharacterPlanner.Simulation.Tests;

public sealed class ShockFireStateTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(10)]
    public void OneAttackersShareIsBlockedAcrossAggregateAttempts(int attackers)
    {
        var state = new ShockFireState(attackers);
        var blocked = Enumerable.Range(0, 100).Count(_ => state.Blocks(true));
        Assert.Equal(100 / attackers, blocked);
    }

    [Fact]
    public void NonShockedAttemptsDoNotAdvanceOrDiscardFractionalCredit()
    {
        var state = new ShockFireState(3);
        Assert.False(state.Blocks(true));
        Assert.False(state.Blocks(false));
        Assert.False(state.Blocks(true));
        Assert.False(state.Blocks(false));
        Assert.True(state.Blocks(true));
        Assert.False(state.Blocks(true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidAttackerCountIsRefused(int attackers) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShockFireState(attackers));
}
