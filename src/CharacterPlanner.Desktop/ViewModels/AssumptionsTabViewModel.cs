using System.Collections.Immutable;
using System.ComponentModel;
using CharacterPlanner.Application.Assumptions;

namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>
/// The Assumptions tab, as the Go app's: the assumption set bar, a search box narrowing the cards by name and
/// description (every word), the caption, and the sections; a note says when no card is shown.
/// </summary>
public sealed class AssumptionsTabViewModel : ViewModelBase
{
    public const string SearchPlaceholder = "Search assumptions";
    public const string Caption = "These temporary settings affect calculations and are not saved with the build.";
    public const string EmptyText = "No assumptions apply to this build. Equip items, talents or skills with conditions to see their settings here.";

    private readonly ImmutableArray<AssumptionSectionViewModel> sections;
    private string search = "";

    internal AssumptionsTabViewModel(ImmutableArray<AssumptionSectionViewModel> sections, AssumptionSetBarViewModel sets)
    {
        this.sections = sections;
        Sets = sets;
        // The editor updates the sections as the build changes; the note follows them.
        foreach (var section in sections) { section.PropertyChanged += OnSectionChanged; }
        Update();
    }

    /// <summary>Saved assumption sets (F45).</summary>
    public AssumptionSetBarViewModel Sets { get; }

    public string Search { get => search; set { if (SetProperty(ref search, value ?? "")) { Update(); } } }

    /// <summary>No card is shown, so the note is.</summary>
    public bool IsEmpty { get; private set => SetProperty(ref field, value); }

    private void Update()
    {
        foreach (var section in sections)
        {
            foreach (var assumption in section.Assumptions) { assumption.Filter(Search); }
            section.Update();
        }
        IsEmpty = !sections.Any(section => section.HasVisible);
    }

    private void OnSectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AssumptionSectionViewModel.HasVisible)) { IsEmpty = !sections.Any(section => section.HasVisible); }
    }
}

/// <summary>
/// The assumption set bar, as the Go app's (G396): a chooser showing the saved set the assumptions match, or "Custom";
/// choosing one applies it in one undo step (the weapon in hand stays) and reports saved values the pack no longer
/// takes. Save As saves the assumptions under a name, asking before replacing one; Delete removes the chosen set and
/// leaves the assumptions as they are. Without a store the bar isn't shown. Failures are reported in the editor's message.
/// </summary>
public sealed class AssumptionSetBarViewModel : ViewModelBase
{
    public const string Label = "Assumption set";
    public const string Custom = "Custom";
    public const string NamePlaceholder = "Such as Boss, full stacks";
    public const string SaveTitle = "Save Assumption Set";
    public const string ReplaceTitle = "Replace Assumption Set";
    public const string DeleteTitle = "Delete Assumption Set";
    public const string ChooseToDelete = "Choose a saved set to delete";

    private readonly EditorViewModel editor;
    private readonly IAssumptionSetStore? store;
    private ImmutableArray<AssumptionSet> sets = [];
    private ImmutableDictionary<string, string> shown = ImmutableDictionary<string, string>.Empty;
    private string? selected;
    private bool syncing;

    internal AssumptionSetBarViewModel(EditorViewModel editor, IAssumptionSetStore? store) => (this.editor, this.store) = (editor, store);

    public bool IsAvailable => store is not null;

    /// <summary>The pack's saved sets, by name.</summary>
    public ImmutableArray<string> Names { get; private set; } = [];

    /// <summary>The set the assumptions match, or null (the chooser reads "Custom"). Choosing one applies it.</summary>
    public string? Selected
    {
        get => selected;
        set
        {
            if (syncing || value is null || value == selected) { return; }
            selected = value;
            OnPropertyChanged();
            Apply(value);
        }
    }

