using Avalonia.Controls;
using Avalonia.Interactivity;
using CharacterPlanner.Desktop.ViewModels;

namespace CharacterPlanner.Desktop.Views;

public partial class InspectorView : UserControl
{
    private SpecializationTreeWindow? tree;

    public InspectorView()
    {
        InitializeComponent();
    }

    /// <summary>Opens the specialization tree beside the editor, or brings the open one forward.</summary>
    private void OnOpenTree(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not SpecializationViewModel specialization || TopLevel.GetTopLevel(this) is not Window owner) { return; }
        if (tree is not null)
        {
            tree.Activate();
            return;
        }
        tree = new SpecializationTreeWindow { DataContext = specialization.Tree };
        tree.Closed += (_, _) => tree = null;
        tree.Show(owner);
    }
}
