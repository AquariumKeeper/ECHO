using System.Collections.Immutable;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Expressions;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Legacy.Tests;

public sealed class StatResolutionTests
{
    private static readonly ModifierSource Source = new("item", "test", "Test");

    [Fact]
    public void AggregationAddsFlatsThenCompoundsMultipliersBeforeClamping()
    {
        ImmutableArray<Contribution> contributions = [new(Source, "flat", 20), new(Source, "mult", .2), new(Source, "flat", 30), new(Source, "mult", .25)];
        var result = ModifierAggregator.Aggregate(100, contributions);
        Assert.Equal(225, result.Final, 10);
        Assert.Equal(contributions, result.Contributions);
        Assert.False(result.Clamped);
        Assert.Equal(200, ModifierAggregator.Aggregate(100, contributions, max: 200).Final);
        Assert.True(ModifierAggregator.Aggregate(100, contributions, max: 200).Clamped);
        Assert.Equal(7, ModifierAggregator.Aggregate(100, [new(Source, "override", 3), new(Source, "override", 7)]).Final);
        Assert.Throws<InvalidOperationException>(() => ModifierAggregator.Aggregate(100, [new(Source, "inc", 1)]));
    }

    [Fact]
    public void DerivedReadsAppendAfterImmediateOverridesAndConsumersSeeFinalizedValues()
    {
        var compiled = LegacyPacks.FromJson("""
            {"id":"test","schemaVersion":1,"stats":[
              {"id":"strength","kind":"derived","formula":"10","min":0,"max":99},
              {"id":"hp","kind":"derived","formula":"strength * 10"},
              {"id":"ehp","kind":"derived","formula":"hp * 2"}],
             "items":[{"id":"item","occupies":["body"],"modifiers":[
              {"stat":"ehp","op":"override","value":"hp + 1"},
              {"stat":"ehp","op":"override","value":999},
              {"stat":"hp","op":"flat","value":20}]}]}
            """);
        var result = StatResolver.Resolve(compiled, [ModifierBatch.Item(compiled.Pack.Items[0])]);
        Assert.Equal(120, result.Stats["hp"]);
        Assert.Equal(121, result.Stats["ehp"]);
        Assert.Equal(240, result.Breakdowns["ehp"].Base);
        Assert.Equal(new double[] { 999, 121 }, result.Breakdowns["ehp"].Contributions.Select(value => value.Value));
    }

    [Fact]
    public void ConditionsUseScopeAndExplicitContextGates()
    {
        var compiled = LegacyPacks.FromJson("""
            {"id":"test","schemaVersion":1,"stats":[{"id":"power","kind":"derived","formula":"10","min":0,"max":99},
              {"id":"damage","kind":"derived","formula":"power"}],
             "items":[{"id":"item","occupies":["body"],"modifiers":[
              {"stat":"damage","op":"flat","value":"power * 2","when":"has_tag('active')","requiresContext":{"mode":"on"}}]}]}
            """);
        Assert.Equal(["on"], Assert.Single(compiled.Pack.Selectors).Values);
        ImmutableArray<ModifierBatch> batches = [ModifierBatch.Item(compiled.Pack.Items[0])];
        var scope = new EvaluationScope { Tags = ImmutableHashSet.Create("active") };
        Assert.Equal(10, StatResolver.Resolve(compiled, batches, scope).Stats["damage"]);
        var context = ImmutableDictionary<string, string>.Empty.Add("mode", "on");
        Assert.Equal(30, StatResolver.Resolve(compiled, batches, scope, context).Stats["damage"]);
        Assert.Equal(10, StatResolver.Resolve(compiled, batches, context: context).Stats["damage"]);
    }

