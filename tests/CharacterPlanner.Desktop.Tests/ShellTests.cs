using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using CharacterPlanner.Desktop;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Desktop.Views;
using CharacterPlanner.Infrastructure.Storage;

[assembly: AvaloniaTestApplication(typeof(CharacterPlanner.Desktop.Tests.TestAppBuilder))]

namespace CharacterPlanner.Desktop.Tests;

public static class TestAppBuilder
{
    /// <summary>
    /// Headless drawing by default. A snapshot run (CHARACTER_PLANNER_SNAPSHOT set) renders real frames with Skia so
    /// a test can capture what the window looks like; ordinary runs don't need the native renderer.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() => Environment.GetEnvironmentVariable("CHARACTER_PLANNER_SNAPSHOT") is { Length: > 0 }
        ? AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        : AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public class ShellTests
{
    [AvaloniaFact]
    public void ShellLoadsThemeAndCompiledBinding()
    {
        var model = new MainViewModel();
        var window = new MainWindow { DataContext = model };
        try
        {
            window.Show();
            Assert.Equal(model.Status, window.FindControl<TextBlock>("StatusText")!.Text);
            Assert.True(window.TryFindResource("DivisionAccent", out var accent));
            Assert.NotNull(accent);
            Assert.True(window.IsVisible);
            Assert.False(window.FindControl<TextBlock>("ErrorText")!.IsVisible);
            // The planner's own mark, loaded from the app's resources.
            Assert.NotNull(window.Icon);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The pack bundled with the app (artwork included) loads into the editor, as at startup.</summary>
    [AvaloniaFact]
    public async Task TheBundledPackOpensTheEditor()
    {
        var model = new MainViewModel();
        var window = new MainWindow { DataContext = model };
        var library = Path.Combine(Path.GetTempPath(), "bundled-library-" + Guid.NewGuid().ToString("N"));
        try
        {
            window.Show();
            await model.LoadAsync(CharacterPlanner.Desktop.Services.AppPaths.Pack, new FileBuildLibrary(library), new TestPack.QueueDispatcher());
            Assert.Null(model.Error);
            Assert.NotNull(model.Editor);
            Assert.Contains("Division 2", model.Status, StringComparison.Ordinal);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Contains(window.GetVisualDescendants().OfType<EditorView>(), view => view.IsVisible);
        }
        finally
        {
            window.Close();
            model.Dispose();
            if (Directory.Exists(library)) { Directory.Delete(library, recursive: true); }
        }
    }

    /// <summary>A pack that can't be read is reported in the window instead of closing the app.</summary>
    [AvaloniaFact]
    public async Task AMissingPackIsReportedInTheWindow()
    {
        var model = new MainViewModel();
        var window = new MainWindow { DataContext = model };
        try
        {
            window.Show();
            var missing = Path.Combine(Path.GetTempPath(), "no-pack-" + Guid.NewGuid().ToString("N"));
            await model.LoadAsync(missing, new FileBuildLibrary(missing), new TestPack.QueueDispatcher());
            Assert.Null(model.Editor);
            Assert.StartsWith("The Division 2 pack couldn't be read", model.Error, StringComparison.Ordinal);
            Assert.True(window.FindControl<TextBlock>("ErrorText")!.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }
}
