namespace CharacterPlanner.Simulation;

public sealed record IncomingHit(double DamageTaken, double BonusArmorDamage, double ArmorDamage, double HealthDamage,
    bool ArmorBroken, bool Downed)
{
    public double ArmorRepaired { get; init; }
}
public sealed record IncomingRepair(double Armor, double Health);

/// <summary>
/// Player pools for incoming damage. Mitigation is supplied by Core before applying a hit. Bonus armor is the build's
/// static amount plus timed grants (temporary armor), all spent before armor: grants first, soonest to lapse first,
/// since the static amount never expires. A grant is lost at its expiry.
/// </summary>
public sealed class IncomingDamageState
{
    /// <summary>Static bonus armor plus every grant live at the last pool update (a hit, repair or regeneration).</summary>
    public double BonusArmor => staticBonus + timed.Sum(grant => grant.Amount);
    public double MaxArmor => maxArmor;
    public double Armor { get; private set; }
    public double Health { get; private set; }
    public bool Downed => Health == 0;
    public bool ArmorFull => Armor >= maxArmor;
    public double ArmorFraction => maxArmor == 0 ? 1 : Armor / maxArmor;
    public double? FirstArmorBreak { get; private set; }
    public double? DownAt { get; private set; }
    private double clock;
    private double staticBonus;
    // Timed bonus armor grants, ordered by expiry so the soonest to lapse is spent first.
    private readonly List<(double Amount, double Until)> timed = [];
    private readonly double maxArmor;
    private readonly double maxHealth;

    public IncomingDamageState(double armor, double bonusArmor, double health)
    {
        if (!double.IsFinite(armor) || armor < 0 || !double.IsFinite(bonusArmor) || bonusArmor < 0 ||
            !double.IsFinite(health) || health <= 0)
        { throw new ArgumentOutOfRangeException(nameof(armor), "Player pools must be finite and nonnegative, with positive health"); }
        Armor = armor;
        staticBonus = bonusArmor;
        Health = health;
        maxArmor = armor;
        maxHealth = health;
    }

    /// <summary>Spend a mitigated hit through bonus armor, armor, then health; discard overkill.</summary>
    public IncomingHit Apply(double damage, double time, double armorRepairOnBreak = 0)
    {
        if (!double.IsFinite(time) || time < clock) { throw new ArgumentOutOfRangeException(nameof(time), "Hit time must be finite and monotonic"); }
        if (!double.IsFinite(damage) || damage < 0) { throw new ArgumentOutOfRangeException(nameof(damage)); }
        if (!double.IsFinite(armorRepairOnBreak) || armorRepairOnBreak < 0) { throw new ArgumentOutOfRangeException(nameof(armorRepairOnBreak)); }
        timed.RemoveAll(grant => grant.Until <= time);
        var remaining = Downed ? 0 : damage;
        double bonusDamage = 0;
        for (var index = 0; index < timed.Count && remaining > 0; index++)
        {
            var spent = Math.Min(timed[index].Amount, remaining);
            timed[index] = (timed[index].Amount - spent, timed[index].Until);
            remaining -= spent;
            bonusDamage += spent;
        }
        timed.RemoveAll(grant => grant.Amount <= 0);
        var staticDamage = Math.Min(staticBonus, remaining);
        remaining -= staticDamage;
        bonusDamage += staticDamage;
        var armorDamage = Math.Min(Armor, remaining);
        remaining -= armorDamage;
        var healthDamage = Math.Min(Health, remaining);
        remaining -= healthDamage;
        var armorBroken = Armor > 0 && armorDamage == Armor;
        var downed = Health > 0 && healthDamage == Health;
        // Restored armor does not absorb the same hit's overflow into health, and a downed player receives none.
        var repaired = armorBroken && !downed ? Math.Min(maxArmor, armorRepairOnBreak) : 0;
        var taken = Downed ? 0 : damage - remaining;
        staticBonus -= staticDamage;
        Armor -= armorDamage;
        Armor += repaired;
        Health -= healthDamage;
        if (armorBroken) { FirstArmorBreak ??= time; }
        if (downed) { DownAt = time; }
        clock = time;
        return new(taken, bonusDamage, armorDamage, healthDamage, armorBroken, downed) { ArmorRepaired = repaired };
    }

    /// <summary>
    /// Grant temporary armor (bonus armor) until <paramref name="until"/>. A downed player receives none. It doesn't move the
    /// pool clock, so regeneration still integrates from the last update.
    /// </summary>
    public double GrantBonusArmor(double amount, double time, double until)
    {
        if (!double.IsFinite(time) || time < clock) { throw new ArgumentOutOfRangeException(nameof(time), "Grant time must be finite and monotonic"); }
        if (!double.IsFinite(amount) || amount < 0) { throw new ArgumentOutOfRangeException(nameof(amount)); }
        if (!double.IsFinite(until) || until <= time) { throw new SimulationException("time-not-advancing", "Temporary armor must last past its grant"); }
        if (Downed || amount == 0) { return 0; }
        timed.RemoveAll(grant => grant.Until <= time);
        var index = timed.FindIndex(grant => grant.Until > until);
        timed.Insert(index < 0 ? timed.Count : index, (amount, until));
        return amount;
    }

    /// <summary>Integrate a constant armor rate since the last pool update, capped before multiplication to avoid overflow.</summary>
    public IncomingRepair Regenerate(double armorPerSecond, double time)
    {
        if (!double.IsFinite(time) || time < clock) { throw new ArgumentOutOfRangeException(nameof(time)); }
        if (!double.IsFinite(armorPerSecond) || armorPerSecond < 0) { throw new ArgumentOutOfRangeException(nameof(armorPerSecond)); }
        var elapsed = time - clock;
        var missing = maxArmor - Armor;
        var amount = armorPerSecond == 0 || elapsed == 0 ? 0 :
            elapsed >= missing / armorPerSecond ? missing : armorPerSecond * elapsed;
        return Repair(amount, 0, time);
    }

    /// <summary>Repair a fraction of currently missing armor, including repeated per-pellet recovery.</summary>
    public IncomingRepair RepairMissingArmor(double fraction, double time)
    {
        if (!double.IsFinite(fraction) || fraction < 0 || fraction > 1) { throw new ArgumentOutOfRangeException(nameof(fraction)); }
        return Repair((maxArmor - Armor) * fraction, 0, time);
    }

    /// <summary>Immediate recovery capped to missing pools. It grants no bonus armor and cannot revive a downed player.</summary>
    public IncomingRepair Repair(double armorAmount, double healthAmount, double time)
    {
        if (!double.IsFinite(time) || time < clock) { throw new ArgumentOutOfRangeException(nameof(time), "Repair time must be finite and monotonic"); }
        if (!double.IsFinite(armorAmount) || armorAmount < 0 || !double.IsFinite(healthAmount) || healthAmount < 0)
        { throw new ArgumentOutOfRangeException(nameof(armorAmount), "Repair amounts must be finite and nonnegative"); }
        timed.RemoveAll(grant => grant.Until <= time);
        var armor = Downed ? 0 : Math.Min(armorAmount, maxArmor - Armor);
        var health = Downed ? 0 : Math.Min(healthAmount, maxHealth - Health);
        Armor += armor;
        Health += health;
        clock = time;
        return new(armor, health);
    }
}
