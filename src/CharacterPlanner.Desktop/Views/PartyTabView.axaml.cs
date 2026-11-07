using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using CharacterPlanner.Desktop.ViewModels;

namespace CharacterPlanner.Desktop.Views;

public partial class PartyTabView : UserControl
{
    public PartyTabView()
    {
        InitializeComponent();
    }

    private PartyViewModel? Party => (DataContext as EditorViewModel)?.Party;

    /// <summary>Add from Share Code: a name (the build's own by default) and the pasted code.</summary>
    private async void OnAddFromCode(object? sender, RoutedEventArgs e)
    {
        if (Party is not { } party) { return; }
        var name = new TextBox { Name = "PartyNameBox", PlaceholderText = PartyViewModel.NamePlaceholder };
        var code = new TextBox { Name = "PartyCodeBox", PlaceholderText = PartyViewModel.CodePlaceholder };
        await Dialogs.FormAsync(this, "Add from Share Code", "Add", [name, code], () => Task.FromResult(party.AddFromCode(name.Text, code.Text)));
    }

    /// <summary>Add from Library: a saved build and a name (the build's own by default).</summary>
    private async void OnAddFromLibrary(object? sender, RoutedEventArgs e)
    {
        if (Party is not { } party) { return; }
        var pick = new ComboBox { Name = "PartyLibraryPicker", PlaceholderText = PartyViewModel.LibraryPlaceholder, HorizontalAlignment = HorizontalAlignment.Stretch };
        var name = new TextBox { Name = "PartyNameBox", PlaceholderText = PartyViewModel.NamePlaceholder };
        try { pick.ItemsSource = await party.LibraryChoicesAsync(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            party.Editor.Message = $"The library couldn't be read: {exception.Message}";
            return;
        }
        await Dialogs.FormAsync(this, "Add from Library", "Add", [pick, name], () => party.AddFromLibraryAsync(pick.SelectedItem as Choice, name.Text));
    }

    /// <summary>Add by Hand: a name and, optionally, the giver armor buffs that share it use.</summary>
    private async void OnAddByHand(object? sender, RoutedEventArgs e)
    {
        if (Party is not { } party) { return; }
        var name = new TextBox { Name = "PartyNameBox", PlaceholderText = PartyViewModel.HandNamePlaceholder };
        var armor = new TextBox { Name = "PartyArmorBox", PlaceholderText = PartyViewModel.HandArmorPlaceholder };
        await Dialogs.FormAsync(this, "Add by Hand", "Add", [name, armor], () => Task.FromResult(party.AddByHand(name.Text, armor.Text)));
    }
}
