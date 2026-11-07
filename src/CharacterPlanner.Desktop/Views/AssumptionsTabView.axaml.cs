using Avalonia.Controls;
using Avalonia.Interactivity;
using CharacterPlanner.Desktop.ViewModels;

namespace CharacterPlanner.Desktop.Views;

public partial class AssumptionsTabView : UserControl
{
    public AssumptionsTabView()
    {
        InitializeComponent();
    }

    private AssumptionSetBarViewModel? Sets => (DataContext as EditorViewModel)?.AssumptionsTab.Sets;

    /// <summary>
    /// Save As, as the Go app's: a name (the matching set's, if any), then, when the name is taken, whether to replace it.
    /// </summary>
    private async void OnSaveSet(object? sender, RoutedEventArgs e)
    {
        if (Sets is not { } sets) { return; }
        var name = new TextBox { Name = "AssumptionSetNameBox", Text = sets.SuggestedName, PlaceholderText = AssumptionSetBarViewModel.NamePlaceholder };
        AssumptionSetSave? taken = null;
        await Dialogs.FormAsync(this, AssumptionSetBarViewModel.SaveTitle, "Save", [new TextBlock { Text = "Name" }, name], async () =>
        {
            var saved = await sets.SaveAsync(name.Text ?? "", replace: false);
            if (saved.Exists) { taken = saved; return null; }
            return saved.Problem;
        });
        // The form has closed, so the question doesn't stack on it.
        if (taken is not null && await Dialogs.ConfirmAsync(this, AssumptionSetBarViewModel.ReplaceTitle, $"{taken.Problem}. Replace it?"))
        { await sets.SaveAsync(name.Text ?? "", replace: true); }
    }

    /// <summary>Delete, as the Go app's: the chosen set, after asking; the assumptions stay as they are.</summary>
    private async void OnDeleteSet(object? sender, RoutedEventArgs e)
    {
        if (Sets is not { } sets || sets.DeleteCandidate() is not { } name) { return; }
        if (await Dialogs.ConfirmAsync(this, AssumptionSetBarViewModel.DeleteTitle, AssumptionSetBarViewModel.DeleteQuestion(name))) { await sets.DeleteAsync(name); }
    }
}
