namespace CharacterPlanner.Simulation;

/// <summary>
/// What one enemy holds through a fight (ADR 0011): its pools, the statuses on it and their ticks, the shock credit against its
/// fire, the pellets that landed on it, and the marks and stacks of rules that mean one enemy. What belongs to the player, such as
/// buffs, other stacks and armed counters, stays with the player's own state.
/// </summary>
internal sealed class FightEnemy
{
    private readonly TargetPool? pool;
    private readonly double fullArmor;
    private readonly double fullHealth;

    public FightEnemy(int place, int profile, double? distance, double fullArmor, double fullHealth, double arrivedAt, int attackers, IEnumerable<string> slots, StatusDamageState? dots)
    {
        (Place, Profile, Distance, ArrivedAt, this.fullArmor, this.fullHealth, Dots) = (place, profile, distance, arrivedAt, fullArmor, fullHealth, dots);
        pool = new(fullArmor, fullHealth);
        Shock = new(attackers);
        PelletHits = slots.ToDictionary(slot => slot, _ => 0, StringComparer.Ordinal);
    }

    private FightEnemy(FightEnemy from)
    {
        (Place, Profile, Distance, ArrivedAt) = (from.Place, from.Profile, from.Distance, from.ArrivedAt);
        Shock = new(1);
        PelletHits = from.PelletHits.Keys.ToDictionary(slot => slot, _ => 0, StringComparer.Ordinal);
    }

    /// <summary>
    /// Nobody, while the player moves from this enemy to another. It reads as dead, so a move runs as the wait between a kill and
    /// the next arrival does.
    /// </summary>
    public static FightEnemy Between(FightEnemy from) => new(from);

    /// <summary>This enemy's place in the fight's order.</summary>
    public int Place { get; }

    /// <summary>Its target profile in a sequence, or -1 without one.</summary>
    public int Profile { get; }

    public double? Distance { get; }

    /// <summary>When the player first reached it, which time to kill counts from.</summary>
    public double ArrivedAt { get; }

    public ShockFireState Shock { get; }
    public TargetStatuses Statuses { get; } = new();
    public EnemyMarks Marks { get; } = new();
    public StatusDamageState? Dots { get; }
    public Dictionary<string, int> PelletHits { get; }
    public double Armor => pool?.Armor ?? 0;
    public double Health => pool?.Health ?? 0;
    public bool Dead => pool?.Dead ?? true;
    public double Apply(double raw, double armorFactor, double healthFactor) => pool?.Apply(raw, armorFactor, healthFactor) ?? 0;
    public double RemainingPercent() => pool?.RemainingPercent(fullArmor, fullHealth) ?? 0;
}
