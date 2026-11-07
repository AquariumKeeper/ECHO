namespace CharacterPlanner.Simulation;

/// <summary>Distributes a shocked target's one-attacker share across aggregate enemy attempts.</summary>
/// <remarks>Credit belongs to the shocked enemy, which holds this, so it never carries to the next one.</remarks>
public sealed class ShockFireState
{
    private readonly int attackers;
    private int progress;

    public ShockFireState(int attackers)
    {
        if (attackers < 1) { throw new ArgumentOutOfRangeException(nameof(attackers)); }
        this.attackers = attackers;
    }

    /// <summary>Only shocked attempts add credit. Fractional credit persists between one target's shock windows.</summary>
    public bool Blocks(bool targetShocked)
    {
        if (!targetShocked) { return false; }
        if (++progress < attackers) { return false; }
        progress = 0;
        return true;
    }
}
