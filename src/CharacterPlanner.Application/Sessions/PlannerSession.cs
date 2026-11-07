using System.Collections.Immutable;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Expressions;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Application.Sessions;

/// <summary>
/// The revisioned planner state shared by the desktop window and the local-session transport.
/// Every accepted request (apply, undo, redo) bumps Revision by one; a request naming any
/// other revision is rejected as stale and changes nothing. History is a bounded list of
/// state snapshots with a current index: undo and redo move the index, an apply drops
/// everything after it. Assumption edits are ordinary entries, so undoing one keeps redo.
/// The session holds one pack: a build for another pack needs a new session.
/// </summary>
public sealed class PlannerSession
{
    private const int LabelOperations = 3;

    private readonly Lock gate = new();
    private readonly IOperationApplier applier;
    private readonly PlannerSessionOptions options;
    private readonly List<Entry> entries;
    private int current;
    private long revision;
    private SessionSnapshot snapshot;

    public PlannerSession(CompiledPackExpressions pack, PlannerState initial, IOperationApplier applier, PlannerSessionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(applier);
        this.options = options ?? new();
        ArgumentOutOfRangeException.ThrowIfLessThan(this.options.HistoryLimit, 1, nameof(options));
        if (!BelongsTo(initial, pack)) { throw new ArgumentException($"The build is for pack {initial.Build.PackId}, not {pack.Pack.ID}.", nameof(initial)); }
        Pack = pack;
        this.applier = applier;
        entries = [new(0, EditOrigin.Initial, null, initial, null, this.options.Clock.GetTimestamp())];
        snapshot = TakeSnapshot();
    }

    public CompiledPackExpressions Pack { get; }

    /// <summary>The latest state. Lock-free; safe from any thread.</summary>
    public SessionSnapshot Snapshot => Volatile.Read(ref snapshot);

    /// <summary>
    /// Raised after every accepted change, in revision order, while the session lock is held.
    /// Handlers must not change the session (that call is rejected) or block on a thread that
    /// may call it: post to a dispatcher instead. A handler's exception never stops the others;
    /// it is returned in the result's NotificationErrors.
    /// </summary>
    public event Action<SessionSnapshot>? Changed;

    /// <summary>
    /// Applies a batch atomically. With a coalescing key (e.g. one slider drag), an edit that follows
    /// an edit with the same key and origin within the window replaces it instead of adding an undo step.
    /// </summary>
    public SessionResult Apply(ImmutableArray<BuildOperation> operations, long expectedRevision, EditOrigin origin, string? coalescingKey = null)
    {
        if (origin == EditOrigin.Initial) { throw new ArgumentOutOfRangeException(nameof(origin), "Only the opening state is Initial."); }
        RejectReentry();
        lock (gate)
        {
            if (Refuse(expectedRevision) is { } stale) { return stale; }
            if (operations.IsDefaultOrEmpty) { return Reject(SessionErrorCodes.NoOperations, "the batch has no operations"); }
            // The applier runs under the lock so concurrent batches serialize; if it throws, nothing has changed.
            var outcome = applier.Apply(entries[current].State, operations);
            if (!outcome.Succeeded)
            {
                return new(snapshot, [], outcome.Errors, new(SessionErrorCodes.OperationsRejected, "the batch was rejected; nothing was applied"));
            }
            var entry = new Entry(revision + 1, origin, Label(operations), outcome.State, coalescingKey, options.Clock.GetTimestamp());
            if (Coalesces(entry))
            {
                entries[current] = entry;
            }
            else
            {
                entries.RemoveRange(current + 1, entries.Count - current - 1);
                entries.Add(entry);
                var excess = entries.Count - 1 - options.HistoryLimit;
                if (excess > 0) { entries.RemoveRange(0, excess); }
                current = entries.Count - 1;
            }
            return Commit(outcome.Changes);
        }
    }

    public SessionResult Undo(long expectedRevision)
    {
        RejectReentry();
        lock (gate)
        {
            if (Refuse(expectedRevision) is { } stale) { return stale; }
            if (current == 0) { return Reject(SessionErrorCodes.NothingToUndo, "there is nothing to undo"); }
            current--;
            return Commit([]);
        }
    }

    public SessionResult Redo(long expectedRevision)
    {
        RejectReentry();
        lock (gate)
        {
            if (Refuse(expectedRevision) is { } stale) { return stale; }
            if (current == entries.Count - 1) { return Reject(SessionErrorCodes.NothingToRedo, "there is nothing to redo"); }
            current++;
            return Commit([]);
        }
    }

