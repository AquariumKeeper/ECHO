using CharacterPlanner.Core.Engine;

namespace CharacterPlanner.Legacy.Tests;

/// <summary>
/// The reviewed R29 shield skill-mod adoption after the frozen Division captures (ledger "R29 shield-mod pack
/// adoption"). It adds one mod
/// term inside the tier bracket of thirteen shield previews and one sentence to their descriptions.
/// Reverting exactly that change must reproduce the frozen preview; with no mod selected the value is unchanged.
/// </summary>
internal static class ShieldModDelta
{
    private const string Note = " Includes the selected shield skill mod in the ordinary tier bracket (R29); Overcharge excludes it. " +
        "Source multiplies the tier curve by (1 + mods); the additive bracket is the existing planner model.";

    private static readonly Dictionary<string, string> Terms = new(StringComparer.Ordinal)
    {
        ["skill_health"] = "skill_mod_shield_health",
        ["active_regeneration_reference"] = "skill_mod_active_regeneration",
        ["holstered_regeneration_reference"] = "skill_mod_holstered_regeneration",
        ["average_damage"] = "skill_mod_deflected_damage"
    };

    private static readonly HashSet<string> Shields = new(StringComparer.Ordinal)
    { "skill_bulwark_shield", "skill_crusader_shield", "skill_deflector_shield", "skill_striker_shield" };

    public static SkillPreviewOutput Revert(string skill, SkillPreviewOutput output)
    {
        if (!Shields.Contains(skill) || !Terms.TryGetValue(output.Id, out var input) ||
            output.Id == "average_damage" && skill != "skill_deflector_shield")
        { return output; }
        var term = " + " + input + ")";
        Assert.EndsWith(Note, output.Description, StringComparison.Ordinal);
        Assert.Equal(1, Count(output.Formula, term));
        Assert.Equal(0, output.Inputs.GetValueOrDefault(input));
        return output with
        {
            Description = output.Description[..^Note.Length],
            Formula = output.Formula.Replace(term, ")", StringComparison.Ordinal),
            Inputs = output.Inputs.Remove(input)
        };
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal)) { count++; }
        return count;
    }
}
