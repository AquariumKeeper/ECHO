using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CharacterPlanner.Desktop.ViewModels;

namespace CharacterPlanner.Desktop.Views;

/// <summary>The specialization tree in its own window; it stays open beside the editor and follows the build.</summary>
public partial class SpecializationTreeWindow : Window
{
    public SpecializationTreeWindow()
    {
        InitializeComponent();
        // Ctrl and the wheel zoom, as a tree planner's canvas does; the wheel alone scrolls.
        AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        // It opens with the whole tree in view.
        Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Fit, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Zooms so the whole tree fits the space it has.</summary>
    internal void Fit()
    {
        if (DataContext is not SpecializationTreeViewModel { Width: > 0 } tree || TreeScroll.Bounds.Width <= 0) { return; }
        tree.Zoom = Math.Min(TreeScroll.Bounds.Width / tree.Width, TreeScroll.Bounds.Height / tree.Height) * 0.98;
    }

    private void OnFit(object? sender, RoutedEventArgs e) => Fit();

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || DataContext is not SpecializationTreeViewModel tree) { return; }
        tree.Zoom += e.Delta.Y > 0 ? 0.1 : -0.1;
        e.Handled = true;
    }

    private static TreeNodeViewModel? Node(object? sender) => (sender as Control)?.DataContext as TreeNodeViewModel;

    private void OnNodeEntered(object? sender, PointerEventArgs e) => Node(sender)?.Preview();
    private void OnNodeExited(object? sender, PointerEventArgs e) => Node(sender)?.EndPreview();
    private void OnNodeFocused(object? sender, FocusChangedEventArgs e) => Node(sender)?.Preview();
    private void OnNodeUnfocused(object? sender, RoutedEventArgs e) => Node(sender)?.EndPreview();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
