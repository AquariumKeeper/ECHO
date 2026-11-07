using System.Collections.Immutable;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Application.Tests.Commands;
using CharacterPlanner.Core.Engine;

namespace CharacterPlanner.Application.Tests.Sessions;

/// <summary>Comparing two builds (the Go app's Compare Builds): every stat, largest change first.</summary>
public sealed class BuildComparisonTests
{
    private static readonly ImmutableDictionary<string, string> Assumptions = ImmutableDictionary<string, string>.Empty.Add("enemy_target", "2");

    [Fact]
    public void EveryStatIsComparedLargestChangeFirstWithTiesInPackOrder()
    {
        var a = Division.Empty();
        var b = Division.Geared();
        var comparison = BuildComparison.Compare(Division.Pack, a, b, Assumptions);

        Assert.True(comparison.A.Valid && comparison.B.Valid);
        var stats = Division.Rules.Stats;
        Assert.Equal(stats.Select(stat => stat.ID).Order(StringComparer.Ordinal), comparison.Stats.Select(row => row.Stat.ID).Order(StringComparer.Ordinal));
        var before = BuildResolver.Resolve(Division.Pack, a, Assumptions).Calculation.Stats;
        var after = BuildResolver.Resolve(Division.Pack, b, Assumptions).Calculation.Stats;
        foreach (var row in comparison.Stats)
        {
            Assert.Equal((before[row.Stat.ID], after[row.Stat.ID]), (row.A, row.B));
            Assert.Equal(row.B - row.A, row.Delta);
        }
        Assert.Contains(comparison.Stats, row => row.Delta != 0);
        for (var index = 1; index < comparison.Stats.Length; index++)
        {
            var (previous, next) = (comparison.Stats[index - 1], comparison.Stats[index]);
            Assert.True(Math.Abs(previous.Delta) >= Math.Abs(next.Delta));
            if (Math.Abs(previous.Delta) == Math.Abs(next.Delta)) { Assert.True(stats.IndexOf(previous.Stat) < stats.IndexOf(next.Stat)); }
        }
    }

    [Fact]
    public void ABuildWithAnErrorIsInvalidAndNothingIsCompared()
    {
        var broken = Division.Geared() with { Equipment = Division.Geared().Equipment.SetItem("backpack", "no_such_item") };
        var comparison = BuildComparison.Compare(Division.Pack, Division.Geared(), broken, Assumptions);
        Assert.True(comparison.A.Valid);
        Assert.NotNull(comparison.A.Resolved);
        Assert.False(comparison.B.Valid);
        Assert.Null(comparison.B.Resolved);
        Assert.Equal("build has validation error \"unknown-item\": Unknown item \"no_such_item\"", comparison.B.Problem);
        Assert.Empty(comparison.Stats);
    }

    [Fact]
    public void AnIllegalLoadoutIsInvalidSoItsNumbersAreNotCompared()
    {
        // T2: two exotic gear pieces exceed the pack's limit of one, so the comparison reports the loadout instead of a winner.
        var illegal = Division.Empty().WithEquipment("mask", "mask_catharsis").WithEquipment("chest", "chest_tardigrade_armor_system");
        var comparison = BuildComparison.Compare(Division.Pack, Division.Empty(), illegal, Assumptions);
        Assert.True(comparison.A.Valid);
        Assert.False(comparison.B.Valid);
        Assert.StartsWith("build has validation error \"equipment-limit-exceeded\"", comparison.B.Problem, StringComparison.Ordinal);
        Assert.Empty(comparison.Stats);
    }

    [Fact]
    public void ABuildForAnotherPackIsInvalid()
    {
        var comparison = BuildComparison.Compare(Division.Pack, Division.Empty() with { PackId = "ashen_vale" }, Division.Empty(), Assumptions);
        Assert.Equal($"the build is for pack ashen_vale, not {Division.Rules.ID}", comparison.A.Problem);
        Assert.True(comparison.B.Valid);
        Assert.Empty(comparison.Stats);
    }
}
