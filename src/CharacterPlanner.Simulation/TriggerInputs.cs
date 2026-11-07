using System.Collections.Immutable;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation;

/// <summary>Shared checks binding trigger rules to the pack inputs they drive, so every state class applies one rule.</summary>
internal static class TriggerInputs
{
    /// <summary>Slack for readings computed in floating point, so an exact tier is never floored one step low.</summary>
    internal const double Tolerance = 1e-9;

    internal static bool IsTrueOnly(SelectorDefinition? selector) =>
        selector is not null && selector.Values.Length == 1 && selector.Values[0] == "true";

    /// <summary>Whether value sits on the step grid measured from min; a nonpositive step has no grid.</summary>
    internal static bool OnGrid(double value, double min, double step) =>
        step <= 0 || Math.Abs((value - min) / step - Math.Round((value - min) / step)) <= 1e-8;

    internal static void RequireIncreasingPhases(ImmutableArray<SimPhase> phases, string message)
    {
        double previous = 0;
        foreach (var phase in phases)
        {
            if (!double.IsFinite(phase.Until) || phase.Until <= previous || phase.Value.Length == 0)
            { throw new SimulationException("trigger-phase-invalid", message); }
            previous = phase.Until;
        }
    }
}
