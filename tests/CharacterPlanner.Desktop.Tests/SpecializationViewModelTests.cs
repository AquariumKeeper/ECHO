using System.Globalization;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Infrastructure.Storage;

namespace CharacterPlanner.Desktop.Tests;

/// <summary>The specialization panel (F21): choosing a specialization, and allocating its nodes within their rules.</summary>
public sealed class SpecializationViewModelTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "specialization-" + Guid.NewGuid().ToString("N"));
    private readonly PlannerSession session;
    private readonly EditorViewModel editor;
    private readonly CultureInfo culture = CultureInfo.CurrentCulture;

    public SpecializationViewModelTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        session = new(TestPack.Division, PlannerState.Of(BuildSnapshot.Create(TestPack.Division.Pack)), new OperationApplier(TestPack.Division));
        editor = new(session, new FileBuildLibrary(folder), new TestPack.QueueDispatcher());
    }

    public void Dispose()
    {
        editor.Dispose();
        CultureInfo.CurrentCulture = culture;
        if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); }
    }

    private SpecializationViewModel Panel => editor.Specialization;
    private SpecializationNodeViewModel Node(string id) => Panel.Nodes.Single(node => node.Id == id);
    private BuildSnapshot Build => session.Snapshot.State.Build;

    [Fact]
    public void NodesAreAllocatedOnlyWithinTheirRules()
    {
        Assert.Equal("Select a specialization to plan its tree.", Panel.Description);
        Panel.Selected = Panel.Choices.Single(choice => choice.Id == "gunner");
        Assert.Equal("gunner", Build.SpecializationId);
        Assert.Equal(["Specialization: 0 / 165 points", "Weapon Damage Hub: 0 / 45 points"], Panel.Budgets);
        Assert.Equal("Gunner · 0 / 165 points", Panel.Summary);

        // A second tier needs the first.
        Assert.True(Node("gunner_this_is_my_rifle_tier_1").ToggleCommand.CanExecute(null));
        Assert.False(Node("gunner_this_is_my_rifle_tier_2").ToggleCommand.CanExecute(null));
        Assert.Contains("Requires: ", Node("gunner_this_is_my_rifle_tier_2").Status, StringComparison.Ordinal);
        Node("gunner_this_is_my_rifle_tier_1").ToggleCommand.Execute(null);
        Assert.Contains("gunner_this_is_my_rifle_tier_1", Build.SpecializationNodeSelections);
        Assert.True(Node("gunner_this_is_my_rifle_tier_1").IsAllocated);
        Assert.Equal("Deallocate", Node("gunner_this_is_my_rifle_tier_1").ToggleText);
        Assert.True(Node("gunner_this_is_my_rifle_tier_2").ToggleCommand.CanExecute(null));

        // The weapon damage hub holds 45 points: nine 5-point nodes fill it.
        foreach (var id in new[] { "gunner_this_is_my_rifle_tier_2", "gunner_this_is_my_rifle_tier_3", "gunner_depleted_rounds_tier_1",
            "gunner_depleted_rounds_tier_2", "gunner_depleted_rounds_tier_3", "gunner_e_m_i_tier_1", "gunner_e_m_i_tier_2", "gunner_e_m_i_tier_3" })
        { Node(id).ToggleCommand.Execute(null); }
        Assert.Equal(9, Build.SpecializationNodeSelections.Length);
        Assert.False(Node("gunner_gunslinger_tier_1").ToggleCommand.CanExecute(null));

        // Removing a first tier leaves the later ones allocated but inactive, and still removable.
        Node("gunner_this_is_my_rifle_tier_1").ToggleCommand.Execute(null);
        Assert.Contains("Inactive: missing prerequisite", Node("gunner_this_is_my_rifle_tier_2").Status, StringComparison.Ordinal);
        Assert.True(Node("gunner_this_is_my_rifle_tier_2").ToggleCommand.CanExecute(null));

        // Another specialization starts its tree empty.
        Panel.Selected = Panel.Choices.Single(choice => choice.Id == "survivalist");
        Assert.Empty(Build.SpecializationNodeSelections);
        Assert.All(Panel.Nodes, node => Assert.False(node.IsAllocated));
    }
}
