namespace CharacterPlanner.Simulation;

/// <summary>
/// A talent's bonus on the hit after a headshot kill in one fight (ADR 0023: Headhunter). See <see cref="CharacterPlanner.Core.Packs.SimNextHitBonus"/>
/// for the rules; this holds the amount waiting and when it lapses.
/// </summary>
internal sealed class NextHitBonusState(double share, double window)
{
    private double amount;
    private double lapses;

    /// <summary>The amount waiting at a time, or 0.</summary>
    public double Waiting(double time) => time < lapses ? amount : 0;

    /// <summary>A swap: what waits comes down to the drawn weapon's cap, and doesn't go back up.</summary>
    public void Recap(double cap) => amount = Math.Min(amount, Math.Max(0, cap));

    /// <summary>A landed hit took the waiting amount.</summary>
    public void Spend() => amount = 0;

    /// <summary>A headshot kill: the killing pellet's damage as dealt, without any bonus it carried, sized and capped, replaces what waits.</summary>
    public void Arm(double killingDamage, double cap, double time)
    {
        amount = Math.Min(Math.Max(0, killingDamage) * share, Math.Max(0, cap));
        lapses = time + window;
    }
}
