using System.Collections.Immutable;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

public sealed class HeadshotCycleStateTests
{
    private static readonly SimHeadshotCycle Cycle = new() { Talent = "t", WeaponTag = "mmr", Key = "waiting", Repair = "r", BonusArmorCap = "c", Allowance = "a", KillFrom = 3 };

    [Fact]
    public void HeadshotsStepThroughBonusArmorRefillThenAll()
    {
        // ADR 0021 (R126): the first sets up the bonus, the second the armor, the third the refill; past that a headshot must kill and does
        // all three.
        var state = new HeadshotCycleState(Cycle, 0);
        Assert.Equal(new HeadshotCycleSteps(true, false, false), state.Shot(true, false));
        Assert.True(state.BonusWaiting);
        Assert.Equal(new HeadshotCycleSteps(false, true, false), state.Shot(true, false));
        Assert.Equal(new HeadshotCycleSteps(false, false, true), state.Shot(true, false));
        Assert.Equal(new HeadshotCycleSteps(true, true, true), state.Shot(true, true));
        Assert.Equal(4, state.Count);
    }

    [Fact]
    public void AMissStartsAgainUnlessTheAllowanceForgivesIt()
    {
        var state = new HeadshotCycleState(Cycle, 1);
        state.Shot(true, false);
        state.Shot(true, false);
        // A body hit is forgiven once: the count stays.
        Assert.Equal(default, state.Shot(false, false));
        Assert.Equal(2, state.Count);
        // From the kill threshold a headshot that doesn't kill is a miss too; the allowance is spent, so the cycle starts again.
        state.Shot(true, false);
        Assert.Equal(3, state.Count);
        state.Shot(true, false);
        Assert.Equal((0, false), (state.Count, state.BonusWaiting));
        // A reset makes the allowance whole again.
        state.Shot(true, false);
        state.Shot(false, false);
        Assert.Equal(1, state.Count);
    }

    [Fact]
    public void TheBonusWaitsForTheNextHeadshotAndShowsInTheKey()
    {
        var state = new HeadshotCycleState(Cycle, 0);
        var empty = ImmutableDictionary<string, string>.Empty;
        Assert.False(state.Context(empty.Add("waiting", "true")).ContainsKey("waiting"));
        state.Shot(true, false);
        Assert.Equal("true", state.Context(empty)["waiting"]);
        state.HeadshotLanded();
        Assert.False(state.Context(empty).ContainsKey("waiting"));
        // A swap starts the cycle again.
        state.Shot(true, false);
        state.Reset();
        Assert.Equal((0, false), (state.Count, state.BonusWaiting));
    }
}
