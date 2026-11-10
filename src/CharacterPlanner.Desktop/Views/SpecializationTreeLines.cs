using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CharacterPlanner.Desktop.ViewModels;

namespace CharacterPlanner.Desktop.Views;

/// <summary>
/// The specialization tree's lines behind its cards: from the middle, a budget's box or a perk to what hangs off it,
/// lit in the accent colour once what it leads to is active.
/// </summary>
public sealed class SpecializationTreeLines : Control
{
    public static readonly StyledProperty<ImmutableArray<TreeEdge>> EdgesProperty =
        AvaloniaProperty.Register<SpecializationTreeLines, ImmutableArray<TreeEdge>>(nameof(Edges), []);

    static SpecializationTreeLines() => AffectsRender<SpecializationTreeLines>(EdgesProperty);

    public ImmutableArray<TreeEdge> Edges { get => GetValue(EdgesProperty); set => SetValue(EdgesProperty, value); }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var accent = Brush("DivisionAccent", Colors.DarkOrange);
        var border = Brush("DivisionBorder", Colors.DimGray);
        var lit = new Pen(accent, 4, lineCap: PenLineCap.Round);
        var unlit = new Pen(border, 2, lineCap: PenLineCap.Round);
        if (Edges.IsDefault) { return; }
        // Unlit lines first, so a lit path is never drawn over.
        foreach (var edge in Edges.Where(edge => !edge.Lit)) { context.DrawLine(unlit, edge.From, edge.To); }
        foreach (var edge in Edges.Where(edge => edge.Lit)) { context.DrawLine(lit, edge.From, edge.To); }
    }

    private IBrush Brush(string key, Color fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : new SolidColorBrush(fallback);
}
