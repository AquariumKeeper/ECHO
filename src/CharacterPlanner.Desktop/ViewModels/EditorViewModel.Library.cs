using System.Collections.Immutable;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Library;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CommunityToolkit.Mvvm.Input;

namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>
/// The library: New, Save, Save as new, Open, and a saved build's Rename and Delete, one at a time, and whether the
/// build has unsaved changes.
/// </summary>
public sealed partial class EditorViewModel
{
    private string? recordId;
    private string? recordVersion;
    private BuildSnapshot? replacementChoiceFor;
    /// <summary>The build as last opened, created or saved, as a document; unsaved changes are differences from it.</summary>
    private byte[] baseline = [];

    internal IBuildLibrary Library => library;

    /// <summary>The build differs from what was last opened, created or saved.</summary>
    public bool IsDirty { get; private set => SetProperty(ref field, value); }
    public string? RecordId => recordId;

    /// <summary>A library action is running; the others wait.</summary>
    public bool IsBusy
    {
        get => field;
        private set
        {
            if (!SetProperty(ref field, value)) { return; }
            foreach (var command in new IRelayCommand[] { NewBuildCommand, SaveCommand, SaveAsNewCommand, RefreshLibraryCommand, OpenCommand, RenameSavedCommand, DeleteSavedCommand })
            { command.NotifyCanExecuteChanged(); }
        }
    }

    public ImmutableArray<LibraryEntry> LibraryEntries
    {
        get;
        private set
        {
            if (!SetProperty(ref field, value)) { return; }
            var chosen = SelectedSavedBuild?.Entry.Id;
            SavedBuilds = [.. value.Select(entry => new SavedBuild(entry, Pack.Version))];
            SelectedSavedBuild = SavedBuilds.FirstOrDefault(saved => saved.Entry.Id == chosen);
        }
    } = [];

    /// <summary>The saved builds as the picker lists them, each with when it was saved (G391).</summary>
    public ImmutableArray<SavedBuild> SavedBuilds { get; private set => SetProperty(ref field, value); } = [];

    /// <summary>The saved build chosen in the picker, which Open, Rename and Delete act on.</summary>
    public SavedBuild? SelectedSavedBuild { get; set => SetProperty(ref field, value); }

    /// <summary>Renames a saved build without opening it, as Go's library Rename; the open build's name follows when it is that one.</summary>
    public IAsyncRelayCommand<LibraryEntry> RenameSavedCommand => field ??= new AsyncRelayCommand<LibraryEntry>(RenameSavedAsync, _ => !IsBusy);

    /// <summary>Deletes a saved build after asking; the open build, if it was that one, becomes unsaved.</summary>
    public IAsyncRelayCommand<LibraryEntry> DeleteSavedCommand => field ??= new AsyncRelayCommand<LibraryEntry>(DeleteSavedAsync, _ => !IsBusy);

    private async Task RenameSavedAsync(LibraryEntry? entry) => await LibraryAction(async () =>
    {
        if (entry is null || Host is not { } host) { return; }
        if (entry.Problem is { } problem) { Message = $"That saved build can't be renamed: {problem}"; return; }
        var name = (await host.AskTextAsync("Rename build", "Build name", "Rename", entry.Name, value => value.Trim().Length == 0 ? "Enter a build name" : null))?.Trim();
        if (name is null || name == entry.Name) { return; }
        var loaded = await library.LoadAsync(entry.Id);
        var renamed = loaded.Build with { Meta = loaded.Build.Meta with { Name = name } };
        var saved = await library.ReplaceAsync(entry.Id, renamed, loaded.Version);
        if (saved.Id == recordId)
        {
            // The open build is that record: its name follows, and what is saved now is the renamed record.
            (recordVersion, baseline) = (saved.Version, Document(renamed));
            Apply(new NameOperation(name));
        }
        LibraryEntries = await library.ListAsync();
    });

