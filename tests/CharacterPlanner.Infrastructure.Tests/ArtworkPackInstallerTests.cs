using System.IO.Compression;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CharacterPlanner.Application.Artwork;
using CharacterPlanner.Core.Packs;
using CharacterPlanner.Infrastructure.Artwork;

namespace CharacterPlanner.Infrastructure.Tests;

public sealed partial class ArtworkPackInstallerTests : IDisposable
{
    private static readonly GamePack Pack = new() { ID = "division_2", Slots = [new() { ID = "mask", Name = "Mask" }] };
    private readonly string root = Path.Combine(Path.GetTempPath(), "artwork-install-" + Guid.NewGuid().ToString("N"));
    private string Store => Path.Combine(root, "store");
    private static string Example => Path.Combine(AppContext.BaseDirectory, "artwork-example");
    public ArtworkPackInstallerTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, recursive: true);

    private sealed class Decoder : IArtworkImageDecoder
    {
        public int Calls { get; private set; }
        public Func<CancellationToken, ValueTask<ArtworkImageSize?>> Decode { get; init; } = _ => ValueTask.FromResult<ArtworkImageSize?>(new(1, 1));
        public ValueTask<ArtworkImageSize?> DecodeAsync(ReadOnlyMemory<byte> png, CancellationToken cancellationToken)
        { Calls++; return Decode(cancellationToken); }
    }

    private sealed class Progress(Action<ArtworkInstallProgress> report) : IProgress<ArtworkInstallProgress>
    { public void Report(ArtworkInstallProgress value) => report(value); }

    private static Dictionary<string, byte[]> Files() => Directory.EnumerateFiles(Example, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(Example, path).Replace('\\', '/'), File.ReadAllBytes, StringComparer.Ordinal);

    private static void Manifest(Dictionary<string, byte[]> files, Action<JsonObject> edit)
    {
        var json = JsonNode.Parse(files["artwork.json"])!.AsObject();
        edit(json);
        files["artwork.json"] = Encoding.UTF8.GetBytes(json.ToJsonString());
    }

    private string Zip(IEnumerable<KeyValuePair<string, byte[]>> files, bool reversed = false, string? link = null)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, bytes) in reversed ? files.Reverse() : files)
        {
            var entry = archive.CreateEntry(name, reversed ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            if (name.EndsWith('/')) { entry.ExternalAttributes = 0x41ed0010; }
            if (name == link) { entry.ExternalAttributes = unchecked((int)0xa1ff0000); }
            using var output = entry.Open();
            output.Write(bytes);
        }
        return path;
    }

    private void NoStaging() => Assert.Empty(Directory.Exists(Store) ? Directory.EnumerateDirectories(Store, ".staging-*") : []);

    [Fact]
    public async Task ExplicitZipDirectoriesCannotDisagreeWithMemberSpelling()
    {
        var files = Files();
        files["assets/Icons/"] = [];
        files["assets/icons/placeholder.png"] = files["assets/placeholder.png"];
        files.Remove("assets/placeholder.png");
        Manifest(files, value => value["assets"]![0]!["path"] = "assets/icons/placeholder.png");
        Error(await Manager.InstallAsync(Zip(files), Pack, cancellationToken: TestContext.Current.CancellationToken), "artwork-path-invalid");
        Assert.False(Directory.Exists(Store));
    }

    [Fact]
    public async Task DamagedOldManifestDoesNotPreventInstallingAnotherVersion()
    {
        var first = await InstallManagedAsync();
        File.WriteAllText(Path.Combine(Store, first.Id, first.ContentDigest, "artwork.json"), "{");
        var files = Files();
        Manifest(files, value => value["version"] = "2");
        var result = await Manager.InstallAsync(Zip(files), Pack, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded);
        Assert.Contains(result.Diagnostics, value => value.Code == "artwork-existing-invalid" && value.Severity == "warning");
        var catalog = await Manager.ListAsync(Pack, TestContext.Current.CancellationToken);
        Assert.Equal(2, catalog.Entries.Length);
        Assert.Single(catalog.Entries.Where(value => !value.Usable));
        Assert.Equal("2", Assert.Single(catalog.Entries.Where(value => value.Usable)).Pack!.Manifest.Version);
        // Exact reinstall never overwrites the corrupt immutable revision.
        var original = Files();
        Manifest(original, value => value["version"] = "1");
        Error(await Manager.InstallAsync(Zip(original), Pack, cancellationToken: TestContext.Current.CancellationToken), "artwork-installed-invalid");
    }
    private static void Error(ArtworkInstallResult result, string code)
    {
        Assert.False(result.Succeeded);
        Assert.Null(result.Installed);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code && diagnostic.Severity == "error");
    }

    [Fact]
    public async Task InstallsAnIndependentRevisionWithAllBytesAndProgress()
    {
        var files = Files();
        var zip = Zip(files);
        var source = File.ReadAllBytes(zip);
        var decoder = new Decoder();
        var updates = new List<ArtworkInstallProgress>();
        var result = await new ArtworkPackInstaller(Store, decoder).InstallAsync(zip, Pack, new Progress(updates.Add), TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded);
        Assert.False(result.AlreadyInstalled);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(1, decoder.Calls);
        var installed = result.Installed!;
        Assert.Equal(Path.Combine(Store, installed.Manifest.Id, installed.ContentDigest), installed.Directory);
        Assert.Equal(64, installed.ContentDigest.Length);
        Assert.Equal("placeholder", installed.ApplicableBindings.Icons["slots"]["mask"]);
        foreach (var (name, bytes) in files) { Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(installed.Directory, name))); }
        Assert.Equal(3, updates.Count);
        Assert.Equal(new ArtworkInstallProgress(3, 3), updates[^1]);
        Assert.Equal(source, File.ReadAllBytes(zip));
        File.Delete(zip);
        Assert.True(File.Exists(Path.Combine(installed.Directory, "assets/placeholder.png")));
        NoStaging();
    }

    [Fact]
    public async Task RepackingTheSameContentsIsIdempotentAndNewVersionsCoexist()
    {
        var files = Files();
        var installer = new ArtworkPackInstaller(Store, new Decoder());
        var first = await installer.InstallAsync(Zip(files), Pack, cancellationToken: TestContext.Current.CancellationToken);
        var again = await installer.InstallAsync(Zip(files, reversed: true), Pack, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(again.Succeeded);
        Assert.True(again.AlreadyInstalled);
        Assert.Equal(first.Installed!.Directory, again.Installed!.Directory);
        Manifest(files, manifest => manifest["version"] = "2");
        var update = await installer.InstallAsync(Zip(files), Pack, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(update.Succeeded);
        Assert.NotEqual(first.Installed.Directory, update.Installed!.Directory);
        Assert.Equal(2, Directory.EnumerateDirectories(Path.GetDirectoryName(first.Installed.Directory)!).Count());
        NoStaging();
    }

    [Fact]
    public async Task ChangedContentsUnderTheSameVersionPreserveTheExistingRevision()
    {
        var files = Files();
        var installer = new ArtworkPackInstaller(Store, new Decoder());
        var first = await installer.InstallAsync(Zip(files), Pack, cancellationToken: TestContext.Current.CancellationToken);
        var original = File.ReadAllBytes(Path.Combine(first.Installed!.Directory, "artwork.json"));
        Manifest(files, manifest => manifest["name"] = "Changed contents");
        Error(await installer.InstallAsync(Zip(files), Pack, cancellationToken: TestContext.Current.CancellationToken), "artwork-version-conflict");
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(first.Installed.Directory, "artwork.json")));
        Assert.Single(Directory.EnumerateDirectories(Path.GetDirectoryName(first.Installed.Directory)!));
        NoStaging();
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("extra")]
    [InlineData("missing")]
    [InlineData("manifest")]
    public async Task AChangedInstalledRevisionIsNotSilentlyReused(string damage)
    {
        var files = Files();
        var installer = new ArtworkPackInstaller(Store, new Decoder());
        var zip = Zip(files);
        var first = await installer.InstallAsync(zip, Pack, cancellationToken: TestContext.Current.CancellationToken);
        var directory = first.Installed!.Directory;
        if (damage == "changed") { File.AppendAllText(Path.Combine(directory, "licenses/LICENSE.txt"), "changed"); }
        if (damage == "extra") { File.WriteAllText(Path.Combine(directory, "extra.txt"), "extra"); }
        if (damage == "missing") { File.Delete(Path.Combine(directory, "assets/placeholder.png")); }
        if (damage == "manifest") { File.Delete(Path.Combine(directory, "artwork.json")); }
        Error(await installer.InstallAsync(zip, Pack, cancellationToken: TestContext.Current.CancellationToken), "artwork-installed-invalid");
        NoStaging();
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("assets/../escape.png")]
    [InlineData("assets\\escape.png")]
    [InlineData("C:/escape.png")]
    [InlineData("assets/CON.png")]
    [InlineData("assets/placeholder.png:stream")]
    public async Task UnsafeArchiveMembersAreRejectedBeforeWriting(string name)
    {
        var files = Files();
        files[name] = [1];
        Error(await new ArtworkPackInstaller(Store, new Decoder()).InstallAsync(Zip(files), Pack, cancellationToken: TestContext.Current.CancellationToken), "artwork-path-invalid");
        Assert.False(Directory.Exists(Store));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("case")]
    [InlineData("link")]
    public async Task DuplicateCaseCollidingAndLinkedEntriesAreRejected(string kind)
    {
        var files = Files();
        var entries = files.ToList();
        if (kind == "duplicate") { entries.Add(new("assets/placeholder.png", [1])); }
        if (kind == "case") { entries.Add(new("assets/PLACEHOLDER.png", [1])); }
        var zip = Zip(entries, link: kind == "link" ? "assets/placeholder.png" : null);
        Error(await new ArtworkPackInstaller(Store, new Decoder()).InstallAsync(zip, Pack, cancellationToken: TestContext.Current.CancellationToken), "artwork-path-invalid");
        Assert.False(Directory.Exists(Store));
    }

    [Theory]
    [InlineData("missing", "artwork-file-missing")]
    [InlineData("unlisted", "artwork-file-unlisted")]
    [InlineData("no-manifest", "artwork-manifest-missing")]
    [InlineData("wrong-game", "artwork-game-mismatch")]
    [InlineData("hash", "artwork-hash-mismatch")]
    [InlineData("license", "artwork-license-invalid")]
    public async Task InvalidPackagesDoNotPublishARevision(string kind, string code)
    {
        var files = Files();
        if (kind == "missing") { files.Remove("assets/placeholder.png"); }
        if (kind == "unlisted") { files["assets/unlisted.png"] = [1]; }
        if (kind == "no-manifest") { files.Remove("artwork.json"); }
        if (kind == "wrong-game") { Manifest(files, manifest => manifest["gamePackId"] = "elsewhere"); }
        if (kind == "hash") { files["assets/placeholder.png"][20] ^= 1; }
        if (kind == "license") { files["licenses/LICENSE.txt"] = [255, 255]; }
        Error(await new ArtworkPackInstaller(Store, new Decoder()).InstallAsync(Zip(files), Pack, cancellationToken: TestContext.Current.CancellationToken), code);
        NoStaging();
        Assert.Empty(Directory.Exists(Store) ? Directory.EnumerateDirectories(Store) : []);
    }

    [Theory]
    [InlineData("crc")]
    [InlineData("truncated")]
    [InlineData("trailing")]
    [InlineData("dimensions")]
    [InlineData("animation")]
    public async Task InvalidPngContainersNeverReachTheDecoder(string kind)
    {
        var files = Files();
        var png = files["assets/placeholder.png"];
        if (kind == "crc") { png[33] ^= 1; }
        if (kind == "truncated") { png = png[..^5]; }
        if (kind == "trailing") { png = [.. png, 1]; }
        if (kind == "dimensions") { png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAIAAAAAABCAYAAAB2SyBgAAAADUlEQVR42mP4X8/wHwAGfQJ+unzLBwAAAABJRU5ErkJggg=="); }
        if (kind == "animation") { png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAACGFjVEwAAAABAAAAALQt6aAAAAANSURBVHjaY/hfz/AfAAZ9An66fMsHAAAAAElFTkSuQmCC"); }
        files["assets/placeholder.png"] = png;
        Manifest(files, manifest => manifest["assets"]![0]!["sha256"] = Convert.ToHexStringLower(SHA256.HashData(png)));
        var decoder = new Decoder();
        Error(await new ArtworkPackInstaller(Store, decoder).InstallAsync(Zip(files), Pack, cancellationToken: TestContext.Current.CancellationToken), "artwork-image-invalid");
        Assert.Equal(0, decoder.Calls);
        NoStaging();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecoderFailureOrSizeMismatchDoesNotPublish(bool mismatch)
    {
        var decoder = new Decoder { Decode = _ => ValueTask.FromResult<ArtworkImageSize?>(mismatch ? new(2, 2) : null) };
        Error(await new ArtworkPackInstaller(Store, decoder).InstallAsync(Zip(Files()), Pack, cancellationToken: TestContext.Current.CancellationToken), "artwork-image-invalid");
        Assert.Equal(1, decoder.Calls);
        NoStaging();
    }

    [Theory]
    [InlineData("artwork.json", ArtworkLimits.ManifestBytes)]
    [InlineData("assets/placeholder.png", ArtworkLimits.ImageBytes)]
    [InlineData("licenses/LICENSE.txt", ArtworkLimits.LicenseBytes)]
    public async Task ExpandedMemberLimitsAreEnforced(string path, int limit)
    {
        var files = Files();
        files[path] = new byte[limit + 1];
        Error(await new ArtworkPackInstaller(Store, new Decoder()).InstallAsync(Zip(files), Pack, cancellationToken: TestContext.Current.CancellationToken), "artwork-file-too-large");
        NoStaging();
    }

    [Fact]
    public async Task ArchiveSizeAndEntryCountAreBounded()
    {
        var installer = new ArtworkPackInstaller(Store, new Decoder());
        var large = Path.Combine(root, "large.zip");
        using (var stream = File.Create(large)) { stream.SetLength(ArtworkLimits.ArchiveBytes + 1L); }
        Error(await installer.InstallAsync(large, Pack, cancellationToken: TestContext.Current.CancellationToken), "artwork-archive-too-large");
        var entries = Enumerable.Range(0, ArtworkLimits.Files + 1).Select(index => new KeyValuePair<string, byte[]>($"assets/{index}.png", []));
        Error(await installer.InstallAsync(Zip(entries), Pack, cancellationToken: TestContext.Current.CancellationToken), "artwork-file-limit");
        Assert.False(Directory.Exists(Store));
    }

    [Fact]
    public async Task CancellationDuringStagingPreservesPublishedContent()
    {
        var installer = new ArtworkPackInstaller(Store, new Decoder());
        var files = Files();
        var first = await installer.InstallAsync(Zip(files), Pack, cancellationToken: TestContext.Current.CancellationToken);
        Manifest(files, manifest => manifest["version"] = "2");
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(Zip(files), Pack,
            new Progress(_ => cancellation.Cancel()), cancellation.Token));
        Assert.True(Directory.Exists(first.Installed!.Directory));
        Assert.Single(Directory.EnumerateDirectories(Path.GetDirectoryName(first.Installed.Directory)!));
        NoStaging();
    }

    [Fact]
    public async Task ConcurrentInstallersPublishOneRevision()
    {
        var zip = Zip(Files());
        var first = new ArtworkPackInstaller(Store, new Decoder());
        var second = new ArtworkPackInstaller(Store, new Decoder());
        var results = await Task.WhenAll(first.InstallAsync(zip, Pack, cancellationToken: TestContext.Current.CancellationToken), second.InstallAsync(zip, Pack, cancellationToken: TestContext.Current.CancellationToken));
        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Single(results, result => result.AlreadyInstalled);
        Assert.Equal(results[0].Installed!.Directory, results[1].Installed!.Directory);
        NoStaging();
    }

    [Fact]
    public async Task BomManifestCanBeReinstalledWithoutChangingItsBytes()
    {
        var files = Files();
        files["artwork.json"] = [0xEF, 0xBB, 0xBF, .. files["artwork.json"]];
        var installer = new ArtworkPackInstaller(Store, new Decoder());
        var zip = Zip(files);
        Assert.True((await installer.InstallAsync(zip, Pack, cancellationToken: TestContext.Current.CancellationToken)).Succeeded);
        var repeated = await installer.InstallAsync(zip, Pack, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(repeated.AlreadyInstalled);
        Assert.Equal(files["artwork.json"], File.ReadAllBytes(Path.Combine(repeated.Installed!.Directory, "artwork.json")));
        NoStaging();
    }

    [Fact]
    public async Task CorruptInstalledManifestNamesTheInstalledFile()
    {
        var zip = Zip(Files());
        var installer = new ArtworkPackInstaller(Store, new Decoder());
        var first = await installer.InstallAsync(zip, Pack, cancellationToken: TestContext.Current.CancellationToken);
        var path = Path.Combine(first.Installed!.Directory, "artwork.json");
        File.WriteAllText(path, "{}");
        var result = await installer.InstallAsync(zip, Pack, cancellationToken: TestContext.Current.CancellationToken);
        Error(result, "artwork-installed-invalid");
        Assert.Equal(path, Assert.Single(result.Diagnostics).Path);
        NoStaging();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SpoofedExpandedLengthsCannotBypassValidation(bool expandedLimit)
    {
        var zip = Zip(Files());
        var bytes = File.ReadAllBytes(zip);
        var found = false;
        for (var index = 0; index <= bytes.Length - 46; index++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index)) != 0x02014b50) { continue; }
            var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(index + 28));
            if (Encoding.UTF8.GetString(bytes, index + 46, length) != "assets/placeholder.png") { continue; }
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index + 24), expandedLimit ? (uint)ArtworkLimits.ExpandedBytes + 1 : 1);
            found = true;
            break;
        }
        Assert.True(found);
        File.WriteAllBytes(zip, bytes);
        Error(await new ArtworkPackInstaller(Store, new Decoder()).InstallAsync(zip, Pack, cancellationToken: TestContext.Current.CancellationToken),
            expandedLimit ? "artwork-expanded-limit" : "artwork-file-invalid");
        NoStaging();
    }

    [Fact]
    public async Task CancellationWhileWaitingForPublicationCleansTheStagedCopy()
    {
        Directory.CreateDirectory(Store);
        using var gate = new FileStream(Path.Combine(Store, ".install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var cancellation = new CancellationTokenSource();
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new ArtworkPackInstaller(Store, new Decoder()).InstallAsync(Zip(Files()), Pack,
            new Progress(progress => { if (progress.CompletedFiles == progress.TotalFiles) { staged.TrySetResult(); } }), cancellation.Token);
        await staged.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(Directory.EnumerateDirectories(Store));
        NoStaging();
    }

    [Fact]
    public async Task CombinedPixelBudgetStopsBeforeDecodingTheOverflowingImage()
    {
        var files = Files();
        files.Remove("assets/placeholder.png");
        // The injected decoder simulates large valid images so this budget test does not allocate 64 million pixels.
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAEAAAABAACAYAAADyoyQXAAAADUlEQVR42mP4X8/wHwAGfQJ+unzLBwAAAABJRU5ErkJggg==");
        var assets = new JsonArray();
        for (var index = 0; index < 4; index++)
        {
            var name = $"assets/image{index}.png";
            files[name] = png;
            assets.Add(new JsonObject { ["id"] = index == 0 ? "placeholder" : $"image{index}", ["path"] = name, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(png)) });
        }
        Manifest(files, manifest => manifest["assets"] = assets);
        var decoder = new Decoder { Decode = _ => ValueTask.FromResult<ArtworkImageSize?>(new(4096, 4096)) };
        Error(await new ArtworkPackInstaller(Store, decoder).InstallAsync(Zip(files), Pack, cancellationToken: TestContext.Current.CancellationToken), "artwork-pixel-limit");
        Assert.Equal(1, decoder.Calls); // Identical bytes decode once, but all four assets count toward the pixel budget.
        NoStaging();
    }

    [Fact]
    public async Task EmptyDirectoryEntriesDoNotChangeTheRevision()
    {
        var files = Files();
        var installer = new ArtworkPackInstaller(Store, new Decoder());
        var first = await installer.InstallAsync(Zip(files), Pack, cancellationToken: TestContext.Current.CancellationToken);
        files["assets/"] = [];
        files["licenses/"] = [];
        var second = await installer.InstallAsync(Zip(files), Pack, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(second.AlreadyInstalled);
        Assert.Equal(first.Installed!.ContentDigest, second.Installed!.ContentDigest);
        NoStaging();
    }

    [Fact]
    public async Task CancellationBeforeValidationCreatesNoStore()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ArtworkPackInstaller(Store, new Decoder()).InstallAsync(Zip(Files()), Pack, cancellationToken: cancellation.Token));
        Assert.False(Directory.Exists(Store));
    }

    [Theory]
    [InlineData("empty", "artwork-input-invalid")]
    [InlineData("missing", "artwork-io")]
    [InlineData("malformed", "artwork-package-invalid")]
    public async Task BadArchiveInputsReturnDiagnostics(string kind, string code)
    {
        var path = kind == "empty" ? "" : Path.Combine(root, "missing.zip");
        if (kind == "malformed") { File.WriteAllText(path, "Not a ZIP"); }
        Error(await new ArtworkPackInstaller(Store, new Decoder()).InstallAsync(path, Pack, cancellationToken: TestContext.Current.CancellationToken), code);
        Assert.False(Directory.Exists(Store));
    }
}
