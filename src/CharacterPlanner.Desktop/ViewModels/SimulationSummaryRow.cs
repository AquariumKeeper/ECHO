using CharacterPlanner.Simulation;

namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>Displays an engine summary in its original units without recomputing statistics.</summary>
public sealed record SimulationSummaryRow(string Metric, FightSummary Summary)
{
    public string Mean => Summary.Mean.ToString("N1");
    public string StdDev => Summary.StdDev.ToString("N1");
    public string Minimum => Summary.Min.ToString("N1");
    public string P5 => Summary.P5.ToString("N1");
    public string Median => Summary.P50.ToString("N1");
    public string P95 => Summary.P95.ToString("N1");
    public string Maximum => Summary.Max.ToString("N1");
}
