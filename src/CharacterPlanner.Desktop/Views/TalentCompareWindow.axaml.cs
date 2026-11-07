using Avalonia.Controls;
using Avalonia.Interactivity;
using CharacterPlanner.Desktop.ViewModels;

namespace CharacterPlanner.Desktop.Views;

public partial class TalentCompareWindow : Window
{
    private TalentCompareViewModel? compare;

    public TalentCompareWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // Choosing a talent closes the dialog, as Go's did.
        if (compare is not null) { compare.Chosen -= OnChosen; }
        compare = DataContext as TalentCompareViewModel;
        if (compare is not null) { compare.Chosen += OnChosen; }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (compare is not null) { compare.Chosen -= OnChosen; }
    }

    private void OnChosen(object? sender, EventArgs e) => Close();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
