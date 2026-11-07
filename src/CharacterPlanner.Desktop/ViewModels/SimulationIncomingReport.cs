using System.Collections.Immutable;
using CharacterPlanner.Simulation;

namespace CharacterPlanner.Desktop.ViewModels;

public sealed record SimulationReading(string Label, string Value);

public sealed record SimulationIncomingReport(ImmutableArray<SimulationReading> Readings,
    ImmutableArray<SimulationSummaryRow> Timings, string Note, string Unobserved)
{
    public static SimulationIncomingReport Empty { get; } = new([], [], "", "");
    public bool HasData => !Readings.IsEmpty;

    public static SimulationIncomingReport From(IncomingFightResult? incoming)
    {
        if (incoming is null) { return Empty; }
        ImmutableArray<SimulationReading> readings = [
            new("Enemy attempts / fight", incoming.Attempts.ToString("N1")),
            new("Enemy hits / fight", incoming.Hits.ToString("N1")),
            new("Blocked shots / fight", incoming.BlockedShots.ToString("N1")),
            new("Incoming damage / fight", incoming.DamageTaken.ToString("N1")),
            new("Armor breaks / fight", incoming.ArmorBreaks.ToString("N1")),
            new("Downed fights", incoming.DownedFraction.ToString("P1")),
            new("Armor repaired / fight", incoming.ArmorRepaired.ToString("N1")),
            new("Health repaired / fight", incoming.HealthRepaired.ToString("N1")),
            new("Armor kits used / fight", incoming.ArmorKitsUsed.ToString("N2")),
            new("Armor kits cut short by going down / fight", incoming.ArmorKitsCancelled.ToString("N2")),
            new("Armor repaired by kits / fight", incoming.ArmorKitRepaired.ToString("N1")),
            new("Shield absorbed / fight", incoming.ShieldAbsorbed.ToString("N1")),
            new("Shield breaks / fight", incoming.ShieldBreaks.ToString("N1")),
            new("Shield deployments / fight", incoming.ShieldDeployments.ToString("N1")),
            new("Shield repaired / fight", incoming.ShieldRepaired.ToString("N1")),
            new("Forced shield swaps / fight", incoming.ShieldSwitches.ToString("N1"))];
        var timings = ImmutableArray.CreateBuilder<SimulationSummaryRow>(2);
        var missing = new List<string>(2);
        if (incoming.TimeToFirstArmorBreak is { } armor) { timings.Add(new("Time to first armor break (s)", armor)); }
        else { missing.Add("First armor break: not observed."); }
        if (incoming.TimeToDown is { } down) { timings.Add(new("Time to down (s)", down)); }
        else { missing.Add("Downing: not observed."); }
        return new(readings, timings.ToImmutable(),
            "Counts, damage and actual repairs are means across all fights. Downed fights is the fraction that went down. Zero shield totals can mean no shield was included, and zero kit totals that no kits were set. Times are seconds from fight start, only among fights where that event occurred; other fights are excluded from timing summaries.",
            string.Join(Environment.NewLine, missing));
    }
}
