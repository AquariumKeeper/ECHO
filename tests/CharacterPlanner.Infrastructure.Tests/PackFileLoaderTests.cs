using System.Collections.Immutable;
using System.Text;
using CharacterPlanner.Core.Packs;
using CharacterPlanner.Infrastructure.PackLoading;

namespace CharacterPlanner.Infrastructure.Tests;

public sealed class PackFileLoaderTests : IDisposable
{
    private static readonly byte[] Icon = Encoding.UTF8.GetBytes("icon");

    // A minimal pack with one artwork asset, written by the v2 writer.
    private static readonly GamePack Pack = new()
    {
        ID = "mini",
        Name = "Mini",
        Version = "1",
        Level = 40,
        Stats = [new() { ID = "armor", Name = "Armor", Format = StatFormats.Integer, Formula = "0" }],
        Slots = [new() { ID = "mask", Name = "Mask" }],
        Items = [new() { ID = "mask_a", Name = "Mask A", Slot = "mask", Modifiers = [new() { Stat = "armor", Op = ModifierOps.Flat, Value = new(10) }] }],
        Presentation = new()
        {
            Icons = ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty
                .Add(PresentationIcons.Slots, ImmutableDictionary<string, string>.Empty.Add("mask", "icon_mask")),
            ArtworkSources = [new() { ID = "source", Name = "Source" }],
            ArtworkAssets = [new() { ID = "icon_mask", Path = "assets/icons/mask.png", Kind = "icon", SHA256 = PackFiles.Sha256(Icon), SourceID = "source" }]
        },
        LegacyShareOrder = new() { Slots = ["mask"] }
    };

    private readonly string directory = Path.Combine(Path.GetTempPath(), "pack-loader-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
    }

    private async Task WriteAsync(PackWriteOptions? options = null, bool icon = true)
    {
        Directory.CreateDirectory(Path.Combine(directory, "assets", "icons"));
        foreach (var (name, bytes) in PackWriter.Write(Pack, options))
        { await File.WriteAllBytesAsync(Path.Combine(directory, name), bytes.ToArray(), TestContext.Current.CancellationToken); }
        if (icon) { await File.WriteAllBytesAsync(Path.Combine(directory, "assets", "icons", "mask.png"), Icon, TestContext.Current.CancellationToken); }
    }

    private static string Digest => PackFiles.DirectoryDigest([new("assets/icons/mask.png", PackFiles.Sha256(Icon))]);

    [Fact]
    public async Task LoadsAPackDirectoryAndReportsItsIdentity()
    {
        await WriteAsync(new() { AssetsSha256 = Digest, Provenance = Encoding.UTF8.GetBytes("# Sources\n") });
        var result = await PackFileLoader.LoadAsync(directory, TestContext.Current.CancellationToken);
        Assert.Equal(Path.GetFullPath(directory), result.Directory);
        Assert.Equal(PackFiles.Sha256(await File.ReadAllBytesAsync(Path.Combine(directory, PackFiles.Manifest), TestContext.Current.CancellationToken)), result.ManifestSha256);
        Assert.Equal(("mini", "1"), (result.Manifest!.ID, result.Manifest.Version));
        Assert.Empty(result.Validation.Diagnostics);
        Assert.Equal("mask_a", Assert.Single(Assert.IsType<CompiledPackExpressions>(result.Validation.Compiled).Pack.Items).ID);
    }

