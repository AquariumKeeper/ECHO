using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Core.Tests;

/// <summary>A specialization tree's spending, active nodes and which nodes can be allocated (F21).</summary>
public sealed class SpecializationRulesTests
{
    // A total budget of 10 with a hub of 5 inside it; b needs a, and c is outside the hub.
    private static readonly SpecializationDefinition Tree = new()
    {
        ID = "tree",
        Name = "Tree",
        Budgets = [new() { ID = "total", Name = "Total", Points = 10 }, new() { ID = "hub", Name = "Hub", Points = 5, ParentID = "total" }],
        Nodes =
        [
            new() { ID = "a", Name = "A", BudgetID = "hub", Cost = 3 },
            new() { ID = "b", Name = "B", BudgetID = "hub", Cost = 2, Prerequisites = ["a"] },
            new() { ID = "c", Name = "C", BudgetID = "total", Cost = 4 },
            new() { ID = "d", Name = "D", BudgetID = "hub", Cost = 1 }
        ]
    };

    private static SpecializationNodeDefinition Node(string id) => Tree.Nodes.Single(node => node.ID == id);

    [Fact]
    public void ANodeChargesItsBudgetAndEachParent()
    {
        var status = SpecializationRules.Status(Tree, ["a", "b", "c"]);
        Assert.Equal(5, status.Spent["hub"]);
        Assert.Equal(9, status.Spent["total"]);
        Assert.Equal(["a", "b", "c"], status.Active.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ANodeWithoutItsPrerequisiteIsInactiveButStillCosts()
    {
        var status = SpecializationRules.Status(Tree, ["b"]);
        Assert.Empty(status.Active);
        Assert.Equal(2, status.Spent["hub"]);
    }

    [Fact]
    public void ANodeCanBeAllocatedWithItsPrerequisitesActiveAndRoomInEveryBudget()
    {
        var empty = SpecializationRules.Status(Tree, []);
        Assert.True(SpecializationRules.CanAllocate(Tree, Node("a"), empty));
        Assert.False(SpecializationRules.CanAllocate(Tree, Node("b"), empty));

        var withA = SpecializationRules.Status(Tree, ["a"]);
        Assert.True(SpecializationRules.CanAllocate(Tree, Node("b"), withA));

        // The hub is full at 5, though the total has room.
        var hubFull = SpecializationRules.Status(Tree, ["a", "b"]);
        Assert.False(SpecializationRules.CanAllocate(Tree, Node("d"), hubFull));
        Assert.True(SpecializationRules.CanAllocate(Tree, Node("c"), hubFull));

        // The total is the limit for a hub node when the hub has room but the total doesn't.
        var totalFull = SpecializationRules.Status(Tree, ["a", "c"]);
        Assert.Equal(7, totalFull.Spent["total"]);
        Assert.True(SpecializationRules.CanAllocate(Tree, Node("b"), totalFull));
        var nearlyFull = SpecializationRules.Status(Tree, ["a", "b", "c"]);
        Assert.False(SpecializationRules.CanAllocate(Tree, Node("d"), nearlyFull));
    }
}
