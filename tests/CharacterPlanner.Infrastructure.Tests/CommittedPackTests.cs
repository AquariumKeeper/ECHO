using CharacterPlanner.Infrastructure.PackLoading;

namespace CharacterPlanner.Infrastructure.Tests;

/// <summary>
/// The committed Division pack loads completely, artwork included: every one of its 555 artwork files is present
/// and matches the hash the pack records. Until the delivery arrived, full loading failed on the missing files.
/// </summary>
public sealed class CommittedPackTests
{
    [Fact]
    public async Task TheDivisionPackLoadsWithItsArtwork()
    {
        var loaded = await PackFileLoader.LoadAsync(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"), TestContext.Current.CancellationToken);
        Assert.Empty(loaded.Validation.Diagnostics);
        Assert.NotNull(loaded.Validation.Compiled);
        // R34: only the eight original slot images (MIT, Shade-Tech contributors); no game art is registered.
        var presentation = loaded.Validation.Compiled.Pack.Presentation;
        Assert.Equal(8, presentation.ArtworkAssets.Length);
        Assert.All(presentation.ArtworkSources, source => Assert.Equal("MIT", source.License));
        Assert.Equal(["slots"], presentation.Icons.Keys);
        Assert.Empty(presentation.ItemArt);
        Assert.Empty(presentation.SpecializationImages);
        Assert.False(loaded.ArtworkWithheld);
        Assert.Equal("division_2", loaded.Manifest?.ID);
    }
}
