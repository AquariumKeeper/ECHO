using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using V1 = CharacterPlanner.Legacy.PackV1;

namespace CharacterPlanner.Legacy.Tests;

/// <summary>Loads v1 JSON through the v1 reader and the converter, then compiles or fully validates it, failing loudly at each step.</summary>
internal static class LegacyPacks
{
    public static byte[] Bytes(string relativePath) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, relativePath));

    public static V1.GamePack Read(byte[] json) => V1.PackJson.Deserialize(json);

    /// <summary>A v2 pack directory's files, keyed by file name.</summary>
    public static Dictionary<string, ReadOnlyMemory<byte>> V2Files(string relativeDirectory) =>
        Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, relativeDirectory))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path), StringComparer.Ordinal);

    /// <summary>Reads a committed v2 pack directory with the strict reader and requires it to validate cleanly.</summary>
    public static CompiledPackExpressions LoadV2(string relativeDirectory)
    {
        var result = PackValidator.Load(V2Files(relativeDirectory));
        if (result.Compiled is null || !result.Diagnostics.IsEmpty)
        { throw new InvalidOperationException($"v2 pack {relativeDirectory} failed to load:{Environment.NewLine}{string.Join(Environment.NewLine, result.Diagnostics)}"); }
        return result.Compiled;
    }

    public static GamePack Convert(V1.GamePack source)
    {
        var conversion = V1.PackV1Converter.Convert(source);
        return conversion.Pack ?? throw new InvalidOperationException(
            $"Pack v1 conversion of {source.ID} failed:{Environment.NewLine}{string.Join(Environment.NewLine, conversion.Problems)}");
    }

    /// <summary>
    /// Compiles expressions only. The engine tests author deliberately minimal packs (no names, no
    /// ids) that full v2 validation would rightly reject; real packs are read as v2 through <see cref="LoadV2"/>.
    /// </summary>
    public static CompiledPackExpressions Compile(GamePack pack)
    {
        var result = PackExpressionCompiler.Compile(pack);
        if (result.Compiled is null || !result.Diagnostics.IsEmpty)
        { throw new InvalidOperationException($"Pack {pack.ID} did not compile cleanly:{Environment.NewLine}{string.Join(Environment.NewLine, result.Diagnostics)}"); }
        return result.Compiled;
    }

    public static CompiledPackExpressions Load(byte[] json, Func<V1.GamePack, V1.GamePack>? transform = null) =>
        Compile(Convert(transform is null ? Read(json) : transform(Read(json))));

    public static CompiledPackExpressions Load(string relativePath, Func<V1.GamePack, V1.GamePack>? transform = null) => Load(Bytes(relativePath), transform);

    public static CompiledPackExpressions FromJson(string json, Func<V1.GamePack, V1.GamePack>? transform = null) => Load(Encoding.UTF8.GetBytes(json), transform);
}

/// <summary>
/// Test-only rewrites of the synthetic v1 corpora into the shape v2 can express. Production
/// conversion rejects every construct rewritten here. Each rewrite keeps the meaning of the cases
/// that don't depend on it; the cases that do are retired in <see cref="LegacyResolutionTests"/>.
/// </summary>
internal static class SyntheticV1
{
    private const string WeaponSlot = GamePack.WeaponSlotSelector;

    public static V1.GamePack Simplify(V1.GamePack pack) => Simplify(pack, null);

