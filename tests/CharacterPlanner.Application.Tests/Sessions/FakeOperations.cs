using System.Collections.Immutable;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Application.Tests.Sessions;

// Stand-ins for the real operations, so the session tests don't depend on their rules.

internal sealed record FakeNotes(string Text) : BuildOperation
{
    public override string Kind => "notes";
    public override string Label => "notes";
}

internal sealed record FakeContext(string Id, string Value) : BuildOperation
{
    public override string Kind => "context";
    public override string Label => $"context {Id} {Value}";
}

/// <summary>Any other kind: changes nothing but carries a label.</summary>
internal sealed record FakeEdit(string FakeKind, string FakeLabel) : BuildOperation
{
    public override string Kind => FakeKind;
    public override string Label => FakeLabel;
}

internal sealed record FakeInvalid : BuildOperation
{
    public override string Kind => "invalid";
    public override string Label => "invalid";
}

internal sealed record FakeThrows : BuildOperation
{
    public override string Kind => "throws";
    public override string Label => "throws";
}

internal sealed class FakeApplier : IOperationApplier
{
    private int inside;
    private int calls;

    /// <summary>Runs inside Apply, before the operations, e.g. to hold a caller at a gate.</summary>
    public Action? OnEnter { get; init; }

    public int MaxConcurrent { get; private set; }

    public int Calls => Volatile.Read(ref calls);

    public OperationOutcome Apply(PlannerState state, ImmutableArray<BuildOperation> operations)
    {
        Interlocked.Increment(ref calls);
        var now = Interlocked.Increment(ref inside);
        lock (this) { MaxConcurrent = Math.Max(MaxConcurrent, now); }
        try
        {
            OnEnter?.Invoke();
            var next = state;
            var changes = ImmutableArray.CreateBuilder<OperationChange>();
            var errors = ImmutableArray.CreateBuilder<OperationError>();
            foreach (var (operation, index) in operations.Select((operation, index) => (operation, index + 1)))
            {
                switch (operation)
                {
                    case FakeNotes notes:
                        next = next with { Build = next.Build with { Meta = next.Build.Meta with { Notes = notes.Text } } };
                        changes.Add(new(index, operation.Kind, ["meta.Notes"], []));
                        break;
                    case FakeContext context:
                        next = next with { Assumptions = next.Assumptions.SetItem(context.Id, context.Value) };
                        changes.Add(new(index, operation.Kind, [$"context.{context.Id}"], []));
                        break;
                    case FakeInvalid:
                        errors.Add(new(index, operation.Kind, "invalid", "always rejected"));
                        break;
                    case FakeThrows:
                        throw new InvalidOperationException("applier failed");
                    default:
                        changes.Add(new(index, operation.Kind, [], []));
                        break;
                }
            }
            return errors.Count > 0 ? new(state, [], errors.ToImmutable()) : new(next, changes.ToImmutable(), []);
        }
        finally
        {
            Interlocked.Decrement(ref inside);
        }
    }
}

internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => now;

    // The session times coalescing with monotonic timestamps; this clock's are its own ticks.
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => now.UtcTicks;

    public void Advance(TimeSpan by) => now += by;
}

internal static class SessionFixtures
{
    public static CompiledPackExpressions Pack { get; } = new(
        new GamePack { ID = "test", Version = "1", Level = 40 },
        ImmutableDictionary<string, Core.Expressions.Expression>.Empty,
        ImmutableDictionary<string, Core.Expressions.Expression>.Empty,
        ImmutableDictionary<string, Core.Expressions.Expression>.Empty,
        ImmutableDictionary<string, Core.Expressions.Expression>.Empty,
        [],
        [],
        ImmutableDictionary<string, Core.Expressions.Expression>.Empty);

    public static PlannerState Initial { get; } = PlannerState.Of(new BuildSnapshot { PackId = "test", PackVersion = "1" });

    public static PlannerSession Session(FakeApplier? applier = null, PlannerSessionOptions? options = null) =>
        new(Pack, Initial, applier ?? new FakeApplier(), options);

    public static ImmutableArray<BuildOperation> Ops(params BuildOperation[] operations) => [.. operations];
}
