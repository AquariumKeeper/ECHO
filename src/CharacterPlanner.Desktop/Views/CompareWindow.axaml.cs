using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CharacterPlanner.Desktop.Views;

public partial class CompareWindow : Window
{
    public CompareWindow()
    {
        InitializeComponent();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
