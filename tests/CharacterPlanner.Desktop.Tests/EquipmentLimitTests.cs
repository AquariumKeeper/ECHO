using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Infrastructure.Storage;

namespace CharacterPlanner.Desktop.Tests;

public sealed class EquipmentLimitTests
{
    private const string Mask = "mask_striker_s_battlegear_mask";
    private const string Chest = "chest_striker_s_battlegear_chest";
    private static PlannerSession Session(bool invalid)
    {
        var pack = PackExpressionCompiler.Compile(Division.Pack with
        {
            EquipmentLimits = [new() { ID = "test_allowance", Name = "Test allowance", Tag = "gear_set_striker_s_battlegear", Slots = ["mask", "chest"], Maximum = 1 }]
        }).Compiled!;
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("mask", Mask);
        if (invalid) { build = build.WithEquipment("chest", Chest); }
        return new(pack, PlannerState.Of(build), new OperationApplier(pack));
    }

    [Fact]
    public void SimulationIsBlockedUntilTheEquipmentIsRepairedAndUndoBlocksItAgain()
    {
        var session = Session(invalid: true);
        var ui = new QueueDispatcher();
        using var simulate = new SimulateViewModel(session, ui);
        Assert.Contains("Test allowance", simulate.Problem!, StringComparison.Ordinal);
        Assert.False(simulate.RunCommand.CanExecute(null));
        simulate.AverageFight = true;
        Assert.False(simulate.RunCommand.CanExecute(null));
        simulate.DriveBuffs = false;
        Assert.True(session.Apply([EquipmentOperation.Cleared("chest")], 0, EditOrigin.Desktop).Succeeded);
        ui.RunPending();
        Assert.Null(simulate.Problem);
        Assert.True(simulate.RunCommand.CanExecute(null));
        Assert.True(session.Undo(1).Succeeded);
        ui.RunPending();
        Assert.False(simulate.RunCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(true, "mask", "mask_n_5_11_tactical_mask")]
    [InlineData(false, "chest", Chest)]
    public async Task RepairAndIllegalCandidatesRemainEquippableWithoutComparisonMarks(bool invalid, string slot, string candidateId)
    {
        var session = Session(invalid);
        var ui = new QueueDispatcher();
        var folder = Path.Combine(Path.GetTempPath(), "limits-" + Guid.NewGuid().ToString("N"));
        using var editor = new EditorViewModel(session, new FileBuildLibrary(folder), ui);
        await editor.Resolution;
        ui.RunPending();
        editor.Slot(slot).BrowseCommand.Execute(null);
        var picker = editor.Picker!;
        await picker.Comparison;
        ui.RunPending();
        var row = picker.Rows.Single(row => row.Id == candidateId);
        Assert.All(row.Stats, stat => Assert.Equal(0, stat.Direction));
        Assert.False(row.HasChange);
        picker.Highlighted = row;
        await editor.PreviewResolution;
        ui.RunPending();
        var candidate = Assert.IsType<CandidateViewModel>(editor.Inspected);
        Assert.True(candidate.EquipCommand.CanExecute(null));
        Assert.All(editor.Headline, figure => Assert.Equal(0, figure.Change));
        candidate.EquipCommand.Execute(null);
        Assert.Equal(candidateId, session.Snapshot.State.Build.Equipment[slot]);
        Assert.Null(editor.Picker);
    }

    [Fact]
    public async Task TalentChoicesStayEditableButHaveNoComparisonDeltasOnAnIllegalBuild()
    {
        var session = Session(invalid: true);
        var ui = new QueueDispatcher();
        using var editor = new EditorViewModel(session, new FileBuildLibrary(Path.Combine(Path.GetTempPath(), "limits-" + Guid.NewGuid().ToString("N"))), ui);
        Assert.True(session.Apply([new EquipmentOperation("primary_weapon", "primary_weapon_acr")], 0, EditOrigin.Desktop).Succeeded);
        await editor.Resolution;
        ui.RunPending();
        TalentCompareViewModel? opened = null;
        editor.Host = new TestHost { Compared = compare => opened = compare };
        editor.Slot("primary_weapon").Talents.Single().CompareCommand.Execute(null);
        using var compare = Assert.IsType<TalentCompareViewModel>(opened);
        await compare.LoadAsync();
        ui.RunPending();
        Assert.NotNull(compare.Problem);
        Assert.NotEmpty(compare.Rows);
        Assert.All(compare.Rows, row =>
        {
            Assert.Empty(row.Deltas);
            Assert.Contains("Comparison unavailable", row.Effect, StringComparison.Ordinal);
        });
        Assert.Contains(compare.Rows, row => row.ChooseCommand.CanExecute(null));
    }
}
