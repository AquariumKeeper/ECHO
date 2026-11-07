using Avalonia.Threading;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Infrastructure.Storage;

namespace CharacterPlanner.Desktop.Services;

/// <summary>Posts view-model updates to Avalonia's UI thread.</summary>
public sealed class AvaloniaDispatcher : IUiDispatcher
{
    public static readonly AvaloniaDispatcher Instance = new();

    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}

/// <summary>Where the desktop app finds its pack and keeps its library.</summary>
public static class AppPaths
{
    /// <summary>The Division 2 pack bundled beside the app.</summary>
    public static string Pack => Path.Combine(AppContext.BaseDirectory, "packs", "division-2");

    /// <summary>
    /// The app's own library, under the user's local app data. It's kept apart from the Go app's library, which the
    /// C# app never writes; a copy of that one is imported once (<see cref="GoLibraryImport"/>).
    /// </summary>
    public static string Library => AppDataPaths.Library;

    /// <summary>The saved assumption sets (F45), the app's own file; the Go app's is never read or written.</summary>
    public static string AssumptionSets => Path.Combine(Data, "assumption-sets.json");

    /// <summary>The window's last size.</summary>
    public static string Window => Path.Combine(Data, "window.json");

    /// <summary>The app's own data folder: the library, assumption sets, window size and the Go library's import record.</summary>
    public static string Data => AppDataPaths.Folder;
    public static string Artwork => Path.Combine(Data, "artwork-packs");
}
