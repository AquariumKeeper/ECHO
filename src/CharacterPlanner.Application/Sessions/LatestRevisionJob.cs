namespace CharacterPlanner.Application.Sessions;

/// <summary>What a job run produced for a revision: a value, or the exception the work threw.</summary>
public sealed record JobResult<T>(long Revision, T? Value, Exception? Error)
{
    public bool Succeeded => Error is null;

    internal long Run { get; init; }
}

/// <summary>
/// Background work that only ever publishes for the latest request. Each Start cancels the
/// previous run; a result (or failure) is published only if no newer Start, Cancel or Dispose
/// happened when it finished. Publishing runs on a thread-pool thread and outside this job's
/// lock, so publish may post to a dispatcher, call into the session or even start another run
/// without deadlocking. A result can be overtaken while it waits in a dispatcher queue: check
/// IsCurrent before using it.
/// </summary>
public sealed class LatestRevisionJob<T>(Action<JobResult<T>> publish) : IDisposable
{
    private readonly Lock gate = new();
    private CancellationTokenSource? running;
    private long run;
    private long latestRevision = long.MinValue;
    private object? latestPack;
    private bool disposed;

    /// <summary>
    /// Runs work for the snapshot on the thread pool. Within one pack, a request for an older
    /// revision than one already requested is ignored; a snapshot from another pack (a new
    /// session) starts a new sequence. After Dispose nothing runs. The task completes after
    /// publishing or dropping, and faults only if publish throws.
    /// </summary>
    public Task Start(SessionSnapshot snapshot, Func<SessionSnapshot, CancellationToken, T> work)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(work);
        CancellationTokenSource source;
        long mine;
        lock (gate)
        {
            if (disposed) { return Task.CompletedTask; }
            if (ReferenceEquals(snapshot.Pack, latestPack) && snapshot.Revision < latestRevision) { return Task.CompletedTask; }
            Supersede();
            latestPack = snapshot.Pack;
            latestRevision = snapshot.Revision;
            mine = run;
            source = running = new();
        }
        return Execute(snapshot, work, mine, source);
    }

    /// <summary>Cancels the running work; its result is dropped. The next Start begins a new sequence.</summary>
    public void Cancel()
    {
        lock (gate)
        {
            Supersede();
            latestPack = null;
            latestRevision = long.MinValue;
        }
    }

    /// <summary>Whether the result is still the latest request's result.</summary>
    public bool IsCurrent(JobResult<T> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (gate) { return !disposed && result.Run == run; }
    }

    /// <summary>Cancels the running work; afterwards Start does nothing and nothing is published.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            Supersede();
        }
    }

    private void Supersede()
    {
        run++;
        running?.Cancel();
        running = null;
    }

    private async Task Execute(SessionSnapshot snapshot, Func<SessionSnapshot, CancellationToken, T> work, long mine, CancellationTokenSource source)
    {
        var token = source.Token;
        JobResult<T>? result;
        try
        {
            var value = await Task.Run(() => work(snapshot, token), token).ConfigureAwait(false);
            result = new(snapshot.Revision, value, null) { Run = mine };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            result = null;
        }
#pragma warning disable CA1031 // The work's failure is the result, delivered to publish rather than lost.
        catch (Exception error)
#pragma warning restore CA1031
        {
            result = new(snapshot.Revision, default, error) { Run = mine };
        }
        bool current;
        lock (gate)
        {
            if (running == source) { running = null; }
            source.Dispose();
            current = result is not null && !disposed && mine == run;
        }
        // Outside the lock: a newer Start may overtake this publish, which IsCurrent reports.
        if (current) { publish(result!); }
    }
}
