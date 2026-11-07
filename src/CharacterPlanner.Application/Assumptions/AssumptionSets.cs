using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Application.Assumptions;

/// <summary>
/// A named preset of view assumptions for one pack, such as "Boss, full stacks": the values it sets, keyed by
/// assumption id, as the session stores them. Sets are never part of builds or share codes.
/// </summary>
public sealed record AssumptionSet(string PackId, string Name, ImmutableDictionary<string, string> Values);

/// <summary>Stable assumption-set error codes. Messages are for people and may change; codes may not.</summary>
public static class AssumptionSetErrorCodes
{
    public const string NameRequired = "assumption-set-name-required";
    public const string Exists = "assumption-set-exists";
    public const string NotFound = "assumption-set-not-found";
    /// <summary>The stored sets can't be read; nothing is written over them.</summary>
    public const string Damaged = "assumption-set-damaged";
    /// <summary>Another writer held the store for too long.</summary>
    public const string Busy = "assumption-set-busy";
}

public sealed class AssumptionSetException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>
/// The user's saved assumption sets (the Go app's G396), per pack. Names are unique per pack ignoring case. Failures
/// callers can act on are <see cref="AssumptionSetException"/>s with a stable code; other I/O failures stay exceptions.
/// </summary>
public interface IAssumptionSetStore
{
    /// <summary>The pack's sets, by name ignoring case.</summary>
    /// <exception cref="AssumptionSetException"><see cref="AssumptionSetErrorCodes.Damaged"/>.</exception>
    Task<ImmutableArray<AssumptionSet>> ListAsync(string packId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves values as the pack's set named <paramref name="name"/> (trimmed) and returns it. A set with that name,
    /// ignoring case, is replaced only when <paramref name="replace"/> is set.
    /// </summary>
    /// <exception cref="AssumptionSetException"><see cref="AssumptionSetErrorCodes.NameRequired"/>, <see cref="AssumptionSetErrorCodes.Exists"/>, <see cref="AssumptionSetErrorCodes.Damaged"/> or <see cref="AssumptionSetErrorCodes.Busy"/>.</exception>
    Task<AssumptionSet> SaveAsync(string packId, string name, ImmutableDictionary<string, string> values, bool replace, CancellationToken cancellationToken = default);

    /// <summary>Deletes the pack's set named <paramref name="name"/>, ignoring case. The current assumptions are not touched.</summary>
    /// <exception cref="AssumptionSetException"><see cref="AssumptionSetErrorCodes.NotFound"/>, <see cref="AssumptionSetErrorCodes.Damaged"/> or <see cref="AssumptionSetErrorCodes.Busy"/>.</exception>
    Task DeleteAsync(string packId, string name, CancellationToken cancellationToken = default);
}

/// <summary>
/// A saved set as one batch for <c>PlannerSession.Apply</c> (one undo step), and the saved ids it leaves out because
/// the pack no longer knows them or refuses their value. Operations is empty when the assumptions already match.
/// </summary>
public sealed record AssumptionSetApplication(ImmutableArray<BuildOperation> Operations, ImmutableArray<string> Skipped);

/// <summary>
/// What a set saves and what applying one does. The weapon in hand is chosen with the build, not the scenario, so a set
/// neither saves nor changes it. Applying replaces every other assumption: ids the set lacks return to their defaults.
/// Saved values are checked as the applier checks a context edit, so a pack update that drops or narrows an
/// assumption leaves that value out instead of rejecting the whole set.
/// </summary>
public static class AssumptionSets
{
    /// <summary>The assumptions as a set saves them: all but the weapon in hand.</summary>
    public static ImmutableDictionary<string, string> Values(ImmutableDictionary<string, string> assumptions)
    {
        ArgumentNullException.ThrowIfNull(assumptions);
        return assumptions.Remove(GamePack.WeaponSlotSelector).WithComparers(StringComparer.Ordinal);
    }

    /// <summary>The context operations that make <paramref name="assumptions"/> the set's: clears first, then values, each by id.</summary>
    public static AssumptionSetApplication Apply(GamePack pack, ImmutableDictionary<string, string> assumptions, AssumptionSet set)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(assumptions);
        ArgumentNullException.ThrowIfNull(set);
        var (accepted, skipped) = Accepted(pack, set.Values);
        var current = Values(assumptions);
        var operations = ImmutableArray.CreateBuilder<BuildOperation>();
        operations.AddRange(current.Keys.Where(id => !accepted.ContainsKey(id)).Order(StringComparer.Ordinal).Select(ContextOperation.Cleared));
        foreach (var (id, value) in accepted.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (current.GetValueOrDefault(id) == value.Stored) { continue; }
            operations.Add(value.Number is { } number ? new ContextOperation(id, number) : new ContextOperation(id, Text: value.Stored));
        }
        return new(operations.ToImmutable(), skipped);
    }

    /// <summary>The first set (in the order given) the assumptions equal, apart from the weapon in hand; null for none.</summary>
    public static AssumptionSet? Matching(GamePack pack, ImmutableDictionary<string, string> assumptions, IEnumerable<AssumptionSet> sets)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(sets);
        var current = Values(assumptions);
        return sets.FirstOrDefault(set =>
        {
            var accepted = Accepted(pack, set.Values).Accepted;
            return accepted.Count == current.Count && accepted.All(pair => current.GetValueOrDefault(pair.Key) == pair.Value.Stored);
        });
    }

    /// <summary>
    /// The saved values the pack accepts, as the applier would store them (a number snapped onto its step and written
    /// invariantly), and the ids it doesn't, sorted. A saved weapon in hand is ignored, not reported.
    /// </summary>
    private static (ImmutableDictionary<string, (string Stored, double? Number)> Accepted, ImmutableArray<string> Skipped) Accepted(GamePack pack, ImmutableDictionary<string, string> values)
    {
        var accepted = ImmutableDictionary.CreateBuilder<string, (string, double?)>(StringComparer.Ordinal);
        var skipped = new List<string>();
        foreach (var (id, raw) in values)
        {
            if (id == GamePack.WeaponSlotSelector) { continue; }
            if (pack.NumericContextInputs.FirstOrDefault(input => input.ID == id) is { } numeric)
            {
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && numeric.Accepts(number))
                {
                    var snapped = numeric.Snap(number);
                    accepted[id] = (snapped.ToString("R", CultureInfo.InvariantCulture), snapped);
                    continue;
                }
            }
            else if (pack.Selectors.FirstOrDefault(selector => selector.ID == id) is { } selector && selector.Values.Contains(raw))
            {
                accepted[id] = (raw, null);
                continue;
            }
            skipped.Add(id);
        }
        return (accepted.ToImmutable(), [.. skipped.Order(StringComparer.Ordinal)]);
    }
}
