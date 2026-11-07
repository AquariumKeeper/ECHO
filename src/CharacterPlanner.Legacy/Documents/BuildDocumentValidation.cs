using System.Collections.Immutable;
using static CharacterPlanner.Core.Diagnostics.ReferenceText;

using CharacterPlanner.Core.Builds;

namespace CharacterPlanner.Legacy.Documents;

public static partial class BuildDocumentJson
{
    // CR5: keys are visited in ordinal order so the reported error doesn't depend on hash order.
    private static void ValidateValues(BuildSnapshot build)
    {
        foreach (var (slot, rolls) in Sorted(build.AttributeRolls))
        { foreach (var (id, roll) in Sorted(rolls)) { if (!double.IsFinite(roll.Value)) { throw Error($"attribute roll {Quote(slot)}/{Quote(id)} must be finite"); } } }
        foreach (var (slot, rolls) in Sorted(build.ModRolls))
        { foreach (var (id, value) in Sorted(rolls)) { if (!double.IsFinite(value)) { throw Error($"mod roll {Quote(slot)}/{Quote(id)} must be finite"); } } }
        foreach (var (slot, selection) in Sorted(build.SkillMods))
        { if (selection.Mods is not null) { foreach (var (id, roll) in Sorted(selection.Mods)) { if (!double.IsFinite(roll.Value)) { throw Error($"skill mod roll {Quote(slot)}/{Quote(id)} must be finite"); } } } }
        if (PartyRules.Problem(build.Party) is { } problem) { throw Error(problem); }
    }

    private static IEnumerable<KeyValuePair<string, T>> Sorted<T>(ImmutableDictionary<string, T> values) => values.OrderBy(pair => pair.Key, StringComparer.Ordinal);
}
