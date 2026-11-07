using CharacterPlanner.Core.Engine;

namespace CharacterPlanner.Simulation;

/// <summary>
/// Weapon damage falloff at a fixed fight distance (R7's in-game readings, R42's source review): full damage out to optimal
/// range, linear down to long range, then the weapon's floor out to maximum range, and none beyond it. Optimal and long
/// range include optimal-range bonuses; maximum range doesn't. It applies only when the pack
/// declares <see cref="FloorStat"/> (R42); without it a fight distance doesn't change weapon damage.
/// </summary>
internal static class WeaponFalloff
{
    /// <summary>A fixed weapon stat: the share of full damage dealt from long range out to maximum range.</summary>
    internal const string FloorStat = "weapon_falloff_floor";

    /// <summary>Whether the pack declares the floor, so a fight distance applies falloff.</summary>
    internal static bool Declared(ResolvedBuild resolved) => resolved.Calculation.Stats.ContainsKey(FloorStat);

    /// <summary>The share of full damage the weapon deals at the distance; 1 without a distance or a declared floor.</summary>
    internal static double Factor(ResolvedBuild resolved, double? distance)
    {
        if (distance is not { } at || !Declared(resolved)) { return 1; }
        var stats = resolved.Calculation.Stats;
        var floor = stats[FloorStat];
        if (!double.IsFinite(floor) || floor is <= 0 or > 1)
        { throw new SimulationException("weapon-stat-invalid", "A weapon's falloff floor must be above 0 and at most 1"); }
        // Beyond maximum range a weapon deals no damage (the schema's myRangeMax). Range bonuses don't move it; 0 means the
        // weapon has no reviewed range data, so no cut-off applies.
        var maximum = stats.GetValueOrDefault("equipped_weapon_max_range");
        if (!double.IsFinite(maximum) || maximum < 0) { throw new SimulationException("weapon-stat-invalid", "A weapon's maximum range must be finite and not negative"); }
        if (maximum > 0 && at > maximum) { return 0; }
        // A floor of 1 is no falloff (Nemesis), which also stands for weapons without source data; their ranges may be unknown.
        if (floor == 1) { return 1; }
        var optimal = stats.GetValueOrDefault("equipped_weapon_optimal_range");
        var longRange = stats.GetValueOrDefault("equipped_weapon_long_range");
        if (!double.IsFinite(optimal) || !double.IsFinite(longRange) || optimal <= 0 || longRange < optimal)
        { throw new SimulationException("weapon-stat-invalid", "Falloff needs the weapon's optimal range above 0 and a long range at least as far"); }
        if (at <= optimal) { return 1; }
        if (at >= longRange) { return floor; }
        return 1 - (1 - floor) * (at - optimal) / (longRange - optimal);
    }
}
