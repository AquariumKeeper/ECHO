using CharacterPlanner.Application.Commands;

namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>
/// The Notes tab (F47), as the Go app's <c>notes.go</c>: notes saved with the build. Typing is saved after a pause, so a
/// burst of typing is one undo step rather than one per key. Undo, opening a build or another client's edit replace
/// the text, unless typing is still waiting to be saved.
/// </summary>
public sealed class NotesViewModel : ViewModelBase, IDraftValue
{
    public const string Placeholder = "Notes saved with this build: rotation, swaps, what to farm next...";
    public const string Hint = "Saved with the build in the library and in share codes.";

    /// <summary>The pause after the last keystroke before the notes are saved, as the Go app's.</summary>
    public static readonly TimeSpan CommitDelay = TimeSpan.FromMilliseconds(700);

    private readonly EditorViewModel editor;
    private string text = "";
    /// <summary>Keystrokes so far, and the one last saved: typing is waiting while they differ.</summary>
    private long edits;
    private long saved;

    internal NotesViewModel(EditorViewModel editor, IUiDispatcher ui)
    {
        this.editor = editor;
        Schedule = action => _ = Later(action);

        async Task Later(Action action)
        {
            await Task.Delay(CommitDelay).ConfigureAwait(false);
            ui.Post(action);
        }
    }

    /// <summary>Runs a save once the typing pause ends, on the UI thread. Tests replace it to end the pause themselves.</summary>
    public Action<Action> Schedule { get; set; }

    public string Text
    {
        get => text;
        set
        {
            if (!SetProperty(ref text, value ?? "") || editor.Refreshing) { return; }
            // Only the latest keystroke's save runs; earlier ones find a newer edit.
            var edit = ++edits;
            Schedule(() => { if (edit == edits) { CommitValue(); } });
        }
    }

    /// <summary>Whether typing is waiting to be saved.</summary>
    public bool IsPending => saved != edits;

    /// <summary>Saves typing that is still waiting now (before a save or an open), as one undo step.</summary>
    public bool CommitValue()
    {
        if (!IsPending) { return true; }
        var edit = edits;
        // The typing counts as saved only once the session takes it; a refused edit keeps it waiting, and on show.
        if (Text != editor.Session.Snapshot.State.Build.Meta.Notes && !editor.Apply(new NotesOperation(Text))) { return false; }
        saved = edit;
        return true;
    }

    internal void Refresh(string notes)
    {
        // Shown while the editor refreshes, so the setter schedules no save.
        if (!IsPending) { Text = notes; }
    }
}
