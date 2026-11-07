using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Legacy.Tests;

public sealed class PrototypeEffectTests
{
    private static readonly CompiledPackExpressions Compiled = LegacyPacks.Load(Path.Combine("prototype-resolution", "pack.json"), SyntheticV1.Simplify);
    private static readonly ImmutableDictionary<string, string> Active = ImmutableDictionary<string, string>.Empty.Add("weapon_slot", "main");
    private static BuildSnapshot Configured => BuildSnapshot.Create(Compiled.Pack).WithEquipment("main", "weapon")
        .WithAccountUpgrade("watch", 1).WithEquipmentUpgrade("main", "weapon", 2)
        .WithPrototype("main", new("weapon", "entropy", 3))
        .WithAttributeRoll("main", "core", new("damage_attr", 16))
        .WithAttributeRoll("main", "fallback", new("fallback_attr", 10))
        .WithAttributeRoll("main", "skill", new("skill_attr", 1));

    [Theory]
    [InlineData("1", true)]
    [InlineData("1.0", true)]
    [InlineData("1e0", true)]
    [InlineData("+1", false)]
    [InlineData("01", false)]
    [InlineData("1_0e-1", false)]
    [InlineData("0x1p0", false)]
    [InlineData("0x.8p1", false)]
    [InlineData("0x_1p+0", false)]
    [InlineData("0x1.00000000000008p0", false)]
    [InlineData("0x0.fffffffffffffcp0", false)]
    [InlineData("1.0000000000000001", true)]
    [InlineData(" 1", false)]
    [InlineData("1 ", false)]
    [InlineData("1__0e-1", false)]
    [InlineData("0x1.00000000000009p0", false)]
    [InlineData("0x0.fffffffffffffbp0", false)]
    [InlineData("0x1p1", false)]
    [InlineData("-0x1p0", false)]
    [InlineData("1e999", false)]
    [InlineData("NaN", false)]
    [InlineData("Infinity", false)]
    public void NormalizationUsesValidatedNumericAssumptions(string raw, bool normalized)
    {
        var context = Active.Add("normalized", raw);
        var result = BuildResolver.Resolve(Compiled, Configured, context);
        Assert.Equal(normalized, PrototypeRules.IsNormalized(Compiled.Pack, result.Assumptions.Numeric));
        Assert.Equal(!normalized, result.Diagnostics.Any(value => value.Code == "context-value-invalid"));
        Assert.Equal(normalized ? 129 : 144, result.Calculation.Stats["damage"]);
        Assert.Equal(1, result.Calculation.Stats["skill_tier"]);
        Assert.Equal(1, result.Calculation.Stats["prototype_count"]);
    }

    [Fact]
    public void InvalidAugmentRetainsPrototypeAttributeRangesButAddsNoCountOrAugment()
    {
        var build = Configured.WithPrototype("main", new("weapon", "missing", 3));
        var result = BuildResolver.Resolve(Compiled, build, Active);
        Assert.Equal("prototype-invalid", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(135, result.Calculation.Stats["damage"]);
        Assert.Equal(0, result.Calculation.Stats["prototype_count"]);
        Assert.DoesNotContain(result.Calculation.Breakdowns["damage"].Contributions, value => value.Source.Kind == "prototype");
    }

    [Fact]
    public void PrototypeNormalizationUsesTheValidatedNumericDefault()
    {
        var pack = Compiled.Pack with { NumericContextInputs = [Compiled.Pack.NumericContextInputs[0] with { Default = 1 }] };
        var compiled = LegacyPacks.Compile(pack);
        var result = BuildResolver.Resolve(compiled, Configured, Active);
        Assert.Equal(1, result.Assumptions.Numeric["normalized"]);
        Assert.True(PrototypeRules.IsNormalized(pack, result.Assumptions.Numeric));
        Assert.Equal(129, result.Calculation.Stats["damage"]);
    }

    [Fact]
    public void InvalidNormalizationUsesTheSameDefaultAsOtherNumericConsumers()
    {
        var pack = Compiled.Pack with { NumericContextInputs = [Compiled.Pack.NumericContextInputs[0] with { Default = 1 }] };
        var result = BuildResolver.Resolve(LegacyPacks.Compile(pack), Configured, Active.Add("normalized", "0x1p0"));
        Assert.Equal(1, result.Assumptions.Numeric["normalized"]);
        Assert.Equal("context-value-invalid", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(129, result.Calculation.Stats["damage"]);
    }

    [Fact]
    public void DiagnosticPrecedenceIsAccountThenPrototypeThenItemUpgradeThenConfiguration()
    {
        var build = Configured.WithAccountUpgrade("missing", 1).WithPrototype("main", new("weapon", "missing", 3))
            .WithEquipmentUpgrade("main", "weapon", 4).WithAttributeRoll("main", "core", new("damage_attr", 4));
        var result = BuildResolver.Resolve(Compiled, build, Active);
        Assert.Equal(new[] { "unknown-account-upgrade", "prototype-invalid", "equipment-upgrade-invalid", "attribute-roll-invalid" }, result.Diagnostics.Select(value => value.Code));
    }

    [Fact]
    public void MissingPrototypeMaximumKeepsNormalRangeAndFallbackMinimumUsesChoiceMaximum()
    {
        var attributes = Compiled.Pack.Attributes;
        var choices = Compiled.Pack.Items[0].AttributeSlots;
        Assert.Equal(new AttributeRollBounds(8, 20, 2), PrototypeRules.RollBounds(attributes[1], choices[1].Choices[0], true));
        Assert.Equal(new AttributeRollBounds(1, 1, 1), PrototypeRules.RollBounds(attributes[2], choices[2].Choices[0], true));
        Assert.Equal(new AttributeRollBounds(2, 8, 2), PrototypeRules.RollBounds(attributes[0], choices[0].Choices[0], false));
    }
}