    /// <summary>
    /// Opens another build (or a new one) in this session: the state becomes a fresh opening entry and
    /// history restarts there, so nothing from the previous build can be undone into it.
    /// </summary>
    public SessionResult Reset(PlannerState state, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(state);
        RejectReentry();
        lock (gate)
        {
            if (Refuse(expectedRevision) is { } stale) { return stale; }
            if (!BelongsTo(state, Pack)) { return Reject(SessionErrorCodes.PackMismatch, $"the build is for pack {state.Build.PackId}, not {Pack.Pack.ID}; open it in a new session"); }
            entries.Clear();
            entries.Add(new(revision + 1, EditOrigin.Initial, null, state, null, options.Clock.GetTimestamp()));
            current = 0;
            return Commit([]);
        }
    }

    /// <summary>
    /// What applying operations would make of the snapshot's state, without applying them (a picker's highlighted
    /// item, previewed). Safe from any thread; the applier must be too.
    /// </summary>
    public OperationOutcome Preview(SessionSnapshot snapshot, ImmutableArray<BuildOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return applier.Apply(snapshot.State, operations);
    }

    /// <summary>
    /// What equipping each item in a slot would make of the snapshot's build, without applying anything (F40). Each
    /// candidate is equipped as an edit would equip it (its fixed choices filled), and it and the build as it is are
    /// resolved in the slot's context: a weapon slot's items are compared as the weapon in hand. An item that can't be
    /// equipped there, or whose build can't be evaluated, is left out. Safe from any thread; the applier must be too.
    /// </summary>
    public ItemComparison CompareItems(SessionSnapshot snapshot, string slotId, IEnumerable<string> itemIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(itemIds);
        cancellationToken.ThrowIfCancellationRequested();
        var state = InSlotContext(snapshot, slotId);
        if (LoadoutResolver.EquipmentLimitProblem(snapshot.Pack.Pack, state.Build) is { } problem)
        { return new(null, ImmutableDictionary<string, ResolvedBuild>.Empty) { Problem = problem.Message }; }
        var current = Resolve(snapshot.Pack, state);
        var candidates = ImmutableDictionary.CreateBuilder<string, ResolvedBuild>(StringComparer.Ordinal);
        var coverage = ImmutableDictionary.CreateBuilder<string, EffectCoverageReport>(StringComparer.Ordinal);
        foreach (var id in itemIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = applier.Apply(state, [new EquipmentOperation(slotId, id)]);
            if (outcome.Succeeded && Resolve(snapshot.Pack, outcome.State) is { } resolved)
            {
                candidates[id] = resolved;
                coverage[id] = EffectCoverage.Describe(snapshot.Pack.Pack, outcome.State.Build, outcome.State.Assumptions);
            }
        }
        return new(current, candidates.ToImmutable())
        {
            Coverage = EffectCoverage.Describe(snapshot.Pack.Pack, state.Build, state.Assumptions),
            CandidateCoverage = coverage.ToImmutable()
        };

        static ResolvedBuild? Resolve(CompiledPackExpressions pack, PlannerState state)
        {
            try
            {
                var resolved = BuildResolver.Resolve(pack, state.Build, state.Assumptions);
                return resolved.Diagnostics.Any(LoadoutResolver.IsEquipmentLimitError) ? null : resolved;
            }
            catch (ExpressionException) { return null; }
        }
    }

