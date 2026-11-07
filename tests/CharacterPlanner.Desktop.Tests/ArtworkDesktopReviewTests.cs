using System.Buffers.Binary;
using Avalonia.Headless.XUnit;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Desktop.Services;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Desktop.Views;
using CharacterPlanner.Infrastructure.Storage;

namespace CharacterPlanner.Desktop.Tests;

public sealed partial class ArtworkDesktopTests
{
    [AvaloniaFact]
    public async Task BundledImagesSurviveAViewGapWithinABoundedIdleCache()
    {
        await using var fixture = new Fixture();
        var firstPath = Path.Combine(fixture.Root, "first.png");
        File.WriteAllBytes(firstPath, Png);
        var first = ArtworkBitmapCache.Acquire(firstPath, 32)!;
        var bitmap = await first.Ready;
        Assert.NotNull(bitmap);
        first.Dispose();
        using (var repeated = ArtworkBitmapCache.Acquire(firstPath, 32)!) { Assert.Same(bitmap, await repeated.Ready); }
        for (var index = 0; index < 129; index++)
        {
            var path = Path.Combine(fixture.Root, $"{index}.png");
            File.WriteAllBytes(path, Png);
            using var image = ArtworkBitmapCache.Acquire(path, 32)!;
            Assert.NotNull(await image.Ready);
        }
        Assert.InRange(ArtworkBitmapCache.IdleCount, 1, 128);
        Assert.InRange(ArtworkBitmapCache.IdleBytes, 1, 32 * 1024 * 1024);
        using var evicted = ArtworkBitmapCache.Acquire(firstPath, 32)!;
        Assert.NotSame(bitmap, await evicted.Ready);
    }

    [AvaloniaFact]
    public async Task BundledImagesCanUseOtherFormatsAndExceedTheUserFileLimit()
    {
        await using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "bundled.bmp");
        var bytes = new byte[CharacterPlanner.Application.Artwork.ArtworkLimits.ImageBytes + 1];
        "BM"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28), 24);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(34), 4);
        bytes[56] = 255;
        File.WriteAllBytes(path, bytes);
        using (var bundled = ArtworkBitmapCache.Acquire(path, 32)!) { Assert.NotNull(await bundled.Ready); }
        // A registered user revision still has the PNG/byte gates.
        using var registration = ArtworkBitmapCache.Register(fixture.Root, new Lifetime());
        using var user = ArtworkBitmapCache.Acquire(path, 32)!;
        Assert.Null(await user.Ready);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovalAwaitsLocalImageReleaseAndCanBeCanceledAndRetried(bool cancel)
    {
        await using var fixture = new Fixture();
        var (_, _, manager) = fixture.Create();
        await manager.InitializeAsync();
        await manager.OpenAsync();
        await manager.InstallAsync(fixture.Zip());
        await Until(() => !manager.IsPreviewLoading && manager.Previews.Length == 1);
        using var held = ArtworkBitmapCache.Acquire(manager.Previews[0].Path, 32)!;
        Assert.NotNull(await held.Ready);
        var removal = manager.RemoveCommand.ExecuteAsync(null);
        await Until(() => manager.Status.StartsWith("Waiting for displayed artwork", StringComparison.Ordinal));
        if (cancel)
        {
            manager.CancelCommand.Execute(null);
            await removal;
            Assert.Single(manager.Rows);
            Assert.Contains("canceled", manager.Status, StringComparison.Ordinal);
        }
        else
        {
            await Task.Delay(1250, TestContext.Current.CancellationToken);
            Assert.False(removal.IsCompleted); // Exceeds the old 4 × 250ms retry window.
        }
        held.Dispose();
        if (cancel) { await manager.RemoveCommand.ExecuteAsync(null); }
        else { await removal; }
        Assert.Null(manager.Problem);
        Assert.Empty(manager.Rows);
    }

    [AvaloniaFact]
    public async Task StartupListsTheLibraryBeforeInitializingOptionalArtwork()
    {
        await using var fixture = new Fixture();
        var library = new FileBuildLibrary(Path.Combine(fixture.Root, "library"));
        await library.CreateAsync(BuildSnapshot.Create(Division.Pack, "Existing build"), TestContext.Current.CancellationToken);
        using var model = new MainViewModel();
        var libraryReady = false;
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(MainViewModel.Editor) || model.Editor is not { } editor) { return; }
            editor.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(EditorViewModel.ArtworkManager) && editor.ArtworkManager is not null)
                { libraryReady = editor.SavedBuilds.Length == 1; }
            };
        };
        await model.LoadAsync(Path.Combine(AppContext.BaseDirectory, "packs/division-2"), library,
            AvaloniaDispatcher.Instance, artworkDirectory: fixture.Store);
        Assert.Null(model.Error);
        Assert.True(libraryReady);
    }
}
