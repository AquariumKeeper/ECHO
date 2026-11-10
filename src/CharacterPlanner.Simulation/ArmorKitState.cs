using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation;

/// <summary>
/// A fight's armor kits (ADR 0010): how many are left and the use under way. A kit is spent when its repair lands, so a fight
/// ending first, or a downed player, spends nothing. The fight counts a use a down cut short, from <see cref="Ends"/>.
/// </summary>
internal sealed class ArmorKitState(SimArmorKit kit, int carried, double atPercent)
{
    // Hits arrive on computed times, so "a whole extension later" allows for rounding.
    private const double Tolerance = 1e-9;
    private readonly int capacity = carried;
    private int left = carried;
    private double started;
    private double nextExtension;

    /// <summary>When the use under way ends, or null with none.</summary>
    public double? Ends { get; private set; }

    /// <summary>Whether the player would start a kit now, with armor at this share of its maximum.</summary>
    public bool Wanted(double armorFraction) => left > 0 && Ends is null && armorFraction * 100 < atPercent;

    /// <summary>Starts a use, returning when it ends.</summary>
    public double Start(double time)
    {
        started = nextExtension = time;
        Ends = time + kit.UseSeconds;
        return Ends.Value;
    }

    /// <summary>A hit during use lengthens it, at most once per extension and up to the longest use. Returns the new end, if it moved.</summary>
    public double? Hit(double at)
    {
        if (Ends is not { } ends || at < nextExtension - Tolerance) { return null; }
        var extended = Math.Min(ends + kit.ExtendSeconds, started + kit.MaxUseSeconds);
        if (extended <= ends) { return null; }
        nextExtension = at + kit.ExtendSeconds;
        Ends = extended;
        return extended;
    }

    /// <summary>Gives back one kit, never above the number carried (Efficient).</summary>
    public void Refund()
    {
        if (left < capacity) { left++; }
    }

    /// <summary>Spends the kit whose use just ended, returning the share of max armor it repairs.</summary>
    public double Finish()
    {
        Ends = null;
        left--;
        return kit.Repair;
    }
}
