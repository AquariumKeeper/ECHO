using CharacterPlanner.Application.Commands;
using CharacterPlanner.Core.Packs;
using CharacterPlanner.Simulation;

namespace CharacterPlanner.Application.Sessions;

public sealed record SimulationRequest(DummyFightOptions Options, bool UseTargets = true, double RetargetDelay = 1)
{
    /// <summary>Runs this request on one state: against targets, or on the unlimited dummy.</summary>
    public DummyFightResult Run(CompiledPackExpressions pack, PlannerState state, CancellationToken cancellationToken = default,
        IProgress<SimulationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(state);
        return UseTargets
            ? DummyWeaponSimulator.RunAgainstTargets(pack, state.Build, state.Assumptions, Options, RetargetDelay, cancellationToken, progress)
            : DummyWeaponSimulator.Run(pack, state.Build, state.Assumptions, Options, cancellationToken, progress);
    }
}
public sealed record SessionSimulationResult(SimulationRequest Request, DummyFightResult Fight, SessionSnapshot Snapshot);
public sealed record SimulationRunProgress(long Revision, SimulationProgress Progress)
{
    internal long Run { get; init; }
}

/// <summary>Runs a captured session revision in the background. Edits invalidate work and queued notifications.
/// Callbacks run outside locks on worker threads; consumers posting to a dispatcher must recheck IsCurrent there.</summary>
public sealed class SessionSimulation : IDisposable
{
    private readonly Lock gate = new();
    private readonly PlannerSession session;
    private readonly LatestRevisionJob<SessionSimulationResult> job;
    private readonly Action<SimulationRunProgress>? progress;
    private long generation;
    private long? requestedRevision;
    private bool disposed;

    public SessionSimulation(PlannerSession session, Action<JobResult<SessionSimulationResult>> publish,
        Action<SimulationRunProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(publish);
        this.session = session;
        this.progress = progress;
        job = new(result => { if (IsCurrent(result)) { publish(result); } });
        session.Changed += OnChanged;
    }

    /// <summary>Every request supersedes the previous one, including different options at the same revision.
    /// Engine validation failures are published as job errors; cancellation never publishes a failure.</summary>
    public Task Start(SimulationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Options);
        lock (gate)
        {
            if (disposed) { return Task.CompletedTask; }
            var snapshot = session.Snapshot;
            requestedRevision = snapshot.Revision;
            var mine = ++generation;
            return job.Start(snapshot, (captured, token) =>
            {
                var reporter = progress is null ? null : new Reporter(value =>
                {
                    var update = new SimulationRunProgress(captured.Revision, value) { Run = mine };
                    if (!token.IsCancellationRequested && IsCurrent(update)) { progress(update); }
                });
                return new(request, request.Run(captured.Pack, captured.State, token, reporter), captured);
            });
        }
    }

    public bool IsCurrent(JobResult<SessionSimulationResult> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (gate) { return !disposed && requestedRevision == result.Revision && session.Snapshot.Revision == result.Revision && job.IsCurrent(result); }
    }

    public bool IsCurrent(SimulationRunProgress update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (gate) { return !disposed && update.Run == generation && requestedRevision == update.Revision && session.Snapshot.Revision == update.Revision; }
    }

    public void Cancel()
    {
        lock (gate) { Invalidate(); }
    }

    private void OnChanged(SessionSnapshot snapshot)
    {
        lock (gate)
        {
            // A Start may already have captured this new revision before its change notification reaches us.
            if (requestedRevision is { } revision && snapshot.Revision > revision) { Invalidate(); }
        }
    }

    private void Invalidate()
    {
        generation++;
        requestedRevision = null;
        job.Cancel();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) { return; }
            disposed = true;
            generation++;
            requestedRevision = null;
            session.Changed -= OnChanged;
            job.Dispose();
        }
    }

    private sealed class Reporter(Action<SimulationProgress> report) : IProgress<SimulationProgress>
    {
        public void Report(SimulationProgress value) => report(value);
    }
}
