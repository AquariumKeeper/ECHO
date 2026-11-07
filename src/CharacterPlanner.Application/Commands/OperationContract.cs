using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;

namespace CharacterPlanner.Application.Commands;

// The contract between build operations and the session. Operations are data: the same
// records drive desktop edits, CLI `build apply`, live-session transactions and undo labels.

/// <summary>One edit to a build or to its view assumptions. Concrete kinds live in BuildOperations.cs.</summary>
public abstract record BuildOperation
{
    /// <summary>The operation kind as written in operation JSON, e.g. "equipment" or "context".</summary>
    public abstract string Kind { get; }

    /// <summary>A short human label for history, e.g. "equipment primary_weapon primary_weapon_acr".</summary>
    public abstract string Label { get; }
}

/// <summary>What an edit acts on: the build itself and the view assumptions, which are never saved with the build.</summary>
public sealed record PlannerState(BuildSnapshot Build, ImmutableDictionary<string, string> Assumptions)
{
    public static PlannerState Of(BuildSnapshot build) => new(build, ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal));
}

/// <summary>The document paths one operation set or cleared (1-based operation index), e.g. "equipment.mask".</summary>
public sealed record OperationChange(int Operation, string Kind, ImmutableArray<string> Set, ImmutableArray<string> Cleared);

/// <summary>Why an operation was rejected (1-based operation index). Any error rejects the whole batch.</summary>
public sealed record OperationError(int Operation, string Kind, string Code, string Message);

/// <summary>The result of applying a batch: all operations or none.</summary>
public sealed record OperationOutcome(PlannerState State, ImmutableArray<OperationChange> Changes, ImmutableArray<OperationError> Errors)
{
    public bool Succeeded => Errors.IsEmpty;
}

/// <summary>Applies a batch of operations atomically against a pack. Implementations are pure and thread-safe.</summary>
public interface IOperationApplier
{
    OperationOutcome Apply(PlannerState state, ImmutableArray<BuildOperation> operations);
}
