namespace CharacterPlanner.Infrastructure.Storage;

/// <summary>The app's own data folder under the user's local app data, shared by the desktop and the CLI.</summary>
public static class AppDataPaths
{
    /// <summary>The library, assumption sets, window size and the Go library's import record live here.</summary>
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CharacterPlanner", "alpha");

    /// <summary>The saved-build library both programs read and write, kept apart from the Go app's library.</summary>
    public static string Library => Path.Combine(Folder, "library");
}
