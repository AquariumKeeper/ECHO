using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CharacterPlanner.Application.Artwork;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Infrastructure.Artwork;

public sealed partial class ArtworkPackInstaller
{
    private const int SettingsLimit = 64 * 1024;
    private string SettingsPath => Path.Combine(root, "artwork-settings.json");
    private static readonly JsonSerializerOptions SettingsJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 16
    };

    private sealed record Settings
    {
        public required int SchemaVersion { get; init; }
        public required long Revision { get; init; }
        public required ImmutableDictionary<string, ArtworkPackReference> Selections { get; init; }
        public static Settings Empty => new() { SchemaVersion = 1, Revision = 0, Selections = ImmutableDictionary<string, ArtworkPackReference>.Empty };
    }

    private sealed class Lease(InstalledArtworkPack pack, FileStream pin) : IArtworkPackLease
    {
        private readonly object gate = new();
        private bool disposed;
        private int readers;
        public InstalledArtworkPack Pack { get; } = pack;
        public void Dispose() { lock (gate) { disposed = true; if (readers == 0) { pin.Dispose(); } } }
        public async Task<string> ReadNoticeAsync(string licenseFile, CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!Pack.Manifest.Credits.Any(credit => credit.LicenseFile == licenseFile)) { throw new ArgumentException("Only declared license notices can be read", nameof(licenseFile)); }
                readers++;
            }
            try
            {
                var path = Pack.Directory;
                NotLinked(path);
                foreach (var part in licenseFile.Split('/')) { path = Path.Combine(path, part); NotLinked(path); }
                var bytes = await ReadFileAsync(path, ArtworkLimits.LicenseBytes, cancellationToken).ConfigureAwait(false);
                return new System.Text.UTF8Encoding(false, true).GetString(bytes.Span);
            }
            catch (Rejected exception) { throw new InvalidDataException(exception.Message, exception); }
            finally { lock (gate) { readers--; if (disposed && readers == 0) { pin.Dispose(); } } }
        }
    }

    public async Task<ArtworkRevisionState> OpenRevisionAsync(GamePack pack, ArtworkPackReference reference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        FileStream? pin = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!DirectoryPresent(root)) { throw new Rejected("artwork-installed-unavailable", root, "Artwork store is missing"); }
            using (await LockAsync(cancellationToken).ConfigureAwait(false)) { pin = Pin(reference); }
            var validation = await ValidateRevisionAsync(reference, pack, cancellationToken).ConfigureAwait(false);
            if (validation.Pack is null) { return new(null, validation.Diagnostics); }
            var lease = new Lease(validation.Pack, pin);
            pin = null;
            return new(lease, validation.Diagnostics);
        }
        catch (Exception exception) when (Expected(exception)) { return new(null, [Diagnostic(exception, root)]); }
        finally { pin?.Dispose(); }
    }

    private sealed record RevisionResult(InstalledArtworkPack? Pack, ImmutableArray<ArtworkDiagnostic> Diagnostics);
    private static bool Expected(Exception exception) => exception is Rejected or IOException or UnauthorizedAccessException;
    private static ArtworkDiagnostic Diagnostic(Exception exception, string path) => exception is Rejected rejected ? rejected.Diagnostic :
        new("error", "artwork-io", path, exception.Message);

    public async Task<ArtworkSelectionState> LoadSelectionAsync(GamePack pack, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        long revision = -1;
        ArtworkPackReference? selected = null;
        FileStream? pin = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!DirectoryPresent(root)) { return new(0, null, null, []); }
            for (var attempt = 0; attempt < 3; attempt++)
            {
                using (await LockAsync(cancellationToken).ConfigureAwait(false))
                {
                    var settings = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
                    revision = settings.Revision;
                    selected = settings.Selections.GetValueOrDefault(pack.ID);
                    if (selected is null) { return new(revision, null, null, []); }
                    pin = Pin(selected);
                }
                var validation = await ValidateRevisionAsync(selected, pack, cancellationToken).ConfigureAwait(false);
                using (await LockAsync(cancellationToken).ConfigureAwait(false))
                {
                    var latest = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
                    if (latest.Revision != revision || latest.Selections.GetValueOrDefault(pack.ID) != selected)
                    {
                        pin.Dispose();
                        pin = null;
                        revision = latest.Revision;
                        selected = latest.Selections.GetValueOrDefault(pack.ID);
                        continue;
                    }
                }
                if (validation.Pack is null) { return new(revision, selected, null, validation.Diagnostics); }
                var lease = new Lease(validation.Pack, pin);
                pin = null;
                return new(revision, selected, lease, validation.Diagnostics);
            }
            return new(revision, selected, null, [new("warning", "artwork-selection-changing", SettingsPath, "Artwork selection kept changing during validation; refresh to load it")]);
        }
        catch (Exception exception) when (Expected(exception)) { return new(revision, selected, null, [Diagnostic(exception, SettingsPath)]); }
        finally { pin?.Dispose(); }
    }

    public async Task<ArtworkCatalog> ListAsync(GamePack pack, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var entries = ImmutableArray.CreateBuilder<ArtworkCatalogEntry>();
        var diagnostics = ImmutableArray.CreateBuilder<ArtworkDiagnostic>();
        long revision = -1;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!DirectoryPresent(root)) { return new(0, [], []); }
            var references = new List<ArtworkPackReference>();
            using (await LockAsync(cancellationToken).ConfigureAwait(false))
            {
                try { revision = (await ReadSettingsAsync(cancellationToken).ConfigureAwait(false)).Revision; }
                catch (Exception exception) when (Expected(exception)) { diagnostics.Add(Diagnostic(exception, SettingsPath)); }
                foreach (var idDirectory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var id = Path.GetFileName(idDirectory);
                    if (!ArtworkPaths.IsIdentifier(id)) { continue; }
                    try
                    {
                        NotLinked(idDirectory);
                        foreach (var directory in Directory.EnumerateDirectories(idDirectory).Order(StringComparer.Ordinal))
                        {
                            var reference = new ArtworkPackReference(id, Path.GetFileName(directory));
                            if (ValidReference(reference)) { references.Add(reference); }
                        }
                    }
                    catch (Exception exception) when (Expected(exception)) { diagnostics.Add(Diagnostic(exception, idDirectory)); }
                }
            }
            foreach (var reference in references)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    FileStream pin;
                    using (await LockAsync(cancellationToken).ConfigureAwait(false)) { pin = Pin(reference); }
                    using (pin)
                    {
                        var validation = await ValidateRevisionAsync(reference, pack, cancellationToken).ConfigureAwait(false);
                        entries.Add(new(reference, validation.Pack, validation.Diagnostics));
                    }
                }
                catch (Exception exception) when (Expected(exception)) { entries.Add(new(reference, null, [Diagnostic(exception, RevisionPath(reference))])); }
            }
        }
        catch (Exception exception) when (Expected(exception)) { diagnostics.Add(Diagnostic(exception, root)); }
        return new(revision, entries.ToImmutable(), diagnostics.ToImmutable());
    }

    public async Task<ArtworkStoreChange> SelectAsync(GamePack pack, ArtworkPackReference? reference, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        FileStream? pin = null;
        var revision = expectedRevision;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureRoot();
            using (await LockAsync(cancellationToken).ConfigureAwait(false))
            {
                var settings = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
                revision = settings.Revision;
                CheckRevision(settings, expectedRevision);
                if (reference is not null) { pin = Pin(reference); }
            }
            var diagnostics = ImmutableArray<ArtworkDiagnostic>.Empty;
            if (reference is not null)
            {
                var validation = await ValidateRevisionAsync(reference, pack, cancellationToken).ConfigureAwait(false);
                if (validation.Pack is null) { return new(false, false, revision, validation.Diagnostics); }
                diagnostics = validation.Diagnostics;
            }
            using (await LockAsync(cancellationToken).ConfigureAwait(false))
            {
                var settings = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
                revision = settings.Revision;
                CheckRevision(settings, expectedRevision);
                if (settings.Selections.GetValueOrDefault(pack.ID) == reference) { return new(true, false, revision, diagnostics); }
                if (revision == long.MaxValue) { throw new Rejected("artwork-settings-invalid", SettingsPath, "Artwork selection revision is exhausted"); }
                var selections = reference is null ? settings.Selections.Remove(pack.ID) : settings.Selections.SetItem(pack.ID, reference);
                var next = settings with { Revision = revision + 1, Selections = selections };
                await SaveSettingsAsync(next, cancellationToken).ConfigureAwait(false);
                return new(true, true, next.Revision, diagnostics);
            }
        }
        catch (Exception exception) when (Expected(exception)) { return new(false, false, revision, [Diagnostic(exception, SettingsPath)]); }
        finally { pin?.Dispose(); }
    }

    public async Task<ArtworkStoreChange> RemoveAsync(ArtworkPackReference reference, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var revision = expectedRevision;
        string? removed = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureRoot();
            using (await LockAsync(cancellationToken).ConfigureAwait(false))
            {
                var settings = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
                revision = settings.Revision;
                CheckRevision(settings, expectedRevision);
                var path = RevisionPath(reference);
                if (settings.Selections.Values.Contains(reference)) { throw new Rejected("artwork-selected", path, "Switch to bundled artwork before removing this selected revision"); }
                if (!DirectoryPresent(Path.GetDirectoryName(path)!) || !DirectoryPresent(path)) { return new(true, false, revision, []); }
                var leases = LeaseDirectory(reference, create: false);
                if (leases is not null)
                {
                    foreach (var file in Directory.EnumerateFiles(leases, "*.lease"))
                    {
                        NotLinked(file);
                        try { using var probe = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
                        catch (FileNotFoundException) { /* Another process released its lease. */ }
                        catch (IOException) { throw new Rejected("artwork-in-use", path, "A window or preview is still using this revision; close its preview or refresh that window before removing it"); }
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                removed = Path.Combine(root, ".removed-" + Guid.NewGuid().ToString("N"));
                Directory.Move(path, removed);
            }
            try
            {
                var full = Path.GetFullPath(removed);
                if (!full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFileName(full).StartsWith(".removed-", StringComparison.Ordinal)) { throw new InvalidOperationException("Invalid artwork cleanup target"); }
                NotLinked(full);
                Directory.Delete(full, recursive: true);
                return new(true, true, revision, []);
            }
            catch (Exception exception) when (Expected(exception))
            { return new(true, true, revision, [new("warning", "artwork-cleanup-pending", removed, "Revision removed from the catalog; private cleanup is pending: " + exception.Message)]); }
        }
        catch (Exception exception) when (Expected(exception)) { return new(false, false, revision, [Diagnostic(exception, removed ?? root)]); }
    }

    private static bool DirectoryPresent(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
            { throw new Rejected("artwork-path-invalid", path, "Expected an artwork directory without links"); }
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private void EnsureRoot() { Directory.CreateDirectory(root); NotLinked(root); }

    public async Task<ArtworkStoreChange> ResetInvalidSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureRoot();
            using var gate = await LockAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var current = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
                if (current.Revision != long.MaxValue)
                { return new(false, false, current.Revision, [new("error", "artwork-settings-valid", SettingsPath, "Settings are valid; refresh instead of resetting them")]); }
            }
            catch (Rejected exception) when (exception.Diagnostic.Code == "artwork-settings-invalid") { /* Explicit recovery only applies to malformed settings. */ }
            var attributes = File.GetAttributes(SettingsPath);
            var directoryLink = (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == (FileAttributes.Directory | FileAttributes.ReparsePoint);
            var backup = Path.Combine(root, ".settings-backup-" + Guid.NewGuid().ToString("N") + ".json");
            // A new random revision epoch prevents an old window's pre-corruption revision from normally matching the reset state.
            long revision;
            do { revision = BinaryPrimitives.ReadInt64LittleEndian(RandomNumberGenerator.GetBytes(8)) & long.MaxValue; }
            while (revision is 0 or long.MaxValue);
            cancellationToken.ThrowIfCancellationRequested();
            // Rename the entry itself, including a link, without reading or modifying its target.
            MoveSettingsEntry(SettingsPath, backup, directoryLink);
            try { await SaveSettingsAsync(Settings.Empty with { Revision = revision }, cancellationToken).ConfigureAwait(false); }
            catch { MoveSettingsEntry(backup, SettingsPath, directoryLink); throw; }
            return new(true, true, revision, [new("warning", "artwork-settings-reset", backup, "Invalid settings were preserved here; bundled artwork is now selected")]);
        }
        catch (Exception exception) when (Expected(exception)) { return new(false, false, -1, [Diagnostic(exception, SettingsPath)]); }
    }
    private static void MoveSettingsEntry(string from, string to, bool directoryLink)
    {
        if (directoryLink) { Directory.Move(from, to); }
        else { File.Move(from, to); }
    }
    private static bool ValidReference(ArtworkPackReference reference) => reference is not null && reference.Id is not null &&
        ArtworkPaths.IsIdentifier(reference.Id) && reference.ContentDigest is { Length: 64 } &&
        reference.ContentDigest.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private string RevisionPath(ArtworkPackReference reference)
    {
        if (!ValidReference(reference)) { throw new Rejected("artwork-reference-invalid", "", "Expected an artwork ID and lowercase SHA-256 revision"); }
        return Path.Combine(root, reference.Id, reference.ContentDigest);
    }

    private string? LeaseDirectory(ArtworkPackReference reference, bool create)
    {
        var path = root;
        foreach (var part in new[] { ".leases", reference.Id, reference.ContentDigest })
        {
            path = Path.Combine(path, part);
            if (create) { Directory.CreateDirectory(path); }
            else if (!DirectoryPresent(path)) { return null; }
            NotLinked(path);
        }
        return path;
    }

    private FileStream Pin(ArtworkPackReference reference)
    {
        var path = RevisionPath(reference);
        try
        {
            if (!DirectoryPresent(Path.GetDirectoryName(path)!) || !DirectoryPresent(path))
            { throw new Rejected("artwork-installed-unavailable", path, "Installed artwork revision is missing"); }
            var leases = LeaseDirectory(reference, create: true)!;
            return new FileStream(Path.Combine(leases, Guid.NewGuid().ToString("N") + ".lease"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new Rejected("artwork-installed-unavailable", path, "Installed artwork is unavailable: " + exception.Message); }
    }

    private void CheckRevision(Settings settings, long expected)
    {
        if (expected != settings.Revision) { throw new Rejected("artwork-selection-conflict", SettingsPath, "Artwork selection changed; refresh before trying again"); }
    }

    private async Task<Settings> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            NotLinked(SettingsPath);
            var bytes = await ReadFileAsync(SettingsPath, SettingsLimit, cancellationToken).ConfigureAwait(false);
            var settings = JsonSerializer.Deserialize<Settings>(bytes.Span, SettingsJson);
            if (settings is null || settings.SchemaVersion != 1 || settings.Revision < 0 ||
                settings.Selections.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 256 || pair.Key.Any(char.IsControl) || !ValidReference(pair.Value)))
            { throw new JsonException("Invalid artwork selection document"); }
            return settings;
        }
        catch (FileNotFoundException) { return Settings.Empty; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or Rejected)
        { throw new Rejected("artwork-settings-invalid", SettingsPath, "Artwork settings could not be read; preserve or restore this file before changing selections: " + exception.Message); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new Rejected("artwork-settings-unavailable", SettingsPath, exception.Message); }
    }

    private async Task SaveSettingsAsync(Settings settings, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = settings.SchemaVersion,
            revision = settings.Revision,
            selections = settings.Selections.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        }, SettingsJson);
        if (bytes.Length > SettingsLimit) { throw new Rejected("artwork-settings-invalid", SettingsPath, "Artwork settings exceed their byte limit"); }
        var temporary = Path.Combine(root, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* An unpublished temporary file is not a settings document. */ }
        }
    }

    private async Task<RevisionResult> ValidateRevisionAsync(ArtworkPackReference reference, GamePack pack, CancellationToken cancellationToken)
    {
        var directory = RevisionPath(reference);
        try
        {
            NotLinked(Path.GetDirectoryName(directory)!);
            NotLinked(directory);
            NotLinked(Path.Combine(directory, "artwork.json"));
            var manifestBytes = await ReadFileAsync(Path.Combine(directory, "artwork.json"), ArtworkLimits.ManifestBytes, cancellationToken).ConfigureAwait(false);
            var read = ArtworkManifestReader.Read(manifestBytes, pack);
            if (!read.Succeeded) { return new(null, read.Diagnostics); }
            var manifest = read.Manifest!;
            if (manifest.Id != reference.Id) { throw new Rejected("artwork-installed-invalid", directory, "Installed manifest identity differs from its directory"); }
            var files = DeclaredFiles(manifest);
            if (!Inventory(directory, cancellationToken).SetEquals(files.Keys)) { throw new Rejected("artwork-installed-invalid", directory, "Installed file inventory differs from its manifest"); }
            var assets = manifest.Assets.ToDictionary(file => file.Path, StringComparer.Ordinal);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long pixels = 0;
            long expanded = 0;
            foreach (var path in files.Keys.Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = path == "artwork.json" ? manifestBytes : await ReadFileAsync(Path.Combine(directory, path), files[path], cancellationToken).ConfigureAwait(false);
                expanded += bytes.Length;
                if (expanded > ArtworkLimits.ExpandedBytes) { throw new Rejected("artwork-expanded-limit", directory, "Installed revision exceeds the expanded-byte limit"); }
                assets.TryGetValue(path, out var asset);
                var validated = await ValidateMemberAsync(path, bytes, asset, pixels, cancellationToken).ConfigureAwait(false);
                pixels = validated.Pixels;
                AppendDigest(digest, path, validated.Hash);
            }
            if (Convert.ToHexStringLower(digest.GetHashAndReset()) != reference.ContentDigest)
            { throw new Rejected("artwork-installed-invalid", directory, "Installed contents no longer match their revision digest"); }
            return new(new(manifest, read.ApplicableBindings!, directory, reference.ContentDigest), read.Diagnostics);
        }
        catch (Exception exception) when (Expected(exception)) { return new(null, [Diagnostic(exception, directory)]); }
    }

    private static HashSet<string> Inventory(string directory, CancellationToken cancellationToken)
    {
        var actual = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                NotLinked(entry);
                var relative = Path.GetRelativePath(directory, entry).Replace('\\', '/');
                if (!names.Add(relative)) { throw new Rejected("artwork-path-invalid", entry, "Installed paths collide by case"); }
                if (Directory.Exists(entry))
                {
                    if (!ArtworkPaths.IsFile(relative + "/x", "assets") && !ArtworkPaths.IsFile(relative + "/x", "licenses"))
                    { throw new Rejected("artwork-path-invalid", entry, "Unexpected installed directory"); }
                    pending.Push(entry);
                }
                else
                {
                    actual.Add(relative);
                    if (actual.Count > ArtworkLimits.Files) { throw new Rejected("artwork-file-limit", directory, "Too many installed files"); }
                }
            }
        }
        return actual;
    }
}
