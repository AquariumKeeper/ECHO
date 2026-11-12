using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Simulation;

namespace CharacterPlanner.Desktop.Tests;

public sealed class SimulationDistributionsTests
{
    private static DummyFightResult Fight(SimulationMode mode = SimulationMode.Random, int iterations = 500) =>
        new("test", mode, iterations, 10, 1, new(100, 20, 50, 60, 90, 140, 150), 0, 0, [], [], false, [], []);

    [Fact]
    public void MissingResultHasNoDistributionOrSampleNote()
    {
        var report = SimulationDistributions.From(null);
        Assert.Same(SimulationDistributions.Empty, report);
        Assert.False(report.HasRows);
        Assert.Empty(report.Note);
    }

    [Fact]
    public void ExistingEngineSummariesAreKeptWithoutReaveragingOrInventingTotal()
    {
        var fight = Fight() with { SkillDps = new(new(10, 2, 5, 6, 9, 14, 15), new(0, 0, 0, 0, 0, 0, 0), new(0, 0, 0, 0, 0, 0, 0)) };
        var report = SimulationDistributions.From(fight);
        Assert.True(report.HasRows);
        Assert.Equal(["Weapon DPS", "Skill DPS", "Skill impact DPS", "Skill continuous-window DPS"], report.Rows.Select(row => row.Metric));
        Assert.Same(fight.WeaponDps, report.Rows[0].Summary);
        Assert.Same(fight.SkillDps.Total, report.Rows[1].Summary);
        Assert.DoesNotContain(report.Rows, row => row.Metric == "Total DPS");
        var total = new FightSummary(110, 22, 55, 66, 99, 154, 165);
        var withTotal = SimulationDistributions.From(fight with { TotalDps = total });
        Assert.Same(total, withTotal.Rows[4].Summary);
        var onlyWeapon = SimulationDistributions.From(Fight());
        Assert.Equal("Weapon DPS", Assert.Single(onlyWeapon.Rows).Metric);
    }

    [Fact]
    public void BurnAndBleedDpsIsShownBeforeTotalDps()
    {
        var status = new FightSummary(140, 0, 140, 140, 140, 140, 140);
        var fight = Fight() with { StatusDps = status, TotalDps = new FightSummary(150, 0, 150, 150, 150, 150, 150) };
        var report = SimulationDistributions.From(fight);
        Assert.Equal(["Weapon DPS", "Burn and bleed DPS", "Total DPS"], report.Rows.Select(row => row.Metric));
        Assert.Same(status, report.Rows[1].Summary);
        Assert.Contains("it is included in total DPS", report.Note, StringComparison.Ordinal);
        Assert.Contains("Component percentiles are not additive", report.Note, StringComparison.Ordinal);
        Assert.DoesNotContain("not additive", SimulationDistributions.From(Fight()).Note, StringComparison.Ordinal);
        Assert.Contains("Burn and bleed DPS: 140", SimulationPresentation.Summary(fight), StringComparison.Ordinal);
    }

    [Fact]
    public void TimeToClearIsSummarisedWhenAskedFor()
    {
        // P10: the time to kill the asked number of targets, and how many fights got there.
        var clear = new FightSummary(12.5, 1, 11, 11, 12, 14, 15);
        Assert.Contains("Time to clear, in fights that cleared: 12.5 s (median 12.0 s) · Cleared: 80.0% of fights",
            SimulationPresentation.Summary(Fight() with { TimeToClear = clear, ClearedFraction = 0.8 }), StringComparison.InvariantCulture);
        Assert.Contains("Time to clear: not reached in any fight", SimulationPresentation.Summary(Fight() with { ClearedFraction = 0 }), StringComparison.Ordinal);
        Assert.DoesNotContain("Time to clear", SimulationPresentation.Summary(Fight()), StringComparison.Ordinal);
        // P15: each fight's average time to kill, its spread, and the share of fights with a kill when some had none.
        Assert.Contains("Time to kill (fight average): 1.50 s (fights' averages 1.20 to 1.90 s) · Fights with a kill: 75.0%",
            SimulationPresentation.Summary(Fight() with { TimeToKill = new FightSummary(1.5, 0.2, 1.1, 1.2, 1.5, 1.9, 2), KilledFraction = 0.75 }), StringComparison.InvariantCulture);
        // One value (an average fight) shows no range; every fight killing shows no share.
        Assert.Contains("Time to kill (fight average): 1.50 s\n",
            SimulationPresentation.Summary(Fight() with { TimeToKill = new FightSummary(1.5, 0, 1.5, 1.5, 1.5, 1.5, 1.5), KilledFraction = 1 }) + "\n", StringComparison.InvariantCulture);
        Assert.Contains("Time to kill (fight average): no target died in any fight", SimulationPresentation.Summary(Fight() with { KilledFraction = 0 }), StringComparison.Ordinal);
        Assert.DoesNotContain("Time to kill", SimulationPresentation.Summary(Fight()), StringComparison.Ordinal);
    }

