using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Legacy.Tests;

public sealed class EquipmentEffectTests
{
    private static readonly CompiledPackExpressions Compiled = LegacyPacks.Load(Path.Combine("equipment-resolution", "pack.json"), SyntheticV1.Simplify);
    private static readonly ImmutableDictionary<string, string> Active = ImmutableDictionary<string, string>.Empty.Add("weapon_slot", "main");
    private static BuildSnapshot Configured => BuildSnapshot.Create(Compiled.Pack).WithEquipment("main", "weapon")
        .WithAttributeRoll("main", "core", new("damage_attr", 4)).WithModSelection("main", "socket", "ranged")
        .WithModRoll("main", "socket", 3).WithTalentSelection("main", "talent", "talent");

    [Theory]
    [InlineData(11)]
    [InlineData(2.5)]
    public void InvalidModRollStillAppliesFixedModifiersButNotItsRolledAttribute(double invalid)
    {
        var result = BuildResolver.Resolve(Compiled, Configured.WithModRoll("main", "socket", invalid), Active);
        Assert.Equal("mod-roll-invalid", Assert.Single(result.Diagnostics).Code);
        var contributions = result.Calculation.Breakdowns["damage"].Contributions;
        Assert.Equal(2, Assert.Single(contributions.Where(value => value.Source.Kind == "mod")).Value);
        Assert.Equal(1, result.Calculation.Stats["cores"]);
        Assert.Equal(130, result.Calculation.Stats["damage"]);
    }

    [Fact]
    public void FixedModWithUnexpectedRollStillAppliesFixedEffectsAndReportsError()
    {
        var build = Configured.WithModSelection("main", "socket", "fixed").WithModRoll("main", "socket", 3);
        var result = BuildResolver.Resolve(Compiled, build, Active);
        Assert.Equal("mod-roll-not-allowed", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(4, Assert.Single(result.Calculation.Breakdowns["damage"].Contributions.Where(value => value.Source.Kind == "mod")).Value);
    }

    [Theory]
    [InlineData("main", 3)]
    [InlineData("off", 7)]
    public void TalentFilteringKeepsOriginalCompiledExpressionIndicesAndCredit(string selected, double talentValue)
    {
        var result = BuildResolver.Resolve(Compiled, Configured, Active.SetItem("weapon_slot", selected));
        Assert.Empty(result.Diagnostics);
        Assert.Equal(talentValue, Assert.Single(result.Calculation.Breakdowns["damage"].Contributions.Where(value => value.Source.Kind == "talent")).Value);
        Assert.Equal(1, Assert.Single(result.ActiveSets).BonusPieces);
        Assert.Equal(2, result.ActiveSets[0].Pieces);
        Assert.Equal(selected == "main" ? 1 : 0, result.Calculation.Stats["cores"]);
    }

    [Theory]
    [InlineData(4.000001, true)]
    [InlineData(4.00001, false)]
    public void AttributeRollUsesReferenceStepTolerance(double value, bool accepted)
    {
        var result = BuildResolver.Resolve(Compiled, Configured.WithAttributeRoll("main", "core", new("damage_attr", value)), Active);
        Assert.Equal(accepted ? 1 : 0, result.Calculation.Stats["cores"]);
        Assert.Equal(!accepted, result.Diagnostics.Any(diagnostic => diagnostic.Code == "attribute-roll-invalid"));
    }

    [Fact]
    public void TalentCopyRuleWithoutMatchingEndpointsDoesNotCopy()
    {
        var pack = Compiled.Pack with
        {
            Talents = Compiled.Pack.Talents.Select(talent => talent.ID == "talent" ? talent with { CopiesTalent = new() } : talent).ToImmutableArray()
        };
        var compiled = LegacyPacks.Compile(pack);
        var result = BuildResolver.Resolve(compiled, Configured, Active);
        Assert.Empty(result.Diagnostics);
        Assert.DoesNotContain(result.Calculation.Breakdowns["damage"].Contributions, value => value.Source.Name == "Talent (Talent)");
    }
}