    private async Task DeleteSavedAsync(LibraryEntry? entry) => await LibraryAction(async () =>
    {
        if (entry is null || Host is not { } host) { return; }
        // A record this version can't read may be fine in a newer one, and it has no name to show.
        var question = entry.Problem is { } problem
            ? $"This saved build can't be read: {problem}. If a newer planner made it, that planner can still open it. Delete it from the library? This can't be undone."
            : $"Delete \"{entry.Name}\" from the library? This can't be undone.";
        if (!await host.ConfirmAsync("Delete build", question, "Delete")) { return; }
        await library.DeleteAsync(entry.Id, entry.Version);
        if (entry.Id == recordId)
        {
            // The open build stays open, no longer saved anywhere.
            (recordId, recordVersion, baseline) = (null, null, []);
            OnPropertyChanged(nameof(RecordId));
            UpdateDirty(session.Snapshot);
        }
        LibraryEntries = await library.ListAsync();
    });

    private async Task NewBuildAsync() => await LibraryAction(async () =>
    {
        if (!await MayDiscardAsync()) { return; }
        Open(BuildSnapshot.Create(Pack), null);
    });

    private async Task SaveAsync(bool asNew) => await LibraryAction(async () =>
    {
        if (asNew) { replacementChoiceFor = null; }
        // A value still being typed is part of what the user means to save (Ctrl+S doesn't move focus). One that can't be
        // committed stops the save, with its message showing, rather than being silently left out.
        var drafts = SlotGroups.SelectMany(group => group.Slots).SelectMany(slot => slot.Drafts).Concat(SkillSlots.SelectMany(slot => slot.Drafts)).Concat(AccountUpgrades)
            .Concat(AssumptionSections.SelectMany(section => section.Assumptions).Where(assumption => assumption.IsVisible)).Concat(Party.Members.Select(member => member.Armor)).Append<IDraftValue>(Notes).ToList();
        if (!drafts.Select(draft => draft.CommitValue()).ToList().TrueForAll(committed => committed))
        {
            var reason = Message;
            Message = $"Not saved: {reason}";
            return;
        }
        var build = session.Snapshot.State.Build;
        var listedForName = !(recordId is not null && recordVersion is not null && !asNew) && !asNew && !string.IsNullOrWhiteSpace(build.Meta.Name);
        var saved = recordId is { } id && recordVersion is { } version && !asNew
            ? await library.ReplaceAsync(id, build, version)
            : await SaveNewAsync(build, asNew);
        if (saved is null) { return; }
        replacementChoiceFor = null;
        (recordId, recordVersion, baseline) = (saved.Id, saved.Version, Document(build));
        OnPropertyChanged(nameof(RecordId));
        UpdateDirty(session.Snapshot);
        LibraryEntries = listedForName ? WithSavedEntry(saved) : await library.ListAsync();
    }, preserveSaveChoice: true);

