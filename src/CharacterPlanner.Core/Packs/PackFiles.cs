using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CharacterPlanner.Core.Packs;

/// <summary>The files of a v2 pack directory (docs/PACK_SCHEMA_V2.md §1).</summary>
public static partial class PackFiles
{
    public const string Manifest = "manifest.json";
    public const string Rules = "rules.json";
    public const string Simulation = "simulation.json";
    public const string Presentation = "presentation.json";
    public const string ShareCodes = "share-codes.json";
    public const string Provenance = "provenance.md";
    public const string AssetsDirectory = "assets";

    /// <summary>The JSON files every manifest must list.</summary>
    public static ImmutableArray<string> Required { get; } = [Rules, Simulation, Presentation, ShareCodes];

    /// <summary>Every name a manifest may list. Loaders read only these, so a manifest cannot name arbitrary paths.</summary>
    public static ImmutableArray<string> Listable { get; } = [Rules, Simulation, Presentation, ShareCodes, Provenance];

    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Lowercase 64-digit hex, anchored at both ends.</summary>
    public static bool IsSha256(string value) => Sha256Pattern().IsMatch(value);

    /// <summary>
    /// The directory digest: SHA-256 of the lines "&lt;sha256&gt;  &lt;path&gt;\n", sorted by path (ordinal).
    /// For assetsSha256, paths are slash-separated and relative to the pack directory ("assets/…").
    /// </summary>
    public static string DirectoryDigest(IEnumerable<KeyValuePair<string, string>> pathHashes)
    {
        var text = new StringBuilder();
        foreach (var (path, hash) in pathHashes.OrderBy(pair => pair.Key, StringComparer.Ordinal)) { text.Append(hash).Append("  ").Append(path).Append('\n'); }
        return Sha256(Encoding.UTF8.GetBytes(text.ToString()));
    }

    [GeneratedRegex(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}

/// <summary>Capabilities a manifest may declare. A pack declaring any other capability is rejected.</summary>
public static class PackCapabilities
{
    public const string Weapons = "weapons";
    public const string Prototype = "prototype";
    public const string SkillMods = "skill-mods";
    public const string Party = "party";
    public const string TalentCopy = "talent-copy";
    public const string Simulation = "simulation";
    public const string EquipmentLimits = "equipment-limits";
    public const string TalentCoverage = "talent-coverage";

    public static ImmutableArray<string> Implemented { get; } = [Weapons, Prototype, SkillMods, Party, TalentCopy, Simulation, EquipmentLimits, TalentCoverage];

    /// <summary>The capabilities a pack's content uses, in <see cref="Implemented"/> order.</summary>
    public static ImmutableArray<string> Used(GamePack pack) => Implemented.Where(capability => capability switch
    {
        Weapons => pack.Slots.Any(slot => slot.Weapon) || pack.Items.Any(item => !item.Weapon.IsDefaultOrEmpty),
        Prototype => pack.Prototype is not null,
        SkillMods => pack.ActiveSkills.Any(skill => !skill.ModSlots.IsDefaultOrEmpty),
        Party => pack.Talents.Any(talent => !talent.AllyEffects.IsDefaultOrEmpty),
        TalentCopy => pack.Talents.Any(talent => talent.CopiesTalent is not null),
        EquipmentLimits => !pack.EquipmentLimits.IsDefaultOrEmpty,
        TalentCoverage => pack.Talents.Any(talent => talent.Coverage is not null),
        _ => !pack.Simulation.Triggers.IsDefaultOrEmpty || !pack.Simulation.Skills.IsDefaultOrEmpty || !pack.Simulation.StatusDamage.IsDefaultOrEmpty
    }).ToImmutableArray();
}

public sealed record PackFileHash(string Sha256, long Bytes);

/// <summary>The identity and integrity data of manifest.json.</summary>
public sealed record PackManifest
{
    public const long CurrentSchemaVersion = 2;

    public long SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string ID { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string GameVersion { get; init; } = "";
    public string Description { get; init; } = "";
    public ImmutableArray<string> Capabilities { get; init; } = [];
    public ImmutableSortedDictionary<string, PackFileHash> Files { get; init; } = ImmutableSortedDictionary.Create<string, PackFileHash>(StringComparer.Ordinal);
    /// <summary>Empty when the pack has no assets directory digest.</summary>
    public string AssetsSha256 { get; init; } = "";
}
