using System.Collections.Immutable;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Application.Sessions;

/// <summary>Who made a history entry. Initial marks the state the session opened with.</summary>
public enum EditOrigin
{
    Initial,
    Desktop,
    Session
}

/// <summary>
/// One history listing row. Revision is the revision the entry was recorded at, so an
/// entry keeps its number after undo and redo move past it. Label is null for Initial.
/// </summary>
public sealed record HistoryEntry(long Revision, EditOrigin Origin, string? Label, bool Current);

/// <summary>An immutable view of the session. Compare Revision to detect change, never the records.</summary>
public sealed record SessionSnapshot(
    long Revision,
    CompiledPackExpressions Pack,
    PlannerState State,
    bool CanUndo,
    bool CanRedo,
    ImmutableArray<HistoryEntry> History);

/// <summary>
/// The build as it is and with each candidate item equipped, resolved in the slot's context. Current is null when the
/// build itself can't be evaluated; Candidates lacks the items that couldn't be equipped or evaluated.
/// </summary>
public sealed record ItemComparison(ResolvedBuild? Current, ImmutableDictionary<string, ResolvedBuild> Candidates)
{
    public string? Problem { get; init; }
    public EffectCoverageReport Coverage { get; init; } = EffectCoverageReport.Empty;
    public ImmutableDictionary<string, EffectCoverageReport> CandidateCoverage { get; init; } = ImmutableDictionary<string, EffectCoverageReport>.Empty;
}

/// <summary>Why the session refused a request; nothing changed.</summary>
public sealed record SessionError(string Code, string Message);

public static class SessionErrorCodes
{
    public const string StaleRevision = "stale-revision";
    public const string NoOperations = "no-operations";
    public const string OperationsRejected = "operations-rejected";
    public const string NothingToUndo = "nothing-to-undo";
    public const string NothingToRedo = "nothing-to-redo";
    public const string PackMismatch = "pack-mismatch";
}

/// <summary>
/// The session after a request. On rejection Snapshot is the unchanged current state and
/// Error says why; OperationErrors carries the applier's per-operation reasons.
/// NotificationErrors lists exceptions thrown by Changed handlers: the change itself was
/// committed and every other handler was still notified.
/// </summary>
public sealed record SessionResult(
    SessionSnapshot Snapshot,
    ImmutableArray<OperationChange> Changes,
    ImmutableArray<OperationError> OperationErrors,
    SessionError? Error)
{
    public bool Succeeded => Error is null;

    public ImmutableArray<Exception> NotificationErrors { get; init; } = [];
}

/// <summary>Session tuning. HistoryLimit counts undo steps; the initial entry is extra.</summary>
public sealed record PlannerSessionOptions
{
    public int HistoryLimit { get; init; } = 100;

    /// <summary>Edits with the same coalescing key within this window of the previous one share an undo step.</summary>
    public TimeSpan CoalescingWindow { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeProvider Clock { get; init; } = TimeProvider.System;
}
