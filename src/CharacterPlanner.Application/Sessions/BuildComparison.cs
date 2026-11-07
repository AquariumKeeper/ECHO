using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Expressions;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Application.Sessions;

/// <summary>One side of a comparison: the build resolved, or why it can't be compared (Resolved is then null).</summary>
public sealed record ComparedBuild(ResolvedBuild? Resolved, string? Problem)
{
    public bool Valid => Problem is null;
    public EffectCoverageReport Coverage { get; init; } = EffectCoverageReport.Empty;
}

/// <summary>A pack stat in both builds, and how much B changes it.</summary>
public sealed record StatComparison(StatDefinition Stat, double A, double B)
{
    public double Delta => B - A;
}

/// <summary>Both builds and every pack stat's change, largest first. Stats is empty unless both builds are valid.</summary>
public sealed record BuildComparisonResult(ComparedBuild A, ComparedBuild B, ImmutableArray<StatComparison> Stats);

/// <summary>
/// Compares two builds under the same assumptions, as the Go app's Compare Builds dialog did. A build is only worth
/// comparing when it resolves cleanly: one for another pack, one the engine can't evaluate, or one with an error
/// diagnostic is reported invalid with its first error instead, so a partial result never reads as a real difference.
/// </summary>
public static class BuildComparison
{
    public static BuildComparisonResult Compare(CompiledPackExpressions pack, BuildSnapshot a, BuildSnapshot b, ImmutableDictionary<string, string> assumptions)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(assumptions);
        var first = Resolve(pack, a, assumptions);
        var second = Resolve(pack, b, assumptions);
        if (first.Resolved is not { } before || second.Resolved is not { } after) { return new(first, second, []); }
        // OrderByDescending is stable, so equal changes keep the pack's stat order.
        var stats = pack.Pack.Stats.Select(stat => new StatComparison(stat, Value(before, stat.ID), Value(after, stat.ID)))
            .OrderByDescending(comparison => Math.Abs(comparison.Delta));
        return new(first, second, [.. stats]);
    }

    private static double Value(ResolvedBuild build, string stat) => build.Calculation.Stats.GetValueOrDefault(stat);

    private static ComparedBuild Resolve(CompiledPackExpressions pack, BuildSnapshot build, ImmutableDictionary<string, string> assumptions)
    {
        if (build.PackId.Length > 0 && build.PackId != pack.Pack.ID) { return new(null, $"the build is for pack {build.PackId}, not {pack.Pack.ID}"); }
        ResolvedBuild resolved;
        try { resolved = BuildResolver.Resolve(pack, build, assumptions); }
        catch (ExpressionException exception) { return new(null, $"the build can't be evaluated: {exception.Message}"); }
        return resolved.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Severity == "error") is { } error
            ? new(null, $"build has validation error \"{error.Code}\": {error.Message}")
            : new(resolved, null) { Coverage = EffectCoverage.Describe(pack.Pack, build, assumptions) };
    }
}
