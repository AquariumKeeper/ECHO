using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Application.Artwork;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Desktop.Services;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Desktop.Views;
using CharacterPlanner.Infrastructure.Artwork;
using CharacterPlanner.Infrastructure.Storage;

namespace CharacterPlanner.Desktop.Tests;

/// <summary>Manual acceptance with a locally supplied historical ZIP; no external artwork is a build dependency.</summary>
public sealed class RealArtworkAcceptanceTests
{
    private sealed class CountingDecoder : IArtworkImageDecoder
    {
        public int Calls { get; private set; }
        public ValueTask<ArtworkImageSize?> DecodeAsync(ReadOnlyMemory<byte> png, CancellationToken cancellationToken)
        { Calls++; return new ArtworkPngDecoder().DecodeAsync(png, cancellationToken); }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task HistoricalCollectionInstallsDisplaysRestoresAndRemoves()
    {
        var archive = Environment.GetEnvironmentVariable("CHARACTER_PLANNER_ARTWORK_ZIP");
        Assert.True(File.Exists(archive), "Set CHARACTER_PLANNER_ARTWORK_ZIP to the converted historical ZIP");
        var root = Path.Combine(Path.GetTempPath(), "real-artwork-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var decoder = new CountingDecoder();
        var store = new ArtworkPackInstaller(Path.Combine(root, "artwork"), decoder);
        var session = new PlannerSession(Division, PlannerState.Of(BuildSnapshot.Create(Division.Pack)), new OperationApplier(Division));
        using var editor = new EditorViewModel(session, new FileBuildLibrary(Path.Combine(root, "library")), AvaloniaDispatcher.Instance);
        using var manager = new ArtworkManagerViewModel(editor, PackArtwork.None, store, store, AvaloniaDispatcher.Instance);
        var window = new ArtworkWindow { DataContext = manager };
        try
        {
            await manager.InitializeAsync();
            await manager.OpenAsync();
            await manager.InstallAsync(archive!);
            Assert.True(manager.Problem is null, manager.Problem);
            var installed = Assert.Single(manager.Rows).Entry.Pack!;
            Assert.Equal(555, installed.Manifest.Assets.Length);
            Assert.Equal(555, installed.Manifest.Credits.Length);
            Assert.Equal(9, installed.ApplicableBindings.Icons["slots"].Count);
            await Until(() => !manager.IsPreviewLoading && manager.LicenseText.Length > 0);
            Assert.Equal(6, manager.Previews.Length);
            Assert.Contains("No separate artwork license asserted", manager.LicenseText, StringComparison.Ordinal);
            window.Show();
            await Until(() => window.GetVisualDescendants().OfType<Image>().Count(image => image.Source is not null) == 6);
            if (Environment.GetEnvironmentVariable("CHARACTER_PLANNER_SNAPSHOT") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                frame.Save(Path.Combine(directory, "historical-artwork.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            var before = session.Snapshot;
            await manager.UseCommand.ExecuteAsync(null);
            Assert.True(manager.Problem is null, manager.Problem);
            Assert.NotNull(editor.Artwork.Icon("slots", "mask"));
            Assert.NotNull(editor.Artwork.Icon("skills", installed.ApplicableBindings.Icons["skills"].Keys.First()));
            Assert.Equal(before, session.Snapshot);
            // A new store instance must restore the on-disk preference, not the manager's current state.
            var restarted = new ArtworkPackInstaller(Path.Combine(root, "artwork"), new ArtworkPngDecoder());
            using (var restored = await restarted.LoadSelectionAsync(Division.Pack))
            {
                Assert.False(restored.UsesBundled);
                Assert.Equal(installed.ContentDigest, restored.Active!.Pack.ContentDigest);
            }
            var repeated = await store.InstallAsync(archive!, Division.Pack);
            Assert.True(repeated.Succeeded);
            Assert.True(repeated.AlreadyInstalled);
            await manager.BundledCommand.ExecuteAsync(null);
            Assert.Null(editor.Artwork.Icon("slots", "mask"));
            await manager.RemoveCommand.ExecuteAsync(null);
            Assert.True(manager.Problem is null, manager.Problem);
            Assert.Empty(manager.Rows);
            Assert.Equal(before, session.Snapshot);
            Assert.Equal(installed.Manifest.Assets.Select(asset => asset.Sha256).Distinct(StringComparer.Ordinal).Count(), decoder.Calls);
        }
        finally
        {
            window.Close();
            manager.Dispose();
            editor.Dispose();
            await manager.DisposeAsync();
            await Until(() => ArtworkBitmapCache.OwnersUnder(root) == 0);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < timeout, "Historical artwork UI did not settle");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
