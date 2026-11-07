using System.Collections.Immutable;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using CommunityToolkit.Mvvm.Input;

namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>
/// One ranked change, as the Go app's row: the coloured change to the stat ("+70,000 Estimated Total Armor"), the
/// change itself ("Core 1 · Armor (Defensive) at 170,000 (reroll)") and Apply.
/// </summary>
public sealed class UpgradeRowViewModel
{
    internal UpgradeRowViewModel(string change, int direction, string label, IRelayCommand apply)
    {
        (Change, Direction, Label, ApplyCommand) = (change, direction, label, apply);
    }

    public string Change { get; }
    /// <summary>1 for a gain, -1 for a loss, 0 for no change.</summary>
    public int Direction { get; }
    public bool IsGain => Direction > 0;
    public bool IsLoss => Direction < 0;
    public string Label { get; }
    /// <summary>Makes the change in one undo step and closes the dialog.</summary>
    public IRelayCommand ApplyCommand { get; }
}

/// <summary>
/// Rank Upgrades (F42), as the Go app's <c>upgrade_ranking.go</c>: every attribute, mod and talent choice on the item in
/// one slot, each tried on its own against the current build and assumptions, ranked by its change to a summary stat.
/// The ranking runs off the UI thread whenever the stat changes; the search and Only gains filter the result. Apply
/// makes the picked change in one undo step and closes the dialog.
/// </summary>
public sealed class RankUpgradesViewModel : ViewModelBase, IDisposable
{
    public const string SearchPlaceholder = "Search attributes, mods and talents";
    public string Coverage { get; private set => SetProperty(ref field, value); } = EffectCoverage.ComparisonNote;
    public const string Hint = "Each change is tried on its own against the current build and assumptions; attributes and mods at their highest roll.";
    public const string Empty = "No change raises this stat with the current build and assumptions.";

    private readonly EditorViewModel editor;
    private readonly LatestRevisionJob<UpgradeRanking> ranker;
    private Choice rankBy;
    private string search = "";
    private UpgradeRanking? ranking;
    private ImmutableArray<UpgradeRowViewModel> rows = [];

    internal RankUpgradesViewModel(EditorViewModel editor, string slotId, IUiDispatcher ui)
    {
        (this.editor, SlotId) = (editor, slotId);
        var item = editor.Items.GetValueOrDefault(editor.Session.Snapshot.State.Build.Equipment.GetValueOrDefault(slotId, ""));
        Title = item is null ? "Rank upgrades" : $"Rank upgrades · {item.Name}";
        // The summary's stats, in its order, once each (Go's rankingStats).
        Stats = [.. editor.Summary.SelectMany(group => group.Rows).Select(row => row.Id).Distinct(StringComparer.Ordinal)
            .Select(editor.Statistics.GetValueOrDefault).OfType<StatDefinition>().Select(stat => new Choice(stat.ID, stat.Name))];
        rankBy = Stats.FirstOrDefault(stat => stat.Id == editor.LastRankedStat) ?? Stats.FirstOrDefault() ?? Choice.None;
        ranker = new(result => ui.Post(() => ShowRanking(result)));
    }

    public string Title { get; }
    public string SlotId { get; }

    /// <summary>The stats a ranking can be by: the summary's, in its order.</summary>
    public ImmutableArray<Choice> Stats { get; }

    /// <summary>The stat ranked by; the next dialog opens on it.</summary>
    public Choice RankBy
    {
        get => rankBy;
        set
        {
            if (value is null || !SetProperty(ref rankBy, value)) { return; }
            editor.LastRankedStat = value.Id;
            Rank();
        }
    }

    /// <summary>Keeps the changes whose description has every typed word.</summary>
    public string Search { get => search; set { if (SetProperty(ref search, value ?? "")) { Rebuild(); } } }

    /// <summary>Keeps only the changes that raise the stat.</summary>
    public bool OnlyGains { get; set { if (SetProperty(ref field, value)) { Rebuild(); } } } = true;

