using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using CharacterPlanner.Core.Engine;

namespace CharacterPlanner.Legacy.Tests;

/// <summary>
/// The R54 skill-family adoption after the frozen Division captures (ledger "R54 skill family mods"). It gives six
/// frozen skills new mod terms and new wording. Those skills' formulas and text aren't compared, but every frozen
/// input must still read the same, and every frozen value must still match: the frozen builds carry no roll and no
/// bonus that the changed brackets read.
/// </summary>
internal static class SkillModDelta
{
    private static readonly ImmutableHashSet<string> Skills = ImmutableHashSet.Create(StringComparer.Ordinal,
        "skill_artillery_turret", "skill_assault_turret", "skill_firestarter_chem_launcher", "skill_reviver_hive", "skill_shock_trap", "skill_striker_drone");

    // R60 changed only these skills' wording, so their formulas are still compared.
    private static readonly ImmutableHashSet<string> Worded = ImmutableHashSet.Create(StringComparer.Ordinal, "skill_bulwark_shield", "skill_striker_shield");

    public static bool Covers(string skill) => Skills.Contains(skill);

    /// <summary>Whether a frozen value moved on purpose: Artillery Turret's cycle now runs from the end of a use and spaces its shells, and
    /// its bleed counts from the first shell to 7 s after the last (ledger "Skill cycles", R66, R69). Its bleed's damage isn't an output, so
    /// the frozen value can't be scaled to it; CommittedCycleDpsAtTierZero has ShadeTech's figure.</summary>
    public static bool Moved(string skill, string output) => skill == "skill_artillery_turret" && output == "assumed_cycle_dps";

    // R66 to R69 added these outputs after the freeze; the frozen ones are still compared in their places.
    private static readonly ImmutableHashSet<(string Skill, string Output)> Added = [("skill_sniper_turret", "reload_seconds"),
        ("skill_artillery_turret", "assumed_shell_interval_seconds"), ("skill_artillery_turret", "use_end_cooldown_seconds"),
        ("skill_cluster_seeker_mine", "assumed_mine_interval_seconds"), ("skill_incinerator_turret", "burn_duration_seconds")];

    /// <summary>Whether an output came after the freeze (R66 to R69).</summary>
    public static bool IsAdded(string skill, string output) => Added.Contains((skill, output));

    /// <summary>A formula with the tier read as it was frozen: R64's tier formula is in every ordinary output, and nothing else in it may differ.</summary>
    public static string FrozenTier(string formula) =>
        ShieldTier.Replace(SkillTier.Replace(formula, "min(6, max(0, floor(skill_tier)))"), "min(6, max(0, floor(skill_tier + armor_core_count)))");

    // R67 moved R64's tier formula into two stats, which every ordinary output reads.
    private static readonly Regex SkillTier = new(@"\bovercharged_skill_tier\b", RegexOptions.Compiled);
    private static readonly Regex ShieldTier = new(@"\bovercharged_shield_tier\b", RegexOptions.Compiled);

    /// <summary>The inputs with the tier read as it was frozen: the stats the tier stat reads, not the stat.</summary>
    public static ImmutableDictionary<string, double> FrozenTierInputs(ImmutableDictionary<string, double> inputs, ImmutableDictionary<string, double> stats)
    {
        var frozen = inputs.Remove("skill_overcharge");
        if (frozen.ContainsKey("overcharged_skill_tier")) { frozen = frozen.Remove("overcharged_skill_tier").SetItem("skill_tier", stats["skill_tier"]); }
        if (frozen.ContainsKey("overcharged_shield_tier"))
        { frozen = frozen.Remove("overcharged_shield_tier").SetItem("skill_tier", stats["skill_tier"]).SetItem("armor_core_count", stats["armor_core_count"]); }
        return frozen;
    }

    /// <summary>How far the moved cycle DPS shifts a frozen build's total, which adds it up. Anything else moving the total still fails. Time to kill goes unchecked only when this isn't zero.</summary>
    public static double BuildDpsShift(JsonElement frozenPreviews, ImmutableDictionary<string, SkillPreview> previews) =>
        previews.Where(pair => pair.Value.SkillId == "skill_artillery_turret").Sum(pair =>
            pair.Value.Outputs.Single(output => output.Id == "assumed_cycle_dps").Value -
            frozenPreviews.GetProperty(pair.Key).GetProperty("outputs").EnumerateArray().Single(output => output.GetProperty("id").GetString() == "assumed_cycle_dps").GetProperty("value").GetDouble());

    public static bool Reworded(string skill) => Skills.Contains(skill) || Worded.Contains(skill);

    /// <summary>The inputs the frozen output had: a reviewed formula may read more.</summary>
    public static ImmutableDictionary<string, double> FrozenInputs(JsonElement frozen, ImmutableDictionary<string, double> inputs)
    {
        var names = frozen.EnumerateObject().Select(input => input.Name).ToHashSet(StringComparer.Ordinal);
        return inputs.Where(input => names.Contains(input.Key)).ToImmutableDictionary(StringComparer.Ordinal);
    }
}
