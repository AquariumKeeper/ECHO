using Avalonia;
using Avalonia.Controls;

namespace CharacterPlanner.Desktop.Views;

/// <summary>
/// One loadout group's tiles on the grid every group shares, as the Go app's tileGridLayout: the width splits into
/// two equal cells (one when two tiles of the minimum width don't fit), a group with <see cref="Columns"/> set fills
/// only that many a row, and every tile in the group is as tall as the tallest.
/// </summary>
public sealed class TileGridPanel : Panel
{
    public const int SharedColumns = 2;
    public static readonly Size TileSize = new(200, 92);
    public const double Gap = 8;

    public static readonly StyledProperty<int> ColumnsProperty = AvaloniaProperty.Register<TileGridPanel, int>(nameof(Columns));

    static TileGridPanel() => AffectsMeasure<TileGridPanel>(ColumnsProperty);

    /// <summary>The most tiles a row of this group holds; 0 for the shared count.</summary>
    public int Columns { get => GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }

    private (double Cell, int Columns) Grid(double width)
    {
        var shared = double.IsInfinity(width) ? SharedColumns : Math.Clamp((int)((width + Gap) / (TileSize.Width + Gap)), 1, SharedColumns);
        var cell = double.IsInfinity(width) ? TileSize.Width : (width - Gap * (shared - 1)) / shared;
        return (Math.Max(cell, TileSize.Width), Columns > 0 ? Math.Min(Columns, shared) : shared);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (cell, columns) = Grid(availableSize.Width);
        var height = TileSize.Height;
        foreach (var child in Children)
        {
            child.Measure(new Size(cell, double.PositiveInfinity));
            height = Math.Max(height, child.DesiredSize.Height);
        }
        var rows = (Children.Count + columns - 1) / columns;
        var width = double.IsInfinity(availableSize.Width) ? cell * columns + Gap * (columns - 1) : availableSize.Width;
        return rows == 0 ? default : new Size(width, rows * height + (rows - 1) * Gap);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (cell, columns) = Grid(finalSize.Width);
        var height = Children.Count == 0 ? 0 : Math.Max(TileSize.Height, Children.Max(child => child.DesiredSize.Height));
        for (var i = 0; i < Children.Count; i++)
        {
            var (row, column) = (i / columns, i % columns);
            Children[i].Arrange(new Rect(column * (cell + Gap), row * (height + Gap), cell, height));
        }
        return finalSize;
    }
}
