namespace CharacterPlanner.Simulation;

/// <summary>
/// The player's cover cycle (ADR 0009): each cycle peeks for <see cref="Peek"/> seconds, then stays fully behind cover for
/// <see cref="Cover"/> seconds. The fight starts at a peek.
/// </summary>
internal sealed class CoverCycle(double peek, double cover)
{
    public double Peek { get; } = peek;
    public double Cover { get; } = cover;
    private double Period { get; } = peek + cover;

    /// <summary>The cycle a time falls in, corrected so that the cycle's start is never after the time and its end always is.</summary>
    public double Index(double time)
    {
        var index = Math.Floor(time / Period);
        if ((index + 1) * Period <= time) { index++; }
        else if (index * Period > time) { index--; }
        return index;
    }

    /// <summary>When the cycle's cover phase starts.</summary>
    public double CoverStart(double index) => index * Period + Peek;

    /// <summary>When the cycle's peek starts.</summary>
    public double PeekStart(double index) => index * Period;

    /// <summary>Whether the player is fully behind cover at this time.</summary>
    public bool Behind(double time) => time >= CoverStart(Index(time));

    /// <summary>The share of a fight of this length spent behind cover: a fight that ends mid-cycle has less than a full cycle's share.</summary>
    public double BehindShare(double duration)
    {
        var index = Index(duration);
        return (index * Cover + Math.Max(0, duration - CoverStart(index))) / duration;
    }
}
