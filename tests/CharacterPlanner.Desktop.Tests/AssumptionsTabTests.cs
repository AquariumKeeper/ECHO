using System.Collections.Immutable;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CharacterPlanner.Application.Assumptions;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Desktop.Views;
using CharacterPlanner.Infrastructure.Storage;

namespace CharacterPlanner.Desktop.Tests;

/// <summary>
/// The Assumptions tab as the Go app's: the search, the note when nothing applies, the grouped Off/On cards (G339), the
/// typed numbers' hints, and the assumption set bar (F45, Go's G396).
/// </summary>
public sealed class AssumptionsTabTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "assumptions-tab-" + Guid.NewGuid().ToString("N"));
    private readonly TestPack.QueueDispatcher dispatcher = new();
    private readonly PlannerSession session;
    private readonly FileAssumptionSetStore store;
    private readonly EditorViewModel editor;
    private readonly CultureInfo culture = CultureInfo.CurrentCulture;

    public AssumptionsTabTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        session = new(TestPack.Division, PlannerState.Of(BuildSnapshot.Create(TestPack.Division.Pack)), new OperationApplier(TestPack.Division));
        store = new FileAssumptionSetStore(Path.Combine(folder, "assumption-sets.json"));
        editor = new(session, new FileBuildLibrary(Path.Combine(folder, "library")), dispatcher, assumptionSets: store);
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
    private ImmutableDictionary<string, string> Assumptions => session.Snapshot.State.Assumptions;

    /// <summary>Sets assumptions as one edit, as another client would, and shows the result.</summary>
    private void Set(params (string Id, string? Value)[] values)
    {
        var operations = values.Select(pair => pair.Value is null ? ContextOperation.Cleared(pair.Id)
            : double.TryParse(pair.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && TestPack.Division.Pack.NumericContextInputs.Any(input => input.ID == pair.Id)
                ? new ContextOperation(pair.Id, number) : new ContextOperation(pair.Id, Text: pair.Value)).ToArray<BuildOperation>();
        Assert.True(session.Apply([.. operations], session.Snapshot.Revision, EditOrigin.Desktop).Succeeded);
        dispatcher.RunPending();
    }

    [Fact]
    public async Task TheSearchMatchesEveryWordOfNamesAndDescriptions()
    {
        await Resolved();
        var tab = editor.AssumptionsTab;
        var hitRate = Assumption("enemy_hit_rate");
        Assert.True(hitRate.IsShown);
        // "Enemy Hit Rate (%)": "… for time to armor break …"; every word, in any order and case.
        tab.Search = "ARMOR enemy";
        Assert.True(hitRate.IsShown);
        tab.Search = "enemy nothing-like-this";
        Assert.False(hitRate.IsShown);
        Assert.True(tab.IsEmpty);
        Assert.All(editor.AssumptionSections, section => Assert.False(section.HasVisible));
        tab.Search = " ";
        Assert.True(hitRate.IsShown);
        Assert.False(tab.IsEmpty);
        // A card that doesn't apply stays hidden whatever the search.
        tab.Search = "gunslinger";
        Assert.False(Assumption("gunslinger_active").IsShown);
        Assert.True(tab.IsEmpty);
    }

    [Fact]
    public void TypedNumbersHaveARangeAndStepHintAndCardsTheirDescriptions()
    {
        var hitRate = Assert.IsType<NumericAssumptionViewModel>(Assumption("enemy_hit_rate"));
        Assert.Equal("Range 0–100 · step 1 · empty follows the distance", hitRate.Hint);
        Assert.True(hitRate.HasDescription);
        Assert.Equal(hitRate.Description, hitRate.ShownDescription);
        // The Go app's shorter text for a skill comparison.
        Assert.StartsWith("0: Off; 1: On. Compare Restorer duration", Assumption("restorer_duration_source_preview").ShownDescription, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GroupedInputsAreOneOffOnCardInOneUndoStep()
    {
        await Resolved();
        var group = Assert.IsType<GroupedAssumptionViewModel>(Assumption("overcharge_comparison"));
        Assert.Equal("Overcharge Comparison", group.Name);
        Assert.Equal(9, group.Members.Length);
        Assert.Equal("Shows all 9 Overcharge Comparison outputs for the selected skills. Ordinary values stay unchanged.", group.ShownDescription);
        Assert.DoesNotContain(editor.AssumptionSections.SelectMany(section => section.Assumptions), assumption => assumption.Id == "overcharge_damage_preview");
        Assert.Contains(editor.AssumptionSections.Single(section => section.Name == "Skill comparisons").Assumptions, assumption => assumption == group);
        Assert.Equal(GroupedAssumptionViewModel.Off, group.Selected);

        var before = session.Snapshot.Revision;
        group.Selected = GroupedAssumptionViewModel.On;
        Assert.Equal(before + 1, session.Snapshot.Revision);
        Assert.All(group.Members, id => Assert.Equal("1", Assumptions[id]));
        // Set, it stays shown so it can be turned off.
        await Resolved();
        Assert.True(group.IsVisible);

        // One member back at its default reads Off; Off clears every member in one step.
        Set(("overcharge_health_preview", null));
        Assert.Equal(GroupedAssumptionViewModel.Off, group.Selected);
        Set(("overcharge_health_preview", "1"));
        Assert.Equal(GroupedAssumptionViewModel.On, group.Selected);
        before = session.Snapshot.Revision;
        group.Selected = GroupedAssumptionViewModel.Off;
        Assert.Equal(before + 1, session.Snapshot.Revision);
        Assert.DoesNotContain(group.Members, Assumptions.ContainsKey);
        editor.UndoCommand.Execute(null);
        Assert.Equal(GroupedAssumptionViewModel.On, group.Selected);
    }

    /// <summary>Go's G396: save, match, apply in one undo step keeping the weapon in hand, stale values left out, delete.</summary>
    [Fact]
    public async Task AssumptionSetsAreSavedMatchedAppliedAndDeleted()
    {
        var sets = editor.AssumptionsTab.Sets;
        await sets.RefreshAsync();
        Assert.True(sets.IsAvailable);
        Assert.Empty(sets.Names);
        Assert.Null(sets.Selected);

        Set(("enemy_hit_rate", "70"), ("enemy_attackers", "3"));
        Assert.True((await sets.SaveAsync("Boss", replace: false)).Saved);
        Assert.Equal("Boss", sets.Selected);
        Set(("enemy_hit_rate", "30"), ("enemy_attackers", null));
        Assert.Null(sets.Selected);
        Assert.True((await sets.SaveAsync("Trash", replace: false)).Saved);
        Set((CharacterPlanner.Core.Packs.GamePack.WeaponSlotSelector, "sidearm"));
        Assert.Equal("Trash", sets.Selected);
        Assert.Equal(["Boss", "Trash"], sets.Names);
        Assert.Equal("Trash", sets.SuggestedName);

        // A taken name (ignoring case) is reported rather than replaced, and a blank one refused.
        var taken = await sets.SaveAsync("boss", replace: false);
        Assert.True(taken is { Saved: false, Exists: true });
        Assert.Contains("already exists", taken.Problem, StringComparison.Ordinal);
        var blank = await sets.SaveAsync(" ", replace: false);
        Assert.True(blank is { Saved: false, Exists: false, Problem: not null });

        var before = session.Snapshot.Revision;
        sets.Selected = "Boss";
        Assert.Equal(before + 1, session.Snapshot.Revision);
        Assert.Equal("70", Assumptions["enemy_hit_rate"]);
        Assert.Equal("3", Assumptions["enemy_attackers"]);
        Assert.Equal("sidearm", Assumptions[CharacterPlanner.Core.Packs.GamePack.WeaponSlotSelector]);
        Assert.Equal("Boss", sets.Selected);
        Set(("enemy_attackers", "4"));
        Assert.Null(sets.Selected);
        editor.UndoCommand.Execute(null);
        Assert.Equal("Boss", sets.Selected);
        // Choosing the set the assumptions already match changes nothing.
        before = session.Snapshot.Revision;
        Assert.True(sets.Apply("BOSS"));
        Assert.Equal(before, session.Snapshot.Revision);

        // Values the pack no longer takes are left out and reported.
        await store.SaveAsync(TestPack.Division.Pack.ID, "Old", ImmutableDictionary<string, string>.Empty
            .Add("enemy_hit_rate", "40").Add("removed_input", "1").Add("enemy_attackers", "99"), replace: false, TestContext.Current.CancellationToken);
        await sets.RefreshAsync();
        Assert.True(sets.Apply("old"));
        Assert.Equal("These saved assumptions no longer apply and were left out: enemy_attackers, removed_input", sets.Notice);
        Assert.Equal("40", Assumptions["enemy_hit_rate"]);

        await sets.DeleteAsync("Trash");
        Assert.Equal(["Boss", "Old"], sets.Names);
        Assert.Equal("40", Assumptions["enemy_hit_rate"]);
        Set(("enemy_hit_rate", "41"));
        Assert.Null(sets.DeleteCandidate());
        Assert.Equal(AssumptionSetBarViewModel.ChooseToDelete, editor.Message);

        // Replacing keeps one set of that name, now with the current assumptions.
        Assert.True((await sets.SaveAsync("old", replace: true)).Saved);
        Assert.Equal(["Boss", "old"], sets.Names);
        Assert.Equal("old", sets.Selected);
    }

    [Fact]
    public async Task WithoutAStoreThereIsNoSetBar()
    {
        using var plain = new EditorViewModel(session, new FileBuildLibrary(Path.Combine(folder, "plain")), dispatcher);
        Assert.False(plain.AssumptionsTab.Sets.IsAvailable);
        await plain.AssumptionsTab.Sets.RefreshAsync();
        Assert.Empty(plain.AssumptionsTab.Sets.Names);
    }

    /// <summary>The bar, the search and the cards are bound in the window.</summary>
    [AvaloniaFact]
    public async Task TheAssumptionsTabIsBoundInTheWindow()
    {
        await store.SaveAsync(TestPack.Division.Pack.ID, "Boss", ImmutableDictionary<string, string>.Empty.Add("enemy_hit_rate", "70"), replace: false, TestContext.Current.CancellationToken);
        await editor.AssumptionsTab.Sets.RefreshAsync();
        var window = new Window { Width = 1360, Height = 860, Content = new EditorView { DataContext = editor } };
        window.Show();
        try
        {
            window.GetVisualDescendants().OfType<TabItem>().Single(item => item.Name == "AssumptionsTab").IsSelected = true;
            await Resolved();
            Dispatcher.UIThread.RunJobs();
            T Find<T>(string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

            Assert.True(Find<Grid>("AssumptionSetBar").IsVisible);
            var picker = Find<ComboBox>("AssumptionSetPicker");
            Assert.Equal(AssumptionSetBarViewModel.Custom, picker.PlaceholderText);
            Assert.Null(picker.SelectedItem);
            picker.SelectedItem = "Boss";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("70", Assumptions["enemy_hit_rate"]);
            Assert.Equal("Boss", picker.SelectedItem);
            // An edit elsewhere returns the chooser to Custom.
            Set(("enemy_hit_rate", "71"));
            Dispatcher.UIThread.RunJobs();
            Assert.Null(picker.SelectedItem);

            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == AssumptionsTabViewModel.Caption);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Range 0–100 · step 1 · empty follows the distance" && text.IsEffectivelyVisible);
            var search = Find<TextBox>("AssumptionSearchBox");
            Assert.Equal(AssumptionsTabViewModel.SearchPlaceholder, search.PlaceholderText);
            search.Text = "no such assumption";
            Dispatcher.UIThread.RunJobs();
            Assert.True(Find<TextBlock>("AssumptionsEmptyText").IsVisible);
            Assert.Equal(AssumptionsTabViewModel.EmptyText, Find<TextBlock>("AssumptionsEmptyText").Text);
            search.Text = "";
            Dispatcher.UIThread.RunJobs();
            Assert.False(Find<TextBlock>("AssumptionsEmptyText").IsVisible);
        }
        finally { window.Close(); }
    }
}
