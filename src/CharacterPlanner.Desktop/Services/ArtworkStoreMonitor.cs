using CharacterPlanner.Application.Artwork;

namespace CharacterPlanner.Desktop.Services;

/// <summary>Coalesces published artwork/settings changes and ignores installer, lease and backup housekeeping.</summary>
internal sealed class ArtworkStoreMonitor : IDisposable
{
    private readonly FileSystemWatcher watcher;
    private readonly Timer timer;
    private readonly string root;

    public ArtworkStoreMonitor(string directory, Action changed)
    {
        root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) { throw new IOException("Artwork store cannot be a link"); }
        timer = new(_ => changed(), null, Timeout.Infinite, Timeout.Infinite);
        FileSystemWatcher? created = null;
        try
        {
            created = new(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite };
            created.Created += OnChanged;
            created.Changed += OnChanged;
            created.Deleted += OnChanged;
            created.Renamed += OnChanged;
            created.Error += (_, _) => Schedule();
            created.EnableRaisingEvents = true;
            watcher = created;
        }
        catch { created?.Dispose(); timer.Dispose(); throw; }
    }

    private void OnChanged(object sender, FileSystemEventArgs args)
    {
        if (Relevant(args.FullPath) || args is RenamedEventArgs renamed && Relevant(renamed.OldFullPath)) { Schedule(); }
    }

    private bool Relevant(string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var first = relative.Split(Path.DirectorySeparatorChar)[0];
        return relative.Equals("artwork-settings.json", StringComparison.OrdinalIgnoreCase) || ArtworkPaths.IsIdentifier(first.ToLowerInvariant());
    }

    private void Schedule()
    {
        try { timer.Change(350, Timeout.Infinite); }
        catch (ObjectDisposedException) { /* A queued filesystem event arrived after shutdown. */ }
    }

    public void Dispose() { watcher.Dispose(); timer.Dispose(); }
}
