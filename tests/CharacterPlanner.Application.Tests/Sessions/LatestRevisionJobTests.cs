using System.Collections.Concurrent;
using CharacterPlanner.Application.Sessions;
using static CharacterPlanner.Application.Tests.Sessions.SessionFixtures;

namespace CharacterPlanner.Application.Tests.Sessions;

public sealed class LatestRevisionJobTests
{
    private static SessionSnapshot At(long revision) => new(revision, Pack, Initial, false, false, []);

    private static CancellationToken Test => TestContext.Current.CancellationToken;

    [Fact(Timeout = TestTimeouts.Gate)]
    public async Task DropsAResultOvertakenByANewerRevision()
    {
        var published = new ConcurrentQueue<JobResult<string>>();
        using var job = new LatestRevisionJob<string>(published.Enqueue);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();

        // The old run ignores its token and finishes after the new one; its result must still be dropped.
        var old = job.Start(At(1), (_, _) => { entered.SetResult(); release.Wait(Test); return "old"; });
        await entered.Task;
        await job.Start(At(2), (_, _) => "new");
        release.Set();
        await old;

        var result = Assert.Single(published);
        Assert.Equal((2L, "new"), (result.Revision, result.Value));
        Assert.True(result.Succeeded);
        Assert.True(job.IsCurrent(result));
    }

    [Fact(Timeout = TestTimeouts.Gate)]
    public async Task StartingANewRunCancelsThePreviousOne()
    {
        var published = new ConcurrentQueue<JobResult<int>>();
        using var job = new LatestRevisionJob<int>(published.Enqueue);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = job.Start(At(1), (_, token) =>
        {
            entered.SetResult();
            token.WaitHandle.WaitOne();
            observed.SetResult(token.IsCancellationRequested);
            token.ThrowIfCancellationRequested();
            return 1;
        });
        await entered.Task;
        var second = job.Start(At(2), (_, _) => 2);

        Assert.True(await observed.Task);
        await first;
        await second;
        Assert.Equal([2], published.Select(result => result.Value));
    }

    [Fact(Timeout = TestTimeouts.Gate)]
    public async Task CancelDropsTheRunningWork()
    {
        var published = new ConcurrentQueue<JobResult<int>>();
        using var job = new LatestRevisionJob<int>(published.Enqueue);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = job.Start(At(1), (_, token) => { entered.SetResult(); token.WaitHandle.WaitOne(); return 1; });
        await entered.Task;
        job.Cancel();
        await run;
        Assert.Empty(published);
    }

    [Fact(Timeout = TestTimeouts.Gate)]
    public async Task SurfacesExceptionsFromTheWork()
    {
        var published = new ConcurrentQueue<JobResult<int>>();
        using var job = new LatestRevisionJob<int>(published.Enqueue);

        await job.Start(At(3), (_, _) => throw new InvalidOperationException("resolver failed"));
        // An OperationCanceledException not caused by our token is a failure too.
        await job.Start(At(4), (_, _) => throw new OperationCanceledException());

        Assert.Collection(published,
            result =>
            {
                Assert.Equal(3, result.Revision);
                Assert.False(result.Succeeded);
                Assert.Equal("resolver failed", Assert.IsType<InvalidOperationException>(result.Error).Message);
            },
            result => Assert.IsType<OperationCanceledException>(result.Error));
    }

    [Fact(Timeout = TestTimeouts.Gate)]
    public async Task DropsAFailureOvertakenByANewerRevision()
    {
        var published = new ConcurrentQueue<JobResult<int>>();
        using var job = new LatestRevisionJob<int>(published.Enqueue);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();

        var old = job.Start(At(1), (_, _) => { entered.SetResult(); release.Wait(Test); throw new InvalidOperationException("stale"); });
        await entered.Task;
        await job.Start(At(2), (_, _) => 2);
        release.Set();
        await old;
        Assert.Equal([2], published.Select(result => result.Value));
    }

    [Fact(Timeout = TestTimeouts.Gate)]
    public async Task IgnoresRequestsOlderThanTheLatestAndReportsSupersededResults()
    {
        var published = new ConcurrentQueue<JobResult<int>>();
        using var job = new LatestRevisionJob<int>(published.Enqueue);

        await job.Start(At(5), (_, _) => 5);
        var ran = false;
        await job.Start(At(4), (_, _) => { ran = true; return 4; });
        Assert.False(ran);

        // A result queued to a dispatcher can be checked again before it is applied.
        var queued = Assert.Single(published);
        Assert.True(job.IsCurrent(queued));
        await job.Start(At(5), (_, _) => 50);
        Assert.False(job.IsCurrent(queued));
        Assert.Equal([5, 50], published.Select(result => result.Value));
    }
}
