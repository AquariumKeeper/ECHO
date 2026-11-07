namespace CharacterPlanner.Simulation;

/// <summary>One validated reading of a resolved weapon's timing, shared by the shot profile and the rotation clock.</summary>
internal sealed record WeaponTiming(double Magazine, double Interval, double Reload, bool NoReload)
{
    internal static double Require(IReadOnlyDictionary<string, double> stats, string id)
    {
        if (!stats.TryGetValue(id, out var value) || !double.IsFinite(value) || value < 0)
        { throw new SimulationException("weapon-stat-invalid", $"Missing, negative or non-finite resolved stat {id}"); }
        return value;
    }

    internal static WeaponTiming Read(IReadOnlyDictionary<string, double> stats)
    {
        var rpm = Require(stats, "equipped_weapon_rpm");
        var magazine = Require(stats, "equipped_weapon_magazine_size");
        var reload = Require(stats, "equipped_weapon_reload_time");
        if (rpm <= 0 || magazine < 1 || magazine != Math.Floor(magazine))
        { throw new SimulationException("weapon-stat-invalid", "A positive RPM and a whole magazine are required"); }
        var interval = 60 / rpm;
        if (!double.IsFinite(interval) || interval <= 0) { throw new SimulationException("weapon-stat-invalid", "Invalid shot interval"); }
        return new(magazine, interval, reload, stats.GetValueOrDefault("weapon_no_reload") > 0);
    }
}
