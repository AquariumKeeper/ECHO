using System.Globalization;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Infrastructure.Storage;

namespace CharacterPlanner.Desktop.Tests;

/// <summary>The assumptions view: only what the build reads is shown (F46), and each kind is set and cleared.</summary>
public sealed class AssumptionViewModelTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "assumptions-" + Guid.NewGuid().ToString("N"));
    private readonly TestPack.QueueDispatcher dispatcher = new();
    private readonly PlannerSession session;
    private readonly EditorViewModel editor;
    private readonly CultureInfo culture = CultureInfo.CurrentCulture;

    public AssumptionViewModelTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        session = new(TestPack.Division, PlannerState.Of(BuildSnapshot.Create(TestPack.Division.Pack)), new OperationApplier(TestPack.Division));
        editor = new(session, new FileBuildLibrary(folder), dispatcher);
    }

    public void Dispose()
    {
        editor.Dispose();
        CultureInfo.CurrentCulture = culture;
        if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); }
    }

    private async Task Resolved()
    {
        await editor.Resolution;
        dispatcher.RunPending();
    }

    private AssumptionViewModel Assumption(string id) => editor.AssumptionSections.SelectMany(section => section.Assumptions).Single(assumption => assumption.Id == id);
    private System.Collections.Immutable.ImmutableDictionary<string, string> Assumptions => session.Snapshot.State.Assumptions;

    [Fact]
    public async Task OnlyAssumptionsTheBuildReadsAreShown()
    {
        await Resolved();
        // An empty build reads the global inputs (the enemy target and the like), and nothing a talent gates on.
        Assert.True(Assumption("enemy_target").IsVisible);
        Assert.False(Assumption("gunslinger_active").IsVisible);
        Assert.False(Assumption("prototype_activity").IsVisible);
        Assert.Contains(editor.AssumptionSections, section => section.Name == "Enemy target" && section.HasVisible);

        // Gunslinger gates its bonus on gunslinger_active, so equipping it shows the switch.
        var chest = editor.Slot("chest");
        chest.Equip("chest_n_5_11_tactical_chest");
        var talent = chest.Talents.Single();
        talent.Selected = Pick(talent.Choices, "gear_talent_gunslinger");
        await Resolved();
        Assert.True(Assumption("gunslinger_active").IsVisible);

        // A Prototype item reads the activity, which normalizes Prototype items in raids.
        var mask = editor.Slot("mask");
        mask.Equip("mask_n_5_11_tactical_mask");
        mask.IsPrototype = true;
        await Resolved();
        Assert.True(Assumption("prototype_activity").IsVisible);
    }

    [Fact]
    public async Task SelectorsAreSetAndStayShownWhileSet()
    {
        var chest = editor.Slot("chest");
        chest.Equip("chest_n_5_11_tactical_chest");
        chest.Talents.Single().Selected = Pick(chest.Talents.Single().Choices, "gear_talent_gunslinger");
        await Resolved();
        var gunslinger = Assert.IsType<SelectorAssumptionViewModel>(Assumption("gunslinger_active"));
        Assert.Equal("Gunslinger bonus active", gunslinger.Name);
        gunslinger.Selected = Pick(gunslinger.Choices, "true");
        Assert.Equal("true", Assumptions["gunslinger_active"]);

        // Unequipping the talent leaves the switch shown while it's set, so it can be cleared.
        chest.UnequipCommand.Execute(null);
        await Resolved();
        Assert.True(gunslinger.IsVisible);
        gunslinger.Selected = SelectorAssumptionViewModel.Default;
        Assert.False(Assumptions.ContainsKey("gunslinger_active"));
        await Resolved();
        Assert.False(gunslinger.IsVisible);
    }

    /// <summary>A stored value the pack doesn't list is shown in the dropdown, not as a blank.</summary>
    [Fact]
    public async Task UnlistedValuesAreShown()
    {
        var state = PlannerState.Of(BuildSnapshot.Create(TestPack.Division.Pack)) with
        {
            Assumptions = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("boiling_point_phase", "retired")
        };
        var other = new PlannerSession(TestPack.Division, state, new OperationApplier(TestPack.Division));
        using var viewer = new EditorViewModel(other, new FileBuildLibrary(folder), dispatcher);
        await viewer.Resolution;
        dispatcher.RunPending();
        var phase = Assert.IsType<SelectorAssumptionViewModel>(viewer.AssumptionSections.SelectMany(section => section.Assumptions).Single(assumption => assumption.Id == "boiling_point_phase"));
        Assert.Equal("retired", phase.Selected.Id);
        Assert.Contains(phase.Selected, phase.Choices);
        Assert.True(phase.IsVisible);
    }

    [Fact]
    public void NumericAssumptionsAreChosenOrTyped()
    {
        // Labelled options: choosing the default stores nothing.
        var target = Assert.IsType<NumericAssumptionViewModel>(Assumption("enemy_target"));
        Assert.True(target.HasChoices);
        Assert.Equal("6", target.Selected.Id);
        target.Selected = Pick(target.Choices, "1");
        Assert.Equal(1, double.Parse(Assumptions["enemy_target"], CultureInfo.InvariantCulture));
        target.Selected = Pick(target.Choices, "6");
        Assert.False(Assumptions.ContainsKey("enemy_target"));

        // A typed number, within its range.
        var hitRate = Assert.IsType<NumericAssumptionViewModel>(Assumption("enemy_hit_rate"));
        Assert.False(hitRate.HasChoices);
        // Unset, it follows the fight distance (R44), so the box is empty.
        Assert.Equal("", hitRate.ValueText);
        hitRate.ValueText = "150";
        Assert.False(hitRate.CommitValue());
        Assert.Contains("must be a number from", editor.Message, StringComparison.Ordinal);
        hitRate.ValueText = "35";
        Assert.True(hitRate.CommitValue());
        Assert.Equal(35, double.Parse(Assumptions["enemy_hit_rate"], CultureInfo.InvariantCulture));
        editor.UndoCommand.Execute(null);
        Assert.Equal("", hitRate.ValueText);
        // Its default is a real choice here: typing it stores it, so a fight uses it rather than the distance's band. Clearing the
        // box goes back to following the distance.
        hitRate.ValueText = "50";
        Assert.True(hitRate.CommitValue());
        Assert.Equal(50, double.Parse(Assumptions["enemy_hit_rate"], CultureInfo.InvariantCulture));
        hitRate.ValueText = "";
        Assert.True(hitRate.CommitValue());
        Assert.False(Assumptions.ContainsKey("enemy_hit_rate"));
        // An input without distance bands keeps storing nothing for its default.
        var firing = Assert.IsType<NumericAssumptionViewModel>(Assumption("enemy_firing_share"));
        firing.ValueText = "70";
        Assert.True(firing.CommitValue());
        Assert.True(Assumptions.ContainsKey("enemy_firing_share"));
        firing.ValueText = "50";
        Assert.True(firing.CommitValue());
        Assert.False(Assumptions.ContainsKey("enemy_firing_share"));
    }
}
