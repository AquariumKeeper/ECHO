namespace CharacterPlanner.Simulation.Tests;

public sealed class TimedArmorRepairQueueTests
{
    [Fact]
    public void OverlapsKeepIndependentRatesAndExactExpiries()
    {
        var queue = new TimedArmorRepairQueue();
        queue.Enqueue(10, 5, 0);
        Assert.Equal(2, queue.Rate);
        queue.Advance(1);
        queue.Enqueue(9, 3, 1);
        Assert.Equal(5, queue.Rate);
        Assert.Equal(4, queue.NextExpiry);
        queue.Advance(4);
        Assert.Equal(2, queue.Rate);
        Assert.Equal(5, queue.NextExpiry);
        queue.Advance(5);
        Assert.Equal(0, queue.Rate);
        Assert.Null(queue.NextExpiry);
    }

    [Fact]
    public void FullArmorTimeCanBeDiscardedWithoutBankingBudgets()
    {
        var queue = new TimedArmorRepairQueue();
        queue.Enqueue(10, 5, 0);
        queue.Advance(10);
        Assert.Equal(0, queue.Rate);
        Assert.Null(queue.NextExpiry);
        queue.Enqueue(5, 2, 10);
        Assert.Equal(2.5, queue.Rate);
        Assert.Equal(12, queue.NextExpiry);
    }

    [Fact]
    public void SmallSurvivingRatesAreNotLostWhenLargeRatesExpire()
    {
        var queue = new TimedArmorRepairQueue();
        queue.Enqueue(1e200, 1, 0);
        queue.Enqueue(1, 2, 0);
        queue.Advance(1);
        Assert.Equal(0.5, queue.Rate);
        queue.Advance(2);
        Assert.Equal(0, queue.Rate);
    }

    [Fact]
    public void InvalidEnqueuesAndAdvancesAreAtomic()
    {
        var queue = new TimedArmorRepairQueue();
        queue.Enqueue(10, 5, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.Enqueue(-1, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.Enqueue(1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.Enqueue(1, double.NaN, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.Enqueue(1, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.Advance(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.Advance(-1));
        Assert.Equal("repair-rate-invalid", Assert.Throws<SimulationException>(() => queue.Enqueue(double.MaxValue, 0.5, 0)).Code);
        Assert.Equal("repair-rate-invalid", Assert.Throws<SimulationException>(() => queue.Enqueue(double.Epsilon, 600, 0)).Code);
        Assert.Equal(2, queue.Rate);
        Assert.Equal(5, queue.NextExpiry);
    }

    [Fact]
    public void RateOverflowAndUnrepresentableExpiryFailExplicitly()
    {
        var queue = new TimedArmorRepairQueue();
        queue.Enqueue(double.MaxValue, 1, 0);
        Assert.Equal("repair-overflow", Assert.Throws<SimulationException>(() => queue.Enqueue(double.MaxValue, 1, 0)).Code);
        Assert.Equal(double.MaxValue, queue.Rate);
        queue.Advance(double.MaxValue);
        Assert.Equal("repair-rate-invalid", Assert.Throws<SimulationException>(() => queue.Enqueue(1, 1, double.MaxValue)).Code);
        Assert.Equal(0, queue.Rate);
    }
}
