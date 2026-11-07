using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using CharacterPlanner.Desktop.ViewModels;

namespace CharacterPlanner.Desktop.Views;

/// <summary>Bounded mean-DPS bars, selectable by click/drag or keyboard. A native interval picker provides the same values.</summary>
public sealed class SimulationTimelineChart : Control
{
    public static readonly StyledProperty<SimulationTimeline> SeriesProperty = AvaloniaProperty.Register<SimulationTimelineChart, SimulationTimeline>(nameof(Series), SimulationTimeline.Empty);
    public static readonly StyledProperty<int> SelectedIndexProperty = AvaloniaProperty.Register<SimulationTimelineChart, int>(nameof(SelectedIndex), -1,
        coerce: (owner, value) => ((SimulationTimelineChart)owner).Series is { Bins.IsEmpty: false } series ? Math.Clamp(value, 0, series.Bins.Length - 1) : -1);
    public static readonly StyledProperty<IBrush> BarBrushProperty = AvaloniaProperty.Register<SimulationTimelineChart, IBrush>(nameof(BarBrush), Brushes.Orange);
    public static readonly StyledProperty<IBrush> AxisBrushProperty = AvaloniaProperty.Register<SimulationTimelineChart, IBrush>(nameof(AxisBrush), Brushes.Gray);
    public static readonly StyledProperty<IBrush> SelectionBrushProperty = AvaloniaProperty.Register<SimulationTimelineChart, IBrush>(nameof(SelectionBrush), Brushes.Yellow);

    static SimulationTimelineChart() => AffectsRender<SimulationTimelineChart>(SeriesProperty, SelectedIndexProperty, BarBrushProperty, AxisBrushProperty, SelectionBrushProperty, IsFocusedProperty);
    public SimulationTimelineChart() => Focusable = true;
    public SimulationTimeline Series { get => GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public int SelectedIndex { get => GetValue(SelectedIndexProperty); set => SetCurrentValue(SelectedIndexProperty, value); }
    public IBrush BarBrush { get => GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }
    public IBrush AxisBrush { get => GetValue(AxisBrushProperty); set => SetValue(AxisBrushProperty, value); }
    public IBrush SelectionBrush { get => GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }
    private IBrush cachedAxisBrush = Brushes.Gray;
    private IBrush cachedSelectionBrush = Brushes.Yellow;
    private Pen axisPen = new(Brushes.Gray, 1);
    private Pen selectionPen = new(Brushes.Yellow, 2);
    private Pen focusPen = new(Brushes.Yellow, 1);
    private SimulationTimeline? labelSeries;
    private CultureInfo? labelCulture;
    private FormattedText[] labels = [];

    private void PrepareResources(SimulationTimeline series)
    {
        var axisChanged = !ReferenceEquals(cachedAxisBrush, AxisBrush);
        if (axisChanged) { cachedAxisBrush = AxisBrush; axisPen = new(AxisBrush, 1); }
        if (!ReferenceEquals(cachedSelectionBrush, SelectionBrush))
        {
            cachedSelectionBrush = SelectionBrush;
            selectionPen = new(SelectionBrush, 2);
            focusPen = new(SelectionBrush, 1);
        }
        var culture = CultureInfo.CurrentCulture;
        if (axisChanged || !ReferenceEquals(labelSeries, series) || !ReferenceEquals(labelCulture, culture))
        {
            labelSeries = series;
            labelCulture = culture;
            string[] text = ["Mean DPS", series.PeakDps.ToString("G3", culture), "0", "0 s", $"{series.Duration:G6} s", "No damage dealt"];
            labels = [.. text.Select(value => new FormattedText(value, culture, FlowDirection.LeftToRight, Typeface.Default, 12, AxisBrush))];
        }
    }
    private Rect Plot => new(72, 22, Math.Max(0, Bounds.Width - 88), Math.Max(0, Bounds.Height - 54));

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SeriesProperty)
        {
            labelSeries = null;
            labels = [];
            SelectedIndex = Series is { Bins.IsEmpty: false } series ? Math.Clamp(SelectedIndex, 0, series.Bins.Length - 1) : -1;
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var series = Series;
        var plot = Plot;
        if (series is null || series.Bins.IsEmpty || plot.Width <= 0 || plot.Height <= 0) { return; }
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        PrepareResources(series);
        var axis = axisPen;
        context.DrawLine(axis, plot.BottomLeft, plot.BottomRight);
        context.DrawLine(axis, plot.TopLeft, plot.BottomLeft);
        context.DrawText(labels[0], new Point(0, 0));
        context.DrawText(labels[1], new Point(0, plot.Top));
        context.DrawText(labels[2], new Point(50, plot.Bottom - 14));
        context.DrawText(labels[3], new Point(plot.Left, plot.Bottom + 8));
        context.DrawText(labels[4], new Point(Math.Max(plot.Left, plot.Right - 58), plot.Bottom + 8));
        if (series.PeakDps == 0) { context.DrawText(labels[5], new Point(plot.Left + 8, plot.Top + 8)); }
        for (var index = 0; index < series.Bins.Length; index++)
        {
            var bar = series.Bar(index, plot.Width, plot.Height).Translate(plot.Position);
            if (bar.Height > 0) { context.DrawRectangle(BarBrush, null, bar); }
        }
        if (SelectedIndex >= 0 && SelectedIndex < series.Bins.Length)
        {
            var bar = series.Bar(SelectedIndex, plot.Width, plot.Height).Translate(plot.Position);
            context.DrawRectangle(null, selectionPen, new Rect(bar.Left, plot.Top, bar.Width, plot.Height));
        }
        if (IsFocused) { context.DrawRectangle(null, focusPen, new Rect(1, 1, Math.Max(0, Bounds.Width - 2), Math.Max(0, Bounds.Height - 2))); }
    }

    private void Select(Point point)
    {
        if (Series is { } series && Plot.Width > 0 && Plot.Contains(point))
        {
            var index = series.IndexAtFraction((point.X - Plot.Left) / Plot.Width);
            if (index >= 0) { SelectedIndex = index; }
        }
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { Select(e.GetPosition(this)); }
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        { Focus(); e.Pointer.Capture(this); Select(e.GetPosition(this)); e.Handled = true; }
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton == MouseButton.Left) { e.Pointer.Capture(null); }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Series is not { Bins.IsEmpty: false } series) { return; }
        var next = e.Key switch { Key.Left => Math.Max(0, SelectedIndex - 1), Key.Right => Math.Min(series.Bins.Length - 1, SelectedIndex + 1), Key.Home => 0, Key.End => series.Bins.Length - 1, _ => -1 };
        if (next >= 0) { SelectedIndex = next; e.Handled = true; }
    }
}
