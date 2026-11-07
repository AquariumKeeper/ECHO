using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using CharacterPlanner.Desktop.ViewModels;

namespace CharacterPlanner.Desktop.Views;

public partial class EditorView : UserControl, IEditorHost
{
    public EditorView()
    {
        InitializeComponent();
        // A typed roll or rank commits when focus leaves its box, as well as on Enter.
        AddHandler(LostFocusEvent, OnLostFocus, RoutingStrategies.Bubble);
        // Enter commits a typed value too, in every box that holds one.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private EditorViewModel? attached;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        artworkWindow?.Close();
        // An editor this view no longer shows mustn't keep asking through it.
        if (attached is not null)
        {
            if (attached.Host == this) { attached.Host = null; }
            attached.PropertyChanged -= OnEditorChanged;
        }
        attached = DataContext as EditorViewModel;
        if (attached is not null)
        {
            attached.Host = this;
            attached.PropertyChanged += OnEditorChanged;
        }
    }

    private RankUpgradesWindow? rankWindow;
    private ArtworkWindow? artworkWindow;

    private async void OnArtwork(object? sender, RoutedEventArgs args)
    {
        if (artworkWindow is { } existing) { existing.Activate(); return; }
        if (DataContext is not EditorViewModel { ArtworkManager: { } manager } || TopLevel.GetTopLevel(this) is not Window owner) { return; }
        var window = artworkWindow = new ArtworkWindow { DataContext = manager };
        window.Closed += (_, _) => { if (ReferenceEquals(artworkWindow, window)) { artworkWindow = null; } };
        var showing = window.ShowDialog(owner);
        await manager.OpenAsync();
        await showing;
    }

    /// <summary>Shows Rank Upgrades while the editor has one open, and closes it when Apply (or anything else) closes it.</summary>
    private void OnEditorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(EditorViewModel.RankUpgrades) || sender is not EditorViewModel editor) { return; }
        if (rankWindow is { } open && !ReferenceEquals(open.DataContext, editor.RankUpgrades))
        {
            rankWindow = null;
            open.Close();
        }
        if (editor.RankUpgrades is not { } ranking || rankWindow is not null || TopLevel.GetTopLevel(this) is not Window owner) { return; }
        var window = rankWindow = new RankUpgradesWindow { DataContext = ranking };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(rankWindow, window)) { rankWindow = null; }
            if (ReferenceEquals(editor.RankUpgrades, ranking)) { editor.CloseRankUpgrades(); }
        };
        _ = window.ShowDialog(owner);
    }

    /// <summary>Compare talents, as the Go app's: shown at once, its changes worked out while it is open.</summary>
    async void IEditorHost.CompareTalents(TalentCompareViewModel compare)
    {
        using (compare)
        {
            if (TopLevel.GetTopLevel(this) is not Window owner) { return; }
            var dialog = new TalentCompareWindow { DataContext = compare };
            var shown = dialog.ShowDialog(owner);
            _ = compare.LoadAsync();
            await shown;
        }
    }

    /// <summary>Compare Builds, as the Go app's: the current and saved builds side by side, every stat with its change.</summary>
    private async void OnCompare(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner || DataContext is not EditorViewModel editor) { return; }
        using var compare = editor.CreateCompare();
        var dialog = new CompareWindow { DataContext = compare };
        var shown = dialog.ShowDialog(owner);
        await compare.LoadAsync();
        await shown;
    }

    void IEditorHost.ShowAllStats() => StatsTab.IsSelected = true;

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.Source is TextBox { DataContext: IDraftValue draft, AcceptsReturn: false }) { draft.CommitValue(); }
    }

    private static void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TextBox { DataContext: IDraftValue draft }) { draft.CommitValue(); }
    }

    /// <summary>About Character Planner, as the Go app's: the program build, the game release and the data version.</summary>
    private async void OnAbout(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EditorViewModel editor) { return; }
        var form = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 16, RowSpacing = 8 };
        foreach (var (caption, value) in editor.About)
        {
            form.RowDefinitions.Add(new(GridLength.Auto));
            var row = form.RowDefinitions.Count - 1;
            var label = new TextBlock { Text = caption, Classes = { "label" } };
            var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(label, row);
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 1);
            form.Children.Add(label);
            form.Children.Add(text);
        }
        await Dialogs.InfoAsync(this, "About Character Planner",
            [form, new TextBlock { Text = "Unofficial fan tool. Not affiliated with Ubisoft or Massive Entertainment.", Classes = { "label" }, TextWrapping = TextWrapping.Wrap }]);
    }

    /// <summary>Share Build, as the Go app's: the build's code, read-only, with Copy to Clipboard; or why it can't be shared.</summary>
    private async void OnShare(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EditorViewModel editor) { return; }
        var shared = editor.Sharing.Share();
        var copy = new Button { Name = "CopyButton", Content = SharingViewModel.CopyLabel, IsEnabled = shared.Problem is null };
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(copy)?.Clipboard is { } clipboard) { await clipboard.SetTextAsync(shared.Code); }
        };
        await Dialogs.InfoAsync(this, SharingViewModel.ShareTitle,
        [
            new TextBlock { Text = SharingViewModel.ShareLabel },
            shared.Problem is { } problem
                ? new TextBlock { Text = problem, Classes = { "error" } }
                : new TextBox { Name = "ShareCodeBox", Text = shared.Code, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 200 }
        ], copy);
    }

    /// <summary>Import Build, as the Go app's: a pasted code (either format) checked inline, then opened as Open opens a build.</summary>
    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EditorViewModel editor) { return; }
        var code = new TextBox { Name = "ImportCodeBox", PlaceholderText = SharingViewModel.ImportPlaceholder };
        ImportedBuild? read = null;
        await Dialogs.FormAsync(this, SharingViewModel.ImportTitle, "Load", [new TextBlock { Text = SharingViewModel.ImportLabel }, code], () =>
        {
            read = editor.Sharing.Read(code.Text, out var problem);
            return Task.FromResult(read is null ? problem : null);
        });
        // The dialog has closed, so asking about unsaved changes doesn't stack on it.
        if (read is not null) { await editor.Sharing.ImportAsync(read); }
    }

    Task<bool> IEditorHost.ConfirmAsync(string title, string question, string confirm) => Dialogs.ConfirmAsync(this, title, question, confirm, "Cancel");

    async Task<string?> IEditorHost.AskTextAsync(string title, string label, string action, string text, Func<string, string?> check)
    {
        var box = new TextBox { Name = "AskTextBox", Text = text };
        Avalonia.Automation.AutomationProperties.SetName(box, label);
        string? answer = null;
        await Dialogs.FormAsync(this, title, action, [new TextBlock { Text = label }, box], () =>
        {
            var problem = check(box.Text ?? "");
            if (problem is null) { answer = box.Text ?? ""; }
            return Task.FromResult(problem);
        });
        return answer;
    }

    /// <summary>Asks before unsaved changes are discarded; anything but an explicit "Discard" keeps them.</summary>
    Task<bool> IEditorHost.ConfirmDiscardAsync() =>
        Dialogs.ConfirmAsync(this, "Unsaved changes", "This build has unsaved changes. Discard them?", "Discard changes", "Keep editing");
}
