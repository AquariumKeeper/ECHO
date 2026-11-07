using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Legacy.Tests;

public sealed class BuildOutputTests
{
    private static readonly CompiledPackExpressions Compiled = LegacyPacks.Load(Path.Combine("build-output-resolution", "pack.json"), SyntheticV1.Simplify);
    private static BuildSnapshot Configured => BuildSnapshot.Create(Compiled.Pack).WithActiveSkillSelection("one", "skill").WithActiveSkillSelection("two", "other");

    [Fact]
    public void OutputsUseHeadlineAndSummedShieldsWithoutLeakingTransientStats()
    {
        var result = BuildResolver.Resolve(Compiled, Configured);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(new[] { "combined", "shielded", "risky" }, result.BuildOutputs.Select(output => output.Id));
        Assert.Equal(new double[] { 335, 140, 225 }, result.BuildOutputs.Select(output => output.Value));
        Assert.DoesNotContain(result.Calculation.Stats.Keys, id => id.EndsWith("_dps", StringComparison.Ordinal) || id.EndsWith("_shield", StringComparison.Ordinal));
    }

    [Fact]
    public void PreviewFailureUsesZeroRateButRetainsShieldsAndOtherOutputs()
    {
        var result = BuildResolver.Resolve(Compiled, Configured, ImmutableDictionary<string, string>.Empty.Add("preview_bad", "1"));
        Assert.Equal("active-skill-preview-eval", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(new double[] { 115, 140, 5 }, result.BuildOutputs.Select(output => output.Value));
    }

    [Fact]
    public void OutputFailureDoesNotDiscardOtherValuesAndFollowsPreviewDiagnostics()
    {
        var context = ImmutableDictionary<string, string>.Empty.Add("preview_bad", "1").Add("output_bad", "1");
        var result = BuildResolver.Resolve(Compiled, Configured, context);
        Assert.Equal(new[] { "active-skill-preview-eval", "build-output-eval" }, result.Diagnostics.Select(diagnostic => diagnostic.Code));
        Assert.Equal(new[] { "combined", "shielded" }, result.BuildOutputs.Select(output => output.Id));
        Assert.Equal("risky", result.Diagnostics[1].Subject);
    }

    [Fact]
    public void MissingCompiledFormulaReportsItsOwnErrorAndKeepsRemainingOutputs()
    {
        var compiled = Compiled with { BuildOutputExpressions = Compiled.BuildOutputExpressions.Remove("combined") };
        var result = BuildResolver.Resolve(compiled, Configured);
        Assert.Equal(("error", "build-output-eval", "combined", ""), Assert.Single(result.Diagnostics).Key());
        Assert.Equal(new[] { "shielded", "risky" }, result.BuildOutputs.Select(output => output.Id));
    }

    [Fact]
    public void UnmarkedRateStaysVisibleInPreviewButDoesNotSupplyHeadlineDps()
    {
        var result = BuildResolver.Resolve(Compiled, Configured.WithActiveSkillSelection("one", "plain"));
        Assert.Empty(result.Diagnostics);
        Assert.Equal(999, Assert.Single(result.ActiveSkillPreviews["one"].Outputs).Value);
        Assert.Equal(105, result.BuildOutputs[0].Value);
        Assert.Equal("decimal", result.BuildOutputs[0].Format);
        Assert.Equal("integer", result.BuildOutputs[1].Format);
    }
}
