using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Desktop.Views;

namespace CharacterPlanner.Desktop.Tests;

public sealed class GlyphTests
{
    [AvaloniaFact]
    public void EverySlotAndKindHasItsOwnSymbol()
    {
        var generic = Glyphs.Get(ArtGlyphs.Item);
        Assert.NotNull(generic);
        foreach (var slot in TestPack.Division.Pack.Slots) { Assert.NotSame(generic, Glyphs.Get(slot.ID)); }
        foreach (var kind in new[] { ArtGlyphs.Talent, ArtGlyphs.Skill, ArtGlyphs.Specialization, ArtGlyphs.Node, ArtGlyphs.Upgrades }) { Assert.NotSame(generic, Glyphs.Get(kind)); }
        foreach (var key in Glyphs.Keys) { Assert.False(Glyphs.Get(key)!.Bounds.Width <= 0, key); }
        // An unknown slot gets the generic symbol; no key gets none.
        Assert.Same(generic, Glyphs.Get("no_such_slot"));
        Assert.Null(Glyphs.Get(null));
    }

    [AvaloniaFact]
    public void ArtAndIconsDrawTheSymbolWhenTheirImageIsMissing()
    {
        var art = new ArtView { Width = 84, Height = 48, Art = new ItemArt(null, null, "mask") };
        var icon = new IconView { Width = 32, Height = 32, Source = Path.Combine(AppContext.BaseDirectory, "no-such-icon.png"), Glyph = ArtGlyphs.Talent };
        var window = new Window { Content = new StackPanel { Children = { art, icon } } };
        window.Show();
        try
        {
            Assert.Same(Glyphs.Get("mask"), Symbol(art).Data);
            Assert.True(Symbol(art).IsVisible);
            Assert.Same(Glyphs.Get(ArtGlyphs.Talent), Symbol(icon).Data);
            Assert.True(Symbol(icon).IsVisible);

            // Art with nothing to show, not even a symbol, draws nothing.
            art.Art = ItemArt.None;
            Assert.False(Symbol(art).IsVisible);
        }
        finally { window.Close(); }
    }

    private static Avalonia.Controls.Shapes.Path Symbol(Panel view) => Assert.Single(view.Children.OfType<Avalonia.Controls.Shapes.Path>());
}
