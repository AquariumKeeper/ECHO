using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

public sealed class MagazinePhaseStateTests
{
    private static SimTriggerDefinition Rule => new()
    {
        Talent = "test",
        Key = "phase",
        Kind = "magazine",
        Phases = [new() { Until = 0.5, Value = "top" }, new() { Until = 1, Value = "bottom" }]
    };

    [Fact]
    public void ObservationMeasuresPostShotPhasesAndClosesOnReloadCompletion()
    {
        var state = new MagazinePhaseState([Rule], 4);
        state.Observe(3, 0);
        state.Observe(2, 1);
        state.Observe(0, 2);
        state.Observe(4, 5);
        var values = state.Measure(5.5)["phase"];
        Assert.Equal(1.5, values["top"]);
        Assert.Equal(4, values["bottom"]);
        Assert.Equal(5.5, values.Values.Sum());
    }

    [Fact]
    public void PercentHistogramIsBoundedAndFloorsActualAmmoReadings()
    {
        var state = new MagazinePhaseState([Rule with { Percent = true, Phases = [] }], 3);
        state.Observe(2, 0);
        state.Observe(1, 1);
        state.Observe(0, 2);
        var values = state.Measure(4)["phase"];
        Assert.Equal(101, values.Count);
        Assert.Equal(1, values["33"]);
        Assert.Equal(1, values["66"]);
        Assert.Equal(2, values["100"]);
        Assert.Equal(0, values["0"]);
    }

    [Fact]
    public void FutureReadsAndRejectedObservationsCannotChangeHistory()
    {
        var state = new MagazinePhaseState([Rule], 4);
        Assert.Equal(100, state.Measure(100)["phase"]["top"]);
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Observe(5, 2));
        state.Observe(0, 1);
        Assert.Equal(1, state.Measure(2)["phase"]["top"]);
        Assert.Equal(1, state.Measure(2)["phase"]["bottom"]);
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Observe(4, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Measure(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Measure(0));
    }

    [Fact]
    public void SameTimeRefillHasNoEmptyIntervalAndRepeatedValuesCombine()
    {
        var state = new MagazinePhaseState([Rule with { Phases = [new() { Until = 0.5, Value = "same" }, new() { Until = 1, Value = "same" }] }], 4);
        state.Observe(0, 1);
        state.Observe(4, 1);
        Assert.Equal(3, Assert.Single(state.Measure(3)["phase"]).Value);
    }

    [Fact]
    public void NoMagazineRulesProduceNoHistogramAndInvalidCapacityIsRefused()
    {
        Assert.Empty(new MagazinePhaseState([], 4).Measure(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MagazinePhaseState([Rule], 0));
    }
}
