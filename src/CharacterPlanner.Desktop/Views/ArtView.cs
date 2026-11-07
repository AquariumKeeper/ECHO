using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CharacterPlanner.Desktop.ViewModels;
using Avalonia.VisualTree;

namespace CharacterPlanner.Desktop.Views;

/// <summary>
/// An item's art, layered as the Go app's: with both an image and a brand or set logo, the logo sits translucent on
/// the left 55% and the image, shifted a quarter right, fills the rest; with one, it fills the area. Images keep their
/// proportions. When neither can be shown, the app's own symbol for the slot or kind stands in; with no symbol either,
/// nothing shows. Images are decoded for the view's width, again when it changes.
/// </summary>
public class ArtView : Panel
{
    public static readonly StyledProperty<ItemArt?> ArtProperty = AvaloniaProperty.Register<ArtView, ItemArt?>(nameof(Art));

    private readonly Image icon = new() { Stretch = Stretch.Uniform };
    private readonly Image image = new() { Stretch = Stretch.Uniform };
    private readonly Avalonia.Controls.Shapes.Path glyph = Glyphs.NewShape();
    private ArtworkBitmapCache.Handle? iconHandle;
    private ArtworkBitmapCache.Handle? imageHandle;
    private long generation;
    private bool detached = true;

    static ArtView() => AffectsArrange<ArtView>(ArtProperty);

    public ArtView()
    {
        Children.Add(glyph);
        Children.Add(icon);
        Children.Add(image);
    }

    public ItemArt? Art { get => GetValue(ArtProperty); set => SetValue(ArtProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ArtProperty && change.Property != WidthProperty) { return; }
        Refresh();
    }

    private async void Refresh()
    {
        var current = ++generation;
        var previousIcon = iconHandle;
        var previousImage = imageHandle;
        icon.Source = null;
        image.Source = null;
        iconHandle = detached ? null : ArtworkBitmapCache.Acquire(Art?.Icon, Width);
        imageHandle = detached ? null : ArtworkBitmapCache.Acquire(Art?.Image, Width);
        previousIcon?.Dispose();
        previousImage?.Dispose();
        ShowGlyph();
        var nextIcon = iconHandle;
        var nextImage = imageHandle;
        var iconBitmap = nextIcon is null ? null : await nextIcon.Ready;
        var imageBitmap = nextImage is null ? null : await nextImage.Ready;
        if (current != generation || detached) { return; }
        icon.Source = iconBitmap;
        image.Source = imageBitmap;
        icon.Opacity = image.Source is not null ? 0.8 : 1;
        ShowGlyph();
        InvalidateArrange();
    }

    private void ShowGlyph()
    {
        glyph.Data = icon.Source is null && image.Source is null ? Glyphs.Get(Art?.Glyph) : null;
        glyph.IsVisible = glyph.Data is not null;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); detached = false; Refresh(); }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { detached = true; Refresh(); base.OnDetachedFromVisualTree(e); }

    /// <summary>The largest the symbol is drawn: item art keeps it small inside a wide box.</summary>
    protected virtual double MaxSymbolSize => 40;

    protected override Size ArrangeOverride(Size finalSize)
    {
        var both = icon.Source is not null && image.Source is not null;
        icon.Arrange(both ? new Rect(0, 0, finalSize.Width * 0.55, finalSize.Height) : new Rect(finalSize));
        image.Arrange(both ? new Rect(finalSize.Width * 0.25, 0, finalSize.Width * 0.75, finalSize.Height) : new Rect(finalSize));
        // The symbol is square and centred in the art's box.
        var side = Math.Min(Math.Min(finalSize.Width, finalSize.Height), MaxSymbolSize);
        glyph.Arrange(new Rect((finalSize.Width - side) / 2, (finalSize.Height - side) / 2, side, side));
        return finalSize;
    }
}
