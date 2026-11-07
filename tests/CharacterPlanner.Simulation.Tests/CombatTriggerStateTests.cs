using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

public sealed class CombatTriggerStateTests
{
    private static ImmutableDictionary<string, string> Empty => ImmutableDictionary<string, string>.Empty;
    private static CompiledPackExpressions Pack()
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        return Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(3.5, "4")]
    [InlineData(100, "100")]
    public void CommittedRangerRoundsDistanceToWholeMeters(double distance, string expected)
    {
        var pack = Pack();
        var rule = pack.Pack.Simulation.Triggers.Single(rule => rule.Talent == "weapon_talent_ranger");
        var state = new CombatTriggerState(pack, [rule]);
        Assert.Equal(expected, state.Context(Empty.Add(rule.Key, "99"), distance, 0, 100)[rule.Key]);
        Assert.Equal("trigger-reading-out-of-range", Assert.Throws<SimulationException>(() => state.Context(Empty, 101, 0, 100)).Code);
    }

    [Theory]
    [InlineData(14.999, "close", true)]
    [InlineData(15, "mid", true)]
    [InlineData(20, "mid", true)]
    [InlineData(20.001, "mid", false)]
    [InlineData(25, "far", false)]
    [InlineData(1000, "far", false)]
    public void DistancePhasesAndInclusiveWithinUseDeclaredBoundaries(double distance, string phase, bool active)
    {
        var pack = Pack();
        var rules = pack.Pack.Simulation.Triggers.Where(rule => rule.Talent is "gear_talent_challenger" or "gear_talent_pack_instincts");
        var state = new CombatTriggerState(pack, rules);
        var context = state.Context(Empty.Add("challenger_active", "true"), distance, 0, 100);
        Assert.Equal(phase, context["pack_instincts_range"]);
        Assert.Equal(active, context.ContainsKey("challenger_active"));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(5, false)]
    [InlineData(6, true)]
    [InlineData(8, true)]
    public void BrazenReadsPriorHitsAndUsesAnInclusiveThreshold(int hits, bool active)
    {
        var pack = Pack();
        var state = new CombatTriggerState(pack, pack.Pack.Simulation.Triggers.Where(rule => rule.Talent == "weapon_talent_brazen"));
        var context = state.Context(Empty.Add("brazen_next_shot_active", "true"), 0, hits, 100);
        Assert.Equal(hits.ToString(System.Globalization.CultureInfo.InvariantCulture), context["brazen_previous_shot_pellets"]);
        Assert.Equal(active, context.ContainsKey("brazen_next_shot_active"));
        Assert.Throws<SimulationException>(() => state.Context(Empty, 0, 101, 100));
    }

    [Theory]
    [InlineData(100, "100")]
    [InlineData(99.999, "90")]
    [InlineData(90, "90")]
    [InlineData(89.999, "80")]
    [InlineData(0, "0")]
    public void TargetPercentageFloorsToTheCommittedGrid(double percent, string expected)
    {
        var pack = Pack();
        var rule = pack.Pack.Simulation.Triggers.Single(rule => rule.Talent == "weapon_talent_blossom_harvest" && rule.Kind == "target");
        Assert.Equal(expected, new CombatTriggerState(pack, [rule]).Context(Empty, 0, 0, percent)[rule.Key]);
    }

    [Theory]
    [InlineData(1, 57, "57")]
    [InlineData(0.1, 30, "30")]
    public void ExactTargetTiersAreNotFlooredOneStepLow(double step, double remaining, string expected)
    {
        // 100 * (57 / 100) is 56.99999999999999; the pool multiplies first and the reading tolerates the rest.
        var pool = new TargetPool(0, 100);
        pool.Apply(100 - remaining);
        Assert.Equal(remaining, pool.RemainingPercent(0, 100));
        var committed = Pack();
        var rule = committed.Pack.Simulation.Triggers.Single(rule => rule.Talent == "weapon_talent_blossom_harvest" && rule.Kind == "target");
        var pack = committed with
        {
            Pack = committed.Pack with
            {
                NumericContextInputs =
            [.. committed.Pack.NumericContextInputs.Select(input => input.ID == rule.Key ? input with { Step = step } : input)]
            }
        };
        Assert.Equal(expected, new CombatTriggerState(pack, [rule]).Context(Empty, 0, 0, pool.RemainingPercent(0, 100))[rule.Key]);
    }

    [Theory]
    [InlineData(double.NaN, 0, 100)]
    [InlineData(-1, 0, 100)]
    [InlineData(0, -1, 100)]
    [InlineData(0, 1001, 100)]
    [InlineData(0, 0, 101)]
    [InlineData(0, 0, double.PositiveInfinity)]
    public void InvalidReadingsAreRefused(double distance, int hits, double percent) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new CombatTriggerState(Pack(), []).Context(Empty, distance, hits, percent));

    [Fact]
    public void SideEffectsUnknownRulesAndDuplicateOwnersAreRefused()
    {
        var pack = Pack();
        var rule = pack.Pack.Simulation.Triggers.Single(rule => rule.Talent == "weapon_talent_ranger");
        Assert.Throws<SimulationException>(() => new CombatTriggerState(pack, [rule with { Refill = true }]));
        Assert.Throws<SimulationException>(() => new CombatTriggerState(pack, [rule with { Key = "unknown" }]));
        Assert.Throws<SimulationException>(() => new CombatTriggerState(pack, [rule, rule]));
    }

    [Fact]
    public void CommittedRangerDamageIsResolvedByCore()
    {
        var pack = Pack();
        var rule = pack.Pack.Simulation.Triggers.Single(rule => rule.Talent == "weapon_talent_ranger");
        var state = new CombatTriggerState(pack, [rule]);
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr")
            .WithTalentSelection("primary_weapon", "talent", rule.Talent);
        var context = Empty.Add(GamePack.WeaponSlotSelector, "primary_weapon");
        var close = state.Resolve(build, context, 0, 0, 100).Calculation.Stats["equipped_weapon_damage"];
        var far = state.Resolve(build, context, 20, 0, 100).Calculation.Stats["equipped_weapon_damage"];
        Assert.True(far > close);
    }
}