    [Fact]
    public void TalentDebuffDpsHasItsOwnRowAfterBurnAndBleed()
    {
        // Toxic Delivery's damage over time (R38) isn't a burn or bleed, so it is shown and explained separately.
        var talent = new FightSummary(80, 0, 80, 80, 80, 80, 80);
        var fight = Fight() with { StatusDps = new FightSummary(140, 0, 140, 140, 140, 140, 140), TalentDps = talent, TotalDps = new FightSummary(320, 0, 320, 320, 320, 320, 320) };
        var report = SimulationDistributions.From(fight);
        Assert.Equal(["Weapon DPS", "Burn and bleed DPS", "Talent debuff DPS", "Total DPS"], report.Rows.Select(row => row.Metric));
        Assert.Same(talent, report.Rows[2].Summary);
        Assert.Contains("not in burn and bleed DPS", report.Note, StringComparison.Ordinal);
        Assert.Contains("Talent debuff DPS: 80", SimulationPresentation.Summary(fight), StringComparison.Ordinal);
        Assert.DoesNotContain("Talent debuff", SimulationDistributions.From(Fight()).Note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SkillComponentStatisticsAreDisplayedWithoutSummingTheirPercentiles(bool incoming)
    {
        var impact = new FightSummary(1, 2, 0, 0, 1, 10, 20);
        var overTime = new FightSummary(2, 3, 0, 0, 2, 20, 30);
        var skill = new FightSummary(3, 4, 0, 0, 3, 25, 40);
        var fight = Fight() with
        {
            SkillDps = new(skill, impact, overTime),
            Incoming = incoming ? new(0, 0, 0, 0, 0, null, null) : null
        };
        var report = SimulationDistributions.From(fight);
        Assert.Equal(["Weapon DPS", "Skill DPS", "Skill impact DPS", "Skill continuous-window DPS"], report.Rows.Select(row => row.Metric));
        Assert.Same(skill, report.Rows[1].Summary);
        Assert.Same(impact, report.Rows[2].Summary);
        Assert.Same(overTime, report.Rows[3].Summary);
        Assert.Equal(25, report.Rows[1].Summary.P95);
        Assert.Contains("Component percentiles are not additive", report.Note, StringComparison.Ordinal);
        Assert.Contains("Burn and bleed ticks are not modeled for this build or pack", report.Note, StringComparison.Ordinal);
        var text = SimulationPresentation.Summary(fight);
        foreach (var row in report.Rows.Skip(1))
        { Assert.Contains(row.Metric + ":", text, StringComparison.Ordinal); }
        Assert.Contains("Skill impact DPS:", SimulationPresentation.Summary(fight), StringComparison.Ordinal);
        Assert.Contains("Skill continuous-window DPS:", SimulationPresentation.Summary(fight), StringComparison.Ordinal);
        // ADR 0014: a line per held slot, in the pack's order, named where the caller can; none without a "Use when".
        Assert.DoesNotContain("uses/fight", text, StringComparison.Ordinal);
        var held = fight with { SkillUses = ImmutableDictionary.CreateRange(StringComparer.Ordinal, [new KeyValuePair<string, SkillSlotUse>("b", new(2, 4.25)), new("a", new(1.5, 0))]) };
        var heldText = SimulationPresentation.Summary(held, [("b", "Right"), ("a", "Left")]);
        Assert.Contains(string.Create(CultureInfo.CurrentCulture, $"Right uses/fight: {2:N2} · Held while ready: {4.25:N1} s/fight\nLeft uses/fight: {1.5:N2} · Held while ready: {0:N1} s/fight"), heldText, StringComparison.Ordinal);
        Assert.Contains(string.Create(CultureInfo.CurrentCulture, $"\na uses/fight: {1.5:N2}"), SimulationPresentation.Summary(held, [("b", "Right")]), StringComparison.Ordinal);
        // A slot named twice gets one line.
        Assert.Single(SimulationPresentation.Summary(held, [("b", "Right"), ("b", "Right"), ("a", "Left")]).Split('\n'), line => line.StartsWith("Right uses/fight", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(SimulationMode.Average, 1, "one deterministic fight")]
    [InlineData(SimulationMode.Random, 1, "One random fight")]
    [InlineData(SimulationMode.Random, 500, "not confidence intervals")]
    public void NotesDistinguishSamplingModesAndExplainTheDenominator(SimulationMode mode, int count, string expected)
    {
        var report = SimulationDistributions.From(Fight(mode, count));
        Assert.Contains(expected, report.Note, StringComparison.Ordinal);
        Assert.Contains("full requested fight duration", report.Note, StringComparison.Ordinal);
        Assert.DoesNotContain("going down", report.Note, StringComparison.Ordinal);
        var incoming = SimulationDistributions.From(Fight(mode, count) with { Incoming = new(0, 0, 0, 0, 0, null, null) });
        Assert.Contains("including time after going down", incoming.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void AllSevenReadingsUseTheirOwnSummaryFieldAndCurrentCulture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
            var row = SimulationDistributions.From(Fight()).Rows[0];
            Assert.Equal(["100,0", "20,0", "50,0", "60,0", "90,0", "140,0", "150,0"],
                new[] { row.Mean, row.StdDev, row.Minimum, row.P5, row.Median, row.P95, row.Maximum });
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }
}
