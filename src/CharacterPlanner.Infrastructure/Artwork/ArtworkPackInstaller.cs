using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CharacterPlanner.Application.Artwork;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Infrastructure.Artwork;

/// <summary>Validates a private staged copy and publishes immutable revisions; never extracts beside the source ZIP or into the game pack.</summary>
public sealed partial class ArtworkPackInstaller(string root, IArtworkImageDecoder decoder) : IArtworkPackInstaller, IArtworkPackStore
{
    private readonly string root = Path.GetFullPath(root);
    private readonly IArtworkImageDecoder decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
    private readonly SemaphoreSlim imageValidation = new(1);
    private readonly HashSet<string> validatedImages = new(StringComparer.Ordinal);

    private sealed class Rejected(string code, string path, string message) : Exception(message)
    {
        public ArtworkDiagnostic Diagnostic { get; } = new("error", code, path, message);
    }

    public async Task<ArtworkInstallResult> InstallAsync(string archivePath, GamePack pack, IProgress<ArtworkInstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        string? staging = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(archivePath)) { throw new Rejected("artwork-input-invalid", "", "Choose an artwork ZIP"); }
            using var input = File.OpenRead(archivePath);
            if (input.Length > ArtworkLimits.ArchiveBytes) { throw new Rejected("artwork-archive-too-large", archivePath, "Artwork ZIP exceeds 128 MiB"); }
            using var archive = new ZipArchive(input, ZipArchiveMode.Read);
            var entries = Index(archive, cancellationToken);
            if (!entries.TryGetValue("artwork.json", out var manifestEntry)) { throw new Rejected("artwork-manifest-missing", "artwork.json", "Artwork ZIP needs a root artwork.json"); }
            var budget = new Budget();
            var manifestBytes = await ReadAsync(manifestEntry, ArtworkLimits.ManifestBytes, budget, cancellationToken).ConfigureAwait(false);
            var read = ArtworkManifestReader.Read(manifestBytes, pack);
            if (!read.Succeeded) { return new(null, false, read.Diagnostics); }
            var manifest = read.Manifest!;
            var files = DeclaredFiles(manifest);
            foreach (var path in entries.Keys.Order(StringComparer.Ordinal))
            { if (!files.ContainsKey(path)) { throw new Rejected("artwork-file-unlisted", path, "ZIP member is not declared by the manifest"); } }
            foreach (var path in files.Keys.Order(StringComparer.Ordinal))
            { if (!entries.ContainsKey(path)) { throw new Rejected("artwork-file-missing", path, "Declared file is missing from the ZIP"); } }

