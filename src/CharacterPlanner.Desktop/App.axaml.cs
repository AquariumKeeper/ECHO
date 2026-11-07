using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CharacterPlanner.Desktop.Services;
using CharacterPlanner.Desktop.Themes;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Desktop.Views;
using CharacterPlanner.Infrastructure.Sessions;
using CharacterPlanner.Infrastructure.Storage;

namespace CharacterPlanner.Desktop;

public partial class App : Avalonia.Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The window appears at once; the pack loads behind it, and the editor replaces the loading text.
            var model = new MainViewModel();
            desktop.MainWindow = new MainWindow(AppPaths.Window) { DataContext = model };
            desktop.Exit += (_, _) => model.Dispose();
            _ = model.LoadAsync(AppPaths.Pack, new FileBuildLibrary(AppPaths.Library), AvaloniaDispatcher.Instance,
                pack => PackTheme.Apply(this, pack.Presentation.Theme), new FileAssumptionSetStore(AppPaths.AssumptionSets),
                new GoLibraryImport(GoLibraryImport.DefaultSource, AppPaths.Data), SessionRegistry.DefaultDirectory, AppPaths.Artwork);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
