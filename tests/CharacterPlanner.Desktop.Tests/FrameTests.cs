using System.Globalization;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Infrastructure.Storage;

namespace CharacterPlanner.Desktop.Tests;

/// <summary>The window's frame, as the Go app's: the headline's rules, the diagnostics strip, number formats, artwork and theme.</summary>
public sealed class FrameTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "shell-" + Guid.NewGuid().ToString("N"));
    private readonly TestPack.QueueDispatcher dispatcher = new();
    private readonly PlannerSession session;
    private readonly EditorViewModel editor;
    private readonly CultureInfo culture = CultureInfo.CurrentCulture;

    public FrameTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        session = new(TestPack.Division, PlannerState.Of(BuildSnapshot.Create(TestPack.Division.Pack)), new OperationApplier(TestPack.Division));
        editor = new(session, new FileBuildLibrary(folder), dispatcher);
    }

    public void Dispose()
    {
        editor.Dispose();
        CultureInfo.CurrentCulture = culture;
        if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); }
    }

    private async Task Resolved()
    {
        await editor.Resolution;
        dispatcher.RunPending();
    }

    private FigureViewModel Figure(string label) => editor.Headline.Single(figure => figure.Label == label);

    [Fact]
    public async Task TheHeadlineFollowsTheGoRules()
    {
        await Resolved();
        // A figure with an unavailable reason stays, reading Unavailable with the reason.
        var practical = Figure("Practical Reload-Cycle DPS");
        Assert.True(practical.IsVisible);
        Assert.True(practical.IsMessage);
        Assert.Equal(("Unavailable", "Needs an equipped active weapon"), (practical.Value, practical.Detail));
        Assert.Equal("No active weapon selected", editor.ActiveWeaponText);

        // Effective HP always shows; a conditional figure hides while it equals an empty build's.
        Assert.True(Figure("Effective HP").IsVisible);
        Assert.False(Figure("Time to Armor Break (s)").IsVisible);

        // With a weapon in hand, the build outputs show with their descriptions; large values are whole numbers.
        session.Apply([new EquipmentOperation("primary_weapon", "primary_weapon_acr"), new ContextOperation(GamePack.WeaponSlotSelector, Text: "primary_weapon")],
            session.Snapshot.Revision, EditOrigin.Desktop);
        dispatcher.RunPending();
        await Resolved();
        var dps = Figure("Build DPS");
        Assert.True(dps.IsVisible);
        Assert.Equal("Practical weapon + skills", dps.Detail);
        Assert.DoesNotContain(".", dps.Value, StringComparison.Ordinal);
        Assert.Equal("Primary Weapon · ACR", editor.ActiveWeaponText);
        Assert.False(practical.IsMessage);
    }

    [Fact]
    public void DiagnosticsListErrorsAndCollapseWarnings()
    {
        var lines = DiagnosticLine.Of(
        [
            new("warning", "slot-empty", "Mask is empty"),
            new("error", "bad", "Something is wrong"),
            new("warning", "slot-empty", "Chest is empty"),
            new("warning", "slot-empty", "Gloves is empty"),
            new("warning", "other", "Another warning")
        ]);
        Assert.Equal(
        [
            new DiagnosticLine("Error: Something is wrong", true),
            new DiagnosticLine("Warning: Mask is empty (and 2 more like this)", false),
            new DiagnosticLine("Warning: Another warning", false)
        ], lines);
    }

    [Fact]
    public async Task AnEmptyBuildShowsItsWarnings()
    {
        await Resolved();
        Assert.True(editor.HasDiagnostics);
        Assert.All(editor.Diagnostics, line => Assert.StartsWith(line.IsError ? "Error: " : "Warning: ", line.Text, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(StatFormats.Decimal, 3, "3")]
    [InlineData(StatFormats.Decimal, 2.5, "2.50")]
    [InlineData(StatFormats.Integer, 693012, "693,012")]
    [InlineData(StatFormats.Percent, 0.12345, "12.345%")]
    [InlineData(StatFormats.Percent, 0.15, "15%")]
    public void ValuesReadAsTheGoAppShowedThem(string format, double value, string text) =>
        Assert.Equal(text, Formatting.Value(format, value, CultureInfo.InvariantCulture));

    [Theory]
    [InlineData(StatFormats.Decimal, 733751.6, "733,752")]
    [InlineData(StatFormats.Decimal, 2.034, "2.03")]
    public void HeadlineValuesOverAThousandAreWhole(string format, double value, string text) =>
        Assert.Equal(text, Formatting.Headline(format, value, CultureInfo.InvariantCulture));

    [Theory]
    [InlineData(StatFormats.Decimal, 34, "+34")]
    [InlineData(StatFormats.Decimal, -2.5, "-2.50")]
    [InlineData(StatFormats.Percent, 0.012, "+1.20 pp")]
    [InlineData(StatFormats.Decimal, 0.001, "0")]
    public void ChangesHaveSigns(string format, double delta, string text) =>
        Assert.Equal(text, Formatting.Delta(format, delta, CultureInfo.InvariantCulture));

    [Fact]
    public void ArtworkIsFoundByItemIconAndSpecialization()
    {
        var pack = TestPack.Division.Pack;
        var directory = Path.Combine(AppContext.BaseDirectory, "packs", "division-2");
        var artwork = new PackArtwork(pack.Presentation, directory);
        Assert.True(artwork.HasArtwork);

        // The pack registers only original slot art (R34): every item shows its slot's image, with the slot's symbol
        // behind it, and no item, set or specialization art is registered.
        Assert.Empty(pack.Presentation.ItemArt);
        foreach (var slot in pack.Slots)
        {
            var art = artwork.Item("any_item", slot.ID);
            Assert.True(File.Exists(art.Image), slot.ID);
            Assert.Equal((artwork.Icon("slots", slot.ID), (string?)null, slot.ID), (art.Image, art.Icon, art.Glyph));
        }
        Assert.Null(artwork.SpecializationImage("sharpshooter"));
        Assert.Null(artwork.Asset("no_such_asset"));

        // A directory given with a trailing separator still holds its assets.
        Assert.True(new PackArtwork(pack.Presentation, directory + Path.DirectorySeparatorChar).HasArtwork);
        Assert.False(PackArtwork.None.HasArtwork);
    }

    [Fact]
    public void WithoutAssetsAnItemShowsItsSlotSymbol()
    {
        // A release's pack: the loader reports its artwork withheld, so composition passes no directory.
        var pack = TestPack.Division.Pack;
        var artwork = new PackArtwork(pack.Presentation, null);
        Assert.False(artwork.HasArtwork);
        Assert.Equal(new ItemArt(null, null, "primary_weapon"), artwork.Item("primary_weapon_acr", "primary_weapon"));
        Assert.Null(artwork.Icon("talents", pack.Talents[0].ID));
        Assert.False(artwork.Item("primary_weapon_acr", "primary_weapon").IsEmpty);
    }
}
