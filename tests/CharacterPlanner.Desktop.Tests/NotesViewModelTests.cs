using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Infrastructure.Storage;

namespace CharacterPlanner.Desktop.Tests;

/// <summary>The Notes tab (F47), as the Go app's TestG388: typing saves after a pause as one undo step, and isn't overwritten while waiting.</summary>
public sealed class NotesViewModelTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "notes-" + Guid.NewGuid().ToString("N"));
    private readonly TestPack.QueueDispatcher dispatcher = new();
    private readonly PlannerSession session;
    private readonly EditorViewModel editor;
    private readonly List<Action> waiting = [];

    public NotesViewModelTests()
    {
        session = new(TestPack.Division, PlannerState.Of(BuildSnapshot.Create(TestPack.Division.Pack)), new OperationApplier(TestPack.Division));
        editor = new(session, new FileBuildLibrary(folder), dispatcher);
        editor.Notes.Schedule = waiting.Add;
    }

    public void Dispose()
    {
        editor.Dispose();
        if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); }
    }

    /// <summary>Applies operations as the editor's controls do, and shows the result.</summary>
    private bool Edit(params BuildOperation[] operations)
    {
        var result = session.Apply([.. operations], session.Snapshot.Revision, EditOrigin.Desktop);
        dispatcher.RunPending();
        return result.Succeeded;
    }

    /// <summary>Ends the typing pause.</summary>
    private void Pause()
    {
        var due = waiting.ToList();
        waiting.Clear();
        foreach (var action in due) { action(); }
    }

    private string Stored => session.Snapshot.State.Build.Meta.Notes;

    [Fact]
    public void TypingIsSavedAfterAPauseAsOneUndoStep()
    {
        var notes = editor.Notes;
        var before = session.Snapshot.Revision;
        notes.Text = "Swap to";
        Assert.True(notes.IsPending);
        // An edit while typing waits doesn't replace the text being typed.
        Assert.True(Edit(new ContextOperation("enemy_hit_rate", Value: 60)));
        Assert.Equal("Swap to", notes.Text);
        notes.Text = "Swap to Pestilence for bosses";
        Pause();
        Assert.Equal("Swap to Pestilence for bosses", Stored);
        Assert.Equal(before + 2, session.Snapshot.Revision);
        Assert.False(notes.IsPending);

        editor.UndoCommand.Execute(null);
        Assert.Equal(("", ""), (Stored, notes.Text));

        // Another client's change shows, and showing it saves nothing.
        var loaded = session.Snapshot.State.Build with { Meta = new("New Build", "Loaded from the library") };
        session.Reset(PlannerState.Of(loaded), session.Snapshot.Revision);
        dispatcher.RunPending();
        var revision = session.Snapshot.Revision;
        Pause();
        Assert.Equal("Loaded from the library", notes.Text);
        Assert.Equal(revision, session.Snapshot.Revision);
    }

    [Fact]
    public async Task WaitingNotesAreSavedWithTheBuildAndCountAsUnsavedBeforeOpening()
    {
        editor.Notes.Text = "Farm a better mask";
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Farm a better mask", Stored);
        Assert.False(editor.IsDirty);
        Pause();
        Assert.Equal("Farm a better mask", Stored);

        editor.Notes.Text = "Unsaved";
        var asked = 0;
        editor.Host = new TestHost(() => { asked++; return Task.FromResult(false); });
        await editor.NewBuildCommand.ExecuteAsync(null);
        Assert.Equal(1, asked);
        Assert.Equal("Unsaved", Stored);
    }
}