    /// <summary>
    /// Ranks every change to the item equipped in a slot by what it does to one stat, largest gain first, without
    /// applying anything (the Go app's Rank Upgrades). Each change is tried on its own against the snapshot's build and
    /// assumptions, resolved in the slot's context as <see cref="CompareItems"/> does: every attribute choice at its
    /// highest legal roll (the current choice only as a reroll, when its roll is below that), every mod at its
    /// default roll (the current mod likewise), and every other talent. A change the applier refuses, that can't be
    /// evaluated, or that adds an error diagnostic (such as a repeated attribute) is left out. Each result carries the
    /// operations that make it, so applying one is a normal undo step. Safe from any thread; the applier must be too.
    /// </summary>
    public UpgradeRanking RankUpgrades(SessionSnapshot snapshot, string slotId, string statId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        var pack = snapshot.Pack.Pack;
        var state = InSlotContext(snapshot, slotId);
        var build = state.Build;
        if (LoadoutResolver.EquipmentLimitProblem(pack, build) is { } problem)
        { return new(statId, 0, [], problem.Message); }
        var itemId = build.Equipment.GetValueOrDefault(slotId, "");
        if (itemId.Length == 0 || pack.Items.FirstOrDefault(item => item.ID == itemId) is not { } item)
        { return new(statId, 0, [], $"slot \"{slotId}\" has no item equipped"); }
        if (!pack.Stats.Any(stat => stat.ID == statId)) { return new(statId, 0, [], $"unknown stat \"{statId}\""); }
        ResolvedBuild current;
        try { current = BuildResolver.Resolve(snapshot.Pack, build, state.Assumptions); }
        catch (ExpressionException exception) { return new(statId, 0, [], $"the build can't be evaluated: {exception.Message}"); }
        var before = current.Calculation.Stats.GetValueOrDefault(statId);
        var errors = Errors(current);

        var rolls = build.AttributeRolls.GetValueOrDefault(slotId);
        var candidates = new List<(UpgradeKind Kind, string Target, string TargetName, string Id, bool Reroll, BuildOperation Operation)>();
        foreach (var slot in item.AttributeSlots)
        {
            var rolled = rolls?.GetValueOrDefault(slot.ID);
            foreach (var choice in slot.Choices)
            { candidates.Add((UpgradeKind.Attribute, slot.ID, slot.Name, choice.AttributeID, rolled?.AttributeId == choice.AttributeID, new AttributeOperation(slotId, slot.ID, choice.AttributeID))); }
        }
        foreach (var slot in item.ModSlots)
        {
            var selected = build.ModSelections.GetValueOrDefault(slotId)?.GetValueOrDefault(slot.ID);
            // Reselecting the current mod keeps its roll, so its reroll sets the roll instead.
            foreach (var mod in slot.Choices)
            {
                BuildOperation operation = mod == selected ? new ModRollOperation(slotId, slot.ID) : new ModOperation(slotId, slot.ID, mod);
                candidates.Add((UpgradeKind.Mod, slot.ID, slot.Name, mod, mod == selected, operation));
            }
        }
        foreach (var slot in item.TalentSlots)
        {
            var selected = build.TalentSelections.GetValueOrDefault(slotId)?.GetValueOrDefault(slot.ID);
            foreach (var talent in slot.Choices.Where(talent => talent != selected))
            { candidates.Add((UpgradeKind.Talent, slot.ID, slot.Name, talent, false, new TalentOperation(slotId, slot.ID, talent))); }
        }

        var upgrades = new List<RankedUpgrade>();
        foreach (var (kind, target, targetName, id, reroll, operation) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = applier.Apply(state, [operation]);
            if (!outcome.Succeeded) { continue; }
            var edited = outcome.State.Build;
            var value = Roll(edited, kind, slotId, target);
            // A reroll is only a change when it raises the roll.
            if (reroll && (value is not { } raised || Roll(build, kind, slotId, target) >= raised)) { continue; }
            ResolvedBuild resolved;
            try { resolved = BuildResolver.Resolve(snapshot.Pack, edited, outcome.State.Assumptions); }
            catch (ExpressionException) { continue; }
            if (resolved.Diagnostics.Any(LoadoutResolver.IsEquipmentLimitError)) { continue; }
            if (Errors(resolved) > errors) { continue; }
            var (name, format) = Describe(pack, kind, id);
            upgrades.Add(new(kind, target, targetName, id, name, value, value is null ? "" : format, reroll, before,
                resolved.Calculation.Stats.GetValueOrDefault(statId), [operation])
            { Coverage = EffectCoverage.Describe(pack, edited, outcome.State.Assumptions) });
        }
        // OrderByDescending is stable: equal gains keep the attribute, mod, talent order the Go app listed them in.
        return new(statId, before, [.. upgrades.OrderByDescending(upgrade => upgrade.Delta)])
        { Coverage = EffectCoverage.Describe(pack, build, state.Assumptions) };

        static int Errors(ResolvedBuild resolved) => resolved.Diagnostics.Count(diagnostic => diagnostic.Severity == "error");
    }

