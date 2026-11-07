using System.Collections.Immutable;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Application.Library;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;

namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>A build the Compare dialog can pick: the current build (Build is null), or a saved one.</summary>
public sealed record CompareOption(string Label, BuildSnapshot? Build, string? SavedName = null)
{
    public override string ToString() => Label;
}

/// <summary>One stat in both builds, as the Go app's compare row: the name, both values and the change from A to B.</summary>
public sealed record CompareRowViewModel(string Stat, string A, string B, string Delta);

/// <summary>
/// Compare Builds (F43), as the Go app's <c>compare.go</c>: Build A and Build B from the current build and the saved
/// ones, every pack stat in both with its change, largest change first, under the current assumptions. The comparison
/// runs off the UI thread each time a selector changes. A build that can't be compared, a damaged record or an
/// unreadable library is reported in <see cref="Status"/>.
/// </summary>
public sealed class CompareViewModel : ViewModelBase, IDisposable
{
    public const string Title = "Compare Builds";
    public const string CurrentBuild = "Current Build";
    public const string UnnamedBuild = "(unnamed build)";
    public string Coverage { get; private set => SetProperty(ref field, value); } = EffectCoverage.ComparisonNote;

    private readonly EditorViewModel editor;
    private readonly LatestRevisionJob<BuildComparisonResult> comparer;
    private ImmutableArray<CompareOption> options;
    private ImmutableArray<string> libraryProblems = [];
    private ImmutableArray<string> comparisonProblems = [];
    private CompareOption buildA;
    private CompareOption buildB;

    internal CompareViewModel(EditorViewModel editor, IUiDispatcher ui)
    {
        this.editor = editor;
        var current = new CompareOption(CurrentBuild, null);
        options = [current];
        (buildA, buildB) = (current, current);
        comparer = new(result => ui.Post(() => ShowComparison(result)));
    }

    /// <summary>"Current Build", then each saved build that can be read, as "Saved: name".</summary>
    public ImmutableArray<CompareOption> Options { get => options; private set => SetProperty(ref options, value); }

    public CompareOption BuildA { get => buildA; set { if (value is not null && SetProperty(ref buildA, value)) { Recompute(); } } }
    public CompareOption BuildB { get => buildB; set { if (value is not null && SetProperty(ref buildB, value)) { Recompute(); } } }

    /// <summary>Every pack stat in both builds, largest change first; empty while either build can't be compared.</summary>
    public ImmutableArray<CompareRowViewModel> Rows { get; private set => SetProperty(ref field, value); } = [];

    /// <summary>Why a build can't be compared or the library couldn't be read, one a line; empty when all is well.</summary>
    public string Status { get; private set { if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(HasStatus)); } } } = "";
    public bool HasStatus => Status.Length > 0;

    public bool IsComparing { get; private set => SetProperty(ref field, value); }

    /// <summary>The latest comparison, for callers (and tests) that need to wait for it.</summary>
    public Task Comparison { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Reads the saved builds into the selectors, then compares. A damaged record, one for another pack or one that
    /// can't be read is left out with a line saying so; a library that can't be listed leaves only the current build.
    /// </summary>
    public async Task LoadAsync()
    {
        var problems = ImmutableArray.CreateBuilder<string>();
        var saved = ImmutableArray.CreateBuilder<CompareOption>();
        try
        {
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var entry in await editor.Library.ListAsync())
            {
                var name = entry.Name.Trim().Length > 0 ? entry.Name : UnnamedBuild;
                if (entry.Problem is { } problem) { problems.Add(Invalid(name, problem)); continue; }
                if (entry.PackId.Length > 0 && entry.PackId != editor.Pack.ID) { problems.Add(Invalid(name, $"the build is for pack {entry.PackId}, not {editor.Pack.ID}")); continue; }
                LibraryBuild loaded;
                try { loaded = await editor.Library.LoadAsync(entry.Id); }
                catch (LibraryException exception) { problems.Add(Invalid(name, exception.Message)); continue; }
                // Two saved builds of one name stay apart: "Saved: Name", "Saved: Name (2)".
                var count = seen[name] = seen.GetValueOrDefault(name) + 1;
                saved.Add(new($"Saved: {name}{(count > 1 ? $" ({count})" : "")}", loaded.Build, name));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or LibraryException)
        {
            problems.Add("Could not read build library: " + exception.Message);
        }
        libraryProblems = problems.ToImmutable();
        Options = [Options[0], .. saved];
        Recompute();
    }

    private void Recompute()
    {
        Coverage = EffectCoverage.ComparisonNote;
        IsComparing = true;
        var (a, b) = (BuildA.Build, BuildB.Build);
        Comparison = comparer.Start(editor.Session.Snapshot, (state, _) =>
            BuildComparison.Compare(state.Pack, a ?? state.State.Build, b ?? state.State.Build, state.State.Assumptions));
        ShowStatus();
    }

    private void ShowComparison(JobResult<BuildComparisonResult> result)
    {
        if (!comparer.IsCurrent(result)) { return; }
        IsComparing = false;
        if (result.Value is not { } comparison)
        {
            Rows = [];
            comparisonProblems = [$"The builds couldn't be compared: {result.Error?.Message}"];
            ShowStatus();
            return;
        }
        var problems = ImmutableArray.CreateBuilder<string>();
        foreach (var (side, option) in new[] { (comparison.A, BuildA), (comparison.B, BuildB) })
        {
            if (side.Problem is not { } problem) { continue; }
            var line = option.Build is null ? $"Current build is invalid: {problem}" : Invalid(option.SavedName ?? UnnamedBuild, problem);
            if (!problems.Contains(line)) { problems.Add(line); }
        }
        comparisonProblems = problems.ToImmutable();
        Coverage = EffectCoverage.ComparisonDisclosure([comparison.A.Coverage, comparison.B.Coverage]);
        Rows = [.. comparison.Stats.Select(stat => new CompareRowViewModel(stat.Stat.Name,
            Formatting.Value(stat.Stat.Format, stat.A), Formatting.Value(stat.Stat.Format, stat.B), Formatting.Delta(stat.Stat.Format, stat.Delta)))];
        ShowStatus();
    }

    private void ShowStatus() => Status = string.Join("\n", libraryProblems.Concat(comparisonProblems));

    private static string Invalid(string name, string problem) => $"Saved build \"{name}\" is invalid: {problem}";

    public void Dispose() => comparer.Dispose();
}
