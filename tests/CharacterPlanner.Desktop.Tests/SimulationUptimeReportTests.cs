using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Core.Packs;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Simulation;

namespace CharacterPlanner.Desktop.Tests;

public sealed class SimulationUptimeReportTests
{
    [Fact]
    public void ResultVisibilityDoesNotDependOnNoteText()
    {
        var report = SimulationUptimeReport.From(Fight(), new()) with { Note = "" };
        Assert.True(report.HasResult);
        Assert.NotEmpty(report.EmptyMessage);
    }

    private static DummyFightResult Fight() => new("test", SimulationMode.Random, 500, 10, 1,
        new(0, 0, 0, 0, 0, 0, 0), 0, 0, [], [], false, [], []);

    [Fact]
    public void MissingResultsAndUnmeasuredStatesHaveDistinctPresentation()
    {
        var absent = SimulationUptimeReport.From(null, new());
        Assert.Same(SimulationUptimeReport.Empty, absent);
        Assert.False(absent.HasResult);
        Assert.Empty(absent.EmptyMessage);
        var unmeasured = SimulationUptimeReport.From(Fight(), new());
        Assert.True(unmeasured.HasResult);
        Assert.Empty(unmeasured.Sections);
        Assert.Contains("No state measurements", unmeasured.EmptyMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusOverlapAndStackMeansAreNotRenormalizedAndMeasuredZerosRemain()
    {
        var fight = Fight() with
        {
            TargetStatusUptime = ImmutableDictionary<string, StatusUptime>.Empty
                .Add("any", new(9, 0.9)).Add("burning", new(8, 0.8)).Add("bleeding", new(7, 0.7)).Add("shocked", new(0, 0)),
            TimedTriggerUptime = ImmutableDictionary<string, TimedTriggerUptime>.Empty
                .Add("ready", new(8, 0.8, null)).Add("stacks", new(2, 0.2, 1.5))
        };
        var report = SimulationUptimeReport.From(fight, new());
        Assert.Equal(["Target statuses", "Timed conditions and stacks"], report.Sections.Select(section => section.Name));
        var statuses = report.Sections[0].Rows;
        Assert.Equal(new SimulationUptimeRow("Any status", "", 9, 0.9), statuses[0]);
        Assert.Contains(new SimulationUptimeRow("Shocked", "", 0, 0), statuses);
        Assert.Equal(1.5, statuses.Where(row => row.Name != "Any status").Sum(row => row.Fraction));
        Assert.Equal(new SimulationUptimeRow("Ready", "", 8, 0.8), report.Sections[1].Rows[0]);
        Assert.Equal(new SimulationUptimeRow("Stacks", "", 2, 0.2, 1.5), report.Sections[1].Rows[1]);
        Assert.Contains("full requested fight duration", report.Note, StringComparison.Ordinal);
        Assert.Contains("Measurement stops at downing", report.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void PhaseValuesSortNumericallyAndUsePackNamesAndOptionLabels()
    {
        var values = ImmutableDictionary<string, PhaseUptime>.Empty
            .Add("100", new(0, 0)).Add("2", new(1, 0.1)).Add("10", new(3, 0.3)).Add("not_visited", new(0, 0));
        var fight = Fight() with
        {
            MagazinePhaseUptime = ImmutableDictionary<string, ImmutableDictionary<string, PhaseUptime>>.Empty.Add("charge", values),
            CyclePhaseUptime = ImmutableDictionary<string, ImmutableDictionary<string, PhaseUptime>>.Empty.Add("unknown_phase", values)
        };
        var pack = new GamePack
        {
            NumericContextInputs = [new() { ID = "charge", Name = "Charge level", Options = [new(2, "Low"), new(10, "High")] }]
        };
        var report = SimulationUptimeReport.From(fight, pack);
        Assert.Equal(["Magazine readings", "Cycle readings"], report.Sections.Select(section => section.Name));
        var rows = report.Sections[0].Rows;
        Assert.Equal(["Low", "High", "100", "Not visited"], rows.Select(row => row.Value));
        Assert.All(rows, row => Assert.Equal("Charge level", row.Name));
        Assert.Equal(new SimulationUptimeRow("Charge level", "High", 3, 0.3), rows[1]);
        Assert.Equal("Charge level · High", rows[1].Label);
        Assert.All(report.Sections[1].Rows, row => Assert.Equal("Unknown phase", row.Name));
        Assert.Empty(report.EmptyMessage);
    }

    [Fact]
    public void SelectorNamesOverrideFallbackAndRowsStayInStableKeyOrder()
    {
        var readings = ImmutableDictionary<string, TimedTriggerUptime>.Empty.Add("z_key", new(0, 0, null)).Add("a_key", new(1, 0.1, null));
        var pack = new GamePack { Selectors = [new() { ID = "a_key", Name = "Ready to repair" }, new() { ID = "z_key", Name = "z_key" }] };
        var report = SimulationUptimeReport.From(Fight() with { TimedTriggerUptime = readings }, pack);
        Assert.Equal(["Ready to repair", "Z key"], Assert.Single(report.Sections).Rows.Select(row => row.Label));
    }

    [Fact]
    public void EachWeaponsCopyOfATalentIsLabeledWithItsSlot()
    {
        var readings = ImmutableDictionary<string, TimedTriggerUptime>.Empty.Add("a_key@primary_weapon", new(2, 0.2, null))
            .Add("a_key@secondary_weapon", new(1, 0.1, null)).Add("b_key@unknown", new(0, 0, null));
        var pack = new GamePack
        {
            Selectors = [new() { ID = "a_key", Name = "Ready to repair" }],
            Slots = [new() { ID = "primary_weapon", Name = "Primary", Weapon = true }, new() { ID = "secondary_weapon", Name = "Secondary", Weapon = true }]
        };
        var report = SimulationUptimeReport.From(Fight() with { TimedTriggerUptime = readings }, pack);
        Assert.Equal(["Ready to repair (Primary)", "Ready to repair (Secondary)", "B key@unknown"], Assert.Single(report.Sections).Rows.Select(row => row.Label));
    }

    [Fact]
    public void LocalizedReadingsDistinguishZeroStacksFromNotApplicable()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var row = new SimulationUptimeRow("Stacks", "", 1.25, 0.125, 0);
            Assert.Equal("1,25", row.Time);
            Assert.Equal(0.125.ToString("P1"), row.Share);
            Assert.Equal("0,00", row.Stacks);
            Assert.Equal("—", (row with { MeanStacks = null }).Stacks);
            var phases = ImmutableDictionary<string, ImmutableDictionary<string, PhaseUptime>>.Empty
                .Add("value", ImmutableDictionary<string, PhaseUptime>.Empty.Add("1.5", new(1.25, 0.125)));
            var report = SimulationUptimeReport.From(Fight() with { CyclePhaseUptime = phases }, new());
            Assert.Equal("1,5", report.Sections[0].Rows[0].Value);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