    /// <summary>The roll an attribute or mod slot holds, if any.</summary>
    private static double? Roll(BuildSnapshot build, UpgradeKind kind, string slotId, string target) => kind switch
    {
        UpgradeKind.Attribute => build.AttributeRolls.GetValueOrDefault(slotId)?.GetValueOrDefault(target)?.Value,
        UpgradeKind.Mod when build.ModRolls.GetValueOrDefault(slotId) is { } rolls && rolls.TryGetValue(target, out var roll) => roll,
        _ => null
    };

    /// <summary>A choice's name, and the format of the attribute its roll is in (a mod's roll attribute).</summary>
    private static (string Name, string Format) Describe(GamePack pack, UpgradeKind kind, string id)
    {
        string? Format(string attribute) => pack.Attributes.FirstOrDefault(definition => definition.ID == attribute)?.Format;
        return kind switch
        {
            UpgradeKind.Attribute => (pack.Attributes.FirstOrDefault(attribute => attribute.ID == id)?.Name ?? id, Format(id) ?? ""),
            UpgradeKind.Mod => pack.Mods.FirstOrDefault(mod => mod.ID == id) is { } mod ? (mod.Name, Format(mod.RollAttributeID) ?? "") : (id, ""),
            _ => (pack.Talents.FirstOrDefault(talent => talent.ID == id)?.Name ?? id, "")
        };
    }

    /// <summary>The snapshot's state as a slot is judged in: a weapon slot's item as the weapon in hand.</summary>
    private static PlannerState InSlotContext(SessionSnapshot snapshot, string slotId)
    {
        var state = snapshot.State;
        return snapshot.Pack.Pack.Slots.Any(slot => slot.ID == slotId && slot.Weapon)
            ? state with { Assumptions = state.Assumptions.SetItem(GamePack.WeaponSlotSelector, slotId) }
            : state;
    }

    /// <summary>The history label: the first three operation labels, then how many more.</summary>
    public static string Label(ImmutableArray<BuildOperation> operations)
    {
        var shown = string.Join("; ", operations.Take(LabelOperations).Select(operation => operation.Label));
        return operations.Length > LabelOperations ? $"{shown}; {operations.Length - LabelOperations} more" : shown;
    }

    private bool Coalesces(Entry next)
    {
        if (next.CoalescingKey is null || current == 0) { return false; }
        var last = entries[current];
        // Only an uninterrupted run coalesces: the current entry must be the latest accepted change,
        // so any undo, redo, reset or other edit in between breaks it. Timestamps are monotonic.
        return last.Revision == revision && last.CoalescingKey == next.CoalescingKey && last.Origin == next.Origin &&
            options.Clock.GetElapsedTime(last.At, next.At) <= options.CoalescingWindow;
    }

    // A Changed handler runs under the lock; letting it change the session would deliver the
    // nested revision to later handlers before the outer one.
    private void RejectReentry()
    {
        if (gate.IsHeldByCurrentThread) { throw new InvalidOperationException("A Changed handler must not change the session; post the change to a dispatcher instead."); }
    }

    private static bool BelongsTo(PlannerState state, CompiledPackExpressions pack) =>
        state.Build.PackId.Length == 0 || state.Build.PackId == pack.Pack.ID;

    private SessionResult? Refuse(long expectedRevision) => expectedRevision == revision
        ? null
        : Reject(SessionErrorCodes.StaleRevision, "session revision changed; inspect the current state before retrying");

    private SessionResult Reject(string code, string message) => new(snapshot, [], [], new(code, message));

    private SessionResult Commit(ImmutableArray<OperationChange> changes)
    {
        revision++;
        var next = TakeSnapshot();
        Volatile.Write(ref snapshot, next);
        var failures = ImmutableArray.CreateBuilder<Exception>();
        foreach (var handler in Changed?.GetInvocationList().Cast<Action<SessionSnapshot>>() ?? [])
        {
#pragma warning disable CA1031 // A failing subscriber must not hide the committed change from the caller or the other subscribers.
            try { handler(next); }
            catch (Exception exception) { failures.Add(exception); }
#pragma warning restore CA1031
        }
        return new(next, changes, [], null) { NotificationErrors = failures.ToImmutable() };
    }

    private SessionSnapshot TakeSnapshot()
    {
        var history = entries.Select((entry, index) => new HistoryEntry(entry.Revision, entry.Origin, entry.Label, index == current)).ToImmutableArray();
        return new(revision, Pack, entries[current].State, current > 0, current < entries.Count - 1, history);
    }

    private sealed record Entry(long Revision, EditOrigin Origin, string? Label, PlannerState State, string? CoalescingKey, long At);
}
