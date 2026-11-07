using System.Globalization;
using System.Collections;
using System.Collections.Immutable;
using CharacterPlanner.Core.Packs;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Simulation;

namespace CharacterPlanner.Desktop.Tests;

public sealed class SimulationEventReportTests
{
    [Fact]
    public void LargeCapturesStayLazyAndCacheUnknownSlotNames()
    {
        var events = ImmutableArray.CreateRange(Enumerable.Repeat(new DummyEvent(0, DummyEventKind.Shot, 1, "unresolved_slot"), 100000));
        var fight = Fight() with { FirstFightEvents = events };
        var pack = new GamePack();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var report = SimulationEventReport.From(fight, true, pack);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 65536, $"Projection allocated {allocated} bytes before any rows were requested");
        Assert.Equal(100000, report.Rows.Count);
        Assert.IsAssignableFrom<IList>(report.Rows);
        Assert.Same(report.Rows[0].Slot, report.Rows[99999].Slot);
        Assert.Equal("Unresolved slot", report.Rows[99999].Slot);
    }

    [Fact]
    public void CaptureVisibilityDoesNotDependOnNoteText()
    {
        var report = SimulationEventReport.From(Fight(), true, new()) with { Note = "" };
        Assert.True(report.HasCapture);
        Assert.NotEmpty(report.EmptyMessage);
    }

    private static DummyFightResult Fight(params DummyEvent[] events) => new("test", SimulationMode.Random, 500, 10, 1,
        new(0, 0, 0, 0, 0, 0, 0), 0, 0, [], [.. events], false, [], []);

    [Fact]
    public void MissingResultsAndDisabledCaptureDoNotShowAReport()
    {
        Assert.Same(SimulationEventReport.Empty, SimulationEventReport.From(null, true, new()));
        var disabled = SimulationEventReport.From(Fight(new DummyEvent(0, DummyEventKind.Shot, 10)), false, new());
        Assert.Same(SimulationEventReport.Empty, disabled);
        Assert.False(disabled.HasCapture);
        Assert.Empty(disabled.EmptyMessage);
    }

    [Fact]
    public void CapturedEmptyTraceIsExplicitRatherThanHidden()
    {
        var report = SimulationEventReport.From(Fight(), true, new());
        Assert.True(report.HasCapture);
        Assert.Empty(report.Rows);
        Assert.Contains("No first-fight events", report.EmptyMessage, StringComparison.Ordinal);
        Assert.Contains("First fight only", report.Note, StringComparison.Ordinal);
        Assert.Contains("interval ends", report.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void EventsKeepTheirOrderAndAmountsWithNamedAndUnknownSlots()
    {
        var fight = Fight(new DummyEvent(1, DummyEventKind.ArmorRepaired, 12, "skill_1"), new(1, DummyEventKind.Shot, 120, "primary_weapon"),
            new(1, DummyEventKind.Kill), new(2, (DummyEventKind)999, 0, "unknown_slot"));
        var pack = new GamePack
        {
            Slots = [new() { ID = "primary_weapon", Name = "Primary weapon" }],
            ActiveSkillSlots = [new() { ID = "skill_1", Name = "First skill" }]
        };
        var report = SimulationEventReport.From(fight, true, pack);
        Assert.Equal(fight.FirstFightEvents, report.Rows.Select(row => row.Event));
        Assert.Equal(["First skill", "Primary weapon", "—", "Unknown slot"], report.Rows.Select(row => row.Slot));
        Assert.Equal("Armor Repaired", report.Rows[0].Kind);
        Assert.Equal("999", report.Rows[3].Kind);
        Assert.Equal(120, report.Rows[1].Event.Damage);
        Assert.Empty(report.EmptyMessage);
    }

    [Fact]
    public void TruncationIsReportedWithoutChangingAggregateResults()
    {
        var fight = Fight(new DummyEvent(0, DummyEventKind.Shot, 10)) with { TraceTruncated = true };
        var report = SimulationEventReport.From(fight, true, new());
        Assert.Single(report.Rows);
        Assert.Contains("Capture was truncated", report.Note, StringComparison.Ordinal);
        Assert.Contains("summaries still cover the full run", report.Note, StringComparison.Ordinal);
        Assert.DoesNotContain("Capture was truncated", SimulationEventReport.From(fight with { TraceTruncated = false }, true, new()).Note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DummyEventKind.Shot, 0, true)]
    [InlineData(DummyEventKind.ArmorRepaired, 12.5, true)]
    [InlineData(DummyEventKind.ShieldHit, 4.25, true)]
    [InlineData(DummyEventKind.PreservationStarted, 25, true)]
    [InlineData(DummyEventKind.BurnTick, 33181, true)]
    [InlineData(DummyEventKind.BleedTick, 29863, true)]
    [InlineData(DummyEventKind.ReloadStarted, 0, false)]
    public void AmountsDistinguishAZeroDamageEventFromANonAmountEvent(DummyEventKind kind, double amount, bool applicable)
    {
        var row = new SimulationEventRow(new(0, kind, amount), "—");
        Assert.Equal(applicable ? amount.ToString("G9") : "—", row.Amount);
    }

    [Fact]
    public void StatusTicksHaveReadableLabels()
    {
        Assert.Equal("Burn Tick", new SimulationEventRow(new(1.5, DummyEventKind.BurnTick, 100), "—").Kind);
        Assert.Equal("Bleed Tick", new SimulationEventRow(new(1.5, DummyEventKind.BleedTick, 90), "—").Kind);
    }

    [Fact]
    public void TimesAndAmountsUseCurrentCultureWithoutMillisecondRounding()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var row = new SimulationEventRow(new(0.000125, DummyEventKind.Shot, 1.25), "—");
            Assert.Equal("0,000125", row.Time);
            Assert.Equal("1,25", row.Amount);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
