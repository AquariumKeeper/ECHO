using CharacterPlanner.Core.Engine;

namespace CharacterPlanner.Core.Tests;

public sealed class ModifierAggregatorTests
{
    private static readonly ModifierSource Source = new("item", "test", "Test");

    [Fact]
    public void ClampsLikeMathMaxSoNegativeZeroBecomesZero()
    {
        var result = ModifierAggregator.Aggregate(0, [new(Source, "override", -0.0)], min: 0);
        Assert.False(double.IsNegative(result.Final));
        Assert.False(result.Clamped);
    }

    [Fact]
    public void ReportsClampingOnlyWhenABoundChangedTheValue()
    {
        Assert.True(ModifierAggregator.Aggregate(5, [], min: 10).Clamped);
        Assert.True(ModifierAggregator.Aggregate(50, [], max: 10).Clamped);
        Assert.False(ModifierAggregator.Aggregate(5, [], min: 0, max: 10).Clamped);
        var nan = ModifierAggregator.Aggregate(double.NaN, []);
        Assert.True(double.IsNaN(nan.Final));
        Assert.False(nan.Clamped);
    }
}
