using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Desktop.Views;
using CharacterPlanner.Infrastructure.Storage;

namespace CharacterPlanner.Desktop.Tests;

/// <summary>The Stats tab (F37), as the Go app's stats page (G337): every derived stat, filtered by name and by whether it applies.</summary>
public sealed class StatsTabTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "stats-" + Guid.NewGuid().ToString("N"));
    private readonly TestPack.QueueDispatcher dispatcher = new();
    private readonly PlannerSession session;
    private readonly EditorViewModel editor;
    private readonly CultureInfo culture = CultureInfo.CurrentCulture;

    public StatsTabTests()
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

    private StatsPageRowViewModel Row(string name) => editor.Stats.Groups.SelectMany(group => group.Rows).First(row => row.Row.Name == name);
    private StatsPageGroupViewModel Group(string name) => editor.Stats.Groups.Single(group => group.Name == name);

    [Fact]
    public async Task EveryDerivedStatIsListedAfterTheSummaryGroups()
    {
        await Resolved();
        var page = editor.Stats;
        // The summary's groups in order, then every other stat with a formula.
        Assert.Equal([.. editor.Summary.Select(group => group.Name), StatsPageViewModel.OtherStatsGroup], page.Groups.Select(group => group.Name));
        var listed = page.Groups.SelectMany(group => group.Rows).Select(row => row.Row.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(TestPack.Division.Pack.Stats.Where(stat => stat.Formula.Length > 0), stat => Assert.Contains(stat.ID, listed));
        Assert.DoesNotContain(Group(StatsPageViewModel.OtherStatsGroup).Rows, row => editor.Summary.SelectMany(group => group.Rows).Any(summary => summary.Id == row.Row.Id));
        // Full names, not the summary's short labels.
        Assert.True(Row("Estimated Total Armor").IsShown);
        Assert.True(Row("Combined Skill Damage").IsShown);
        Assert.True(page.OnlyApplicable);
    }

    [Fact]
    public async Task StatsTheBuildLeavesAtTheEmptyBuildsValueHideUntilAsked()
    {
        await Resolved();
        var page = editor.Stats;
        Assert.False(Row("Umbra Light Stack Limit").IsShown);
        Assert.False(Row("Total Skill Damage").IsShown);
        Assert.True(Group("Target").HasVisible);

        page.OnlyApplicable = false;
        Assert.True(Row("Umbra Light Stack Limit").IsShown);
        Assert.True(Row("Total Skill Damage").IsShown);
        Assert.True(Group(StatsPageViewModel.OtherStatsGroup).HasVisible);

        // A stat the build changes appears with its value.
        page.OnlyApplicable = true;
        var chest = editor.Slot("chest");
        chest.Equip("chest_n_5_11_tactical_chest");
        chest.Talents.Single().Selected = chest.Talents.Single().Choices.Single(choice => choice.Id == "gear_talent_kinetic_momentum");
        Assert.True(session.Apply([new ContextOperation("kinetic_momentum_skill_1_stacks", 10)], session.Snapshot.Revision, EditOrigin.Desktop).Succeeded);
        dispatcher.RunPending();
        await Resolved();
        Assert.True(Row("Total Skill Damage").IsShown);
        Assert.Equal("15%", Row("Total Skill Damage").Row.Value);
    }

    [Fact]
    public async Task TheFilterMatchesNamesAndSaysWhenNothingMatches()
    {
        await Resolved();
        var page = editor.Stats;
        page.OnlyApplicable = false;
        page.Filter = "umbra light";
        Assert.True(Row("Umbra Light Stack Limit").IsShown);
        Assert.False(Row("Estimated Total Armor").IsShown);
        Assert.False(Group("Defense").HasVisible);
        Assert.False(page.IsEmpty);

        page.Filter = "no such stat";
        Assert.True(page.IsEmpty);
        Assert.All(page.Groups, group => Assert.False(group.HasVisible));
        page.Filter = "";
        Assert.False(page.IsEmpty);
    }

    [Fact]
    public async Task ARowsBreakdownIsTheGoBreakdown()
    {
        await Resolved();
        var armor = Row("Estimated Total Armor").Row;
        // The row's info button asks through the row itself.
        armor.SelectCommand.Execute(null);
        Assert.NotNull(armor.Details);
        Assert.Equal("Estimated Total Armor", armor.Details.Name);
        Assert.StartsWith("Final: ", armor.Details.Lines[^1], StringComparison.Ordinal);
    }

    /// <summary>The Stats tab follows Build, All stats selects it, and its filter and check are bound.</summary>
    [AvaloniaFact]
    public async Task AllStatsOpensTheStatsTabInTheWindow()
    {
        var window = new Window { Width = 1360, Height = 860, Content = new EditorView { DataContext = editor } };
        window.Show();
        try
        {
            await Resolved();
            Dispatcher.UIThread.RunJobs();
            var tabs = window.GetVisualDescendants().OfType<TabControl>().Single(control => control.Name == "Tabs");
            Assert.Equal(["Build", "Stats"], tabs.Items.OfType<TabItem>().Take(2).Select(item => item.Header as string));
            Assert.Equal(0, tabs.SelectedIndex);
            editor.AllStatsCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("StatsTab", (tabs.SelectedItem as TabItem)?.Name);

            var filter = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "StatsFilterBox");
            Assert.Equal(StatsPageViewModel.FilterPlaceholder, filter.PlaceholderText);
            filter.Text = "no such stat";
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "StatsEmptyText").IsVisible);
            filter.Text = "";
            var check = window.GetVisualDescendants().OfType<CheckBox>().Single(box => box.Name == "StatsApplicableCheck");
            Assert.True(check.IsChecked);
            check.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(editor.Stats.OnlyApplicable);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Umbra Light Stack Limit" && text.IsEffectivelyVisible);
            Assert.True(window.GetVisualDescendants().OfType<DockPanel>().Single(panel => panel.Name == "StatsPage").Bounds.Width <= 760);
        }
        finally { window.Close(); }
    }
}
