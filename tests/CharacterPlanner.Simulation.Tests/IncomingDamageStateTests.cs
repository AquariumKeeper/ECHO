namespace CharacterPlanner.Simulation.Tests;

public sealed class IncomingDamageStateTests
{
    [Fact]
    public void RegenerationUsesElapsedTimeAndActualPoolUpdates()
    {
        var state = new IncomingDamageState(20, 5, 30);
        Assert.Equal(new IncomingRepair(0, 0), state.Regenerate(2, 1));
        state.Apply(15, 1);
        Assert.Equal(new IncomingRepair(3, 0), state.Regenerate(2, 2.5));
        state.Repair(2, 0, 2.5);
        Assert.Equal(new IncomingRepair(5, 0), state.Regenerate(double.MaxValue, 3));
        Assert.Equal(0, state.BonusArmor);
        Assert.Equal(new IncomingRepair(0, 0), state.Regenerate(2, 3));
        state.Apply(100, 4);
        Assert.Equal(new IncomingRepair(0, 0), state.Regenerate(2, 5));
    }

    [Fact]
    public void InvalidRegenerationLeavesClockAndPoolsUnchanged()
    {
        var state = new IncomingDamageState(20, 0, 30);
        state.Apply(10, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Regenerate(-1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Regenerate(double.PositiveInfinity, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Regenerate(1, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Regenerate(1, 0));
        Assert.Equal(new IncomingRepair(1, 0), state.Regenerate(2, 1.5));
    }

    [Fact]
    public void RepairsClampToMissingPoolsWithoutGrantingBonusArmor()
    {
        var state = new IncomingDamageState(20, 5, 30);
        state.Apply(35, 1);
        Assert.Equal(new IncomingRepair(20, 10), state.Repair(100, 100, 2));
        Assert.Equal(0, state.BonusArmor);
        Assert.Equal(new IncomingRepair(0, 0), state.Repair(100, 100, 2));
        Assert.True(state.Apply(20, 3).ArmorBroken);
        Assert.Equal(1, state.FirstArmorBreak);
    }

    [Fact]
    public void DepletionRepairDoesNotAbsorbTheCurrentHitsOverflow()
    {
        var state = new IncomingDamageState(20, 0, 30);
        var hit = state.Apply(25, 1, 10);
        Assert.True(hit.ArmorBroken);
        Assert.Equal(10, hit.ArmorRepaired);
        Assert.Equal(5, hit.HealthDamage);
        Assert.Equal(10, state.Armor);
        Assert.Equal(25, state.Health);
        Assert.True(state.Apply(15, 2, 100).ArmorBroken);
        Assert.Equal(20, state.Armor);
        Assert.Equal(20, state.Health);
        Assert.Equal(1, state.FirstArmorBreak);
    }

    [Fact]
    public void ImmediateRecoveryCannotReviveADownedPlayer()
    {
        var state = new IncomingDamageState(20, 0, 5);
        var hit = state.Apply(100, 1, 20);
        Assert.True(hit.Downed);
        Assert.True(hit.ArmorBroken);
        // The depletion repair of the downing hit is not applied or reported.
        Assert.Equal(0, hit.ArmorRepaired);
        Assert.Equal(0, state.Armor);
        Assert.Equal(new IncomingRepair(0, 0), state.Repair(100, 100, 2));
        Assert.True(state.Downed);
        Assert.Equal(1, state.DownAt);
    }

    [Fact]
    public void InvalidRepairsDoNotMutatePoolsOrClock()
    {
        var state = new IncomingDamageState(20, 0, 30);
        state.Apply(5, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Repair(-1, 0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Repair(0, double.NaN, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Repair(5, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Apply(1, 2, double.PositiveInfinity));
        Assert.Equal(new IncomingRepair(3, 0), state.Repair(3, 2, 1.5));
        Assert.Equal(18, state.Armor);
    }
    [Fact]
    public void HitsSpendBonusArmorThenArmorThenHealthAndDiscardOverkill()
    {
        var state = new IncomingDamageState(100, 50, 100);
        Assert.Equal(new IncomingHit(40, 40, 0, 0, false, false), state.Apply(40, 0));
        Assert.Equal(new IncomingHit(60, 10, 50, 0, false, false), state.Apply(60, 1));
        Assert.Equal(new IncomingHit(70, 0, 50, 20, true, false), state.Apply(70, 2));
        Assert.Equal(2, state.FirstArmorBreak);
        Assert.Equal(new IncomingHit(80, 0, 0, 80, false, true), state.Apply(500, 3));
        Assert.True(state.Downed);
        Assert.Equal(3, state.DownAt);
        Assert.Equal(new IncomingHit(0, 0, 0, 0, false, false), state.Apply(10, 4));
        Assert.Equal(2, state.FirstArmorBreak);
        Assert.Equal(3, state.DownAt);
    }

    [Fact]
    public void ExactDepletionBreaksArmorAndZeroInitialArmorDoesNot()
    {
        var armored = new IncomingDamageState(20, 10, 30);
        Assert.Equal(new IncomingHit(30, 10, 20, 0, true, false), armored.Apply(30, 1));
        Assert.Equal(30, armored.Health);
        var unarmored = new IncomingDamageState(0, 10, 30);
        Assert.False(unarmored.Apply(40, 1).ArmorBroken);
        Assert.Null(unarmored.FirstArmorBreak);
        Assert.Equal(1, unarmored.DownAt);
    }

    [Fact]
    public void ZeroDamageChangesNoPoolsOrEvents()
    {
        var state = new IncomingDamageState(20, 10, 30);
        Assert.Equal(new IncomingHit(0, 0, 0, 0, false, false), state.Apply(0, 0));
        Assert.Equal(20, state.Armor);
        Assert.Equal(10, state.BonusArmor);
        Assert.Equal(30, state.Health);
        Assert.Null(state.FirstArmorBreak);
        Assert.Null(state.DownAt);
    }

    [Fact]
    public void InvalidHitsDoNotMutatePoolsOrAdvanceTheClock()
    {
        var state = new IncomingDamageState(20, 10, 30);
        state.Apply(5, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Apply(double.NaN, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Apply(-1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Apply(5, double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Apply(5, 0));
        Assert.Equal(5, state.BonusArmor);
        Assert.Equal(5, state.Apply(5, 1.5).DamageTaken);
        Assert.Equal(20, state.Armor);
    }

    [Theory]
    [InlineData(-1, 0, 1)]
    [InlineData(0, -1, 1)]
    [InlineData(0, 0, 0)]
    [InlineData(double.NaN, 0, 1)]
    [InlineData(0, double.PositiveInfinity, 1)]
    [InlineData(0, 0, double.PositiveInfinity)]
    public void InvalidInitialPoolsAreRefused(double armor, double bonus, double health) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new IncomingDamageState(armor, bonus, health));

    [Fact]
    public void LargeFinitePoolsDoNotRequireAnOverflowingCombinedTotal()
    {
        var state = new IncomingDamageState(double.MaxValue, double.MaxValue, double.MaxValue);
        Assert.Equal(double.MaxValue, state.Apply(double.MaxValue, 0).BonusArmorDamage);
        Assert.True(state.Apply(double.MaxValue, 1).ArmorBroken);
        Assert.True(state.Apply(double.MaxValue, 2).Downed);
        Assert.Equal(0, state.Health);
    }

    [Fact]
    public void TemporaryArmorIsSpentFirstSoonestToLapseFirstAndExpires()
    {
        var state = new IncomingDamageState(20, 5, 30);
        Assert.Equal(20, state.MaxArmor);
        Assert.Equal(10, state.GrantBonusArmor(10, 0, 10));
        Assert.Equal(4, state.GrantBonusArmor(4, 1, 5));
        Assert.Equal(19, state.BonusArmor);
        // Grants go first, the one lapsing at 5 before the one at 10; the static bonus never lapses, so it goes last.
        var hit = state.Apply(12, 2);
        Assert.Equal((12.0, 0.0), (hit.BonusArmorDamage, hit.ArmorDamage));
        Assert.Equal(7, state.BonusArmor);
        // A repair at 10 updates the pools, and the remaining grant (2) has lapsed: only the static 5 is left.
        state.Repair(0, 0, 10);
        Assert.Equal(5, state.BonusArmor);
        hit = state.Apply(6, 10);
        Assert.Equal((5.0, 1.0), (hit.BonusArmorDamage, hit.ArmorDamage));
        Assert.Equal(0, state.BonusArmor);
    }

    [Fact]
    public void TemporaryArmorNeedsALaterExpiryAndIsNotGrantedWhileDowned()
    {
        var state = new IncomingDamageState(20, 0, 30);
        Assert.Throws<SimulationException>(() => state.GrantBonusArmor(5, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.GrantBonusArmor(-1, 1, 2));
        state.Apply(100, 2);
        Assert.True(state.Downed);
        Assert.Equal(0, state.GrantBonusArmor(5, 2, 10));
        Assert.Equal(0, state.BonusArmor);
    }
}
