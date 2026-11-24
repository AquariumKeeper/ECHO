namespace CharacterPlanner.Simulation.Tests;

public sealed class NextHitBonusStateTests
{
    [Fact]
    public void AHeadshotKillArmsASharedCappedAmountThatLapsesOrIsSpent()
    {
        // ADR 0023 (Headhunter): 125% of the killing pellet, capped, for 30 s; the next landed hit spends it, a new kill replaces it.
        var state = new NextHitBonusState(1.25, 30);
        Assert.Equal(0, state.Waiting(0));
        state.Arm(1000, 10_000, 1);
        Assert.Equal(1250, state.Waiting(1));
        Assert.Equal(1250, state.Waiting(30.9));
        Assert.Equal(0, state.Waiting(31));
        state.Arm(1000, 800, 40);
        Assert.Equal(800, state.Waiting(40));
        state.Arm(100, 800, 41);
        Assert.Equal(125, state.Waiting(41));
        state.Spend();
        Assert.Equal(0, state.Waiting(41));
        // A swap brings it down to the drawn weapon's cap, and a swap back doesn't raise it.
        state.Arm(1000, 10_000, 45);
        state.Recap(300);
        state.Recap(10_000);
        Assert.Equal(300, state.Waiting(45));
        // Nothing negative arms it.
        state.Arm(-5, 800, 50);
        Assert.Equal(0, state.Waiting(50));
    }
}
