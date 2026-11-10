using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace CharacterPlanner.Desktop.Views;

/// <summary>
/// A Prototype card's circuit pathways: traces run in from the card's right edge, turn once at 45°, and end in a pad,
/// with a via where each starts. The pattern comes from the seed, so a card keeps its own pattern; it keeps to the
/// card's right side, clear of its text, and draws nothing for clicks.
/// </summary>
public sealed class CircuitTraces : Control
{
    public static readonly StyledProperty<string> SeedProperty = AvaloniaProperty.Register<CircuitTraces, string>(nameof(Seed), "");
    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<CircuitTraces, IBrush?>(nameof(Stroke));
    /// <summary>How far left of the right edge traces may reach, as a share of the width.</summary>
    public static readonly StyledProperty<double> ReachProperty = AvaloniaProperty.Register<CircuitTraces, double>(nameof(Reach), 0.6);

    static CircuitTraces()
    {
        AffectsRender<CircuitTraces>(SeedProperty, StrokeProperty, ReachProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<CircuitTraces>(false);
    }

    public string Seed { get => GetValue(SeedProperty); set => SetValue(SeedProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double Reach { get => GetValue(ReachProperty); set => SetValue(ReachProperty, value); }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (width, height) = (Bounds.Width, Bounds.Height);
        if (Stroke is not { } stroke || width < 24 || height < 12) { return; }
        var pen = new Pen(stroke, 1.25, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        foreach (var trace in Traces(Seed, width, height, Reach))
        {
            var geometry = new StreamGeometry();
            using (var draw = geometry.Open())
            {
                draw.BeginFigure(trace[0], false);
                foreach (var point in trace.Skip(1)) { draw.LineTo(point); }
                draw.EndFigure(false);
            }
            context.DrawGeometry(null, pen, geometry);
            context.DrawEllipse(null, pen, trace[0], 2.5, 2.5);
            context.DrawEllipse(stroke, null, trace[^1], 2.25, 2.25);
        }
    }

    /// <summary>The traces' points for a card of this size: each starts at the right edge and ends at its pad.</summary>
    internal static IReadOnlyList<Point[]> Traces(string seed, double width, double height, double reach)
    {
        const double grid = 6;
        var random = new Random(StableHash(seed));
        var count = Math.Clamp((int)(height / 12), 2, 9);
        var left = width * (1 - Math.Clamp(reach, 0.1, 1));
        var rows = Enumerable.Range(1, Math.Max((int)(height / grid) - 1, 1)).Select(row => row * grid).OrderBy(_ => random.Next()).Take(count).ToList();
        var traces = new List<Point[]>(rows.Count);
        foreach (var y in rows)
        {
            var start = new Point(width - 3, y);
            var bend = Math.Max(start.X - Snap(random.Next(12, 60), grid), left + 2 * grid);
            var rise = Snap(random.Next(1, 4) * grid, grid) * (random.Next(2) == 0 ? -1 : 1);
            var turnedY = Math.Clamp(y + rise, grid, height - grid);
            var turnedX = Math.Max(bend - Math.Abs(turnedY - y), left + grid);
            var end = Math.Max(turnedX - Snap(random.Next(8, 70), grid), left);
            traces.Add([start, new(bend, y), new(turnedX, turnedY), new(end, turnedY)]);
        }
        return traces;
    }

    private static double Snap(double value, double grid) => Math.Round(value / grid) * grid;

    /// <summary>FNV-1a over the seed's characters: the same seed gives the same pattern in every run.</summary>
    private static int StableHash(string seed)
    {
        unchecked
        {
            var hash = (int)2166136261;
            foreach (var character in seed) { hash = (hash ^ character) * 16777619; }
            return hash;
        }
    }
}
