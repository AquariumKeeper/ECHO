using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CharacterPlanner.Desktop.ViewModels;
using Avalonia.Input;
using System.ComponentModel;
using Avalonia.Threading;

namespace CharacterPlanner.Desktop.Views;

public partial class ArtworkWindow : Window
{
    private ArtworkManagerViewModel? manager;
    private Control? restoreFocus;

    public ArtworkWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (manager is not null) { manager.PropertyChanged -= OnManagerChanged; }
            manager = DataContext as ArtworkManagerViewModel;
            restoreFocus = null;
            if (manager is not null) { manager.PropertyChanged += OnManagerChanged; }
        };
        Closed += (_, _) => { (DataContext as ArtworkManagerViewModel)?.CloseDialog(); DataContext = null; };
        AddHandler(PointerPressedEvent, (_, _) => { if (manager?.IsBusy == true) { restoreFocus = null; } }, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, _) => { if (manager?.IsBusy == true) { restoreFocus = null; } }, RoutingStrategies.Tunnel);
        KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) { return; }
            if (DataContext is ArtworkManagerViewModel { IsBusy: true } busyManager) { busyManager.CancelCommand.Execute(null); }
            else { Close(); }
            args.Handled = true;
        };
    }

    private void OnManagerChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ArtworkManagerViewModel.IsBusy)) { return; }
        if (manager?.IsBusy == true) { restoreFocus = FocusManager?.GetFocusedElement() as Control; return; }
        var previous = restoreFocus;
        restoreFocus = null;
        // Busy commands disable their buttons. Restore keyboard position after re-enabling,
        // unless the user navigated or clicked elsewhere while the operation ran.
        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible && manager?.IsBusy == false && previous is { IsEffectivelyEnabled: true } && TopLevel.GetTopLevel(previous) == this)
            { previous.Focus(); }
        }, DispatcherPriority.Input);
    }
    private void OnClose(object? sender, RoutedEventArgs args) => Close();
    private async void OnInstall(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not ArtworkManagerViewModel manager || manager.IsBusy) { return; }
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new()
            {
                Title = "Install artwork ZIP",
                AllowMultiple = false,
                FileTypeFilter = [new("Artwork ZIP") { Patterns = ["*.zip"] }]
            });
            try
            {
                if (!IsVisible || !ReferenceEquals(DataContext, manager)) { return; }
                if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) { await manager.InstallAsync(path); }
                else if (files.Count > 0) { manager.FilePickerFailed("Select a ZIP available as a local file"); }
            }
            finally { foreach (var file in files) { file.Dispose(); } }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        { manager.FilePickerFailed(exception.Message); }
    }
}
