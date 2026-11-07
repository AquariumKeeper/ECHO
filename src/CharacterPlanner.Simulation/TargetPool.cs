namespace CharacterPlanner.Simulation;

/// <summary>One target's pools; a shot's unused raw damage crosses armor into health, with separate resolved factors.</summary>
public sealed class TargetPool
{
    public double Armor { get; private set; }
    public double Health { get; private set; }
    public bool Dead => Health == 0 && Armor == 0;
    private readonly double armorFactor;
    private readonly double healthFactor;

    public TargetPool(double armor, double health, double armorFactor = 1, double healthFactor = 1)
    {
        if (!double.IsFinite(armor) || armor < 0 || !double.IsFinite(health) || health <= 0 ||
            !double.IsFinite(armorFactor) || armorFactor < 0 || !double.IsFinite(healthFactor) || healthFactor < 0)
        { throw new ArgumentOutOfRangeException(nameof(armor), "Target pools and factors must be finite and nonnegative, with positive health"); }
        Armor = armor;
        Health = health;
        this.armorFactor = armorFactor;
        this.healthFactor = healthFactor;
    }

    /// <summary>
    /// Remaining combined pools as a percentage of full pools. It multiplies before dividing,
    /// so exact tiers such as 57 of 100 are not floored one step low.
    /// </summary>
    public double RemainingPercent(double fullArmor, double fullHealth)
    {
        if (!double.IsFinite(fullArmor) || fullArmor < 0 || !double.IsFinite(fullHealth) || fullHealth <= 0)
        { throw new ArgumentOutOfRangeException(nameof(fullHealth), "Full pools must be finite, with positive health"); }
        var full = fullArmor + fullHealth;
        var left = Armor + Health;
        if (!double.IsFinite(full))
        {
            var scale = Math.Max(fullArmor, fullHealth);
            full = fullArmor / scale + fullHealth / scale;
            left = Armor / scale + Health / scale;
        }
        return Math.Clamp(left <= double.MaxValue / 100 ? left * 100 / full : left / full * 100, 0, 100);
    }

    /// <summary>Returns actual pool damage; overkill is discarded and never spills into another target.</summary>
    public double Apply(double raw) => Apply(raw, armorFactor, healthFactor);

    /// <summary>Apply the firing weapon's resolved factors without resetting the remaining pools.</summary>
    public double Apply(double raw, double armorFactor, double healthFactor)
    {
        if (!double.IsFinite(armorFactor) || armorFactor < 0 || !double.IsFinite(healthFactor) || healthFactor < 0)
        { throw new ArgumentOutOfRangeException(nameof(armorFactor)); }
        if (!double.IsFinite(raw) || raw < 0) { throw new ArgumentOutOfRangeException(nameof(raw)); }
        if (raw == 0 || Dead) { return 0; }
        var dealt = 0d;
        if (Armor > 0)
        {
            if (armorFactor == 0) { return 0; }
            var required = Armor / armorFactor;
            var used = Math.Min(raw, required);
            var damage = raw >= required ? Armor : Math.Min(Armor, used * armorFactor);
            // The division may overflow for a tiny factor; raw itself is still bounded and finite.
            Armor = Math.Max(0, Armor - damage);
            raw = Math.Max(0, raw - used);
            dealt = damage;
            if (Armor > 0) { return dealt; }
        }
        var healthDamage = Math.Min(Health, raw * healthFactor);
        Health = Math.Max(0, Health - healthDamage);
        var total = dealt + healthDamage;
        if (!double.IsFinite(total)) { throw new SimulationException("damage-overflow", "Target damage overflowed"); }
        return total;
    }
}
