using System.Collections.Immutable;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using CommunityToolkit.Mvvm.Input;

namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>One talent in Compare talents: its icon, name, what choosing it would change, its description, and Choose.</summary>
public sealed record TalentChoiceViewModel(string Id, string Name, string Description, string Effect, string? Icon, bool IsCurrent,
    ImmutableDictionary<string, double> Deltas, IRelayCommand ChooseCommand)
{
    public string ChooseText => $"Choose {Name}";
    public string Coverage { get; init; } = "";
    public bool HasDescription => Description.Length > 0;
    internal string Text { get; } = $"{Name} {Description} {Effect}";
}

/// <summary>
/// Compare talents (G356), as the Go app's dialog for a talent slot: every talent it offers with what choosing it would
/// change in the build's stats under the current assumptions (summary stats first, largest change first), a search, an
/// Affects filter that also sorts by that stat's change, and "Only talents that change stats". Choose applies one and
/// closes. The changes are worked out once, in the background, when the dialog opens, and arrive on the UI thread
/// through the editor's dispatcher as every other background result does.
/// </summary>
public sealed class TalentCompareViewModel : ViewModelBase, IDisposable
{
    public static readonly Choice AnyStat = new("", "Any stat");

    private readonly EditorViewModel editor;
    private readonly string slotId;
    private readonly ItemTalentSlotDefinition talentSlot;
    private ImmutableArray<TalentChoiceViewModel> choices = [];
    private Choice affected = AnyStat;
    private string search = "";
    private bool changedOnly;
    private sealed record TalentPreview(ImmutableArray<(string Stat, double Delta)> Changes, string Coverage);
    private readonly LatestRevisionJob<ImmutableDictionary<string, TalentPreview>> comparer;
    private string current = "";

    internal TalentCompareViewModel(EditorViewModel editor, string slotId, ItemTalentSlotDefinition talentSlot, IUiDispatcher ui)
    {
        (this.editor, this.slotId, this.talentSlot) = (editor, slotId, talentSlot);
        comparer = new(result => ui.Post(() => Show(result)));
        Title = string.Equals(talentSlot.Name, "Talent", StringComparison.OrdinalIgnoreCase) ? "Compare talents" : $"Compare talents · {talentSlot.Name}";
    }

    /// <summary>Raised when a talent is chosen, so the window closes.</summary>
    public event EventHandler? Chosen;

    public string Title { get; }
    public string CoverageNote => EffectCoverage.ComparisonNote;
    internal const string CoverageUnavailable = "Coverage unavailable: this talent couldn't be previewed in the current build.";
    public string? Problem { get; private set => SetProperty(ref field, value); }
    public ImmutableArray<TalentChoiceViewModel> Rows { get; private set => SetProperty(ref field, value); } = [];
    /// <summary>"Any stat", then each stat some talent here changes, by name.</summary>
    public ImmutableArray<Choice> Affects { get; private set => SetProperty(ref field, value); } = [AnyStat];
    public Choice Affected { get => affected; set { if (value is not null && SetProperty(ref affected, value)) { Filter(); } } }
    public string Search { get => search; set { if (SetProperty(ref search, value ?? "")) { Filter(); } } }
    public bool ChangedOnly { get => changedOnly; set { if (SetProperty(ref changedOnly, value)) { Filter(); } } }
    /// <summary>"N of M talents".</summary>
    public string Shown { get; private set => SetProperty(ref field, value); } = "";
    public bool IsComparing { get; private set => SetProperty(ref field, value); } = true;

    /// <summary>The latest comparison, for callers (and tests) that wait for it; its result is posted to the UI thread.</summary>
    public Task Comparison { get; private set; } = Task.CompletedTask;