            Directory.CreateDirectory(root);
            NotLinked(root);
            staging = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long pixels = 0;
            var assets = manifest.Assets.ToDictionary(file => file.Path, StringComparer.Ordinal);
            var completed = 0;
            foreach (var path in files.Keys.Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = path == "artwork.json" ? manifestBytes : await ReadAsync(entries[path], files[path], budget, cancellationToken).ConfigureAwait(false);
                assets.TryGetValue(path, out var asset);
                var validated = await ValidateMemberAsync(path, bytes, asset, pixels, cancellationToken).ConfigureAwait(false);
                pixels = validated.Pixels;
                AppendDigest(digest, path, validated.Hash);
                var target = Path.Combine(staging, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, bytes, cancellationToken).ConfigureAwait(false);
                progress?.Report(new(++completed, files.Count));
            }
            var contentDigest = Convert.ToHexStringLower(digest.GetHashAndReset());
            using var gate = await LockAsync(cancellationToken).ConfigureAwait(false);
            var parent = Path.Combine(root, manifest.Id);
            Directory.CreateDirectory(parent);
            NotLinked(parent);
            var destination = Path.Combine(parent, contentDigest);
            var diagnostics = read.Diagnostics.ToBuilder();
            foreach (var revision in Directory.EnumerateDirectories(parent).Order(StringComparer.Ordinal))
            {
                NotLinked(revision);
                var existing = Path.Combine(revision, "artwork.json");
                string version;
                try { version = await ReadVersionAsync(existing, manifest.Id, cancellationToken).ConfigureAwait(false); }
                catch (Rejected exception) when (exception.Diagnostic.Code == "artwork-installed-invalid" && revision != destination)
                {
                    diagnostics.Add(new("warning", "artwork-existing-invalid", existing, "An unreadable old revision was excluded from version-conflict checks; it remains in the catalog for removal"));
                    continue;
                }
                if (version == manifest.Version && revision != destination)
                { throw new Rejected("artwork-version-conflict", existing, "This ID/version is already installed with different contents; choose a new version"); }
            }
            if (Directory.Exists(destination))
            {
                await CheckExistingAsync(destination, staging, files.Keys, cancellationToken).ConfigureAwait(false);
                return new(new(manifest, read.ApplicableBindings!, destination, contentDigest), true, diagnostics.ToImmutable());
            }
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            staging = null;
            return new(new(manifest, read.ApplicableBindings!, destination, contentDigest), false, diagnostics.ToImmutable());
        }
        catch (Rejected exception) { return new(null, false, [exception.Diagnostic]); }
        catch (InvalidDataException exception)
        { return new(null, false, [new("error", "artwork-package-invalid", archivePath, exception.Message)]); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return new(null, false, [new("error", "artwork-io", archivePath, exception.Message)]); }
        finally
        {
            // Only this attempt's app-created staging directory is removed; publication clears the variable.
            if (staging is not null)
            {
                try
                {
                    var full = Path.GetFullPath(staging);
                    if (full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                        Path.GetFileName(full).StartsWith(".staging-", StringComparison.Ordinal) &&
                        (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0)
                    { Directory.Delete(full, recursive: true); }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* Preserve the original outcome; stale staging is never treated as an installed revision. */ }
            }
        }
    }

    private static Dictionary<string, int> DeclaredFiles(ArtworkManifest manifest)
    {
        var files = manifest.Assets.ToDictionary(file => file.Path, _ => ArtworkLimits.ImageBytes, StringComparer.Ordinal);
        foreach (var credit in manifest.Credits) { files[credit.LicenseFile] = ArtworkLimits.LicenseBytes; }
        files.Add("artwork.json", ArtworkLimits.ManifestBytes);
        return files;
    }

    private async Task<(string Hash, long Pixels)> ValidateMemberAsync(string path, ReadOnlyMemory<byte> bytes, ArtworkFile? asset, long pixels, CancellationToken cancellationToken)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes.Span));
        if (asset is not null)
        {
            if (hash != asset.Sha256) { throw new Rejected("artwork-hash-mismatch", path, "Image does not match its declared SHA-256"); }
            // Digest-addressed proofs are process-local and bounded. Hash changed bytes on every
            // read, but never repeat PNG CRC scanning/decompression for an already verified digest.
            var size = ArtworkPngHeader.Inspect(bytes.Span) ?? throw new Rejected("artwork-image-invalid", path, "Expected a PNG within the dimension limits");
            pixels += (long)size.Width * size.Height;
            if (pixels > ArtworkLimits.TotalImagePixels) { throw new Rejected("artwork-pixel-limit", path, "Combined source images exceed 64 million pixels"); }
            await imageValidation.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!validatedImages.Contains(hash))
                {
                    size = ArtworkPngPreflight.Inspect(bytes.Span) ?? throw new Rejected("artwork-image-invalid", path, "Expected a complete static PNG within the dimension limits");
                    if (await decoder.DecodeAsync(bytes, cancellationToken).ConfigureAwait(false) != size)
                    { throw new Rejected("artwork-image-invalid", path, "PNG could not be fully decoded at its declared dimensions"); }
                    if (validatedImages.Count >= ArtworkLimits.Files) { validatedImages.Clear(); }
                    validatedImages.Add(hash);
                }
            }
            finally { imageValidation.Release(); }
        }
        else if (path != "artwork.json")
        {
            try { _ = new UTF8Encoding(false, true).GetString(bytes.Span); }
            catch (DecoderFallbackException) { throw new Rejected("artwork-license-invalid", path, "License must be UTF-8 text"); }
            if (bytes.Span.IndexOf((byte)0) >= 0 || bytes.IsEmpty) { throw new Rejected("artwork-license-invalid", path, "License must contain nonempty text without NUL bytes"); }
        }
        return (hash, pixels);
    }

    private static void AppendDigest(IncrementalHash digest, string path, string hash) =>
        digest.AppendData(Encoding.UTF8.GetBytes(path + "\0" + hash + "\n"));

    private static Dictionary<string, ZipArchiveEntry> Index(ZipArchive archive, CancellationToken cancellationToken)
    {
        if (archive.Entries.Count > ArtworkLimits.Files) { throw new Rejected("artwork-file-limit", "", "Too many ZIP entries"); }
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = entry.FullName.EndsWith('/');
            var path = directory ? entry.FullName[..^1] : entry.FullName;
            var type = (entry.ExternalAttributes >> 16) & 0xf000;
            if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || type != 0 && type != (directory ? 0x4000 : 0x8000))
            { throw new Rejected("artwork-path-invalid", entry.FullName, "Links and special ZIP members are not supported"); }
            var portable = directory ? ArtworkPaths.IsFile(entry.FullName + "placeholder", "assets") || ArtworkPaths.IsFile(entry.FullName + "placeholder", "licenses") :
                path == "artwork.json" || ArtworkPaths.IsFile(path, "assets") || ArtworkPaths.IsFile(path, "licenses");
            if (!portable || !paths.Add(path)) { throw new Rejected("artwork-path-invalid", entry.FullName, "Unsafe, duplicate or case-colliding ZIP member"); }
            expanded += entry.Length;
            if (expanded > ArtworkLimits.ExpandedBytes) { throw new Rejected("artwork-expanded-limit", path, "Expanded ZIP exceeds 256 MiB"); }
            if (directory)
            {
                if (entry.Length != 0) { throw new Rejected("artwork-path-invalid", path, "Directory entries must have no content"); }
            }
            else { files.Add(path, entry); }
        }
        if (!ArtworkPaths.HasConsistentCasing(paths))
        { throw new Rejected("artwork-path-invalid", "", "ZIP directory and file paths must use consistent casing"); }
        return files;
    }

    private sealed class Budget { public long Bytes { get; set; } }

    private static async Task<ReadOnlyMemory<byte>> ReadAsync(ZipArchiveEntry entry, int limit, Budget budget, CancellationToken cancellationToken)
    {
        if (entry.Length > limit) { throw new Rejected("artwork-file-too-large", entry.FullName, "ZIP member exceeds its byte limit"); }
        using var input = entry.Open();
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            budget.Bytes += count;
            if (bytes.Length + count > limit || budget.Bytes > ArtworkLimits.ExpandedBytes)
            { throw new Rejected("artwork-file-too-large", entry.FullName, "Actual expanded content exceeds its byte limit"); }
            bytes.Write(buffer, 0, count);
        }
        if (bytes.Length != entry.Length) { throw new Rejected("artwork-file-invalid", entry.FullName, "Expanded content length differs from ZIP metadata"); }
        if (ArtworkCrc32.Calculate(bytes.GetBuffer().AsSpan(0, (int)bytes.Length)) != entry.Crc32)
        { throw new Rejected("artwork-file-invalid", entry.FullName, "Expanded content does not match its ZIP checksum"); }
        return bytes.GetBuffer().AsMemory(0, (int)bytes.Length);
    }

    private async Task<FileStream> LockAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, ".install.lock");
        try { NotLinked(path); }
        catch (FileNotFoundException) { /* A new store has no lock file yet. */ }
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 199) { await Task.Delay(25, cancellationToken).ConfigureAwait(false); }
        }
    }

    private static void NotLinked(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        { throw new Rejected("artwork-path-invalid", path, "Artwork store members cannot be links"); }
    }

    private static async Task<ReadOnlyMemory<byte>> ReadFileAsync(string path, int limit, CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > limit) { throw new Rejected("artwork-installed-invalid", path, "Installed member exceeds its byte limit"); }
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (stream.ReadByte() != -1) { throw new Rejected("artwork-installed-invalid", path, "Installed member changed while being read"); }
        return bytes;
    }

    private static async Task<string> ReadVersionAsync(string path, string id, CancellationToken cancellationToken)
    {
        try
        {
            NotLinked(path);
            var bytes = await ReadFileAsync(path, ArtworkLimits.ManifestBytes, cancellationToken).ConfigureAwait(false);
            if (bytes.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) { bytes = bytes[3..]; }
            using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
            var root = document.RootElement;
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (root.EnumerateObject().Any(property => !names.Add(property.Name)) ||
                root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("id").GetString() != id ||
                string.IsNullOrWhiteSpace(root.GetProperty("version").GetString()))
            { throw new Rejected("artwork-installed-invalid", path, "Installed revision identity is invalid"); }
            return root.GetProperty("version").GetString()!;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new Rejected("artwork-installed-invalid", path, "Installed manifest is invalid: " + exception.Message); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new Rejected("artwork-installed-invalid", path, "Installed manifest is unreadable: " + exception.Message); }
    }

    private static async Task CheckExistingAsync(string existing, string staged, IEnumerable<string> files, CancellationToken cancellationToken)
    {
        var expected = files.ToHashSet(StringComparer.Ordinal);
        var actual = Inventory(existing, cancellationToken);
        if (!actual.SetEquals(expected)) { throw new Rejected("artwork-installed-invalid", existing, "Installed revision has an unexpected file inventory"); }
        foreach (var file in expected.Order(StringComparer.Ordinal))
        {
            var limit = file == "artwork.json" ? ArtworkLimits.ManifestBytes : file.StartsWith("licenses/", StringComparison.Ordinal) ? ArtworkLimits.LicenseBytes : ArtworkLimits.ImageBytes;
            var left = await ReadFileAsync(Path.Combine(existing, file), limit, cancellationToken).ConfigureAwait(false);
            var right = await ReadFileAsync(Path.Combine(staged, file), limit, cancellationToken).ConfigureAwait(false);
            if (!left.Span.SequenceEqual(right.Span)) { throw new Rejected("artwork-installed-invalid", file, "Installed revision was changed after installation"); }
        }
    }
}
