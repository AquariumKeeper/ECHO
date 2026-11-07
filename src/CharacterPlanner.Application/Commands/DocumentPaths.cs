using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using CharacterPlanner.Core.Builds;

namespace CharacterPlanner.Application.Commands;

/// <summary>
/// Flattens a planner state into document paths, the granularity OperationChange reports. The vocabulary
/// is the frozen apply responses': an attribute roll, a skill's mod map, the node list and the party are
/// each one path; build meta is "meta.Name"; assumptions are "context.&lt;id&gt;". Empty values are absent.
/// </summary>
internal static class DocumentPaths
{
    public static Dictionary<string, string> Flatten(PlannerState state)
    {
        var build = state.Build;
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string path, string value) { if (value.Length > 0) { paths[path] = value; } }
        void Nested<T>(string root, ImmutableDictionary<string, ImmutableDictionary<string, T>> values, Func<T, string> text)
        {
            foreach (var (slot, inner) in values) { foreach (var (id, value) in inner) { Add($"{root}.{slot}.{id}", text(value)); } }
        }

        Add("meta.Name", build.Meta.Name);
        Add("meta.Notes", build.Meta.Notes);
        foreach (var (slot, item) in build.Equipment) { Add($"equipment.{slot}", item); }
        Nested("attributeRolls", build.AttributeRolls, roll => $"{roll.AttributeId}={Number(roll.Value)}");
        Nested("modSelections", build.ModSelections, id => id);
        Nested("modRolls", build.ModRolls, Number);
        Nested("talentSelections", build.TalentSelections, id => id);
        foreach (var (slot, skill) in build.ActiveSkillSelections) { Add($"activeSkillSelections.{slot}", skill); }
        foreach (var (slot, upgrade) in build.SkillUpgrades)
        {
            Add($"skillUpgrades.{slot}.skillId", upgrade.SkillId);
            Add($"skillUpgrades.{slot}.rank", Number(upgrade.Rank));
        }
        foreach (var (slot, selection) in build.SkillMods)
        {
            Add($"skillMods.{slot}.skillId", selection.SkillId);
            Add($"skillMods.{slot}.mods", string.Join(";", (selection.Mods ?? ImmutableDictionary<string, SkillModRoll>.Empty)
                .OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value.ModId}={Number(pair.Value.Value)}")));
        }
        Add("specializationID", build.SpecializationId);
        Add("specializationNodeSelections", string.Join(";", build.SpecializationNodeSelections));
        foreach (var (id, rank) in build.AccountUpgrades) { Add($"accountUpgrades.{id}", Number(rank)); }
        foreach (var (slot, upgrade) in build.EquipmentUpgrades)
        {
            Add($"equipmentUpgrades.{slot}.itemId", upgrade.ItemId);
            Add($"equipmentUpgrades.{slot}.rank", Number(upgrade.Rank));
        }
        foreach (var (slot, prototype) in build.Prototypes)
        {
            Add($"prototypes.{slot}.itemId", prototype.ItemId);
            Add($"prototypes.{slot}.augment", prototype.Augment);
            // A Prototype's level is part of its record even at 0 (no augment), as the frozen responses report it.
            Add($"prototypes.{slot}.level", Number(prototype.Level));
        }
        Add("party", Party(build.Party));
        foreach (var (id, value) in state.Assumptions) { Add($"context.{id}", value); }
        return paths;
    }

    /// <summary>The paths whose value changed or appeared (set) and the paths that disappeared (cleared), each in ordinal order.</summary>
    public static (ImmutableArray<string> Set, ImmutableArray<string> Cleared) Diff(Dictionary<string, string> before, Dictionary<string, string> after) =>
        ([.. after.Where(pair => before.GetValueOrDefault(pair.Key) != pair.Value).Select(pair => pair.Key).Order(StringComparer.Ordinal)],
         [.. before.Keys.Where(path => !after.ContainsKey(path)).Order(StringComparer.Ordinal)]);

    private static string Party(ImmutableArray<PartyMember> party)
    {
        var text = new StringBuilder();
        foreach (var member in party)
        {
            text.Append(member.Name).Append('|').Append(member.Code).Append('|').Append(Number(member.Armor));
            foreach (var effect in member.Effects)
            {
                text.Append('|').Append(effect.Id).Append(effect.Active ? "+" : "-");
                foreach (var (stat, value) in effect.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal)) { text.Append(',').Append(stat).Append('=').Append(Number(value)); }
            }
            text.Append('\n');
        }
        return text.ToString();
    }

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
