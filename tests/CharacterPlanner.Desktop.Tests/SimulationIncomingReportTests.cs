using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Simulation;

namespace CharacterPlanner.Desktop.Tests;

public sealed class SimulationIncomingReportTests
{
    [Fact]
    public void ExcludedIncomingFireHasNoReport()
    {
        var report = SimulationIncomingReport.From(null);
        Assert.Same(SimulationIncomingReport.Empty, report);
        Assert.False(report.HasData);
        Assert.Empty(report.Readings);
        Assert.Empty(report.Timings);
        Assert.Empty(report.Note);
        Assert.Empty(report.Unobserved);
    }

    [Fact]
    public void MeansAndFractionsKeepTheirOriginalDenominatorsAndUnits()
    {
        var report = SimulationIncomingReport.From(new(11, 12, 13, 14, 0.25, null, null)
        {
            BlockedShots = 16,
            ShieldAbsorbed = 17,
            ShieldBreaks = 18,
            ShieldDeployments = 19,
            ShieldRepaired = 20,
            ArmorRepaired = 21,
            HealthRepaired = 22,
            ShieldSwitches = 23,
            ArmorKitsUsed = 1.5,
            ArmorKitsCancelled = 0.25,
            ArmorKitRepaired = 24
        });
        Assert.True(report.HasData);
        Assert.Equal(new[] { "Enemy attempts / fight", "Enemy hits / fight", "Blocked shots / fight", "Incoming damage / fight",
            "Armor breaks / fight", "Downed fights", "Armor repaired / fight", "Health repaired / fight", "Armor kits used / fight",
            "Armor kits cut short by going down / fight", "Armor repaired by kits / fight", "Shield absorbed / fight",
            "Shield breaks / fight", "Shield deployments / fight", "Shield repaired / fight", "Forced shield swaps / fight" }, report.Readings.Select(row => row.Label));
        Assert.Equal(new[] { 11d, 12, 16, 13, 14 }.Select(value => value.ToString("N1")), report.Readings.Take(5).Select(row => row.Value));
        Assert.Equal(0.25.ToString("P1"), report.Readings[5].Value);
        Assert.Equal(new[] { 1.5.ToString("N2"), 0.25.ToString("N2"), 24d.ToString("N1") }, report.Readings.Skip(8).Take(3).Select(row => row.Value));
        Assert.Equal(new[] { 21d, 22 }.Select(value => value.ToString("N1")), report.Readings.Skip(6).Take(2).Select(row => row.Value));
        Assert.Equal(new[] { 17d, 18, 19, 20, 23 }.Select(value => value.ToString("N1")), report.Readings.Skip(11).Select(row => row.Value));
        Assert.Contains("means across all fights", report.Note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void EventTimesAreConditionalAndMissingEventsAreNeverReplacedWithZero(bool armorObserved, bool downObserved)
    {
        var armor = armorObserved ? new FightSummary(1, 2, 3, 4, 5, 6, 7) : null;
        var down = downObserved ? new FightSummary(11, 12, 13, 14, 15, 16, 17) : null;
        var report = SimulationIncomingReport.From(new(10, 5, 100, 0.5, 0.25, armor, down));
        Assert.Equal((armorObserved ? 1 : 0) + (downObserved ? 1 : 0), report.Timings.Length);
        if (armorObserved) { Assert.Same(armor, report.Timings.Single(row => row.Metric == "Time to first armor break (s)").Summary); }
        else { Assert.Contains("First armor break: not observed.", report.Unobserved, StringComparison.Ordinal); }
        if (downObserved) { Assert.Same(down, report.Timings.Single(row => row.Metric == "Time to down (s)").Summary); }
        else { Assert.Contains("Downing: not observed.", report.Unobserved, StringComparison.Ordinal); }
        if (armorObserved && downObserved) { Assert.Empty(report.Unobserved); }
        Assert.Contains("seconds from fight start", report.Note, StringComparison.Ordinal);
        Assert.Contains("other fights are excluded from timing summaries", report.Note, StringComparison.Ordinal);
    }
}
