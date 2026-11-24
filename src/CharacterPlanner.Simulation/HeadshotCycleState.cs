using System.Collections.Immutable;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation;

/// <summary>What a counted headshot sets off (ADR 0021): the next-headshot bonus, the armor step and the magazine refill.</summary>
internal readonly record struct HeadshotCycleSteps(bool Bonus, bool Armor, bool Refill);

/// <summary>
/// A talent's consecutive-headshot cycle in one fight (ADR 0021: Headache, R126). See <see cref="SimHeadshotCycle"/> for the rules; this
/// keeps the count, the misses the allowance has left to forgive, and whether the next-headshot bonus is waiting.
/// </summary>
internal sealed class HeadshotCycleState(SimHeadshotCycle cycle, int allowance)
{
    private readonly int allowed = allowance;
    private int forgivable = allowance;

    public int Count { get; private set; }
    public bool BonusWaiting { get; private set; }

    /// <summary>Back to the start: no count, no bonus waiting, the allowance whole (a miss not forgiven, or a swap).</summary>
    public void Reset()
    {
        Count = 0;
        BonusWaiting = false;
        forgivable = allowed;
    }

    /// <summary>A headshot landed with any weapon: a waiting bonus has been used on it.</summary>
    public void HeadshotLanded() => BonusWaiting = false;

    /// <summary>A shot with the cycle's weapon: whether it landed a headshot, and whether that headshot killed. Returns the steps it sets off.</summary>
    public HeadshotCycleSteps Shot(bool headshot, bool killingHeadshot)
    {
        if (headshot && (Count < cycle.KillFrom || killingHeadshot))
        {
            Count++;
            var steps = Count switch
            {
                1 => new HeadshotCycleSteps(true, false, false),
                2 => new HeadshotCycleSteps(false, true, false),
                3 => new HeadshotCycleSteps(false, false, true),
                _ => new HeadshotCycleSteps(true, true, true)
            };
            if (steps.Bonus) { BonusWaiting = true; }
            return steps;
        }
        // A miss: forgiven while the allowance lasts, otherwise the cycle starts again.
        if (Count > 0)
        {
            if (forgivable > 0) { forgivable--; }
            else { Reset(); }
        }
        return default;
    }

    /// <summary>The cycle's key in a state: on while the bonus waits, otherwise off.</summary>
    public ImmutableDictionary<string, string> Context(ImmutableDictionary<string, string> state) =>
        BonusWaiting ? state.SetItem(cycle.Key, "true") : state.Remove(cycle.Key);
}