    /// <summary>Starts working out every talent's changes in the background; they are listed when they arrive.</summary>
    public Task LoadAsync()
    {
        editor.ArtworkChanged -= RefreshArtwork;
        editor.ArtworkChanged += RefreshArtwork;
        var snapshot = editor.Session.Snapshot;
        Problem = LoadoutResolver.EquipmentLimitProblem(snapshot.Pack.Pack, snapshot.State.Build)?.Message;
        current = snapshot.State.Build.TalentSelections.GetValueOrDefault(slotId)?.GetValueOrDefault(talentSlot.ID) ?? "";
        var selected = current;
        var summary = editor.Summary.SelectMany(group => group.Rows).Select(row => row.Id).Distinct(StringComparer.Ordinal).ToImmutableArray();
        var others = editor.Pack.Stats.Where(stat => stat.Formula.Length > 0 && !summary.Contains(stat.ID)).Select(stat => stat.ID).ToImmutableArray();
        var session = editor.Session;
        Comparison = comparer.Start(snapshot, (state, token) =>
        {
            var before = EditorViewModel.TryResolve(state.Pack, state.State);
            return talentSlot.Choices.ToImmutableDictionary(id => id, id =>
            {
                token.ThrowIfCancellationRequested();
                var outcome = id == selected ? null : session.Preview(state, [new TalentOperation(slotId, talentSlot.ID, id)]);
                var candidate = outcome?.State ?? state.State;
                // A refused edit leaves the current build, which has no row for this talent: that is not missing modeling.
                var coverage = outcome is { Succeeded: false } ? CoverageUnavailable
                    : EffectCoverage.Describe(state.Pack.Pack, candidate.Build, candidate.Assumptions).Talents
                        .FirstOrDefault(talent => talent.Slot == slotId && talent.Talent == id)?.Description ?? CoverageUnavailable;
                var changes = id != selected && before is not null && outcome?.Succeeded == true && EditorViewModel.TryResolve(state.Pack, candidate) is { } after
                    ? Changes(before, after, summary, others) : [];
                return new TalentPreview(changes, coverage);
            }, StringComparer.Ordinal);
        });
        return Comparison;
    }

    private void Show(JobResult<ImmutableDictionary<string, TalentPreview>> result)
    {
        if (!comparer.IsCurrent(result)) { return; }
        var effects = result.Value ?? ImmutableDictionary<string, TalentPreview>.Empty;
        choices = [.. talentSlot.Choices.Select(id =>
        {
            var talent = editor.Talents.GetValueOrDefault(id);
            var shown = effects.GetValueOrDefault(id);
            var changes = shown?.Changes ?? [];
            var lines = changes.Take(4).Select(change => $"{Formatting.Delta(editor.Statistics[change.Stat].Format, change.Delta)} {editor.Statistics[change.Stat].Name}");
            var effect = Problem is not null ? "Comparison unavailable until equipment limits are satisfied."
                : id == current ? "Selected."
                : changes.IsEmpty ? "No change to modeled stats with the current view assumptions."
                : "Compared with the current talent: " + string.Join(", ", lines);
            var choose = new RelayCommand(() =>
            {
                if (editor.Apply(new TalentOperation(slotId, talentSlot.ID, id))) { Chosen?.Invoke(this, EventArgs.Empty); }
            }, () => id != current);
            return new TalentChoiceViewModel(id, talent?.Name ?? id, talent?.Description ?? "", effect, editor.Artwork.Icon("talents", id), id == current,
                changes.ToImmutableDictionary(change => change.Stat, change => change.Delta, StringComparer.Ordinal), choose)
            { Coverage = shown?.Coverage ?? CoverageUnavailable };
        })];
        Affects = [AnyStat, .. choices.SelectMany(choice => choice.Deltas.Keys).Distinct(StringComparer.Ordinal)
            .Select(id => new Choice(id, editor.Statistics[id].Name)).OrderBy(choice => choice.Name, StringComparer.CurrentCultureIgnoreCase)];
        IsComparing = false;
        Filter();
    }

    private void RefreshArtwork(object? sender, EventArgs args)
    {
        choices = [.. choices.Select(choice => choice with { Icon = editor.Artwork.Icon("talents", choice.Id) })];
        Filter();
    }

    public void Dispose() { editor.ArtworkChanged -= RefreshArtwork; comparer.Dispose(); }


    /// <summary>The stats a talent changes, summary stats then the others, each largest change first (Go's statDeltas).</summary>
    private static ImmutableArray<(string Stat, double Delta)> Changes(ResolvedBuild before, ResolvedBuild after, ImmutableArray<string> summary, ImmutableArray<string> others)
    {
        IEnumerable<(string Stat, double Delta)> Of(ImmutableArray<string> ids) => ids
            .Select(id => (Stat: id, Delta: after.Calculation.Stats.GetValueOrDefault(id) - before.Calculation.Stats.GetValueOrDefault(id)))
            .Where(change => Math.Abs(change.Delta) >= 1e-9)
            .OrderByDescending(change => Math.Abs(change.Delta));
        return [.. Of(summary), .. Of(others)];
    }

    private void Filter()
    {
        var kept = choices.Where(choice => TextSearch.MatchesAllWords(choice.Text, search))
            .Where(choice => affected.Id.Length == 0 || choice.Deltas.ContainsKey(affected.Id))
            .Where(choice => !changedOnly || !choice.Deltas.IsEmpty);
        // The Affects filter also sorts by that stat's change, highest first.
        if (affected.Id.Length > 0) { kept = kept.OrderByDescending(choice => choice.Deltas.GetValueOrDefault(affected.Id)); }
        Rows = [.. kept];
        Shown = $"{Rows.Length} of {choices.Length} talents";
    }
}
