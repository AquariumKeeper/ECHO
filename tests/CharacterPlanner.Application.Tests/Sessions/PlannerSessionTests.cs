using System.Collections.Immutable;
using System.Text.Json;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using static CharacterPlanner.Application.Tests.Sessions.SessionFixtures;

namespace CharacterPlanner.Application.Tests.Sessions;

public sealed class PlannerSessionTests
{
    private static JsonElement Fixture(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "session", name))).RootElement;

    // The captured striker-rifle batch as fake operations, labelled "kind" plus its non-text values.
    private static ImmutableArray<BuildOperation> CapturedBuildBatch()
    {
        using var stdin = JsonDocument.Parse(Fixture("apply-build.json").GetProperty("stdin").GetString()!);
        return [.. stdin.RootElement.EnumerateArray().Select(operation =>
        {
            var kind = operation.GetProperty("kind").GetString()!;
            var values = operation.EnumerateObject().Where(p => p.Name is not ("kind" or "text")).Select(p => p.Value.ToString());
            return (BuildOperation)new FakeEdit(kind, string.Join(' ', [kind, .. values]));
        })];
    }

    [Fact]
    public void FollowsTheCapturedSessionSequenceWithUndoOfAContextEditKeepingRedo()
    {
        var session = Session();
        var initial = session.Snapshot;
        Assert.Equal(0, initial.Revision);
        Assert.False(initial.CanUndo);
        Assert.False(initial.CanRedo);

        var batch = CapturedBuildBatch();
        var build = session.Apply(batch, 0, EditOrigin.Session);
        Assert.True(build.Succeeded);
        Assert.Equal(Fixture("apply-build.json").GetProperty("response").GetProperty("revision").GetInt64(), build.Snapshot.Revision);
        Assert.Equal(batch.Length, build.Changes.Length);
        Assert.Equal(Enumerable.Range(1, batch.Length), build.Changes.Select(change => change.Operation));
        Assert.True(build.Snapshot.CanUndo);
        Assert.False(build.Snapshot.CanRedo);

        var stale = session.Apply(Ops(new FakeNotes("stale")), 0, EditOrigin.Session);
        var expected = Fixture("apply-stale.json").GetProperty("response");
        Assert.False(stale.Succeeded);
        Assert.Equal(expected.GetProperty("error").GetProperty("code").GetString(), stale.Error!.Code);
        Assert.Equal(expected.GetProperty("revision").GetInt64(), stale.Snapshot.Revision);
        Assert.Same(build.Snapshot, session.Snapshot);
        Assert.Equal("", session.Snapshot.State.Build.Meta.Notes);

        var notes = session.Apply(Ops(new FakeNotes("Edited through a session.")), 1, EditOrigin.Session);
        Assert.Equal(2, notes.Snapshot.Revision);
        Assert.Equal(["meta.Notes"], notes.Changes.Single().Set);
        Assert.Equal("Edited through a session.", notes.Snapshot.State.Build.Meta.Notes);

        var undo = session.Undo(2);
        Assert.Equal(3, undo.Snapshot.Revision);
        Assert.Same(build.Snapshot.State, undo.Snapshot.State);
        Assert.True(undo.Snapshot.CanUndo);
        Assert.True(undo.Snapshot.CanRedo);

        var redo = session.Redo(3);
        Assert.Equal(4, redo.Snapshot.Revision);
        Assert.Same(notes.Snapshot.State, redo.Snapshot.State);
        Assert.False(redo.Snapshot.CanRedo);

        var context = session.Apply(Ops(new FakeContext("enemy_target", "2")), 4, EditOrigin.Session);
        Assert.Equal(5, context.Snapshot.Revision);
        Assert.Equal("2", context.Snapshot.State.Assumptions["enemy_target"]);
        Assert.Equal(["context.enemy_target"], context.Changes.Single().Set);

        // K01 fixed: an ordinary undo, restoring the assumptions and keeping redo.
        var undoContext = session.Undo(5);
        Assert.Equal(6, undoContext.Snapshot.Revision);
        Assert.False(undoContext.Snapshot.State.Assumptions.ContainsKey("enemy_target"));
        Assert.Same(notes.Snapshot.State, undoContext.Snapshot.State);
        Assert.True(undoContext.Snapshot.CanRedo);
        Assert.Equal(
            [new(0, EditOrigin.Initial, null, false), new(1, EditOrigin.Session, PlannerSession.Label(batch), false),
             new(2, EditOrigin.Session, "notes", true), new HistoryEntry(5, EditOrigin.Session, "context enemy_target 2", false)],
            undoContext.Snapshot.History);

        var redoContext = session.Redo(6);
        Assert.Equal(7, redoContext.Snapshot.Revision);
        Assert.Equal("2", redoContext.Snapshot.State.Assumptions["enemy_target"]);
        Assert.True(redoContext.Snapshot.History[^1].Current);
    }

    [Fact]
    public void HistoryMatchesTheCapturedShapeAndLabels()
    {
        var captured = Fixture("history.json").GetProperty("response").GetProperty("result").GetProperty("history").EnumerateArray().ToArray();
        var session = Session();
        var batch = CapturedBuildBatch();
        session.Apply(batch, 0, EditOrigin.Session);
        session.Apply(Ops(new FakeNotes("Edited through a session.")), 1, EditOrigin.Session);
        session.Apply(Ops(new FakeEdit("name", "name")), 2, EditOrigin.Desktop);
        var history = session.Snapshot.History;

        // The captured listing's first three rows, and its desktop row as the current one.
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(captured[i].GetProperty("revision").GetInt64(), history[i].Revision);
            Assert.Equal(captured[i].GetProperty("origin").GetString(), history[i].Origin.ToString().ToLowerInvariant());
            Assert.Equal(captured[i].TryGetProperty("label", out var label) ? label.GetString() : null, history[i].Label);
            Assert.Equal(captured[i].TryGetProperty("current", out var current) && current.GetBoolean(), history[i].Current);
        }
        Assert.Equal(51, batch.Length);
        Assert.EndsWith("; 48 more", history[1].Label, StringComparison.Ordinal);
        Assert.Equal(captured[^1].GetProperty("origin").GetString(), history[^1].Origin.ToString().ToLowerInvariant());
        Assert.True(history[^1].Current);
    }

    [Fact]
    public void LabelsShowTheFirstThreeOperationsThenHowManyMore()
    {
        static BuildOperation Op(int i) => new FakeEdit("kind", $"op{i}");
        Assert.Equal("op1", PlannerSession.Label(Ops(Op(1))));
        Assert.Equal("op1; op2; op3", PlannerSession.Label(Ops(Op(1), Op(2), Op(3))));
        Assert.Equal("op1; op2; op3; 1 more", PlannerSession.Label(Ops(Op(1), Op(2), Op(3), Op(4))));
    }

    [Fact]
    public void ApplyClearsRedo()
    {
        var session = Session();
        session.Apply(Ops(new FakeNotes("a")), 0, EditOrigin.Desktop);
        session.Apply(Ops(new FakeNotes("b")), 1, EditOrigin.Desktop);
        session.Undo(2);
        Assert.True(session.Snapshot.CanRedo);

        var result = session.Apply(Ops(new FakeNotes("c")), 3, EditOrigin.Desktop);
        Assert.False(result.Snapshot.CanRedo);
        Assert.Equal([0L, 1, 4], result.Snapshot.History.Select(entry => entry.Revision));
        Assert.Equal(SessionErrorCodes.NothingToRedo, session.Redo(4).Error!.Code);
        Assert.Equal(4, session.Snapshot.Revision);
    }

    [Fact]
    public void HistoryIsBounded()
    {
        var session = Session(options: new() { HistoryLimit = 3 });
        for (var i = 0; i < 5; i++) { session.Apply(Ops(new FakeNotes($"n{i}")), i, EditOrigin.Desktop); }
        Assert.Equal([2L, 3, 4, 5], session.Snapshot.History.Select(entry => entry.Revision));

        for (var i = 0; i < 3; i++) { Assert.True(session.Undo(session.Snapshot.Revision).Succeeded); }
        Assert.Equal("n1", session.Snapshot.State.Build.Meta.Notes);
        Assert.False(session.Snapshot.CanUndo);
        var refused = session.Undo(session.Snapshot.Revision);
        Assert.Equal(SessionErrorCodes.NothingToUndo, refused.Error!.Code);
        Assert.Equal(8, session.Snapshot.Revision);
    }

    [Fact]
    public void RejectedOrFailingBatchesChangeNothing()
    {
        var session = Session();
        session.Apply(Ops(new FakeNotes("kept")), 0, EditOrigin.Desktop);
        var before = session.Snapshot;
        var notified = 0;
        session.Changed += _ => notified++;

        var rejected = session.Apply(Ops(new FakeNotes("lost"), new FakeInvalid()), 1, EditOrigin.Session);
        Assert.Equal(SessionErrorCodes.OperationsRejected, rejected.Error!.Code);
        Assert.Equal(2, rejected.OperationErrors.Single().Operation);
        Assert.Same(before, rejected.Snapshot);

        Assert.Throws<InvalidOperationException>(() => session.Apply(Ops(new FakeNotes("lost"), new FakeThrows()), 1, EditOrigin.Session));
        Assert.Equal(SessionErrorCodes.NoOperations, session.Apply([], 1, EditOrigin.Session).Error!.Code);
        Assert.Equal(SessionErrorCodes.StaleRevision, session.Undo(0).Error!.Code);
        Assert.Equal(SessionErrorCodes.StaleRevision, session.Redo(7).Error!.Code);

        Assert.Same(before, session.Snapshot);
        Assert.Equal("kept", session.Snapshot.State.Build.Meta.Notes);
        Assert.Equal(0, notified);
        Assert.True(session.Apply(Ops(new FakeNotes("next")), 1, EditOrigin.Session).Succeeded);
    }

    [Fact]
    public void RapidEditsWithTheSameKeyShareAnUndoStep()
    {
        var clock = new ManualClock();
        var session = Session(options: new() { Clock = clock, CoalescingWindow = TimeSpan.FromMilliseconds(500) });
        session.Apply(Ops(new FakeNotes("before")), 0, EditOrigin.Desktop);
        foreach (var value in new[] { "71", "72", "73" })
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            Assert.True(session.Apply(Ops(new FakeContext("hit", value)), session.Snapshot.Revision, EditOrigin.Desktop, "slider:hit").Succeeded);
        }
        Assert.Equal(4, session.Snapshot.Revision);
        Assert.Equal([0L, 1, 4], session.Snapshot.History.Select(entry => entry.Revision));
        Assert.Equal("context hit 73", session.Snapshot.History[^1].Label);

        // A pause longer than the window starts a new step.
        clock.Advance(TimeSpan.FromSeconds(1));
        session.Apply(Ops(new FakeContext("hit", "80")), 4, EditOrigin.Desktop, "slider:hit");
        Assert.Equal(4, session.Snapshot.History.Length);

        session.Undo(5);
        Assert.Equal("73", session.Snapshot.State.Assumptions["hit"]);
        session.Undo(6);
        Assert.False(session.Snapshot.State.Assumptions.ContainsKey("hit"));
        Assert.Equal("before", session.Snapshot.State.Build.Meta.Notes);

        // After an undo, the same key no longer coalesces into the entry it moved off.
        session.Apply(Ops(new FakeContext("hit", "90")), 7, EditOrigin.Desktop, "slider:hit");
        Assert.Equal([0L, 1, 8], session.Snapshot.History.Select(entry => entry.Revision));
    }

    [Fact]
    public void ChangedReportsEachAcceptedRevisionInOrder()
    {
        var session = Session();
        var seen = new List<long>();
        session.Changed += snapshot => seen.Add(snapshot.Revision);
        session.Apply(Ops(new FakeNotes("a")), 0, EditOrigin.Desktop);
        session.Apply(Ops(new FakeNotes("stale")), 0, EditOrigin.Desktop);
        session.Undo(1);
        session.Redo(2);
        Assert.Equal([1L, 2, 3], seen);
    }

    [Fact(Timeout = TestTimeouts.Gate)]
    public async Task ABlockedApplyHoldsOffTheNextUntilItCommits()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var first = true;
        var applier = new FakeApplier
        {
            OnEnter = () =>
            {
                if (!first) { return; }
                first = false;
                entered.SetResult();
                release.Wait(TestContext.Current.CancellationToken);
            }
        };
        var session = Session(applier);

        var blocked = Task.Run(() => session.Apply(Ops(new FakeNotes("first")), 0, EditOrigin.Desktop), TestContext.Current.CancellationToken);
        await entered.Task;
        var second = Task.Run(() => session.Apply(Ops(new FakeNotes("second")), 0, EditOrigin.Session), TestContext.Current.CancellationToken);
        Assert.Equal(0, session.Snapshot.Revision);
        release.Set();

        Assert.True((await blocked).Succeeded);
        // The second request named revision 0, so once serialized behind the first it is stale.
        Assert.Equal(SessionErrorCodes.StaleRevision, (await second).Error!.Code);
        Assert.Equal("first", session.Snapshot.State.Build.Meta.Notes);
        Assert.Equal(1, applier.MaxConcurrent);
    }

    [Fact(Timeout = TestTimeouts.Gate)]
    public async Task ConcurrentWritersAreSerialized()
    {
        var applier = new FakeApplier();
        var session = Session(applier, new() { HistoryLimit = 1000 });
        const int writers = 8, edits = 50;
        var seen = new List<long>();
        session.Changed += snapshot => seen.Add(snapshot.Revision);

        await Task.WhenAll(Enumerable.Range(0, writers).Select(writer => Task.Run(() =>
        {
            for (var i = 0; i < edits;)
            {
                if (session.Apply(Ops(new FakeNotes($"{writer}:{i}")), session.Snapshot.Revision, EditOrigin.Session).Succeeded) { i++; }
            }
        }, TestContext.Current.CancellationToken)));

        Assert.Equal(writers * edits, session.Snapshot.Revision);
        Assert.Equal(writers * edits + 1, session.Snapshot.History.Length);
        Assert.Equal(Enumerable.Range(1, writers * edits).Select(i => (long)i), seen);
        Assert.Equal(1, applier.MaxConcurrent);
    }
}
