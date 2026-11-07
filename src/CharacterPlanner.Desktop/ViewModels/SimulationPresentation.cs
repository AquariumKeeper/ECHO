using CharacterPlanner.Simulation;

namespace CharacterPlanner.Desktop.ViewModels;

public static class SimulationPresentation
{
    public static double ProgressFraction(SimulationProgress progress) => progress.TotalFights <= 0 ? 0 :
        Math.Clamp((double)progress.CompletedFights / progress.TotalFights, 0, 1);

    public static string Summary(DummyFightResult fight)
    {
        var text = $"Weapon DPS: {fight.WeaponDps.Mean:N0}\nTotal DPS: {(fight.TotalDps is { } total ? total.Mean.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) : "Unavailable")}\nShots/fight: {fight.ShotsFired:N1} · Kills/fight: {fight.Kills:N1}";
        if (fight.SkillDps is { } skill)
        {
            text += $"\n{SimulationMetricLabels.SkillTotal}: {skill.Total.Mean:N0}";
            text += $"\n{SimulationMetricLabels.SkillImpact}: {skill.Impact.Mean:N0} · {SimulationMetricLabels.SkillContinuousWindow}: {skill.ContinuousWindow.Mean:N0}";
        }
        if (fight.StatusDps is { } status) { text += $"\n{SimulationMetricLabels.Status}: {status.Mean:N0}"; }
        if (fight.TalentDps is { } talent) { text += $"\n{SimulationMetricLabels.Talent}: {talent.Mean:N0}"; }
        if (fight.KilledFraction is { } killed)
        {
            // Each fight's average, weighted by fight; fights without a kill are left out, so their share is shown.
            text += fight.TimeToKill is not { } kill ? $"\n{SimulationMetricLabels.TimeToKill}: no target died in any fight"
                : $"\n{SimulationMetricLabels.TimeToKill}: {kill.Mean:N2} s" +
                  (kill.P5 == kill.P95 ? "" : $" (fights' averages {kill.P5:N2} to {kill.P95:N2} s)") +
                  (killed < 1 ? $" · Fights with a kill: {killed * 100:N1}%" : "");
        }
        if (fight.ClearedFraction is { } cleared)
        {
            text += fight.TimeToClear is { } clear
                ? $"\nTime to clear, in fights that cleared: {clear.Mean:N1} s (median {clear.P50:N1} s) · Cleared: {cleared * 100:N1}% of fights"
                : $"\nTime to clear: not reached in any fight";
        }
        if (fight.Incoming is { } received)
        { text += $"\nIncoming damage/fight: {received.DamageTaken:N0} · Downed: {received.DownedFraction:P1}\nArmor repaired/fight: {received.ArmorRepaired:N0} · Shield absorbed/fight: {received.ShieldAbsorbed:N0}"; }
        if (fight.Incoming is { } kits && kits.ArmorKitsUsed + kits.ArmorKitsCancelled > 0)
        { text += $"\nArmor kits/fight: {kits.ArmorKitsUsed:N2} used · {kits.ArmorKitsCancelled:N2} cut short by going down"; }
        return text;
    }
}
