using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using CharacterPlanner.Core.Diagnostics;
using CharacterPlanner.Core.Packs;

[assembly: InternalsVisibleTo("CharacterPlanner.Infrastructure.Tests")]

namespace CharacterPlanner.Infrastructure.PackLoading;

/// <summary>
/// A loaded pack directory: where it is, which manifest was read, and the validation outcome. With
/// <paramref name="ArtworkWithheld"/>, the directory is a release's: its artwork was deliberately left out, so there is
/// no art to show (the app draws its own symbols).
/// </summary>
public sealed record LoadedPack(string Directory, string ManifestSha256, PackValidationResult Validation, bool ArtworkWithheld = false)
{
    /// <summary>The pack's identity, when its manifest could be read.</summary>
    public PackManifest? Manifest => Validation.Manifest;
}

public static class PackFileLoader
{
    /// <summary>
    /// A release's statement, beside the pack files, that it ships without the pack's artwork:
    /// <c>{"assetsSha256": "…"}</c>, naming the manifest's digest of the artwork it leaves out. Only with it, and only
    /// while it names that digest, does a pack without <c>assets/</c> load; otherwise missing artwork is an error.
    /// </summary>
    public const string ArtworkWithheldMarker = "artwork-withheld.json";

    // I/O failures and cancellation stay exceptions, distinct from invalid-pack diagnostics.
    // A missing directory or manifest is an I/O failure; a manifest that lists a missing
    // file is a malformed pack. Nothing is written.
    public static Task<LoadedPack> LoadAsync(string directory, CancellationToken cancellationToken = default) =>
        LoadAsync(directory, phaseCompleted: null, cancellationToken);

    /// <summary>
    /// Reads a pack directory's structure without semantic validation or artwork checks: enough for a pack's
    /// identity and fixed orders (as share-code import needs), not for resolving builds.
    /// </summary>
    public static async Task<PackReadResult> ReadAsync(string directory, CancellationToken cancellationToken = default)
    {
        var files = await ReadFilesAsync(Path.GetFullPath(directory), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() => PackReader.Read(files), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The test seam: <paramref name="phaseCompleted"/> sees "files", "read" and "validate" as each phase ends.</summary>
    internal static async Task<LoadedPack> LoadAsync(string directory, Action<string>? phaseCompleted, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(directory);
        var files = await ReadFilesAsync(root, cancellationToken).ConfigureAwait(false);
        var manifest = files[PackFiles.Manifest];
        phaseCompleted?.Invoke("files");
        cancellationToken.ThrowIfCancellationRequested();
        var read = await Task.Run(() => PackReader.Read(files), cancellationToken).ConfigureAwait(false);
        phaseCompleted?.Invoke("read");
        cancellationToken.ThrowIfCancellationRequested();
        var validation = await Task.Run(() => PackValidator.Validate(read), cancellationToken).ConfigureAwait(false);
        phaseCompleted?.Invoke("validate");
        cancellationToken.ThrowIfCancellationRequested();
        var withheld = false;
        if (read.Manifest is not null && read.Pack is not null)
        {
            ImmutableArray<PackDiagnostic> problems;
            try
            {
                withheld = !Directory.Exists(Path.Combine(root, PackFiles.AssetsDirectory)) && File.Exists(Path.Combine(root, ArtworkWithheldMarker));
                problems = withheld
                    ? CheckWithheld(read.Manifest, await File.ReadAllBytesAsync(Path.Combine(root, ArtworkWithheldMarker), cancellationToken).ConfigureAwait(false))
                    : CheckAssets(read.Manifest, read.Pack.Presentation, await HashAssetsAsync(root, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { problems = [new("error", "assets-unreadable", PackFiles.AssetsDirectory, $"The assets directory could not be read: {exception.Message}")]; }
            if (problems.Length > 0) { validation = validation with { Diagnostics = validation.Diagnostics.AddRange(problems), Compiled = null }; withheld = false; }
        }
        return new(root, PackFiles.Sha256(manifest.Span), validation, withheld);
    }

    // The marker must name the digest this manifest declares, so it can't excuse another pack's missing artwork.
    private static ImmutableArray<PackDiagnostic> CheckWithheld(PackManifest manifest, byte[] marker)
    {
        string? digest = null;
        try
        {
            using var json = JsonDocument.Parse(marker);
            if (json.RootElement is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("assetsSha256", out var value) && value.ValueKind == JsonValueKind.String)
            { digest = value.GetString(); }
        }
        catch (JsonException) { }
        return digest == manifest.AssetsSha256 ? []
            : [new("error", "artwork-withheld-mismatch", ArtworkWithheldMarker, $"{ArtworkWithheldMarker} does not name this pack's assetsSha256")];
    }

    private static async Task<Dictionary<string, ReadOnlyMemory<byte>>> ReadFilesAsync(string root, CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        files[PackFiles.Manifest] = await File.ReadAllBytesAsync(Path.Combine(root, PackFiles.Manifest), cancellationToken).ConfigureAwait(false);
        // Only fixed names are read, so a manifest cannot point the loader at other paths.
        foreach (var name in PackFiles.Listable)
        {
            var path = Path.Combine(root, name);
            if (File.Exists(path)) { files[name] = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false); }
        }
        return files;
    }

    /// <summary>SHA-256 of every file under assets/, keyed by its pack-relative slash path ("assets/…").</summary>
    private static async Task<ImmutableSortedDictionary<string, string>> HashAssetsAsync(string root, CancellationToken cancellationToken)
    {
        var hashes = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var directory = Path.Combine(root, PackFiles.AssetsDirectory);
        if (!Directory.Exists(directory)) { return hashes.ToImmutable(); }
        // Junctions and symlinks are skipped: they could loop or reach files outside the pack.
        // Hidden and system files (Thumbs.db, desktop.ini) are not part of a delivery.
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System };
        foreach (var path in Directory.EnumerateFiles(directory, "*", options))
        {
            var stream = File.OpenRead(path);
            await using (stream.ConfigureAwait(false))
            {
                var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                hashes[Path.GetRelativePath(root, path).Replace('\\', '/')] = Convert.ToHexStringLower(hash);
            }
        }
        return hashes.ToImmutable();
    }

    private static ImmutableArray<PackDiagnostic> CheckAssets(PackManifest manifest, PackPresentation presentation, ImmutableSortedDictionary<string, string> assets)
    {
        var problems = ImmutableArray.CreateBuilder<PackDiagnostic>();
        if (manifest.AssetsSha256.Length > 0 && PackFiles.DirectoryDigest(assets) != manifest.AssetsSha256)
        { problems.Add(new("error", "assets-hash-mismatch", $"{PackFiles.Manifest}:$.assetsSha256", "The assets directory does not match its manifest digest")); }
        for (var i = 0; i < presentation.ArtworkAssets.Length; i++)
        {
            var asset = presentation.ArtworkAssets[i];
            var path = $"presentation.artwork.assets[{i}]";
            if (!assets.TryGetValue(asset.Path, out var hash)) { problems.Add(new("error", "artwork-missing", path + ".path", $"Artwork file {asset.Path} is missing")); }
            else if (hash != asset.SHA256) { problems.Add(new("error", "artwork-hash-mismatch", path + ".sha256", $"Artwork file {asset.Path} does not match its hash")); }
        }
        return problems.ToImmutable();
    }
}
