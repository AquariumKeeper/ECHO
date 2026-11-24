using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Core.Tests;

public sealed class SelectorDefaultTests
{
    // The tiny pack's mask gives 1000 armor while overwatch_active is "true".
    private static (CompiledPackExpressions Pack, BuildSnapshot Build) Fixture(string selectorDefault)
    {
        var pack = TestPacks.Tiny with
        {
            Selectors = [.. TestPacks.Tiny.Selectors.Select(selector => selector.ID == "overwatch_active" ? selector with { Default = selectorDefault } : selector)]
        };
        var compiled = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack).Compiled);
        return (compiled, BuildSnapshot.Create(pack).WithEquipment("mask", "mask_a"));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("true", 1000)]
    public void AnUnsetSelectorReadsItsDefault(string selectorDefault, double bonus)
    {
        var (pack, build) = Fixture(selectorDefault);
        var resolved = BuildResolver.Resolve(pack, build, ImmutableDictionary<string, string>.Empty);
        var (plain, plainBuild) = Fixture("");
        var without = BuildResolver.Resolve(plain, plainBuild, ImmutableDictionary<string, string>.Empty).Calculation.Stats["armor"];
        Assert.Equal(bonus, resolved.Calculation.Stats["armor"] - without);
        // What was supplied stays as it was; only the engine's view carries the default.
        Assert.DoesNotContain("overwatch_active", resolved.Assumptions.Explicit.Keys);
        Assert.Equal(selectorDefault.Length > 0, resolved.Assumptions.Effective.ContainsKey("overwatch_active"));
    }

    [Fact]
    public void ADefaultRoundTripsAndIsOmittedWhenUnset()
    {
        var pack = TestPacks.Tiny with
        {
            Selectors = [.. TestPacks.Tiny.Selectors.Select(selector => selector.ID == "headshot_mode" ? selector with { Default = "body" } : selector)]
        };
        var files = PackWriter.Write(pack);
        var rules = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Encoding.UTF8.GetString(files[PackFiles.Rules].Span))!["selectors"]!.AsArray();
        Assert.Equal("body", rules.Single(node => node!["id"]!.GetValue<string>() == "headshot_mode")!["default"]!.GetValue<string>());
        Assert.Null(rules.Single(node => node!["id"]!.GetValue<string>() == "overwatch_active")!["default"]);
        var read = Assert.IsType<GamePack>(PackReader.Read(files).Pack);
        Assert.Equal(("body", ""), (read.Selectors.Single(s => s.ID == "headshot_mode").Default, read.Selectors.Single(s => s.ID == "overwatch_active").Default));
    }

    [Fact]
    public void AnExplicitValueWinsOverTheDefault()
    {
        var context = ImmutableDictionary<string, string>.Empty.Add("headshot_mode", "body");
        var pack = TestPacks.Tiny with
        {
            Selectors = [.. TestPacks.Tiny.Selectors.Select(selector => selector.ID == "headshot_mode" ? selector with { Default = "headshot" } : selector)]
        };
        Assert.Equal("body", ResolveAssumptions.Resolve(pack, context).Effective["headshot_mode"]);
        Assert.Equal("headshot", ResolveAssumptions.Resolve(pack, ImmutableDictionary<string, string>.Empty).Effective["headshot_mode"]);
    }
}
