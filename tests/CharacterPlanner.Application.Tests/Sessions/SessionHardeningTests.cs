using System.Collections.Concurrent;
using System.Collections.Immutable;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Application.Tests.Sessions.SessionFixtures;

namespace CharacterPlanner.Application.Tests.Sessions;

/// <summary>Regressions for the session review: re-entry, handler failures, coalescing, packs and job lifetime.</summary>
public sealed class SessionHardeningTests
{
    private static CancellationToken Test => TestContext.Current.CancellationToken;

    [Fact]
    public void AHandlerCannotChangeTheSessionSoEventsStayInRevisionOrder()
    {
        var session = Session();
        var seen = new List<long>();
        session.Changed += snapshot => session.Apply(Ops(new FakeNotes("nested")), snapshot.Revision, EditOrigin.Desktop);
        session.Changed += snapshot => seen.Add(snapshot.Revision);

        var result = session.Apply(Ops(new FakeNotes("outer")), 0, EditOrigin.Session);

        Assert.True(result.Succeeded);
        Assert.IsType<InvalidOperationException>(Assert.Single(result.NotificationErrors));
        Assert.Equal([1L], seen);
        Assert.Equal(1, session.Snapshot.Revision);
    }

    [Fact]
    public void AThrowingHandlerNeitherHidesTheCommitNorStopsOtherHandlers()
    {
        var session = Session();
        var later = new List<long>();
        session.Changed += _ => throw new InvalidOperationException("broken subscriber");
        session.Changed += snapshot => later.Add(snapshot.Revision);

        var result = session.Apply(Ops(new FakeNotes("kept")), 0, EditOrigin.Session);

        Assert.True(result.Succeeded);
        Assert.Equal("broken subscriber", Assert.Single(result.NotificationErrors).Message);
        Assert.Equal([1L], later);
        Assert.Equal(1, result.Snapshot.Revision);
    }

    [Fact]
    public void CoalescingDoesNotSurviveAnUndoAndRedo()
    {
        var clock = new ManualClock();
        var session = Session(options: new() { Clock = clock });
        session.Apply(Ops(new FakeNotes("a")), 0, EditOrigin.Desktop, "slider");
        session.Undo(1);
        session.Redo(2);
        clock.Advance(TimeSpan.FromMilliseconds(10));
        session.Apply(Ops(new FakeNotes("b")), 3, EditOrigin.Desktop, "slider");

        // The redone edit stays an undo point; the new edit is its own entry.
        Assert.Equal([0L, 1L, 4L], session.Snapshot.History.Select(entry => entry.Revision));
    }

    [Fact]
    public void ABuildForAnotherPackNeedsANewSession()
    {
        var other = PlannerState.Of(new BuildSnapshot { PackId = "elsewhere", PackVersion = "1" });
        Assert.Throws<ArgumentException>(() => new PlannerSession(Pack, other, new FakeApplier()));

        var session = Session();
        var result = session.Reset(other, 0);
        Assert.Equal(SessionErrorCodes.PackMismatch, result.Error?.Code);
        Assert.Equal(0, session.Snapshot.Revision);
    }

    [Fact(Timeout = TestTimeouts.Gate)]
    public async Task ADisposedJobNeitherRunsNorPublishes()
    {
        var published = new ConcurrentQueue<JobResult<int>>();
        var job = new LatestRevisionJob<int>(published.Enqueue);
        job.Dispose();
        var ran = false;
        await job.Start(new(1, Pack, Initial, false, false, []), (_, _) => { ran = true; return 1; });
        Assert.False(ran);
        Assert.Empty(published);
    }

    [Fact(Timeout = TestTimeouts.Gate)]
    public async Task ANewSessionsSnapshotsStartANewSequence()
    {
        var published = new ConcurrentQueue<JobResult<long>>();
        using var job = new LatestRevisionJob<long>(published.Enqueue);
        await job.Start(new(10, Pack, Initial, false, false, []), (snapshot, _) => snapshot.Revision);
        var otherPack = Pack with { };
        await job.Start(new(1, otherPack, Initial, false, false, []), (snapshot, _) => snapshot.Revision);
        Assert.Equal([10L, 1L], published.Select(result => result.Revision));
        Assert.True(job.IsCurrent(published.Last()));
    }

    [Fact(Timeout = TestTimeouts.Gate)]
    public async Task PublishMayCallBackIntoTheJobAndSessionWithoutDeadlock()
    {
        var session = Session();
        LatestRevisionJob<long>? job = null;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        job = new LatestRevisionJob<long>(result =>
        {
            // A publish that writes back to the session and restarts the job, as an auto-fix might.
            if (result.Revision == 0)
            {
                var applied = session.Apply(Ops(new FakeNotes("derived")), 0, EditOrigin.Desktop);
                _ = job!.Start(applied.Snapshot, (snapshot, _) => snapshot.Revision);
            }
            else { done.TrySetResult(); }
        });
        session.Changed += snapshot => _ = job.Start(snapshot, (s, _) => s.Revision);
        using (job)
        {
            await job.Start(session.Snapshot, (snapshot, _) => snapshot.Revision);
            await done.Task.WaitAsync(TimeSpan.FromSeconds(10), Test);
        }
        Assert.Equal(1, session.Snapshot.Revision);
    }
}