    private ImmutableArray<LibraryEntry> WithSavedEntry(LibraryBuild saved) =>
        [.. LibraryEntries.Where(entry => entry.Id != saved.Id).Append(new(saved.Id, saved.Build.Meta.Name,
            saved.Build.PackId, saved.Build.PackVersion, saved.SavedAt, saved.Version))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.Id, StringComparer.Ordinal)];

    private async Task<LibraryBuild?> SaveNewAsync(BuildSnapshot build, bool asNew)
    {
        // Save as new deliberately makes a copy, even when its name is already in use.
        var name = build.Meta.Name.Trim();
        if (asNew || name.Length == 0) { return await library.CreateAsync(build); }
        LibraryEntries = await library.ListAsync();
        // A real damaged record has no recoverable name/pack metadata. Do not pretend it can be matched.
        if (LibraryEntries.Any(entry => entry.Problem is not null))
        {
            replacementChoiceFor = null;
            Message = "The library contains unreadable records, so name matches can't be checked reliably. Nothing was saved. Use Save as new to create a copy while keeping those records.";
            return null;
        }
        var matches = LibraryEntries.Where(entry => entry.PackId == build.PackId &&
            string.Equals(entry.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0) { return await library.CreateAsync(build); }
        if (matches.Length > 1 && !ReferenceEquals(replacementChoiceFor, build))
        {
            // An earlier picker selection is not consent to replace a duplicate during this save.
            SelectedSavedBuild = null;
            replacementChoiceFor = build;
        }
        var match = matches.Length == 1 ? matches[0] : matches.FirstOrDefault(entry => entry.Id == SelectedSavedBuild?.Entry.Id);
        if (match is null)
        {
            Message = $"More than one saved build is named \"{name}\". Select the one to replace in the library and Save again, or choose Save as new.";
            return null;
        }
        replacementChoiceFor = null; // Consume this choice even if confirmation is cancelled.
        if (Host is not { } host)
        {
            Message = $"Replacing the saved build \"{name}\" requires confirmation. Nothing was saved. Use Save as new to keep both builds.";
            return null;
        }
        var question = matches.Length == 1
            ? $"A saved build named \"{match.Name}\" already exists. Replace it with the current build?"
            : $"Replace the copy of \"{match.Name}\" you just selected in the library? Other saved copies will be kept.";
        if (!string.Equals(match.Name, build.Meta.Name, StringComparison.Ordinal))
        { question += $" Its name will change from \"{match.Name}\" to \"{build.Meta.Name}\"."; }
        if (!await host.ConfirmAsync("Replace saved build", question, "Replace")) { return null; }
        try
        {
            // Keep the version approved by the user; never refresh it after confirmation.
            return await library.ReplaceAsync(match.Id, build, match.Version);
        }
        catch (LibraryException exception) when (exception.Code == LibraryErrorCodes.Conflict)
        {
            Message = $"The saved build \"{match.Name}\" changed while replacement was being confirmed. Nothing was saved. Review the library and Save again, or choose Save as new.";
            return null;
        }
    }

    private async Task RefreshLibraryAsync() => await LibraryAction(async () => LibraryEntries = await library.ListAsync());

    private async Task OpenAsync(LibraryEntry? entry) => await LibraryAction(async () =>
    {
        if (entry is null) { return; }
        if (entry.Problem is { } problem) { Message = $"That saved build can't be opened: {problem}"; return; }
        var loaded = await library.LoadAsync(entry.Id);
        if (!await MayDiscardAsync()) { return; }
        Open(loaded.Build, loaded);
    });

    /// <summary>Runs one library action at a time, reporting its failures in <see cref="Message"/>.</summary>
    private async Task LibraryAction(Func<Task> action, bool preserveSaveChoice = false)
    {
        if (IsBusy) { return; }
        if (!preserveSaveChoice) { replacementChoiceFor = null; }
        IsBusy = true;
        try
        {
            Message = null;
            await action();
        }
        catch (LibraryException exception)
        {
            Message = exception.Code == LibraryErrorCodes.Conflict ? $"{exception.Message}. Save it as a new build, or open the saved one." : exception.Message;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Message = $"The library couldn't be read or written: {exception.Message}";
        }
        finally { IsBusy = false; }
    }

    /// <summary>Opens an imported build as Open does (fresh history, asking before unsaved changes go); false when it wasn't.</summary>
    internal async Task<bool> OpenImportedAsync(BuildSnapshot build)
    {
        var opened = false;
        await LibraryAction(async () =>
        {
            if (!await MayDiscardAsync()) { return; }
            Open(build, null);
            opened = true;
        });
        return opened;
    }

    private async Task<bool> MayDiscardAsync()
    {
        // Notes still waiting to be saved are unsaved changes too.
        Notes.CommitValue();
        if (!IsDirty) { return true; }
        if (Host is { } host && await host.ConfirmDiscardAsync()) { return true; }
        Message = "The build has unsaved changes; save it first, or confirm discarding them.";
        return false;
    }

    /// <summary>Opens a build in the session with fresh history; <paramref name="record"/> is its library record, if any.</summary>
    private void Open(BuildSnapshot build, LibraryBuild? record)
    {
        var result = session.Reset(PlannerState.Of(build), session.Snapshot.Revision);
        if (!result.Succeeded) { Report(result); return; }
        (recordId, recordVersion, baseline) = (record?.Id, record?.Version, Document(build));
        OnPropertyChanged(nameof(RecordId));
        Report(result);
        UpdateDirty(result.Snapshot);
    }

    private void UpdateDirty(SessionSnapshot snapshot) => IsDirty = !Document(snapshot.State.Build).AsSpan().SequenceEqual(baseline);

    /// <summary>The build as its document, for comparing content; a build that can't be saved compares unequal to anything saved.</summary>
    private static byte[] Document(BuildSnapshot build)
    {
        try { return BuildDocument.Encode(build); }
        catch (ArgumentException) { return []; }
    }
}
