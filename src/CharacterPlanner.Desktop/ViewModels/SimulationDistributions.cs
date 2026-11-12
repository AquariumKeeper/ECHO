using System.Collections.Immutable;
using CharacterPlanner.Simulation;

namespace CharacterPlanner.Desktop.ViewModels;

public sealed record SimulationDistributions(ImmutableArray<SimulationSummaryRow> Rows, string Note)
{
    public static SimulationDistributions Empty { get; } = new([], "");
    public bool HasRows => !Rows.IsEmpty;

    public static SimulationDistributions From(DummyFightResult? result)
    {
        if (result is null) { return Empty; }
        var rows = ImmutableArray.CreateBuilder<SimulationSummaryRow>(7);
        rows.Add(new("Weapon DPS", result.WeaponDps));
        if (result.SkillDps is { } skill)
        {
            rows.Add(new(SimulationMetricLabels.SkillTotal, skill.Total));
            rows.Add(new(SimulationMetricLabels.SkillImpact, skill.Impact));
            rows.Add(new(SimulationMetricLabels.SkillContinuousWindow, skill.ContinuousWindow));
        }
        if (result.StatusDps is { } status) { rows.Add(new(SimulationMetricLabels.Status, status)); }
        if (result.TalentDps is { } talent) { rows.Add(new(SimulationMetricLabels.Talent, talent)); }
        if (result.TotalDps is { } total) { rows.Add(new("Total DPS", total)); }
        var sample = result.Mode == SimulationMode.Average
            ? "Average mode uses one deterministic fight with fixed assumptions; it does not measure random variation."
            : result.Iterations == 1
                ? "One random fight; more fights are needed to measure variation."
                : $"Across {result.Iterations:N0} random fights. Percentiles describe the simulated fights, not confidence intervals.";
        return new(rows.ToImmutable(), sample + " Each value is damage per full requested fight duration" +
            (result.Incoming is null ? "." : ", including time after going down.") + " All values are DPS." +
            (result.SkillDps is null ? "" : " Continuous-window DPS includes modeled skill fire such as Assault Turret; it is not a status DoT category.") +
            (result.StatusDps is null ? " Burn and bleed ticks are not modeled for this build or pack." : " Burn and bleed DPS counts status ticks on targets; it is included in total DPS.") +
            (result.TalentDps is null ? "" : " Talent debuff DPS counts a talent's own damage over time, such as Toxic Delivery, and its blasts on other enemies; it is included in total DPS, not in burn and bleed DPS.") +
            (result.SkillDps is null && result.StatusDps is null && result.TalentDps is null ? "" : " Component percentiles are not additive."));
    }
}
