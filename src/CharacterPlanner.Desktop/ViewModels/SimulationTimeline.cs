using System.Collections.Immutable;
using Avalonia;
using CharacterPlanner.Simulation;

namespace CharacterPlanner.Desktop.ViewModels;

public sealed record SimulationTimelineBin(double Start, double End, double Damage, double Dps)
{
    public string Interval => $"{Start:G6}–{End:G6} s";
    public override string ToString() => Interval;
    public string Details => $"{Interval} · Mean damage: {Damage:G8} · Mean DPS: {Dps:G8}";
}

/// <summary>Presentation of mean damage bins as rates; duration, including a partial last bin, is preserved.</summary>
public sealed class SimulationTimeline
{
    public static SimulationTimeline Empty { get; } = new(0, [], 0);
    private SimulationTimeline(double duration, ImmutableArray<SimulationTimelineBin> bins, double peakDps) =>
        (Duration, Bins, PeakDps) = (duration, bins, peakDps);
    public double Duration { get; }
    public ImmutableArray<SimulationTimelineBin> Bins { get; }
    public double PeakDps { get; }

    public static SimulationTimeline From(DummyFightResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!double.IsFinite(result.Duration) || result.Duration <= 0 || result.Duration > SimulationLimits.MaximumFightDuration ||
            result.Timeline.IsDefaultOrEmpty || result.Timeline.Length != (int)Math.Ceiling(result.Duration)) { return Empty; }
        if (result.Timeline.Any(damage => !double.IsFinite(damage) || damage < 0)) { return Empty; }
        var mergeTail = result.Timeline.Length > 1 && result.Duration != Math.Floor(result.Duration);
        var count = result.Timeline.Length - (mergeTail ? 1 : 0);
        var bins = ImmutableArray.CreateBuilder<SimulationTimelineBin>(count);
        double peak = 0;
        for (var index = 0; index < count; index++)
        {
            var damage = result.Timeline[index];
            var end = Math.Min(index + 1, result.Duration);
            if (mergeTail && index == count - 1)
            {
                damage += result.Timeline[^1];
                end = result.Duration;
            }
            var dps = damage / (end - index);
            if (!double.IsFinite(damage) || !double.IsFinite(dps)) { return Empty; }
            bins.Add(new(index, end, damage, dps));
            peak = Math.Max(peak, dps);
        }
        return new(result.Duration, bins.MoveToImmutable(), peak);
    }

    public int IndexAt(double time) => !double.IsFinite(time) || time < 0 || time > Duration || Bins.IsEmpty ? -1 :
        Math.Min((int)Math.Floor(time), Bins.Length - 1);

    /// <summary>Pointer fractions tolerate endpoint rounding while invalid nonfinite input selects nothing.</summary>
    public int IndexAtFraction(double fraction) => !double.IsFinite(fraction) ? -1 : IndexAt(Math.Clamp(fraction, 0, 1) * Duration);

    /// <summary>Bar bounds relative to the plot, with time-proportional width and peak-normalized height.</summary>
    public Rect Bar(int index, double width, double height)
    {
        if (!double.IsFinite(width) || width < 0) { throw new ArgumentOutOfRangeException(nameof(width)); }
        if (!double.IsFinite(height) || height < 0) { throw new ArgumentOutOfRangeException(nameof(height)); }
        var bin = Bins[index];
        var barHeight = PeakDps > 0 ? bin.Dps / PeakDps * height : 0;
        return new(bin.Start / Duration * width, height - barHeight, (bin.End - bin.Start) / Duration * width, barHeight);
    }
}
