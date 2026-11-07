using Avalonia.Media.Imaging;
using Avalonia;
using CharacterPlanner.Application.Artwork;

namespace CharacterPlanner.Desktop.Views;

/// <summary>Shares images only while views use them; pins user revisions for pending decodes and displayed bitmaps.</summary>
internal static class ArtworkBitmapCache
{
    private static readonly object Gate = new();
    private static readonly Dictionary<(string Path, int Width), Entry> Entries = [];
    private static readonly List<Owner> Owners = [];
    private static readonly SemaphoreSlim Decoders = new(2);
    private const long IdleByteLimit = 32 * 1024 * 1024;
    private static readonly LinkedList<Entry> Idle = [];
    private static long idleBytes;

    internal sealed class Owner(string directory, IDisposable lifetime)
    {
        public string Prefix { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        public IDisposable Lifetime { get; } = lifetime;
        public int References { get; set; } = 1;
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class Registration(Owner owner) : IDisposable
    {
        private Owner? current = owner;
        public Task Released { get; } = owner.Released.Task;
        public void Dispose()
        {
            lock (Gate)
            {
                if (current is not { } value) { return; }
                current = null;
                Release(value);
            }
        }
    }

    internal sealed class Handle(Entry entry) : IDisposable
    {
        private Entry? current = entry;
        public Task<Bitmap?> Ready { get; } = entry.Ready;
        public void Dispose()
        {
            lock (Gate)
            {
                if (current is not { } value) { return; }
                current = null;
                if (--value.References != 0) { return; }
                if (value.Loaded) { Finish(value); }
                else { Entries.Remove(value.Key); }
            }
        }
    }

    internal sealed class Entry((string Path, int Width) key)
    {
        public (string Path, int Width) Key { get; } = key;
        public Task<Bitmap?> Ready { get; set; } = Task.FromResult<Bitmap?>(null);
        public int References { get; set; }
        public Bitmap? Bitmap { get; set; }
        public bool Loaded { get; set; }
        public Owner? Owner { get; set; }
        public LinkedListNode<Entry>? IdleNode { get; set; }
        public long Bytes => Bitmap is { } bitmap ? (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4 : 0;
        public (long Length, DateTime Write)? Stamp { get; set; }
    }

    public static Registration Register(string directory, IDisposable lifetime)
    {
        var owner = new Owner(directory, lifetime);
        lock (Gate)
        {
            // A newly managed revision must never inherit an idle, unregistered decode.
            foreach (var entry in Entries.Values.Where(value => value.IdleNode is not null &&
                value.Key.Path.StartsWith(owner.Prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                RemoveIdle(entry);
                Entries.Remove(entry.Key);
                Retire(entry);
            }
            Owners.Add(owner);
        }
        return new Registration(owner);
    }

    public static Handle? Acquire(string? path, double shownWidth)
    {
        if (string.IsNullOrEmpty(path)) { return null; }
        var width = double.IsFinite(shownWidth) && shownWidth > 0 ? (int)Math.Clamp(Math.Ceiling(shownWidth * 2), 1, 4096) : 256;
        var key = (path, width);
        lock (Gate)
        {
            if (Entries.TryGetValue(key, out var idle) && idle.IdleNode is not null)
            {
                RemoveIdle(idle);
                if (idle.Stamp != FileStamp(path)) { Entries.Remove(key); Retire(idle); }
            }
            if (!Entries.TryGetValue(key, out var entry))
            {
                entry = new(key);
                entry.Owner = Owners.LastOrDefault(owner => path.StartsWith(owner.Prefix, StringComparison.OrdinalIgnoreCase));
                if (entry.Owner is { } owner) { owner.References++; }
                Entries.Add(key, entry);
                entry.References = 1;
                entry.Ready = DecodeAsync(entry);
            }
            else { entry.References++; }
            return new(entry);
        }
    }

    private static async Task<Bitmap?> DecodeAsync(Entry entry)
    {
        await Decoders.WaitAsync().ConfigureAwait(false);
        Bitmap? bitmap = null;
        try
        {
            bitmap = await Task.Run(() =>
            {
                lock (Gate) { if (entry.References == 0) { return null; } }
                try
                {
                    using var stream = File.OpenRead(entry.Key.Path);
                    entry.Stamp = FileStamp(entry.Key.Path);
                    Span<byte> header = stackalloc byte[24];
                    var size = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length ? ArtworkPngHeader.Inspect(header) : null;
                    stream.Position = 0;
                    if (entry.Owner is not null && (stream.Length > ArtworkLimits.ImageBytes || size is null)) { return null; }
                    // Small PNGs can use the bounded decoder directly, even in a bundled pack.
                    // A bundled image outside that profile still follows the general decoder below.
                    if (size is not null) { return Bitmap.DecodeToWidth(stream, Math.Min(entry.Key.Width, size.Width), BitmapInterpolationMode.HighQuality); }
                    // Bundled artwork follows the game's presentation contract, which permits other image formats.
                    var source = new Bitmap(stream);
                    if (source.PixelSize.Width <= entry.Key.Width) { return source; }
                    using (source)
                    {
                        var height = Math.Max(1, (int)Math.Ceiling((double)source.PixelSize.Height * entry.Key.Width / source.PixelSize.Width));
                        return source.CreateScaledBitmap(new PixelSize(entry.Key.Width, height), BitmapInterpolationMode.HighQuality);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
                { return null; }
            }).ConfigureAwait(false);
            lock (Gate)
            {
                entry.Loaded = true;
                entry.Bitmap = bitmap;
                if (entry.References == 0) { Retire(entry); return null; }
                return bitmap;
            }
        }
        finally { Decoders.Release(); }
    }

    private static void Retire(Entry entry)
    {
        entry.Bitmap?.Dispose();
        entry.Bitmap = null;
        if (entry.Owner is { } owner) { entry.Owner = null; Release(owner); }
    }

    private static (long Length, DateTime Write)? FileStamp(string path)
    {
        try { var file = new FileInfo(path); return file.Exists ? (file.Length, file.LastWriteTimeUtc) : null; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void Finish(Entry entry)
    {
        if (entry.Owner is null && entry.Bitmap is not null && entry.Bytes <= IdleByteLimit && entry.Stamp is not null)
        {
            entry.IdleNode = Idle.AddLast(entry);
            idleBytes += entry.Bytes;
            while (idleBytes > IdleByteLimit || Idle.Count > 128)
            {
                var oldest = Idle.First!.Value;
                RemoveIdle(oldest);
                Entries.Remove(oldest.Key);
                Retire(oldest);
            }
        }
        else { Entries.Remove(entry.Key); Retire(entry); }
    }

    private static void RemoveIdle(Entry entry)
    {
        if (entry.IdleNode is not { } node) { return; }
        idleBytes -= entry.Bytes;
        Idle.Remove(node);
        entry.IdleNode = null;
    }

    private static void Release(Owner owner)
    {
        if (--owner.References != 0) { return; }
        Owners.Remove(owner);
        owner.Lifetime.Dispose();
        owner.Released.TrySetResult();
    }

    public static Task WaitForReleaseAsync(string directory, CancellationToken cancellationToken)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        Task[] pending;
        lock (Gate) { pending = [.. Owners.Where(owner => owner.Prefix.Equals(prefix, StringComparison.OrdinalIgnoreCase)).Select(owner => owner.Released.Task)]; }
        return Task.WhenAll(pending).WaitAsync(cancellationToken);
    }

    internal static int LiveEntries { get { lock (Gate) { return Entries.Count; } } }
    internal static int IdleCount { get { lock (Gate) { return Idle.Count; } } }
    internal static long IdleBytes { get { lock (Gate) { return idleBytes; } } }
    internal static int OwnersUnder(string directory)
    { lock (Gate) { return Owners.Count(owner => owner.Prefix.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)); } }
}
