using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Desktop.Views;

namespace CharacterPlanner.Desktop.Tests;

/// <summary>Compare, Rank Upgrades and the Simulate tab in the window: what their bindings show and do.</summary>
public sealed class DialogViewTests : IDisposable
{
    private readonly TestPack.QueueDispatcher dispatcher = new();
    private readonly CompareViewModelTests.FakeLibrary library = new();
    private readonly PlannerSession session;
    private readonly EditorViewModel editor;
    private readonly CultureInfo culture = CultureInfo.CurrentCulture;

    public DialogViewTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        session = new(TestPack.Division, PlannerState.Of(BuildSnapshot.Create(TestPack.Division.Pack)), new OperationApplier(TestPack.Division));
        editor = new(session, library, dispatcher);
    }

    private void Apply(params BuildOperation[] operations)
    {
        Assert.True(session.Apply([.. operations], session.Snapshot.Revision, EditOrigin.Desktop).Succeeded);
        dispatcher.RunPending();
    }

    public void Dispose()
    {
        editor.Dispose();
        CultureInfo.CurrentCulture = culture;
    }

    private Window Show()
    {
        var window = new Window { Width = 1360, Height = 860, Content = new EditorView { DataContext = editor } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static T Find<T>(Control root, Func<T, bool> match) where T : Control => root.GetVisualDescendants().OfType<T>().First(match);
    private static IEnumerable<string?> Texts(Control root) => root.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible).Select(text => text.Text);

    private static void AssertReportColumnsAlign(Window window, ListBox list, string headerName, string cellsName)
    {
        var parent = Assert.IsType<StackPanel>(list.Parent);
        var header = parent.Children.OfType<Grid>().Single(grid => grid.Name == headerName);
        var cells = list.GetVisualDescendants().OfType<Grid>().First(grid => grid.Name == cellsName);
        Assert.True(header.Bounds.Width > 0);
        Assert.True(cells.Bounds.Width > 0);
        foreach (var label in header.Children.OfType<TextBlock>())
        {
            var value = cells.Children.OfType<TextBlock>().Single(text => Grid.GetColumn(text) == Grid.GetColumn(label));
            Assert.Equal(label.TranslatePoint(default, window)!.Value.X, value.TranslatePoint(default, window)!.Value.X, 2);
        }
    }

    [AvaloniaFact]
    public async Task CompareOpensFromTheTopBarAndShowsTheTable()
    {
        library.Add("a", CompareViewModelTests.Edited("Tank", new EquipmentOperation("chest", "chest_n_5_11_tactical_chest")));
        var window = Show();
        try
        {
            Find<Button>(window, button => button.Name == "CompareButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var dialog = Assert.IsType<CompareWindow>(Assert.Single(window.OwnedWindows));
            var compare = Assert.IsType<CompareViewModel>(dialog.DataContext);
            Assert.Equal(CompareViewModel.Title, dialog.Title);
            await compare.Comparison;
            dispatcher.RunPending();
            Dispatcher.UIThread.RunJobs();

            var pickerB = Find<ComboBox>(dialog, box => box.Name == "BuildBPicker");
            Assert.Equal(2, pickerB.ItemCount);
            pickerB.SelectedItem = compare.Options[1];
            await compare.Comparison;
            dispatcher.RunPending();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(Texts(dialog), text => text == "Delta (B - A)");
            Assert.Contains(Texts(dialog), text => text == compare.Rows[0].Delta && text!.StartsWith('+'));
            Assert.False(Find<TextBlock>(dialog, text => text.Name == "CompareStatus").IsVisible);

            Find<Button>(dialog, button => button.Name == "CloseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(window.OwnedWindows);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RankUpgradesShowsWhileOpenAndApplyClosesIt()
    {
        Apply(new EquipmentOperation("chest", "chest_n_5_11_tactical_chest"));
        Apply(new AttributeOperation("chest", "core_1", "armor_gear_core", 100000));
        editor.LastRankedStat = "character_armor";
        var window = Show();
        try
        {
            editor.RankUpgradesCommand.Execute("chest");
            Dispatcher.UIThread.RunJobs();
            var dialog = Assert.IsType<RankUpgradesWindow>(Assert.Single(window.OwnedWindows));
            await editor.RankUpgrades!.Ranking;
            dispatcher.RunPending();
            Dispatcher.UIThread.RunJobs();

            Assert.StartsWith("Rank upgrades · ", dialog.Title, StringComparison.Ordinal);
            Assert.Equal(RankUpgradesViewModel.SearchPlaceholder, Find<TextBox>(dialog, box => box.Name == "RankSearchBox").PlaceholderText);
            Assert.True(Find<CheckBox>(dialog, box => box.Name == "OnlyGainsBox").IsChecked);
            Assert.Contains(Texts(dialog), text => text == RankUpgradesViewModel.Hint);
            var change = Find<TextBlock>(dialog, text => text.Text == "+70,000 Estimated Total Armor");
            Assert.Contains("up", change.Classes);
            Assert.Contains(Texts(dialog), text => text == "Core 1 · Armor (Defensive) at 170,000 (reroll)");

            // Searching for nothing that matches shows the empty state.
            Find<TextBox>(dialog, box => box.Name == "RankSearchBox").Text = "no such change";
            Dispatcher.UIThread.RunJobs();
            Assert.True(Find<TextBlock>(dialog, text => text.Name == "RankEmpty").IsVisible);
            Find<TextBox>(dialog, box => box.Name == "RankSearchBox").Text = "";
            Dispatcher.UIThread.RunJobs();

            var revision = session.Snapshot.Revision;
            Find<Button>(dialog, button => Equals(button.Content, "Apply")).Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(revision + 1, session.Snapshot.Revision);
            Assert.Null(editor.RankUpgrades);
            Assert.Empty(window.OwnedWindows);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ClosingRankUpgradesClearsIt()
    {
        Apply(new EquipmentOperation("chest", "chest_n_5_11_tactical_chest"));
        var window = Show();
        try
        {
            editor.RankUpgradesCommand.Execute("chest");
            Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(window.OwnedWindows);
            Find<Button>(dialog, button => button.Name == "CloseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Null(editor.RankUpgrades);
            Assert.Empty(window.OwnedWindows);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TheSimulateTabRunsAndShowsBoundResults()
    {
        Apply(new EquipmentOperation("primary_weapon", "primary_weapon_acr"),
            new ContextOperation("weapon_slot", Text: "primary_weapon"), new ContextOperation("enemy_target", Value: 1));
        var window = Show();
        try
        {
            Find<TabItem>(window, item => item.Name == "SimulateTab").IsSelected = true;
            Dispatcher.UIThread.RunJobs();
            Find<TextBox>(window, box => box.Name == "FightLengthBox").Text = "2.25";
            Find<TextBox>(window, box => box.Name == "FightsBox").Text = "2";
            Dispatcher.UIThread.RunJobs();
            Find<Button>(window, button => button.Name == "RunSimulationButton").Command!.Execute(null);
            await editor.Simulate.Running;
            dispatcher.RunPending();
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(editor.Simulate.Result);
            Assert.Contains("Weapon DPS:", Find<TextBlock>(window, text => text.Name == "SimulationResults").Text, StringComparison.Ordinal);
            Assert.Equal(1, Find<ProgressBar>(window, control => control.Name == "SimulationProgress").Value);
            var distributions = Find<Expander>(window, control => control.Name == "DpsDistributions");
            Assert.True(distributions.IsVisible);
            distributions.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Across 2 random fights", Find<TextBlock>(window, text => text.Name == "DistributionNote").Text, StringComparison.Ordinal);
            Assert.Equal(editor.Simulate.Distributions.Rows.Length, Find<ItemsControl>(window, control => control.Name == "DistributionRows").ItemCount);
            foreach (var row in editor.Simulate.Distributions.Rows)
            {
                Assert.Contains(row.Metric, Texts(window));
                Assert.Contains(row.Mean, Texts(window));
                Assert.Contains(row.StdDev, Texts(window));
                Assert.Contains(row.Minimum, Texts(window));
                Assert.Contains(row.P5, Texts(window));
                Assert.Contains(row.Median, Texts(window));
                Assert.Contains(row.P95, Texts(window));
                Assert.Contains(row.Maximum, Texts(window));
            }
            Assert.Contains("Standard deviation", Texts(window));
            Assert.Contains("95th percentile", Texts(window));
            var chart = Find<SimulationTimelineChart>(window, control => control.Name == "DamageTimeline");
            Assert.Equal(2, chart.Series.Bins.Length);
            Assert.Equal(2.25, chart.Series.Bins[1].End);
            Assert.Equal(0, chart.SelectedIndex);
            chart.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.End });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, editor.Simulate.SelectedTimelineIndex);
            var intervals = Find<ComboBox>(window, control => control.Name == "TimelineIntervalPicker");
            Assert.Equal(1, intervals.SelectedIndex);
            intervals.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, chart.SelectedIndex);
            Assert.Equal(1, editor.Simulate.SelectedTimelineIndex);
            intervals.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            intervals.SelectedIndex = -1;
            Dispatcher.UIThread.RunJobs();
            dispatcher.RunPending();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, editor.Simulate.SelectedTimelineIndex);
            Assert.Equal(0, intervals.SelectedIndex);
            chart.SelectedIndex = -1;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, chart.SelectedIndex);
            Assert.Equal(0, editor.Simulate.SelectedTimelineIndex);
            Assert.Contains("Mean DPS:", Find<TextBlock>(window, text => text.Name == "TimelineDetails").Text, StringComparison.Ordinal);
            Find<TextBox>(window, box => box.Name == "SeedBox").Text = "2";
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(chart.Series.Bins);
            Assert.False(distributions.IsVisible);
            Assert.Empty(editor.Simulate.Distributions.Rows);
            Assert.False(Find<StackPanel>(window, panel => panel.Name == "TimelinePanel").IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task IncomingReportShowsActualReadingsAndClearsWithAnOptionEdit()
    {
        Apply(new EquipmentOperation("primary_weapon", "primary_weapon_acr"),
            new ContextOperation("weapon_slot", Text: "primary_weapon"), new ContextOperation("enemy_target", Value: 1));
        var window = Show();
        try
        {
            Find<TabItem>(window, item => item.Name == "SimulateTab").IsSelected = true;
            editor.Simulate.FightLength = "2";
            editor.Simulate.Fights = "2";
            editor.Simulate.Incoming = true;
            editor.Simulate.RunCommand.Execute(null);
            await editor.Simulate.Running;
            dispatcher.RunPending();
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(editor.Simulate.Result?.Incoming);
            var panel = Find<Expander>(window, control => control.Name == "IncomingReportPanel");
            Assert.True(panel.IsVisible);
            panel.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(16, Find<ItemsControl>(window, control => control.Name == "IncomingReadings").ItemCount);
            foreach (var reading in editor.Simulate.IncomingReport.Readings)
            {
                Assert.Contains(reading.Label, Texts(window));
                Assert.Contains(reading.Value, Texts(window));
            }
            Assert.Equal(editor.Simulate.IncomingReport.Unobserved, Find<TextBlock>(window, text => text.Name == "UnobservedIncomingEvents").Text);
            Assert.Equal(editor.Simulate.IncomingReport.Timings.Length, Find<ItemsControl>(window, control => control.Name == "IncomingTimings").ItemCount);
            foreach (var timing in editor.Simulate.IncomingReport.Timings)
            {
                Assert.Contains(timing.Metric, Texts(window));
                foreach (var value in new[] { timing.Mean, timing.StdDev, timing.Minimum, timing.P5, timing.Median, timing.P95, timing.Maximum })
                { Assert.Contains(value, Texts(window)); }
            }
            Assert.Contains("other fights are excluded", Find<TextBlock>(window, text => text.Name == "IncomingReportNote").Text, StringComparison.Ordinal);
            editor.Simulate.Incoming = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(panel.IsVisible);
            Assert.Empty(editor.Simulate.IncomingReport.Readings);
            Assert.Empty(editor.Simulate.IncomingReport.Unobserved);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task UptimeReadingsShowMeasuredValuesAndDistinguishFixedAssumptions()
    {
        Apply(new EquipmentOperation("primary_weapon", "primary_weapon_acr"),
            new TalentOperation("primary_weapon", "talent", "weapon_talent_optimist"),
            new ContextOperation("weapon_slot", Text: "primary_weapon"), new ContextOperation("enemy_target", Value: 1));
        var window = Show();
        window.Height = 1800;
        try
        {
            Find<TabItem>(window, item => item.Name == "SimulateTab").IsSelected = true;
            editor.Simulate.FightLength = "2";
            editor.Simulate.Fights = "2";
            editor.Simulate.RunCommand.Execute(null);
            await editor.Simulate.Running;
            dispatcher.RunPending();
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(editor.Simulate.Result);
            var panel = Find<Expander>(window, control => control.Name == "UptimeReportPanel");
            Assert.True(panel.IsVisible);
            panel.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("full requested fight duration", Find<TextBlock>(window, text => text.Name == "UptimeReportNote").Text, StringComparison.Ordinal);
            Assert.Equal(editor.Simulate.UptimeReport.Sections.Length, Find<ItemsControl>(window, control => control.Name == "UptimeSections").ItemCount);
            var lists = window.GetVisualDescendants().OfType<ListBox>().Where(list => list.Name == "UptimeRows").ToArray();
            Assert.NotEmpty(lists);
            foreach (var list in lists)
            {
                var section = Assert.IsType<SimulationUptimeSection>(list.DataContext);
                Assert.Equal(section.Rows.Length, list.ItemCount);
                var first = section.Rows[0];
                AssertReportColumnsAlign(window, list, "UptimeHeader", "UptimeCells");
                foreach (var value in new[] { first.Label, first.Time, first.Share, first.Stacks })
                { Assert.Contains(value, Texts(list)); }
            }
            var magazine = lists.Single(list => ((SimulationUptimeSection)list.DataContext!).Name == "Magazine readings");
            Assert.Equal(101, magazine.ItemCount);
            Assert.InRange(magazine.GetVisualDescendants().OfType<ListBoxItem>().Count(), 1, 100);
            editor.Simulate.DriveBuffs = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(panel.IsVisible);
            Assert.False(editor.Simulate.UptimeReport.HasResult);
            editor.Simulate.RunCommand.Execute(null);
            await editor.Simulate.Running;
            dispatcher.RunPending();
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(editor.Simulate.Result);
            Assert.True(panel.IsVisible);
            Assert.Empty(editor.Simulate.UptimeReport.Sections);
            Assert.Contains("No state measurements", Find<TextBlock>(window, text => text.Name == "UptimeEmpty").Text, StringComparison.Ordinal);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task EventCaptureOptionShowsFirstFightRecordsAndClearsWhenUnchecked()
    {
        Apply(new EquipmentOperation("primary_weapon", "primary_weapon_acr"),
            new ContextOperation("weapon_slot", Text: "primary_weapon"), new ContextOperation("enemy_target", Value: 1));
        var window = Show();
        try
        {
            Find<TabItem>(window, item => item.Name == "SimulateTab").IsSelected = true;
            Dispatcher.UIThread.RunJobs();
            var capture = Find<CheckBox>(window, control => control.Name == "CaptureEventsBox");
            Assert.False(capture.IsChecked);
            capture.IsChecked = true;
            editor.Simulate.FightLength = "2";
            editor.Simulate.Fights = "2";
            editor.Simulate.RunCommand.Execute(null);
            await editor.Simulate.Running;
            dispatcher.RunPending();
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(editor.Simulate.Result);
            var panel = Find<Expander>(window, control => control.Name == "EventReportPanel");
            Assert.True(panel.IsVisible);
            panel.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            var rows = Find<ListBox>(window, control => control.Name == "EventRows");
            Assert.NotEmpty(editor.Simulate.Result.FirstFightEvents);
            Assert.Equal(editor.Simulate.Result.FirstFightEvents.Length, rows.ItemCount);
            Assert.Equal(360, rows.MaxHeight);
            AssertReportColumnsAlign(window, rows, "EventHeader", "EventCells");
            window.Width = 620;
            Dispatcher.UIThread.RunJobs();
            AssertReportColumnsAlign(window, rows, "EventHeader", "EventCells");
            var first = editor.Simulate.EventReport.Rows[0];
            foreach (var value in new[] { first.Time, first.Kind, first.Slot, first.Amount })
            { Assert.Contains(value, Texts(rows)); }
            Assert.Contains("First fight only", Find<TextBlock>(window, text => text.Name == "EventReportNote").Text, StringComparison.Ordinal);
            capture.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(panel.IsVisible);
            Assert.Empty(editor.Simulate.EventReport.Rows);
            Assert.Null(editor.Simulate.Result);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task IncludedTurretShowsSeparateImpactAndContinuousSkillStatistics()
    {
        Apply(new EquipmentOperation("primary_weapon", "primary_weapon_acr"),
            new ContextOperation("weapon_slot", Text: "primary_weapon"), new ContextOperation("enemy_target", Value: 1));
        editor.SkillSlots[0].Selected = editor.SkillSlots[0].Choices.Single(choice => choice.Id == "skill_assault_turret");
        dispatcher.RunPending();
        var window = Show();
        try
        {
            Find<TabItem>(window, item => item.Name == "SimulateTab").IsSelected = true;
            editor.Simulate.DriveBuffs = false;
            editor.Simulate.IncludeSkills = true;
            editor.Simulate.FightLength = "2.25";
            editor.Simulate.Fights = "2";
            editor.Simulate.RunCommand.Execute(null);
            await editor.Simulate.Running;
            dispatcher.RunPending();
            Dispatcher.UIThread.RunJobs();
            var result = Assert.IsType<CharacterPlanner.Simulation.DummyFightResult>(editor.Simulate.Result);
            Assert.Equal(0, result.SkillDps!.Impact.Mean);
            Assert.True(result.SkillDps.ContinuousWindow.Mean > 0);
            var panel = Find<Expander>(window, control => control.Name == "DpsDistributions");
            panel.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(5, Find<ItemsControl>(window, control => control.Name == "DistributionRows").ItemCount);
            Assert.Contains("Skill impact DPS", Texts(window));
            Assert.Contains("Skill continuous-window DPS", Texts(window));
            Assert.Contains("Skill continuous-window DPS:", Find<TextBlock>(window, text => text.Name == "SimulationResults").Text, StringComparison.Ordinal);
            editor.Simulate.IncludeSkills = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Null(editor.Simulate.Result);
            Assert.Empty(editor.Simulate.Distributions.Rows);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TheSimulateTabChecksTheFormAndCanStartSessionWork()
    {
        var window = Show();
        try
        {
            var tabs = Find<TabControl>(window, control => control.Name == "Tabs");
            // Go's order: … Party, Simulate, Notes.
            var headers = tabs.Items.OfType<TabItem>().Select(item => item.Header as string).ToList();
            Assert.Equal(headers.IndexOf("Party") + 1, headers.IndexOf("Simulate"));
            Assert.Equal(headers.IndexOf("Simulate") + 1, headers.IndexOf("Notes"));
            Find<TabItem>(window, item => item.Name == "SimulateTab").IsSelected = true;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("60", Find<TextBox>(window, box => box.Name == "FightLengthBox").Text);
            Assert.Equal("none", Find<TextBox>(window, box => box.Name == "DistanceBox").PlaceholderText);
            Assert.Equal(3, Find<ComboBox>(window, box => box.Name == "SwapsPicker").ItemCount);
            Assert.True(Find<RadioButton>(window, button => button.Name == "RandomFightsButton").IsChecked);
            Assert.True(Find<Button>(window, button => button.Name == "RunSimulationButton").IsEffectivelyEnabled);
            Assert.False(Find<Button>(window, button => button.Name == "CancelSimulationButton").IsEffectivelyEnabled);

            Find<TextBox>(window, box => box.Name == "FightsBox").Text = "0";
            Dispatcher.UIThread.RunJobs();
            var status = Find<TextBlock>(window, text => text.Name == "SimulateStatus");
            Assert.Equal("Fights must be a whole number from 1 to 10,000", status.Text);
            Assert.Contains("error", status.Classes);

            Find<RadioButton>(window, button => button.Name == "AverageFightButton").IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(editor.Simulate.AverageFight);
        }
        finally { window.Close(); }
    }
}
