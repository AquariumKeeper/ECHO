using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Legacy.Tests;

public sealed class SpecializationEffectTests
{
    private static readonly GamePack Pack = LegacyPacks.Load(Path.Combine("specialization-resolution", "pack.json"), SyntheticV1.Simplify).Pack;
    private static BuildSnapshot Configured => BuildSnapshot.Create(Pack).WithEquipment("main", "weapon").WithSpecialization("tree");

    [Fact]
    public void OverspendingRetainsActiveNodeEffectsAndAuthoredOrder()
    {
        var build = Configured.WithSpecializationNode("root", true).WithSpecializationNode("child", true).WithSpecializationNode("grandchild", true);
        var result = Resolve(Pack, build);
        Assert.Equal(new[] { "overall", "branch" }, result.Diagnostics.Select(value => value.Subject));
        Assert.All(result.Diagnostics, value => Assert.Equal("specialization-budget-exceeded", value.Code));
        Assert.Equal(178, result.Calculation.Stats["damage"]);
        Assert.Equal(373.8, result.Calculation.Stats["ehp"], 10);
        Assert.Equal(new[] { "weapon", "set", "tree", "grandchild", "child", "root" }, result.Calculation.Breakdowns["damage"].Contributions.Select(value => value.Source.Id));
    }

    [Fact]
    public void InactiveSelectedNodesStillSpendBudgetButContributeNoEffects()
    {
        var build = Configured.WithSpecializationNode("child", true).WithSpecializationNode("grandchild", true);
        var result = Resolve(Pack, build);
        Assert.Equal(new[] { "specialization-budget-exceeded", "specialization-budget-exceeded", "unmet-specialization-prerequisite", "unmet-specialization-prerequisite" }, result.Diagnostics.Select(value => value.Code));
        Assert.Equal(new[] { "overall", "branch", "grandchild", "child" }, result.Diagnostics.Select(value => value.Subject));
        Assert.Equal(108, result.Calculation.Stats["damage"]);
        Assert.DoesNotContain(result.Calculation.Breakdowns["damage"].Contributions, value => value.Source.Kind == "specialization-node");
    }

    [Fact]
    public void CostsPropagateThroughEveryBudgetAncestorOncePerSelectedNode()
    {
        var tree = Pack.Specializations[0];
        tree = tree with
        {
            Budgets = tree.Budgets.Select(budget => budget.ID == "overall" ? budget with { ParentID = "top" } : budget)
                .Append(new() { ID = "top", Name = "Top", Points = 2 }).ToImmutableArray()
        };
        var pack = Pack with { Specializations = [tree] };
        var build = Configured with { SpecializationNodeSelections = ["root", "root", "child", "child"] };
        var result = Resolve(pack, build);
        Assert.Equal(("error", "specialization-budget-exceeded", "top", ""), Assert.Single(result.Diagnostics).Key());
        Assert.Equal(138, result.Calculation.Stats["damage"]);
    }

    [Fact]
    public void MissingSelectionHasWarningOnlyWhenPackOffersSpecializations()
    {
        var build = Configured.WithSpecialization("") with { SpecializationNodeSelections = ["root"] };
        Assert.Equal(new[] { "specialization-unconfigured", "specialization-node-without-specialization" }, Resolve(Pack, build).Diagnostics.Select(value => value.Code));
        var result = Resolve(Pack with { Specializations = [] }, build);
        Assert.Equal("specialization-node-without-specialization", Assert.Single(result.Diagnostics).Code);
    }

    private static ResolvedBuild Resolve(GamePack pack, BuildSnapshot build)
    {
        return BuildResolver.Resolve(LegacyPacks.Compile(pack), build);
    }
}