    [Fact]
    public void NumericContextInputsUseDefaultsWhenMissingOrInvalid()
    {
        var compiled = LegacyPacks.FromJson("""
            {"id":"test","schemaVersion":1,"numericContextInputs":[{"id":"stacks","name":"Stacks","min":0,"max":10,"step":1,"default":3}],
             "stats":[{"id":"damage","kind":"derived","formula":"0"}],
             "items":[{"id":"item","occupies":["body"],"modifiers":[{"stat":"damage","op":"flat","value":"context_value('stacks') * 2"}]}]}
            """);
        ImmutableArray<ModifierBatch> batches = [ModifierBatch.Item(compiled.Pack.Items[0])];
        EvaluationScope With(double stacks) => new() { ContextValues = new Dictionary<string, double> { ["stacks"] = stacks } };
        var missing = StatResolver.Resolve(compiled, batches);
        Assert.Equal(6, missing.Stats["damage"]);
        Assert.Empty(missing.RejectedContextInputs);
        Assert.Equal(10, StatResolver.Resolve(compiled, batches, With(5)).Stats["damage"]);
        foreach (var invalid in new[] { 11, 2.5, double.NaN })
        {
            var result = StatResolver.Resolve(compiled, batches, With(invalid));
            Assert.Equal(6, result.Stats["damage"]);
            Assert.Equal(["stacks"], result.RejectedContextInputs);
        }
    }

    [Fact]
    public void RankedUpgradesScaleByEffectiveRankAndSkipRankZero()
    {
        var compiled = LegacyPacks.FromJson("""
            {"id":"test","schemaVersion":1,"stats":[{"id":"armor","kind":"derived","formula":"100"}],
             "accountUpgrades":[{"id":"watch","name":"Watch","maxRank":10,"effectCap":4,"modifiersPerRank":[{"stat":"armor","op":"flat","value":5}]}],
             "items":[{"id":"vest","name":"Vest","occupies":["chest"],"upgrade":{"name":"Plating","maxRank":3,"modifiersPerRank":[{"stat":"armor","op":"override","value":7}]}}]}
            """);
        var upgrade = ModifierBatch.AccountUpgrade(compiled.Pack.AccountUpgrades[0], 10);
        Assert.Equal(10, upgrade.Source.Rank);
        Assert.Equal("Watch (rank 10)", upgrade.Source.Name);
        Assert.Equal(120, StatResolver.Resolve(compiled, [upgrade]).Stats["armor"]);
        var vest = compiled.Pack.Items[0];
        Assert.Equal(21, StatResolver.Resolve(compiled, [ModifierBatch.ItemUpgrade(vest, 3, "chest")]).Stats["armor"]);
        Assert.Equal(100, StatResolver.Resolve(compiled, [ModifierBatch.ItemUpgrade(vest, 0, "chest")]).Stats["armor"]);
        Assert.Throws<ArgumentOutOfRangeException>(() => ModifierBatch.ItemUpgrade(vest, 4, "chest"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModifierBatch.AccountUpgrade(compiled.Pack.AccountUpgrades[0], -1));
    }

    [Fact]
    public void SetSelectionSkipsUnequippedSetsAndAddsTalentCredit()
    {
        var pack = new GamePack
        {
            Talents = [new() { ID = "credit", SetPieceCredit = 1 }],
            Sets =
            [
                new() { ID = "worn", Name = "Worn", Items = ["a", "b"], Bonuses = [new() { Pieces = 2 }, new() { Pieces = 3 }] },
                new() { ID = "absent", Name = "Absent", Items = ["c"], Bonuses = [new() { Pieces = 1 }] }
            ]
        };
        var active = new HashSet<string>(StringComparer.Ordinal) { "a" };
        Assert.Empty(SetBonusSelection.Resolve(pack, active, ImmutableHashSet<string>.Empty).Batches);
        var credited = SetBonusSelection.Resolve(pack, active, ImmutableHashSet.Create("credit"));
        var bonus = Assert.Single(credited.Batches);
        Assert.Equal("sets.worn.bonuses[0]", bonus.OwnerPath);
        Assert.Equal("Worn (2pc)", bonus.Source.Name);
        var set = Assert.Single(credited.ActiveSets);
        Assert.Equal((2L, 1L), (set.Pieces, set.BonusPieces));
    }
}
