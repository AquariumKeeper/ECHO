using System.Collections.Immutable;
using System.Text.Json;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Application.Tests.Commands;
using CharacterPlanner.Core.Engine;

namespace CharacterPlanner.Application.Tests.Integration;

/// <summary>
/// The real applier inside a real session, replaying the frozen live-session capture
/// (baseline/capture.py sessions()): per-operation changes and history labels must match,
/// and ledger bug K01 must stay fixed.
/// </summary>
public sealed class SessionFlowTests
{
    private static PlannerSession NewSession() => new(Division.Pack, Division.State(), Division.Applier);

    private static ImmutableArray<(int, string, string, string)> Frozen(string fixture) =>
        [.. Division.Json("session", fixture).GetProperty("response").GetProperty("result").GetProperty("changes").EnumerateArray().Select(change => (
            change.GetProperty("operation").GetInt32(), change.GetProperty("kind").GetString()!,
            string.Join(",", change.GetProperty("set").EnumerateArray().Select(path => path.GetString())),
            string.Join(",", change.GetProperty("cleared").EnumerateArray().Select(path => path.GetString()))))];

    private static ImmutableArray<(int, string, string, string)> Actual(SessionResult result) =>
        [.. result.Changes.Select(change => (change.Operation, change.Kind, string.Join(",", change.Set), string.Join(",", change.Cleared)))];

    private static SessionResult Applied(PlannerSession session, string operationsJson)
    {
        var result = session.Apply(Division.Read(operationsJson), session.Snapshot.Revision, EditOrigin.Session);
        Assert.True(result.Succeeded, $"{result.Error}{Environment.NewLine}{string.Join(Environment.NewLine, result.OperationErrors)}");
        return result;
    }

    [Fact]
    public void FrozenSessionSequenceReplaysWithK01Fixed()
    {
        var session = NewSession();
        var build = Applied(session, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "builds", "striker-rifle", "operations.json")));
        Assert.Equal(Frozen("apply-build.json"), Actual(build));

        var stale = session.Apply(Division.Read("""[{"kind":"notes","text":"stale"}]"""), 0, EditOrigin.Session);
        Assert.Equal(SessionErrorCodes.StaleRevision, stale.Error?.Code);
        Assert.Same(build.Snapshot, stale.Snapshot);

        var previousNotes = session.Snapshot.State.Build.Meta.Notes;
        var notes = Applied(session, """[{"kind":"notes","text":"Edited through a session."}]""");
        Assert.Equal(Frozen("apply-notes.json"), Actual(notes));
        Assert.True(session.Undo(session.Snapshot.Revision).Succeeded);
        Assert.Equal(previousNotes, session.Snapshot.State.Build.Meta.Notes);
        Assert.True(session.Redo(session.Snapshot.Revision).Succeeded);
        Assert.Equal("Edited through a session.", session.Snapshot.State.Build.Meta.Notes);

        var context = Applied(session, """[{"kind":"context","id":"enemy_target","value":2}]""");
        Assert.Equal(Frozen("apply-context.json"), Actual(context));
        Assert.Equal("2", session.Snapshot.State.Assumptions["enemy_target"]);

        // K01: undoing an assumption edit is an ordinary undo that keeps redo.
        Assert.True(session.Undo(session.Snapshot.Revision).Succeeded);
        Assert.False(session.Snapshot.State.Assumptions.ContainsKey("enemy_target"));
        Assert.True(session.Snapshot.CanRedo);
        Assert.True(session.Redo(session.Snapshot.Revision).Succeeded);
        Assert.Equal("2", session.Snapshot.State.Assumptions["enemy_target"]);

        // History labels match the frozen history for the two session edits it recorded.
        var frozenLabels = Division.Json("session", "history.json").GetProperty("response").GetProperty("result").GetProperty("history")
            .EnumerateArray().Where(entry => entry.GetProperty("origin").GetString() == "session").Select(entry => entry.GetProperty("label").GetString());
        Assert.Equal(frozenLabels, session.Snapshot.History.Where(entry => entry.Origin == EditOrigin.Session).Take(2).Select(entry => entry.Label));
    }

    [Fact]
    public void SessionStateResolvesLikeTheFrozenBuild()
    {
        var session = NewSession();
        Applied(session, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "builds", "striker-rifle", "operations.json")));
        var snapshot = session.Snapshot;
        var resolved = BuildResolver.Resolve(snapshot.Pack, snapshot.State.Build, snapshot.State.Assumptions);
        Assert.DoesNotContain(resolved.Diagnostics, diagnostic => diagnostic.Severity == "error");
        // The applied build is the frozen document, which the Legacy corpus already resolves against frozen stats.
        var frozen = Division.Decode(Division.Json("builds", "striker-rifle", "apply.json").GetProperty("response").GetProperty("document"));
        Assert.Equal(BuildResolver.Resolve(snapshot.Pack, frozen).Calculation.Stats.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            resolved.Calculation.Stats.OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void ResetOpensAnotherBuildWithFreshHistory()
    {
        var session = NewSession();
        Applied(session, """[{"kind":"name","text":"First"}]""");
        var revision = session.Snapshot.Revision;
        Assert.Equal(SessionErrorCodes.StaleRevision, session.Reset(Division.State(), revision - 1).Error?.Code);

        var reset = session.Reset(Division.State(Division.Geared()), revision);
        Assert.True(reset.Succeeded);
        Assert.Equal(revision + 1, reset.Snapshot.Revision);
        Assert.False(reset.Snapshot.CanUndo);
        Assert.False(reset.Snapshot.CanRedo);
        var entry = Assert.Single(reset.Snapshot.History);
        Assert.Equal((revision + 1, EditOrigin.Initial, true), (entry.Revision, entry.Origin, entry.Current));
        Assert.Equal("mask_striker_s_battlegear_mask", reset.Snapshot.State.Build.Equipment["mask"]);
    }

    [Fact(Timeout = TestTimeouts.Gate)]
    public async Task LatestRevisionJobResolvesSessionSnapshotsOffTheCallingThread()
    {
        var session = NewSession();
        Applied(session, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "builds", "striker-rifle", "operations.json")));
        var published = new TaskCompletionSource<JobResult<ResolvedBuild>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var job = new LatestRevisionJob<ResolvedBuild>(published.SetResult);
        await job.Start(session.Snapshot, (snapshot, _) => BuildResolver.Resolve(snapshot.Pack, snapshot.State.Build, snapshot.State.Assumptions));
        var result = await published.Task;
        Assert.True(result.Succeeded);
        Assert.Equal(session.Snapshot.Revision, result.Revision);
        Assert.True(job.IsCurrent(result));
        Assert.NotEmpty(result.Value!.Calculation.Stats);
    }
}
