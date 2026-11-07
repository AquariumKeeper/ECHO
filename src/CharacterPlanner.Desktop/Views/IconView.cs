using Avalonia;
using CharacterPlanner.Desktop.ViewModels;

namespace CharacterPlanner.Desktop.Views;

/// <summary>
/// A pack icon (a talent's, a skill's, a node's), or the app's own symbol for its kind when the icon is missing or
/// can't be read: an <see cref="ArtView"/> with one image and a symbol that fills it.
/// </summary>
public sealed class IconView : ArtView
{
    public static readonly StyledProperty<string?> SourceProperty = AvaloniaProperty.Register<IconView, string?>(nameof(Source));
    public static readonly StyledProperty<string?> GlyphProperty = AvaloniaProperty.Register<IconView, string?>(nameof(Glyph));

    /// <summary>The icon's file.</summary>
    public string? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    /// <summary>The symbol's key: a slot id or an <see cref="ArtGlyphs"/> kind.</summary>
    public string? Glyph { get => GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    protected override double MaxSymbolSize => double.PositiveInfinity;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty || change.Property == GlyphProperty) { Art = new ItemArt(Source, null, Glyph); }
    }
}