    [Fact]
    public async Task MissingDirectoryOrManifestRemainsAnIoFailure()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => PackFileLoader.LoadAsync(directory, TestContext.Current.CancellationToken));
        Directory.CreateDirectory(directory);
        await Assert.ThrowsAsync<FileNotFoundException>(() => PackFileLoader.LoadAsync(directory, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HonorsCancellation()
    {
        await WriteAsync();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PackFileLoader.LoadAsync(directory, cancellation.Token));
    }

    // Cancellation that arrives while a phase runs stops the load before the next phase. Without
    // the icon, no asset is hashed, so after validation only the loader's own check can throw.
    [Theory]
    [InlineData("files")]
    [InlineData("read")]
    [InlineData("validate")]
    public async Task HonorsCancellationBetweenPhases(string phase)
    {
        await WriteAsync(icon: false);
        using var cancellation = new CancellationTokenSource();
        var completed = new List<string>();
        void PhaseCompleted(string name)
        {
            completed.Add(name);
            if (name == phase) { cancellation.Cancel(); }
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PackFileLoader.LoadAsync(directory, PhaseCompleted, cancellation.Token));
        Assert.Equal(phase, completed[^1]);
        // The same pack loads when nothing cancels it.
        completed.Clear();
        var loaded = await PackFileLoader.LoadAsync(directory, completed.Add, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "files", "read", "validate" }, completed);
        Assert.Equal("artwork-missing", Assert.Single(loaded.Validation.Diagnostics).Code);
    }

    [Fact]
    public async Task MalformedFilesReturnDiagnosticsWithoutChangingFiles()
    {
        await WriteAsync();
        var rules = Path.Combine(directory, PackFiles.Rules);
        await File.WriteAllTextAsync(rules, "{", TestContext.Current.CancellationToken);
        var result = await PackFileLoader.LoadAsync(directory, TestContext.Current.CancellationToken);
        Assert.Null(result.Validation.Compiled);
        Assert.Equal("file-size-mismatch", Assert.Single(result.Validation.Diagnostics).Code);
        Assert.Equal("{", await File.ReadAllTextAsync(rules, TestContext.Current.CancellationToken));
        File.Delete(rules);
        Assert.Equal("file-missing", Assert.Single((await PackFileLoader.LoadAsync(directory, TestContext.Current.CancellationToken)).Validation.Diagnostics).Code);
    }

    [Fact]
    public async Task LoadsAReleaseWithItsArtworkWithheldOnlyWithAMatchingMarker()
    {
        await WriteAsync(new() { AssetsSha256 = Digest }, icon: false);
        Directory.Delete(Path.Combine(directory, "assets"), recursive: true);
        var marker = Path.Combine(directory, PackFileLoader.ArtworkWithheldMarker);

        // Without the marker, missing artwork is an error, as before.
        var missing = await PackFileLoader.LoadAsync(directory, TestContext.Current.CancellationToken);
        Assert.Null(missing.Validation.Compiled);
        Assert.False(missing.ArtworkWithheld);

        // A marker naming this manifest's digest withholds the artwork: no diagnostics, no art.
        await File.WriteAllTextAsync(marker, $$"""{"assetsSha256":"{{Digest}}"}""", TestContext.Current.CancellationToken);
        var withheld = await PackFileLoader.LoadAsync(directory, TestContext.Current.CancellationToken);
        Assert.NotNull(withheld.Validation.Compiled);
        Assert.Empty(withheld.Validation.Diagnostics);
        Assert.True(withheld.ArtworkWithheld);

        // A marker for another digest, or one that isn't the expected JSON, excuses nothing.
        foreach (var text in new[] { $$"""{"assetsSha256":"{{new string('0', 64)}}"}""", "{", "[]" })
        {
            await File.WriteAllTextAsync(marker, text, TestContext.Current.CancellationToken);
            var refused = await PackFileLoader.LoadAsync(directory, TestContext.Current.CancellationToken);
            Assert.Null(refused.Validation.Compiled);
            Assert.False(refused.ArtworkWithheld);
            Assert.Equal("artwork-withheld-mismatch", Assert.Single(refused.Validation.Diagnostics).Code);
        }

        // With assets present the marker is ignored and the files are checked.
        await File.WriteAllTextAsync(marker, $$"""{"assetsSha256":"{{Digest}}"}""", TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(directory, "assets"));
        var partial = await PackFileLoader.LoadAsync(directory, TestContext.Current.CancellationToken);
        Assert.False(partial.ArtworkWithheld);
        Assert.Contains(partial.Validation.Diagnostics, diagnostic => diagnostic.Code == "artwork-missing");
    }

    [Fact]
    public async Task VerifiesAssetsAgainstTheManifestAndPresentation()
    {
        await WriteAsync(new() { AssetsSha256 = Digest }, icon: false);
        var missing = await PackFileLoader.LoadAsync(directory, TestContext.Current.CancellationToken);
        Assert.Null(missing.Validation.Compiled);
        Assert.Equal(new[] { "assets-hash-mismatch", "artwork-missing" }, missing.Validation.Diagnostics.Select(diagnostic => diagnostic.Code));
        await File.WriteAllBytesAsync(Path.Combine(directory, "assets", "icons", "mask.png"), Encoding.UTF8.GetBytes("changed"), TestContext.Current.CancellationToken);
        var changed = await PackFileLoader.LoadAsync(directory, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "assets-hash-mismatch", "artwork-hash-mismatch" }, changed.Validation.Diagnostics.Select(diagnostic => diagnostic.Code));
    }
}
