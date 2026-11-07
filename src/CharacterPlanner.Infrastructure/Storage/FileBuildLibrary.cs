using System.Collections.Immutable;
using System.Security.Cryptography;
using CharacterPlanner.Application.Library;
using CharacterPlanner.Core.Builds;

namespace CharacterPlanner.Infrastructure.Storage;

/// <summary>
/// The library as a folder of build documents (ADR 0004), one file per build named <c>&lt;id&gt;.build.json</c>.
/// Ids are random, so renaming a build never moves its file. A record's version is the SHA-256 of its bytes.
/// Every write goes to a temporary file in the same folder that then replaces the record in one move, so a crash
/// leaves the old record or the new one, never a partial file. A create never replaces an existing file.
/// </summary>
/// <remarks>
/// Writers hold an exclusive lock on <see cref="LockFile"/> from their version check to their move, so two
/// libraries on one folder, in one process or two (the desktop and the CLI), can't overwrite each other's edit.
/// Readers take no lock and open records sharing read, write and delete, so they never block a save.
/// </remarks>
public sealed class FileBuildLibrary(string directory, TimeSpan? lockTimeout = null) : IBuildLibrary
{
    public const string Extension = ".build.json";
    public const string LockFile = ".library.lock";
    /// <summary>How long a writer waits for another writer's lock, by default, before reporting the library busy.</summary>
    public static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(5);

    private readonly string root = Path.GetFullPath(directory);
    private readonly TimeSpan lockWait = lockTimeout ?? DefaultLockTimeout;

    public string Directory => root;

    /// <summary>A record as read: its bytes (null when too large to be a document), version and save time.</summary>
    private sealed record Stored(byte[]? Bytes, string Version, DateTimeOffset SavedAt);

