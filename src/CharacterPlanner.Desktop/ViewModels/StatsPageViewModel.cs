using System.Collections.Immutable;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>
/// The Stats tab (F37), as the Go app's stats page (G337): the summary's groups, then every other derived stat (one
/// with a formula) under "Other stats", each with its full name, value and breakdown. The filter matches names; "Only
/// stats that apply to this build" hides rows the build leaves at the empty build's value, except the summary's
/// always-shown ones. A group's header hides while none of its rows show, and "No stats match." says when none do.
/// </summary>
public sealed class StatsPageViewModel : ViewModelBase
{
    public const string OtherStatsGroup = "Other stats";
    public const string FilterPlaceholder = "Filter stats by name";
    public const string ApplicableLabel = "Only stats that apply to this build";
    public const string NoMatch = "No stats match.";

    private string filter = "";

    internal StatsPageViewModel(IEnumerable<LayoutSummaryGroup> summary, IReadOnlyList<StatDefinition> stats, IReadOnlyDictionary<string, double> baseline,
        Action<StatRowViewModel>? select = null)
    {
        var byId = stats.GroupBy(stat => stat.ID, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var listed = new HashSet<string>(StringComparer.Ordinal);
        var groups = ImmutableArray.CreateBuilder<StatsPageGroupViewModel>();
        StatsPageGroupViewModel Group(string name, IEnumerable<string> core, IEnumerable<string> conditional)
        {
            var coreIds = core.Where(byId.ContainsKey).Distinct(StringComparer.Ordinal).ToList();
            var rows = coreIds.Select(id => (Id: id, Core: true))
                .Concat(conditional.Where(byId.ContainsKey).Distinct(StringComparer.Ordinal).Where(id => !coreIds.Contains(id)).Select(id => (Id: id, Core: false)))
                .Select(entry =>
                {
                    listed.Add(entry.Id);
                    return new StatRowViewModel(byId[entry.Id], !entry.Core, "", baseline.GetValueOrDefault(entry.Id), select);
                });
            return new(name, [.. rows]);
        }
        foreach (var group in summary) { groups.Add(Group(group.Name, group.Stats, group.Conditional)); }
        var others = stats.Where(stat => stat.Formula.Length > 0 && !listed.Contains(stat.ID)).Select(stat => stat.ID).ToList();
        if (others.Count > 0) { groups.Add(Group(OtherStatsGroup, [], others)); }
        Groups = groups.ToImmutable();
        Update();
    }

    public ImmutableArray<StatsPageGroupViewModel> Groups { get; }

    /// <summary>Part of a stat's name, ignoring case; empty shows every stat.</summary>
    public string Filter { get => filter; set { if (SetProperty(ref filter, value ?? "")) { Update(); } } }

    /// <summary>On by default: rows the build leaves at the empty build's value hide, except the summary's always-shown rows.</summary>
    public bool OnlyApplicable { get; set { if (SetProperty(ref field, value)) { Update(); } } } = true;

    /// <summary>No row shows, so "No stats match." does.</summary>
    public bool IsEmpty { get; private set => SetProperty(ref field, value); }

    /// <summary>Shows a resolution's stats; with an empty map (the build couldn't be resolved) none.</summary>
    internal void Show(ImmutableDictionary<string, double> stats)
    {
        foreach (var row in Groups.SelectMany(group => group.Rows)) { row.Row.Show(stats); }
        Update();
    }

    private void Update()
    {
        foreach (var group in Groups)
        {
            foreach (var row in group.Rows)
            {
                row.IsShown = TextSearch.MatchesAllWords(row.Row.Name, Filter) && (!OnlyApplicable || row.Row.IsVisible);
            }
            group.Update();
        }
        IsEmpty = !Groups.Any(group => group.HasVisible);
    }
}

/// <summary>A titled block of the Stats tab; its header hides while none of its rows show.</summary>
public sealed class StatsPageGroupViewModel : ViewModelBase
{

    internal StatsPageGroupViewModel(string name, ImmutableArray<StatRowViewModel> rows)
    {
        Name = name;
        Rows = [.. rows.Select(row => new StatsPageRowViewModel(row))];
    }

    public string Name { get; }
    public ImmutableArray<StatsPageRowViewModel> Rows { get; }
    public bool HasVisible { get; private set => SetProperty(ref field, value); }

    internal void Update() => HasVisible = Rows.Any(row => row.IsShown);
}

/// <summary>
/// One Stats tab row: the stat as a summary row with its full name (whose <see cref="StatRowViewModel.IsVisible"/> says
/// whether it applies to this build), and whether the filter and the applicability check let it show.
/// </summary>
public sealed class StatsPageRowViewModel(StatRowViewModel row) : ViewModelBase
{
    private bool shown;

    public StatRowViewModel Row { get; } = row;
    public bool IsShown { get => shown; internal set => SetProperty(ref shown, value); }
}
