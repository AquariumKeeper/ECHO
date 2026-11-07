using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CharacterPlanner.Desktop.Views;

public partial class RankUpgradesWindow : Window
{
    public RankUpgradesWindow()
    {
        InitializeComponent();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
