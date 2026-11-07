using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// The committed Division 2 pack, and helpers for the game-maths tests ported from the Go engine's G-tests (the
/// frozen reference for the game's maths, ADR 0002): a build, its resolution under assumptions, and closeness.
/// </summary>
internal static class Division
{
    public static CompiledPackExpressions Compiled { get; } = Load();

    private static CompiledPackExpressions Load()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "packs", "division-2");
        var files = Directory.EnumerateFiles(directory).ToImmutableSortedDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path), StringComparer.Ordinal);
        return PackValidator.Load(files).Compiled ?? throw new InvalidOperationException("The Division 2 pack didn't load");
    }

    /// <summary>An empty build, as Go's build.CreateBuild.</summary>
    public static BuildSnapshot Build() => BuildSnapshot.Create(Compiled.Pack);

    /// <summary>Assumptions from key and value pairs, as Go's ResolveContext.</summary>
    public static ImmutableDictionary<string, string> Context(params (string Key, string Value)[] values) =>
        values.ToImmutableDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);

    /// <summary>The weapon in hand, as Go's ResolveContext{"weapon_slot": slot}.</summary>
    public static ImmutableDictionary<string, string> Weapon(string slot) => Context((GamePack.WeaponSlotSelector, slot));

    /// <summary>Resolves a build, as Go's Resolve and ResolveWithContext.</summary>
    public static ResolvedBuild Resolve(BuildSnapshot build, ImmutableDictionary<string, string>? context = null) =>
        BuildResolver.Resolve(Compiled, build, context ?? ImmutableDictionary<string, string>.Empty);

    /// <summary>A resolution's stats.</summary>
    public static ImmutableDictionary<string, double> Stats(BuildSnapshot build, ImmutableDictionary<string, string>? context = null) =>
        Resolve(build, context).Calculation.Stats;

    /// <summary>A stat, 0 when absent, as Go's map read.</summary>
    public static double Of(this ImmutableDictionary<string, double> stats, string id) => stats.GetValueOrDefault(id);

    /// <summary>Go's closeTo: |got - want| within tolerance.</summary>
    public static void CloseTo(double got, double want, double tolerance)
    {
        if (Math.Abs(got - want) > tolerance) { Assert.Fail($"got {got}, want {want} (within {tolerance})"); }
    }
}
