using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace CharacterPlanner.Core.Builds;

/// <summary>Rules a saved party keeps whatever its format: v1 imports and v2 documents both enforce them.</summary>
public static class PartyRules
{
    /// <summary>A party holds at most three other agents.</summary>
    public const int MaxMembers = 3;

    /// <summary>The first problem with a party, or null when it keeps the rules.</summary>
    public static string? Problem(ImmutableArray<PartyMember> party)
    {
        if (party.IsDefaultOrEmpty) { return null; }
        if (party.Length > MaxMembers) { return $"a party has at most {MaxMembers} other members"; }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in party)
        {
            var name = member.Name.Trim();
            if (name.Length == 0) { return "party member names must not be blank"; }
            if (!names.Add(Key(name))) { return $"party member {Quote(name)} is listed twice"; }
            if (!double.IsFinite(member.Armor) || member.Armor < 0) { return $"party member {Quote(name)} armor must be a finite, non-negative number"; }
            var effects = new HashSet<string>(StringComparer.Ordinal);
            foreach (var effect in member.Effects.IsDefault ? [] : member.Effects)
            {
                if (effect.Id.Trim().Length == 0) { return $"party member {Quote(name)} has an effect with a blank id"; }
                if (!effects.Add(effect.Id)) { return $"party member {Quote(name)} lists effect {Quote(effect.Id)} twice"; }
                // Ordinal order, so the reported stat doesn't depend on hash order (CR5).
                foreach (var (stat, value) in effect.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                { if (!double.IsFinite(value)) { return $"party member {Quote(name)} effect {Quote(effect.Id)} value for {Quote(stat)} must be finite"; } }
            }
        }
        return null;
    }

    /// <summary>Text in a message, quoted with quotation marks, backslashes and control characters escaped so it stays on one line.</summary>
    private static string Quote(string text)
    {
        var quoted = new StringBuilder("\"");
        foreach (var character in text)
        {
            _ = character switch
            {
                '"' or '\\' => quoted.Append('\\').Append(character),
                < ' ' => quoted.Append(CultureInfo.InvariantCulture, $"\\u{(int)character:x4}"),
                _ => quoted.Append(character)
            };
        }
        return quoted.Append('"').ToString();
    }

    /// <summary>Member names match case-insensitively, rune by rune and independent of culture.</summary>
    public static string Key(string name) => string.Concat(name.Trim().EnumerateRunes().Select(rune => Rune.ToLowerInvariant(rune).ToString()));
}
