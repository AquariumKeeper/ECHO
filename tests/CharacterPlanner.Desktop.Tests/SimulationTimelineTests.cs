using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Desktop.Views;
using CharacterPlanner.Simulation;

namespace CharacterPlanner.Desktop.Tests;

public sealed class SimulationTimelineTests
{
    private static DummyFightResult Fight(double duration, params double[] damage) => new("test", SimulationMode.Random,
        500, duration, 1, new(0, 0, 0, 0, 0, 0, 0), 0, 0, [.. damage], [], false, [], []);

    [Fact]
    public void TinyTailImpactsDoNotFlattenTheFullIntervals()
    {
        foreach (var duration in new[] { 10.001, Math.BitIncrement(10d) })
        {
            var result = Fight(duration, [.. Enumerable.Repeat(100d, 11)]);
            var series = SimulationTimeline.From(result);
            Assert.Equal(10, series.Bins.Length);
            Assert.InRange(series.PeakDps, 100, 200);
            Assert.InRange(series.Bar(0, 600, 200).Height, 99, 200);
            Assert.Equal(result.Timeline.Sum(), series.Bins.Sum(bin => bin.Damage));
            Assert.Equal(duration, series.Bins[^1].End);
        }
    }

    [Fact]
    public void FractionalPointerEndpointsClampToTheFirstAndLastIntervals()
    {
        var series = SimulationTimeline.From(Fight(2.25, 1, 2, 3));
        Assert.Equal(1, series.IndexAtFraction(Math.BitIncrement(1d)));
        Assert.Equal(0, series.IndexAtFraction(-double.Epsilon));
        Assert.Equal(-1, series.IndexAtFraction(double.NaN));
    }

    [Fact]
    public void InvalidGeometryIdentifiesTheActualParameter()
    {
        var series = SimulationTimeline.From(Fight(1, 1));
        Assert.Equal("width", Assert.Throws<ArgumentOutOfRangeException>(() => series.Bar(0, double.NaN, 100)).ParamName);
        Assert.Equal("height", Assert.Throws<ArgumentOutOfRangeException>(() => series.Bar(0, 100, double.NaN)).ParamName);
    }

    [AvaloniaFact]
    public void HoverDoesNotReplaceSelectionAndDraggingCanSelectAnotherInterval()
    {
        var chart = new SimulationTimelineChart { Series = SimulationTimeline.From(Fight(3, 100, 100, 100)), Width = 640, Height = 240 };
        var window = new Window { Content = chart, Width = 640, Height = 240 };
        window.Show();
        try
        {
            chart.SelectedIndex = 2;
            var first = chart.TranslatePoint(new Point(100, 80), window)!.Value;
            var middle = chart.TranslatePoint(new Point(300, 80), window)!.Value;
            window.MouseMove(first, RawInputModifiers.None);
            Assert.Equal(2, chart.SelectedIndex);
            window.MouseDown(first, MouseButton.Left, RawInputModifiers.None);
            Assert.Equal(0, chart.SelectedIndex);
            window.MouseMove(middle, RawInputModifiers.LeftMouseButton);
            Assert.Equal(1, chart.SelectedIndex);
            window.MouseUp(middle, MouseButton.Left, RawInputModifiers.None);
            window.MouseMove(first, RawInputModifiers.None);
            Assert.Equal(1, chart.SelectedIndex);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void FractionalTailIsCombinedWithoutLosingDamageOrAveragingAgain()
    {
        var series = SimulationTimeline.From(Fight(2.25, 100, 0, 25));
        Assert.Equal(2, series.Bins.Length);
        Assert.Equal(new SimulationTimelineBin(1, 2.25, 25, 20), series.Bins[1]);
        Assert.Equal(series.Bins[1].Interval, series.Bins[1].ToString());
        Assert.Equal(100, series.PeakDps);
        Assert.Equal(125, series.Bins.Sum(bin => bin.Dps * (bin.End - bin.Start)));
        var first = series.Bar(0, 225, 100);
        var last = series.Bar(1, 225, 100);
        Assert.Equal(new Rect(0, 0, 100, 100), first);
        Assert.Equal(new Rect(100, 80, 125, 20), last);
    }

    [Fact]
    public void SubsecondFightsAndZeroDamageHaveFiniteGeometry()
    {
        var series = SimulationTimeline.From(Fight(0.25, 0));
        Assert.Equal(0, series.PeakDps);
        Assert.Equal(new Rect(0, 100, 200, 0), series.Bar(0, 200, 100));
        Assert.Equal(0, series.IndexAt(0.25));
        var damage = SimulationTimeline.From(Fight(0.25, 25));
        Assert.Equal(100, damage.Bins[0].Dps);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.999, 0)]
    [InlineData(1, 1)]
    [InlineData(2.25, 1)]
    [InlineData(-1, -1)]
    [InlineData(3, -1)]
    [InlineData(double.NaN, -1)]
    public void SelectionUsesTimeBoundariesAndIncludesTheFinalEndpoint(double time, int expected) =>
        Assert.Equal(expected, SimulationTimeline.From(Fight(2.25, 10, 20, 3)).IndexAt(time));

    [Fact]
    public void MissingOrInvalidTimelineDataIsUnavailableRatherThanPartiallyPlotted()
    {
        Assert.Same(SimulationTimeline.Empty, SimulationTimeline.From(Fight(2, 1)));
        Assert.Same(SimulationTimeline.Empty, SimulationTimeline.From(Fight(1, double.NaN)));
        Assert.Same(SimulationTimeline.Empty, SimulationTimeline.From(Fight(1, -1)));
        Assert.Same(SimulationTimeline.Empty, SimulationTimeline.From(Fight(0.1, double.MaxValue)));
        Assert.Same(SimulationTimeline.Empty, SimulationTimeline.From(Fight(double.NaN)));
        Assert.Same(SimulationTimeline.Empty, SimulationTimeline.From(Fight(601)));
        Assert.Equal(-1, SimulationTimeline.Empty.IndexAt(0));
    }

    [Fact]
    public void MaximumFightHasAtMostSixHundredSelectableBins()
    {
        var limit = SimulationLimits.MaximumFightDuration;
        var series = SimulationTimeline.From(Fight(limit, new double[(int)Math.Ceiling(limit)]));
        Assert.Equal((int)Math.Ceiling(limit), series.Bins.Length);
        Assert.Equal(series.Bins.Length - 1, series.IndexAt(limit));
        Assert.Null(SimulateViewModel.Validate(limit.ToString(System.Globalization.CultureInfo.InvariantCulture), "1", "1", "", System.Globalization.CultureInfo.InvariantCulture));
    }

    [AvaloniaFact]
    public void ChartKeyboardSelectionStaysWithinTheCurrentSeries()
    {
        var chart = new SimulationTimelineChart { Series = SimulationTimeline.From(Fight(2.25, 100, 0, 25)), Width = 640, Height = 240 };
        var window = new Window { Content = chart };
        window.Show();
        try
        {
            chart.Focus();
            window.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
            Assert.Equal(1, chart.SelectedIndex);
            window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            Assert.Equal(1, chart.SelectedIndex);
            window.KeyPress(Key.Home, RawInputModifiers.None, PhysicalKey.Home, null);
            Assert.Equal(0, chart.SelectedIndex);
            window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
            Assert.Equal(0, chart.SelectedIndex);
            chart.Series = SimulationTimeline.Empty;
            window.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
            Assert.Equal(-1, chart.SelectedIndex);
        }
        finally { window.Close(); }
    }
}
