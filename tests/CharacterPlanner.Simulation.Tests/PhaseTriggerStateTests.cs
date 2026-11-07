using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

public sealed class PhaseTriggerStateTests
{
    private static ImmutableDictionary<string, string> Empty => ImmutableDictionary<string, string>.Empty;
    private static SimTriggerDefinition Magazine => new()
    {
        Talent = "test",
        Key = "phase",
        Kind = "magazine",
        Phases = [new() { Until = 0.5, Value = "top" }, new() { Until = 1, Value = "bottom" }]
    };
    private static SimTriggerDefinition Cycle => Magazine with
    {
        Kind = "cycle",
        Phases = [new() { Until = 5, Value = "first" }, new() { Until = 10, Value = "second" }]
    };

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(2, 2, 0)]
    [InlineData(5, 5, 0)]
    [InlineData(7, 5, 2)]
    [InlineData(10, 5, 5)]
    [InlineData(12, 7, 5)]
    [InlineData(25, 15, 10)]
    public void CycleMeasurementClipsPartialPeriodsAndIncludesZeroPhases(double duration, double first, double second)
    {
        var values = new PhaseTriggerState([Cycle]).MeasureCycles(duration, TestContext.Current.CancellationToken)["phase"];
        Assert.Equal(first, values["first"]);
        Assert.Equal(second, values["second"]);
        Assert.Equal(duration, values.Values.Sum());
    }

    [Fact]
    public void MeasurementCombinesRepeatedValuesAndIgnoresMagazineReadings()
    {
        var rule = Cycle with { Phases = [new() { Until = 1, Value = "on" }, new() { Until = 3, Value = "off" }, new() { Until = 4, Value = "on" }] };
        var state = new PhaseTriggerState([rule, Magazine with { Key = "magazine" }]);
        var values = Assert.Single(state.MeasureCycles(6, TestContext.Current.CancellationToken)).Value;
        Assert.Equal(3, values["on"]);
        Assert.Equal(3, values["off"]);
        Assert.Equal("on", state.Context(Empty, 0, 10, 10)["phase"]);
        Assert.Equal(1, state.MeasureCycles(1, TestContext.Current.CancellationToken)["phase"]["on"]);
    }

    [Fact]
    public void ShortCyclesAreMeasuredWithoutEnumeratingEveryPeriod()
    {
        var state = new PhaseTriggerState([Cycle with { Phases = [new() { Until = 0.00000025, Value = "first" }, new() { Until = 0.000001, Value = "second" }] }]);
        var values = state.MeasureCycles(600, TestContext.Current.CancellationToken)["phase"];
        Assert.Equal(150, values["first"], 8);
        Assert.Equal(450, values["second"], 8);
    }

    [Fact]
    public void DecimalWrapMeasurementConservesDuration()
    {
        var state = new PhaseTriggerState([Cycle with { Phases = [new() { Until = 0.1, Value = "first" }, new() { Until = 0.3, Value = "second" }] }]);
        foreach (var time in new[] { Math.BitDecrement(3 * 0.3), 3 * 0.3, Math.BitIncrement(3 * 0.3), 0.95 })
        {
            var values = state.MeasureCycles(time, TestContext.Current.CancellationToken)["phase"];
            Assert.Equal(time, values.Values.Sum(), 12);
            Assert.Equal(time > 0.9 ? 0.3 + time - 0.9 : 0.3, values["first"], 12);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void MeasurementRejectsInvalidTime(double time) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PhaseTriggerState([Cycle]).MeasureCycles(time, TestContext.Current.CancellationToken));

    [Fact]
    public void MeasurementObservesCancellationAndRefusesUnrepresentablePeriods()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() => new PhaseTriggerState([Cycle]).MeasureCycles(10, source.Token));
        var state = new PhaseTriggerState([Cycle with { Phases = [new() { Until = 1e-20, Value = "only" }] }]);
        Assert.Equal("time-not-advancing", Assert.Throws<SimulationException>(() => state.MeasureCycles(1, TestContext.Current.CancellationToken)).Code);
    }

    [Theory]
    [InlineData(10, "top")]
    [InlineData(6, "top")]
    [InlineData(5, "bottom")]
    [InlineData(0, "bottom")]
    public void MagazinePhasesUsePreShotRoundsAndExactBoundaries(double remaining, string expected)
    {
        var state = new PhaseTriggerState([Magazine]);
        Assert.Equal(expected, state.Context(Empty.Add("phase", "manual"), 0, remaining, 10)["phase"]);
        Assert.Equal("top", state.Context(Empty, 0, 10, 10)["phase"]);
    }

    [Theory]
    [InlineData(0, "first")]
    [InlineData(4.999, "first")]
    [InlineData(5, "second")]
    [InlineData(10, "first")]
    [InlineData(25, "second")]
    public void CyclesWrapAndAdvanceAtExactBoundaries(double time, string expected) =>
        Assert.Equal(expected, new PhaseTriggerState([Cycle]).Context(Empty, time, 10, 10)["phase"]);

    [Theory]
    [InlineData(0, 5)]
    [InlineData(5, 10)]
    [InlineData(10, 15)]
    [InlineData(25, 30)]
    public void NextTransitionFindsStrictFutureCycleBoundaries(double time, double expected) =>
        Assert.Equal(expected, new PhaseTriggerState([Magazine, Cycle with { Key = "cycle" }]).NextTransition(time));

    [Fact]
    public void DecimalCycleWrapUsesTheSameBoundariesForReadsAndScheduling()
    {
        var state = new PhaseTriggerState([Cycle with { Phases = [new() { Until = 0.1, Value = "first" }, new() { Until = 0.3, Value = "second" }] }]);
        var wrap = 3 * 0.3;
        Assert.Equal("first", state.Context(Empty, wrap, 10, 10)["phase"]);
        var next = Assert.IsType<double>(state.NextTransition(wrap));
        Assert.Equal("second", state.Context(Empty, next, 10, 10)["phase"]);
        Assert.True(state.NextTransition(next) > next);
        Assert.Null(new PhaseTriggerState([Magazine]).NextTransition(1));
    }

    [Theory]
    [InlineData(double.Epsilon)]
    [InlineData(1e-20)]
    public void UnrepresentableCycleAdvancementFailsExplicitly(double period)
    {
        var state = new PhaseTriggerState([Cycle with { Phases = [new() { Until = period, Value = "only" }] }]);
        Assert.Equal("time-not-advancing", Assert.Throws<SimulationException>(() => state.NextTransition(1)).Code);
        Assert.Equal("time-not-advancing", Assert.Throws<SimulationException>(() => state.Context(Empty, 1, 10, 10)).Code);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NextTransitionRejectsInvalidTime(double time) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PhaseTriggerState([Cycle]).NextTransition(time));

    [Theory]
    [InlineData(100, 100, "0")]
    [InlineData(71, 100, "29")]
    [InlineData(2, 3, "33")]
    [InlineData(0, 3, "100")]
    public void MissingPercentIsFlooredWithoutLosingWholePercentages(double remaining, double capacity, string expected) =>
        Assert.Equal(expected, new PhaseTriggerState([Magazine with { Percent = true, Phases = [] }]).Context(Empty, 0, remaining, capacity)["phase"]);

    [Fact]
    public void InvalidPhaseDefinitionsAndSideEffectsAreRefused()
    {
        Assert.Throws<SimulationException>(() => new PhaseTriggerState([Magazine with { On = "hit" }]));
        Assert.Throws<SimulationException>(() => new PhaseTriggerState([Cycle with { Refill = true }]));
        Assert.Throws<SimulationException>(() => new PhaseTriggerState([Magazine with { Phases = [] }]));
        Assert.Throws<SimulationException>(() => new PhaseTriggerState([Cycle with { Percent = true }]));
        Assert.Throws<SimulationException>(() => new PhaseTriggerState([Magazine with { Phases = [new() { Until = 0.5, Value = "partial" }] }]));
        Assert.Throws<SimulationException>(() => new PhaseTriggerState([Cycle with { Phases = [new() { Until = double.PositiveInfinity, Value = "bad" }] }]));
        Assert.Throws<SimulationException>(() => new PhaseTriggerState([Cycle with { Phases = [new() { Until = 5, Value = "a" }, new() { Until = 4, Value = "b" }] }]));
        Assert.Throws<SimulationException>(() => new PhaseTriggerState([Magazine, Cycle]));
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(11, 10)]
    [InlineData(1, 0)]
    [InlineData(0.5, 10)]
    [InlineData(double.NaN, 10)]
    public void InvalidMagazineReadingsAreRefused(double remaining, double capacity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PhaseTriggerState([Magazine]).Context(Empty, 0, remaining, capacity));

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidTimeIsRefused(double time) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PhaseTriggerState([Cycle]).Context(Empty, time, 10, 10));

    private static CompiledPackExpressions Pack()
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        return Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
    }

    [Fact]
    public void CommittedOptimistAndMeasuredPhaseValuesFeedCore()
    {
        var pack = Pack();
        var context = Empty.Add(GamePack.WeaponSlotSelector, "primary_weapon");
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr");
        var optimist = pack.Pack.Simulation.Triggers.Single(rule => rule.Talent == "weapon_talent_optimist");
        var state = new PhaseTriggerState([optimist]);
        build = build.WithTalentSelection("primary_weapon", "talent", optimist.Talent);
        var full = state.Resolve(pack, build, context, 0, 30, 30).Calculation.Stats["weapon_damage"];
        Assert.Equal(0.175, state.Resolve(pack, build, context, 0, 15, 30).Calculation.Stats["weapon_damage"] - full, 8);
        var measured = pack.Pack.Simulation.Triggers.Single(rule => rule.Talent == "weapon_talent_measured");
        state = new PhaseTriggerState([measured]);
        build = build.WithTalentSelection("primary_weapon", "talent", measured.Talent);
        Assert.True(state.Resolve(pack, build, context, 0, 30, 30).Calculation.Stats["equipped_weapon_rpm"] >
            state.Resolve(pack, build, context, 0, 15, 30).Calculation.Stats["equipped_weapon_rpm"]);
        Assert.Throws<SimulationException>(() => new PhaseTriggerState([Magazine]).Resolve(pack, build, context, 0, 30, 30));
    }

    [Fact]
    public void CommittedDecoyKingCycleBindsNumericPhasesAndWraps()
    {
        var pack = Pack();
        var rule = pack.Pack.Simulation.Triggers.Single(rule => rule.Talent == "gear_talent_decoy_king");
        var state = new PhaseTriggerState([rule]);
        var build = BuildSnapshot.Create(pack.Pack);
        foreach (var (time, expected) in new[] { (0d, 5d), (5d, 10d), (20d, 25d), (40d, 5d) })
        { Assert.Equal(expected, state.Resolve(pack, build, Empty, time, 10, 10).Assumptions.Numeric[rule.Key]); }
        var values = state.MeasureCycles(45, TestContext.Current.CancellationToken)[rule.Key];
        Assert.Equal(45, values.Values.Sum(), 8);
        Assert.Equal(10, values["5"]);
    }
}