    public async Task<ImmutableArray<LibraryEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!System.IO.Directory.Exists(root)) { return []; }
        // Every record is listed, hidden or not; temporary files and the lock file don't match the pattern.
        var options = new EnumerationOptions { AttributesToSkip = 0 };
        var entries = ImmutableArray.CreateBuilder<LibraryEntry>();
        foreach (var path in System.IO.Directory.EnumerateFiles(root, "*" + Extension, options))
        {
            var id = Path.GetFileName(path)[..^Extension.Length];
            if (!IsId(id)) { continue; }
            Stored? stored;
            // A record that can't be read (locked, or denied) is listed with why, rather than hiding the rest.
            try { stored = await ReadAsync(path, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                entries.Add(new(id, "", "", "", default, "", $"can't be read: {exception.Message}"));
                continue;
            }
            if (stored is null) { continue; }
            try
            {
                var build = Decode(stored);
                entries.Add(new(id, build.Meta.Name, build.PackId, build.PackVersion, stored.SavedAt, stored.Version));
            }
            catch (BuildDocumentException exception) { entries.Add(new(id, "", "", "", stored.SavedAt, stored.Version, exception.Message)); }
        }
        return [.. entries.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.Id, StringComparer.Ordinal)];
    }

    public async Task<LibraryBuild> LoadAsync(string id, CancellationToken cancellationToken = default)
    {
        var stored = await ReadAsync(PathOf(id), cancellationToken).ConfigureAwait(false)
            ?? throw new LibraryException(LibraryErrorCodes.NotFound, $"No saved build has id {id}");
        try { return new(id, Decode(stored), stored.Version, stored.SavedAt); }
        catch (BuildDocumentException exception) { throw new LibraryException(LibraryErrorCodes.Damaged, $"Saved build {id} can't be opened: {exception.Message}", exception); }
    }

    public async Task<LibraryBuild> CreateAsync(BuildSnapshot build, CancellationToken cancellationToken = default)
    {
        var bytes = Encode(build);
        System.IO.Directory.CreateDirectory(root);
        var id = Guid.NewGuid().ToString("N");
        await using var held = await LockAsync(cancellationToken).ConfigureAwait(false);
        var savedAt = await WriteAsync(PathOf(id), bytes, replace: false, cancellationToken).ConfigureAwait(false);
        return new(id, build, Version(bytes), savedAt);
    }

    public async Task<LibraryBuild> ReplaceAsync(string id, BuildSnapshot build, string expectedVersion, CancellationToken cancellationToken = default)
    {
        var path = PathOf(id);
        var bytes = Encode(build);
        await using var held = await LockAsync(cancellationToken).ConfigureAwait(false);
        await CheckVersionAsync(id, path, expectedVersion, cancellationToken).ConfigureAwait(false);
        var savedAt = await WriteAsync(path, bytes, replace: true, cancellationToken).ConfigureAwait(false);
        return new(id, build, Version(bytes), savedAt);
    }

    public async Task DeleteAsync(string id, string expectedVersion, CancellationToken cancellationToken = default)
    {
        var path = PathOf(id);
        await using var held = await LockAsync(cancellationToken).ConfigureAwait(false);
        await CheckVersionAsync(id, path, expectedVersion, cancellationToken).ConfigureAwait(false);
        File.Delete(path);
    }

    /// <summary>Ids are 32 lowercase hex digits, so one can never name a path outside the library.</summary>
    private static bool IsId(string id) => id.Length == 32 && !id.AsSpan().ContainsAnyExcept("0123456789abcdef");

    private string PathOf(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return IsId(id) ? Path.Combine(root, id + Extension) : throw new LibraryException(LibraryErrorCodes.InvalidId, $"{id} isn't a library id");
    }

    private static string Version(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static BuildSnapshot Decode(Stored stored) =>
        stored.Bytes is { } bytes ? BuildDocument.Decode(bytes) : throw new BuildDocumentException($"build document is larger than {BuildDocument.MaxBytes} bytes");

    private static byte[] Encode(BuildSnapshot build)
    {
        ArgumentNullException.ThrowIfNull(build);
        try { return BuildDocument.Encode(build); }
        catch (ArgumentException exception) { throw new LibraryException(LibraryErrorCodes.Unsaveable, $"The build can't be saved: {exception.Message}", exception); }
    }

    /// <summary>
    /// A record as stored, or null when it doesn't exist (including one deleted while being opened). It's opened
    /// sharing read, write and delete, so reading never makes a concurrent save or delete fail. A record too large to
    /// be a document isn't read into memory, but its version is still the hash of its own bytes.
    /// </summary>
    private static async Task<Stored?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous); }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return null; }
        await using (stream.ConfigureAwait(false))
        {
            var savedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(stream.SafeFileHandle), TimeSpan.Zero);
            if (stream.Length > BuildDocument.MaxBytes)
            { return new(null, Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)), savedAt); }
            var bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            return new(bytes, Version(bytes), savedAt);
        }
    }

    private static async Task CheckVersionAsync(string id, string path, string expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedVersion);
        var stored = await ReadAsync(path, cancellationToken).ConfigureAwait(false)
            ?? throw new LibraryException(LibraryErrorCodes.Conflict, $"Saved build {id} was deleted since it was opened");
        if (stored.Version != expectedVersion) { throw new LibraryException(LibraryErrorCodes.Conflict, $"Saved build {id} changed since it was opened"); }
    }

    /// <summary>Takes the folder's writer lock, retrying while another writer holds it.</summary>
    private Task<FileStream> LockAsync(CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(root);
        return AtomicFile.LockAsync(Path.Combine(root, LockFile), lockWait,
            exception => new LibraryException(LibraryErrorCodes.Busy, $"Another program is saving to the library at {root}", exception), cancellationToken);
    }

    /// <summary>Writes the record durably and moves it into place; returns its save time.</summary>
    private Task<DateTimeOffset> WriteAsync(string path, byte[] bytes, bool replace, CancellationToken cancellationToken) =>
        AtomicFile.WriteAsync(path, bytes, replace, lockWait,
            exception => new LibraryException(LibraryErrorCodes.Busy, $"Saved build {Path.GetFileName(path)} is in use by another program or can't be written", exception),
            cancellationToken);
}
