using System.Collections.Concurrent;
using System.Collections.Immutable;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Application.Tests.Commands;
using CharacterPlanner.Core.Packs;
using CharacterPlanner.Simulation;

namespace CharacterPlanner.Application.Tests.Sessions;

public sealed class SessionSimulationTests
{
    private static SimulationRequest Request => new(new() { Duration = 0.1, Iterations = 3, Mode = SimulationMode.Random, Seed = 7 });
    private static PlannerSession Session()
    {
        var state = Division.State(Division.Empty().WithEquipment("primary_weapon", "primary_weapon_acr")) with
        {
            Assumptions = ImmutableDictionary<string, string>.Empty.Add(GamePack.WeaponSlotSelector, "primary_weapon").Add("enemy_target", "1")
        };
        return new(Division.Pack, state, Division.Applier);
    }

    private static void Edit(PlannerSession session) => Assert.True(session.Apply([new NameOperation("Edited")],
        session.Snapshot.Revision, EditOrigin.Desktop).Succeeded);

    [Theory(Timeout = TestTimeouts.Gate)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunsTheCapturedStateAndPreservesTheRequest(bool targets)
    {
        var session = Session();
        var results = new ConcurrentQueue<JobResult<SessionSimulationResult>>();
        var updates = new ConcurrentQueue<SimulationRunProgress>();
        using var simulation = new SessionSimulation(session, results.Enqueue, updates.Enqueue);
        var request = Request with { UseTargets = targets, RetargetDelay = 0.5 };
        var before = session.Snapshot;
        await simulation.Start(request);
        var result = Assert.Single(results);
        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.True(simulation.IsCurrent(result));
        Assert.Equal(0, result.Revision);
        Assert.Same(request, result.Value!.Request);
        Assert.Same(before, result.Value.Snapshot);
        Assert.Same(before.Pack, result.Value.Snapshot.Pack);
        Assert.Equal(3, result.Value.Fight.Iterations);
        Assert.Equal(7UL, result.Value.Fight.Seed);
        Assert.Equal(3, updates.Count);
        Assert.All(updates, update => Assert.True(simulation.IsCurrent(update)));
        var direct = targets ? DummyWeaponSimulator.RunAgainstTargets(session.Pack, session.Snapshot.State.Build,
            session.Snapshot.State.Assumptions, request.Options, request.RetargetDelay, TestContext.Current.CancellationToken)
            : DummyWeaponSimulator.Run(session.Pack, session.Snapshot.State.Build, session.Snapshot.State.Assumptions,
                request.Options, TestContext.Current.CancellationToken);
        Assert.Equal(direct.WeaponDps, result.Value.Fight.WeaponDps);
        Assert.Equal(direct.Timeline, result.Value.Fight.Timeline);
        Assert.Same(before, session.Snapshot);
        Assert.Equal(0, session.Snapshot.Revision);
    }

    [Theory(Timeout = TestTimeouts.Gate)]
    [InlineData("edit")]
    [InlineData("cancel")]
    [InlineData("dispose")]
    public async Task InvalidatingABlockedRunDropsItsResultAndQueuedProgress(string action)
    {
        var session = Session();
        var results = new ConcurrentQueue<JobResult<SessionSimulationResult>>();
        var entered = new TaskCompletionSource<SimulationRunProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var simulation = new SessionSimulation(session, results.Enqueue, update =>
        {
            entered.TrySetResult(update);
            release.Wait(TestContext.Current.CancellationToken);
        });
        var run = simulation.Start(Request);
        try
        {
            var update = await entered.Task;
            Assert.True(simulation.IsCurrent(update));
            if (action == "edit") { Edit(session); }
            else if (action == "cancel") { simulation.Cancel(); }
            else { simulation.Dispose(); }
            Assert.False(simulation.IsCurrent(update));
        }
        finally { release.Set(); }
        await run;
        Assert.Empty(results);
    }

    [Fact(Timeout = TestTimeouts.Gate)]
    public async Task NewOptionsAtTheSameRevisionSupersedeABlockedRun()
    {
        var session = Session();
        var results = new ConcurrentQueue<JobResult<SessionSimulationResult>>();
        var entered = new TaskCompletionSource<SimulationRunProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var calls = 0;
        using var simulation = new SessionSimulation(session, results.Enqueue, update =>
        {
            if (Interlocked.Increment(ref calls) != 1) { return; }
            entered.SetResult(update);
            release.Wait(TestContext.Current.CancellationToken);
        });
        var first = simulation.Start(Request);
        try
        {
            var update = await entered.Task;
            await simulation.Start(Request with { Options = Request.Options with { Seed = 9 } });
            Assert.False(simulation.IsCurrent(update));
        }
        finally { release.Set(); }
        await first;
        Assert.Equal(9UL, Assert.Single(results).Value!.Fight.Seed);
    }

    [Fact(Timeout = TestTimeouts.Gate)]
    public async Task SessionEditsInvalidateAlreadyQueuedResultsAndTheNextRunUsesTheNewRevision()
    {
        var session = Session();
        var results = new ConcurrentQueue<JobResult<SessionSimulationResult>>();
        using var simulation = new SessionSimulation(session, results.Enqueue);
        await simulation.Start(Request);
        var before = Assert.Single(results);
        Edit(session);
        Assert.False(simulation.IsCurrent(before));
        await simulation.Start(Request);
        Assert.Equal(2, results.Count);
        var after = results.Last();
        Assert.True(simulation.IsCurrent(after));
        Assert.Equal(1, after.Revision);
    }

    [Fact(Timeout = TestTimeouts.Gate)]
    public async Task ARunCapturingTheNewRevisionSurvivesItsPendingChangeNotification()
    {
        var session = Session();
        Task? next = null;
        SessionSimulation? simulation = null;
        session.Changed += _ => next = simulation!.Start(Request);
        var results = new ConcurrentQueue<JobResult<SessionSimulationResult>>();
        simulation = new(session, results.Enqueue);
        using (simulation)
        {
            Edit(session);
            await Assert.IsAssignableFrom<Task>(next);
            var result = Assert.Single(results);
            Assert.Equal(1, result.Revision);
            Assert.True(simulation.IsCurrent(result));
        }
    }

    [Fact(Timeout = TestTimeouts.Gate)]
    public async Task EngineRefusalsAreReportedWithTheirSimulationCode()
    {
        var results = new ConcurrentQueue<JobResult<SessionSimulationResult>>();
        using var simulation = new SessionSimulation(Session(), results.Enqueue);
        await simulation.Start(Request with { Options = Request.Options with { IncludeIncoming = true, Mode = SimulationMode.Average } });
        var result = Assert.Single(results);
        Assert.False(result.Succeeded);
        Assert.Null(result.Value);
        Assert.Equal("incoming-mode-unsupported", Assert.IsType<SimulationException>(result.Error).Code);
        Assert.True(simulation.IsCurrent(result));
    }

    [Fact(Timeout = TestTimeouts.Gate)]
    public async Task DisposeMakesLaterStartsNoOpsAndCallbacksMayCallBackIntoTheSession()
    {
        var session = Session();
        JobResult<SessionSimulationResult>? published = null;
        using var simulation = new SessionSimulation(session, result => { published = result; Edit(session); });
        await simulation.Start(Request);
        Assert.NotNull(published);
        Assert.False(simulation.IsCurrent(published));
        simulation.Dispose();
        await simulation.Start(Request);
        Assert.Equal(1, session.Snapshot.Revision);
    }
}