    public ImmutableArray<UpgradeRowViewModel> Rows { get => rows; private set { if (SetProperty(ref rows, value)) { OnPropertyChanged(nameof(IsEmpty)); } } }

    /// <summary>No change is listed: the empty-state line shows (Go showed it whenever nothing was kept).</summary>
    public bool IsEmpty => Rows.IsEmpty && ranking is not null && Problem is null;

    /// <summary>"N of M changes": the changes listed, of all ranked.</summary>
    public string Shown { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>Why nothing could be ranked, or null.</summary>
    public string? Problem { get; private set { if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(IsEmpty)); } } }

    public bool IsRanking { get; private set => SetProperty(ref field, value); }

    /// <summary>The latest ranking, for callers (and tests) that need to wait for it.</summary>
    public Task Ranking { get; private set; } = Task.CompletedTask;

    /// <summary>Ranks the item's changes by the chosen stat in the background.</summary>
    internal void Rank()
    {
        if (rankBy.Id.Length == 0) { return; }
        IsRanking = true;
        Coverage = EffectCoverage.ComparisonNote;
        var (session, slot, stat) = (editor.Session, SlotId, rankBy.Id);
        Ranking = ranker.Start(session.Snapshot, (state, token) => session.RankUpgrades(state, slot, stat, token));
    }

    private void ShowRanking(JobResult<UpgradeRanking> result)
    {
        if (!ranker.IsCurrent(result)) { return; }
        IsRanking = false;
        ranking = result.Value;
        Coverage = ranking is null ? EffectCoverage.ComparisonNote : EffectCoverage.ComparisonDisclosure(
            ranking.Upgrades.Select(upgrade => upgrade.Coverage).Prepend(ranking.Coverage));
        Problem = result.Error is { } error ? $"The changes couldn't be ranked: {error.Message}" : ranking?.Problem;
        Rebuild();
    }

    private void Rebuild()
    {
        if (ranking is null || editor.Statistics.GetValueOrDefault(ranking.Stat) is not { } stat)
        {
            // No ranking (it failed, or another stat's is on its way): never leave another stat's rows to apply.
            (Rows, Shown) = ([], "");
            OnPropertyChanged(nameof(IsEmpty));
            return;
        }
        var kept = ranking.Upgrades.Select(upgrade => (Upgrade: upgrade, Label: Label(upgrade)))
            .Where(entry => !(OnlyGains && entry.Upgrade.Delta <= 1e-9) && TextSearch.MatchesAllWords(entry.Label, Search))
            .Select(entry => Row(stat, entry.Upgrade, entry.Label))
            .ToImmutableArray();
        Rows = kept;
        Shown = $"{kept.Length} of {ranking.Upgrades.Length} changes";
        OnPropertyChanged(nameof(IsEmpty));
    }

    private UpgradeRowViewModel Row(StatDefinition stat, RankedUpgrade upgrade, string label)
    {
        // Floating-point noise reads as no change (Go's cleanDelta).
        var delta = Math.Abs(upgrade.Delta) < 1e-9 ? 0 : upgrade.Delta;
        var apply = new RelayCommand(() =>
        {
            if (editor.Apply([.. upgrade.Operations])) { editor.CloseRankUpgrades(); }
        });
        return new(Formatting.Delta(stat.Format, delta) + " " + stat.Name, Math.Sign(delta), label, apply);
    }

    /// <summary>"Talent · Vanguard", or "Core 1 · Armor (Defensive) at 170,000 (reroll)" (Go's upgradeLabel).</summary>
    public static string Label(RankedUpgrade upgrade)
    {
        var label = upgrade.TargetName + " · " + upgrade.Name;
        if (upgrade.Kind == UpgradeKind.Attribute && upgrade.Value is { } value) { label += " at " + Formatting.Value(upgrade.ValueFormat, value); }
        return upgrade.Reroll ? label + " (reroll)" : label;
    }

    public void Dispose() => ranker.Dispose();
}