    public static V1.GamePack Simplify(V1.GamePack pack, long? level)
    {
        var progression = pack.Progression;
        // The build's level, clamped as v1 clamped it, becomes the pack's fixed level.
        var fixedLevel = Math.Clamp(level ?? progression.StartingLevel, progression.StartingLevel, Math.Max(progression.StartingLevel, progression.MaxLevel));
        // Requirement-gated and multi-slot items have no v2 form.
        var items = pack.Items.Where(item => item.Requirements.IsEmpty && item.Occupies.Length == 1).ToImmutableArray();
        // The synthetic packs spell "accepts" as "acceptTags", which the v1 reader ignored. A slot
        // whose items gate on being the active weapon is a weapon slot.
        var weaponSlots = items.Where(item => item.EffectsRequireContext?.GetValueOrDefault(WeaponSlot) == item.Occupies[0]).Select(item => item.Occupies[0]).ToHashSet(StringComparer.Ordinal);
        V1.ItemDefinition Item(V1.ItemDefinition item)
        {
            var slot = item.Occupies[0];
            // Only the active-weapon gate survives; every item in a weapon slot is a weapon.
            var gate = item.EffectsRequireContext is { } existing && !existing.ContainsKey(WeaponSlot) ? null : item.EffectsRequireContext;
            if (gate is null && weaponSlots.Contains(slot)) { gate = ImmutableDictionary<string, string>.Empty.Add(WeaponSlot, slot); }
            return item with { EffectsRequireContext = gate };
        }
        // Every synthetic case allocates zero points, so a primary stat is its base value. v1
        // conditions could read primary stats; v2 conditions read no stats, so they get the constant.
        var constants = pack.Stats.Where(stat => stat.Kind == "primary").ToDictionary(stat => stat.ID, stat => Number(stat.Base ?? 0), StringComparer.Ordinal);
        string Condition(string when) => constants.Aggregate(Expression(when), (text, pair) => Regex.Replace(text, $@"\b{pair.Key}\b", pair.Value));
        ImmutableArray<V1.StatModifier> Modifiers(ImmutableArray<V1.StatModifier> modifiers) => modifiers.Select(modifier => modifier with
        {
            When = Condition(modifier.When),
            Value = modifier.Value.Formula is { } formula ? new(Expression(formula)) : modifier.Value
        }).ToImmutableArray();
        return pack with
        {
            Progression = new() { StartingLevel = fixedLevel, MaxLevel = fixedLevel },
            Stats = pack.Stats.Select(stat => stat.Kind == "primary"
                ? stat with { Kind = "derived", Base = null, Formula = constants[stat.ID] } : stat with { Formula = Expression(stat.Formula) }).ToImmutableArray(),
            Slots = pack.Slots.Select(slot => weaponSlots.Contains(slot.ID) && !slot.Accepts.Contains("weapon") ? slot with { Accepts = slot.Accepts.Add("weapon") } : slot).ToImmutableArray(),
            Items = items.Select(Item).Select(item => item with
            {
                Modifiers = Modifiers(item.Modifiers),
                Upgrade = item.Upgrade is { } upgrade ? upgrade with { ModifiersPerRank = Modifiers(upgrade.ModifiersPerRank) } : null
            }).ToImmutableArray(),
            Mods = pack.Mods.Select(mod => mod with { Modifiers = Modifiers(mod.Modifiers) }).ToImmutableArray(),
            // v2 has no weapon_slot gates outside a weapon's own effects.
            Talents = pack.Talents.Select(talent => talent with
            {
                Modifiers = Modifiers(talent.Modifiers.RemoveAll(modifier => modifier.RequiresContext?.ContainsKey(WeaponSlot) == true))
            }).ToImmutableArray(),
            Sets = pack.Sets.Select(set => set with { Bonuses = set.Bonuses.Select(bonus => bonus with { Modifiers = Modifiers(bonus.Modifiers) }).ToImmutableArray() }).ToImmutableArray(),
            AccountUpgrades = pack.AccountUpgrades.Select(upgrade => upgrade with { ModifiersPerRank = Modifiers(upgrade.ModifiersPerRank.RemoveAll(modifier => modifier.Op == "inc")) }).ToImmutableArray(),
            ActiveSkills = pack.ActiveSkills.Select(skill => skill with
            {
                Modifiers = Modifiers(skill.Modifiers),
                PreviewOutputs = skill.PreviewOutputs.Select(output => output with { Formula = Expression(output.Formula), When = Expression(output.When) }).ToImmutableArray()
            }).ToImmutableArray(),
            BuildOutputs = pack.BuildOutputs.Select(output => output with { Formula = Expression(output.Formula) }).ToImmutableArray(),
            Specializations = pack.Specializations.Select(specialization => specialization with
            {
                Modifiers = Modifiers(specialization.Modifiers),
                Nodes = specialization.Nodes.Select(node => node with { Modifiers = Modifiers(node.Modifiers) }).ToImmutableArray()
            }).ToImmutableArray(),
            Scenarios = [],
            Skills = []
        };
    }

    /// <summary>
    /// v1 evaluated max() with no arguments to negative infinity, and the synthetic corpora use it to
    /// force an evaluation failure. The v2 compiler rejects that call, so an overflow to negative
    /// infinity stands in for it. Preview formula text echoed in results is compared after the same rewrite.
    /// </summary>
    public static string Expression(string source) => source.Replace("max()", "(-(10 ^ 400))", StringComparison.Ordinal);

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The ids of the v1 primary stats that <see cref="Simplify(V1.GamePack, long?)"/> turns into constants.</summary>
    public static ImmutableHashSet<string> PrimaryStats(V1.GamePack pack) =>
        pack.Stats.Where(stat => stat.Kind == "primary").Select(stat => stat.ID).ToImmutableHashSet(StringComparer.Ordinal);
}

/// <summary>Diagnostics compare by severity, code, subject and target; their wording is the app's own.</summary>
internal static class DiagnosticKeys
{
    public static (string Severity, string Code, string Subject, string Target) Key(this BuildDiagnostic diagnostic) =>
        (diagnostic.Severity, diagnostic.Code, diagnostic.Subject, diagnostic.Target);
}
