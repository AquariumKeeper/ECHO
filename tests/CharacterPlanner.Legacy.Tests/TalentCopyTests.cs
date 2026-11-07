using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Legacy.Tests;

public sealed class TalentCopyTests
{
    private static readonly GamePack Pack = LegacyPacks.Load(Path.Combine("talent-copy-resolution", "pack.json"), SyntheticV1.Simplify).Pack;
    private static readonly ImmutableDictionary<string, string> Context = ImmutableDictionary<string, string>.Empty.Add("weapon_slot", "main").Add("mode", "on");
    private static BuildSnapshot Configured => BuildSnapshot.Create(Pack).WithEquipment("main", "target_weapon").WithEquipment("off", "source_weapon")
        .WithEquipment("chest", "copier").WithTalentSelection("main", "talent", "target").WithTalentSelection("off", "talent", "source").WithTalentSelection("chest", "talent", "copy");

    [Theory]
    [InlineData("type_", true)]
    [InlineData("TYPE_", false)]
    [InlineData("type_rifle", true)]
    [InlineData("weapon", true)]
    [InlineData("rifle", false)]
    public void SharedTagPrefixUsesOrdinalPrefixComparison(string prefix, bool copies)
    {
        var pack = Pack with
        {
            Talents = Pack.Talents.Select(talent => talent.CopiesTalent is { } rule ? talent with { CopiesTalent = rule with { SameTagPrefix = prefix } } : talent).ToImmutableArray()
        };
        var result = Resolve(pack);
        Assert.Equal(copies ? 1 : 0, result.Calculation.Breakdowns["damage"].Contributions.Count(value => value.Source.Name == "Source talent (Abridged)"));
    }

    [Fact]
    public void ExcludedTagsAreCheckedOnEndpointsNotTheCopierItem()
    {
        var pack = Pack with { Items = Pack.Items.Select(item => item.ID == "copier" ? item with { Tags = item.Tags.Add("exotic") } : item).ToImmutableArray() };
        Assert.Contains(Resolve(pack).Calculation.Breakdowns["damage"].Contributions, value => value.Source.Name == "Source talent (Abridged)");
    }

    [Fact]
    public void CopyKeepsOriginalContextGatesAndExcludesHolsteredModifiers()
    {
        // v1 gated this source modifier on the holstered weapon; v2 has no such gate, so a selector stands in.
        var gate = ImmutableDictionary<string, string>.Empty.Add("mode", "off");
        var pack = Pack with
        {
            Talents = Pack.Talents.Select(talent => talent.ID == "source"
                ? talent with { Modifiers = talent.Modifiers.Add(new() { Stat = "other", Op = "flat", Value = new(11), RequiresContext = gate }) } : talent).ToImmutableArray()
        };
        var result = Resolve(pack);
        var copies = result.Calculation.Breakdowns["damage"].Contributions.Where(value => value.Source.Name == "Source talent (Abridged)").ToArray();
        Assert.Equal(5, Assert.Single(copies).Value);
        Assert.Equal(0, result.Calculation.Stats["other"]);
        Assert.Equal(7, Assert.Single(result.Calculation.Breakdowns["damage"].Contributions.Where(value => value.Source.Name == "Source talent")).Value);
        Assert.Equal(1, Assert.Single(result.ActiveSets).BonusPieces);
        Assert.Equal(2, result.ActiveSets[0].Pieces);
    }

    private static ResolvedBuild Resolve(GamePack pack)
    {
        var result = BuildResolver.Resolve(LegacyPacks.Compile(pack), Configured, Context);
        Assert.Empty(result.Diagnostics);
        return result;
    }
}