    /// <summary>What the last apply left out ("These saved assumptions no longer apply…"), or null.</summary>
    public string? Notice { get; private set { if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(HasNotice)); } } }
    public bool HasNotice => Notice is not null;

    /// <summary>The name Save As offers: the set the assumptions match, or nothing.</summary>
    public string SuggestedName => Selected ?? "";

    /// <summary>Reads the pack's sets from the store, then shows which one the assumptions match.</summary>
    public async Task RefreshAsync()
    {
        if (store is null) { return; }
        try { Show(await store.ListAsync(editor.Pack.ID)); }
        catch (Exception exception) when (exception is AssumptionSetException or IOException or UnauthorizedAccessException)
        {
            Show([]);
            editor.Message = exception.Message;
        }
    }

    /// <summary>Shows which set the shown assumptions match.</summary>
    internal void Refresh(ImmutableDictionary<string, string> assumptions)
    {
        shown = assumptions;
        Sync();
    }

    /// <summary>Applies a saved set by name (ignoring case) as one edit; false when there is none or it was refused.</summary>
    public bool Apply(string name)
    {
        Notice = null;
        if (sets.FirstOrDefault(set => string.Equals(set.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } set)
        {
            editor.Message = $"No assumption set named \"{name}\"";
            Sync();
            return false;
        }
        var application = AssumptionSets.Apply(editor.Pack, editor.Session.Snapshot.State.Assumptions, set);
        if (!application.Skipped.IsEmpty) { Notice = $"These saved assumptions no longer apply and were left out: {string.Join(", ", application.Skipped)}"; }
        var applied = application.Operations.IsEmpty || editor.Apply([.. application.Operations]);
        Sync();
        return applied;
    }

    /// <summary>
    /// Saves the assumptions as a named set, replacing one of that name only when <paramref name="replace"/> is set.
    /// The result says whether it was saved, or that the name is taken (with the store's message), or why not.
    /// </summary>
    public async Task<AssumptionSetSave> SaveAsync(string name, bool replace)
    {
        if (store is null) { return new(false, false, "Assumption sets are unavailable"); }
        Notice = null;
        try
        {
            await store.SaveAsync(editor.Pack.ID, name, AssumptionSets.Values(editor.Session.Snapshot.State.Assumptions), replace);
        }
        catch (AssumptionSetException exception) when (exception.Code is AssumptionSetErrorCodes.Exists or AssumptionSetErrorCodes.NameRequired)
        {
            return new(false, exception.Code == AssumptionSetErrorCodes.Exists, Sentence(exception.Message));
        }
        catch (Exception exception) when (exception is AssumptionSetException or IOException or UnauthorizedAccessException)
        {
            editor.Message = exception.Message;
            await RefreshAsync();
            return new(false, false, null);
        }
        await RefreshAsync();
        return new(true, false, null);
    }

    /// <summary>The set Delete would remove, or null, saying to choose one first.</summary>
    public string? DeleteCandidate()
    {
        if (Selected is null) { editor.Message = ChooseToDelete; }
        return Selected;
    }

    /// <summary>The question Delete asks before removing a set.</summary>
    public static string DeleteQuestion(string name) => $"Delete \"{name}\"? The current assumptions stay as they are.";

    /// <summary>Deletes a saved set; the assumptions stay as they are.</summary>
    public async Task DeleteAsync(string name)
    {
        if (store is null) { return; }
        Notice = null;
        try { await store.DeleteAsync(editor.Pack.ID, name); }
        catch (Exception exception) when (exception is AssumptionSetException or IOException or UnauthorizedAccessException) { editor.Message = exception.Message; }
        await RefreshAsync();
    }

    private void Show(ImmutableArray<AssumptionSet> list)
    {
        sets = list;
        syncing = true;
        try { Names = [.. list.Select(set => set.Name)]; OnPropertyChanged(nameof(Names)); }
        finally { syncing = false; }
        Sync();
    }

    private void Sync()
    {
        syncing = true;
        try
        {
            selected = AssumptionSets.Matching(editor.Pack, shown, sets)?.Name;
            // Raised even when unchanged: the chooser may have dropped its selection when the names were replaced.
            OnPropertyChanged(nameof(Selected));
        }
        finally { syncing = false; }
    }

    private static string Sentence(string message) => message.Length == 0 ? message : char.ToUpperInvariant(message[0]) + message[1..];
}

/// <summary>What Save As did: saved, or the name is taken (<see cref="Exists"/>, with the store's message), or why not.</summary>
public sealed record AssumptionSetSave(bool Saved, bool Exists, string? Problem);
