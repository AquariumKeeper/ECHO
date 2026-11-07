using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using CharacterPlanner.Desktop.Views;

namespace CharacterPlanner.Desktop.Tests;

/// <summary>Pack images are decoded for the size they are shown at, once.</summary>
public sealed class ImageTests
{
    private static readonly string Pack = Path.Combine(AppContext.BaseDirectory, "packs", "division-2");

    [AvaloniaFact]
    public async Task AnIconIsDecodedAtTwiceItsShownWidthAndSharedWhileOwned()
    {
        var path = Directory.EnumerateFiles(Pack, "icon_*.png", SearchOption.AllDirectories).First();
        using var small = ArtworkBitmapCache.Acquire(path, 32)!;
        var icon = Assert.IsType<Bitmap>(await small.Ready);
        Assert.Equal(64, icon.PixelSize.Width);
        using var repeated = ArtworkBitmapCache.Acquire(path, 32)!;
        Assert.Same(icon, await repeated.Ready);
        using var larger = ArtworkBitmapCache.Acquire(path, 96)!;
        Assert.NotSame(icon, await larger.Ready);
    }

    [AvaloniaFact]
    public async Task AMissingImageGivesNone()
    {
        using var image = ArtworkBitmapCache.Acquire(Path.Combine(Pack, "no-such-image.png"), 32)!;
        Assert.Null(await image.Ready);
    }
}
