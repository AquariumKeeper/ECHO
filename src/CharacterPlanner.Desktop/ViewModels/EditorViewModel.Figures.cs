using System.Collections.Immutable;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Expressions;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>The headline, the summary and the Stats tab from the latest resolution, and the selected stat's breakdown.</summary>
public sealed partial class EditorViewModel
{
    private FigureViewModel Figure(LayoutHeadlineStat entry, ImmutableDictionary<string, StatDefinition> stats)
    {
        if (entry.Stat.Length > 0)
        {
            var stat = stats.GetValueOrDefault(entry.Stat);
            return new HeadlineViewModel(entry, entry.Label.Length > 0 ? entry.Label : stat?.Name ?? entry.Stat, stat?.Format ?? StatFormats.Decimal, "",
                Baseline.TryGetValue(entry.Stat, out var empty) ? empty : null);
        }
        var output = Pack.BuildOutputs.FirstOrDefault(definition => definition.ID == entry.BuildOutput);
        return new HeadlineViewModel(entry, entry.Label.Length > 0 ? entry.Label : output?.Name ?? entry.BuildOutput,
            output?.Format is { Length: > 0 } format ? format : StatFormats.Decimal, output?.Description ?? "", null);
    }

    private static ImmutableDictionary<string, double> EmptyBuildStats(CompiledPackExpressions compiled, GamePack pack) =>
        TryResolve(compiled, PlannerState.Of(BuildSnapshot.Create(pack)))?.Calculation.Stats ?? ImmutableDictionary<string, double>.Empty;

    /// <summary>Resolves a state, or gives null when the engine can't evaluate it (the caller shows nothing or a note).</summary>
    internal static ResolvedBuild? TryResolve(CompiledPackExpressions compiled, PlannerState state)
    {
        try
        {
            var resolved = BuildResolver.Resolve(compiled, state.Build, state.Assumptions);
            return resolved.Diagnostics.Any(LoadoutResolver.IsEquipmentLimitError) ? null : resolved;
        }
        catch (ExpressionException) { return null; }
    }

    public StatRowViewModel? SelectedStat
    {
        get => selectedStat;
        set { if (SetProperty(ref selectedStat, value)) { ShowDetails(); } }
    }

    private void ShowResolution(JobResult<ResolvedBuild> result)
    {
        if (!resolver.IsCurrent(result)) { return; }
        if (result.Error is { } error)
        {
            // Never leave another revision's numbers on show, nor its idea of which assumptions matter (set ones stay).
            resolved = null;
            foreach (var section in AssumptionSections)
            {
                foreach (var assumption in section.Assumptions) { assumption.Relevant(false); }
                section.Update();
            }
            foreach (var figure in Headline) { figure.Show(null); }
            foreach (var skill in SkillSlots) { skill.ShowPreview(null); }
            foreach (var row in statRows.Values) { row.Show(ImmutableDictionary<string, double>.Empty); }
            Stats.Show(ImmutableDictionary<string, double>.Empty);
            foreach (var group in Summary) { group.Update(); }
            Diagnostics = [];
            Message = $"The stats couldn't be worked out: {error.Message}";
            ShowDetails();
            return;
        }
        resolved = result.Value!;
        foreach (var section in AssumptionSections)
        {
            foreach (var assumption in section.Assumptions) { assumption.Relevant(resolved.ReadAssumptions); }
            section.Update();
        }
        ShowFigures();
        foreach (var skill in SkillSlots) { skill.ShowPreview(resolved); }
        ShowSkillTiles(session.Snapshot.State.Build, resolved);
        ShowInspected();
        Stats.Show(resolved.Calculation.Stats);
        Diagnostics = DiagnosticLine.Of(resolved.Diagnostics);
        ShowDetails();
    }

    private void ShowDetails()
    {
        if (selectedStat is not null) { selectedStat.Details = Breakdown(selectedStat.Id); }
    }

    /// <summary>A stat's breakdown under the shown resolution, as the Go app's popup.</summary>
    internal StatDetailsViewModel? Breakdown(string statId) => StatBreakdown.Of(session.Pack, Statistics, resolved, statId);

    /// <summary>The headline and summary from the resolution, marked against a preview while there is one.</summary>
    private void ShowFigures()
    {
        foreach (var figure in Headline) { figure.Show(resolved, preview); }
        var stats = resolved?.Calculation.Stats ?? ImmutableDictionary<string, double>.Empty;
        foreach (var row in statRows.Values) { row.Show(stats, preview?.Calculation.Stats); }
        foreach (var group in Summary) { group.Update(); }
    }
}
