using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Legacy.Tests;

public sealed class SkillModTests
{
    private static readonly CompiledPackExpressions Compiled = LegacyPacks.Load(Path.Combine("skill-mod-resolution", "pack.json"), pack => SyntheticV1.Simplify(pack, 5));
    private static BuildSnapshot Configured => BuildSnapshot.Create(Compiled.Pack).WithActiveSkillSelection("one", "modded").WithActiveSkillSelection("two", "other");

    [Fact]
    public void DeclaredInputsStartAtZeroWithoutConfiguredMods()
    {
        var result = BuildResolver.Resolve(Compiled, Configured);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(0, result.ActiveSkillPreviews["one"].Outputs[0].Inputs["skill_mod_damage"]);
        Assert.Equal(0, result.ActiveSkillPreviews["one"].Outputs[1].Value);
        Assert.Equal(100, result.ActiveSkillPreviews["two"].Outputs[0].Value);
    }

    [Fact]
    public void SharedInputsSumWithinOneSkillAndNeverEnterCharacterStats()
    {
        var build = Configured.WithSkillMod("one", "modded", "a", new("damage_mod", .2)).WithSkillMod("one", "modded", "b", new("damage_mod_b", .3))
            .WithSkillMod("two", "other", "a", new("other_mod", .7));
        var result = BuildResolver.Resolve(Compiled, build);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(150, result.ActiveSkillPreviews["one"].Outputs[0].Value);
        Assert.Equal(170, result.ActiveSkillPreviews["two"].Outputs[0].Value);
        Assert.False(result.Calculation.Stats.ContainsKey("skill_mod_damage"));
        Assert.Equal(100, result.Calculation.Stats["damage"]);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonfiniteRollsAreDiagnosedWithoutSuppressingOtherValidSlots(double value)
    {
        var build = Configured.WithSkillMod("one", "modded", "a", new("damage_mod", value)).WithSkillMod("one", "modded", "b", new("damage_mod_b", .3));
        var result = BuildResolver.Resolve(Compiled, build);
        Assert.Equal("one.a", Assert.Single(result.Diagnostics).Subject);
        Assert.Equal("invalid-skill-mod", result.Diagnostics[0].Code);
        Assert.Equal(130, result.ActiveSkillPreviews["one"].Outputs[0].Value);
        Assert.Equal(value, build.SkillMods["one"].Mods["a"].Value);
    }

    [Theory]
    [InlineData(.20000001, true)]
    [InlineData(.200001, false)]
    public void StepToleranceMatchesReference(double value, bool accepted)
    {
        var result = BuildResolver.Resolve(Compiled, Configured.WithSkillMod("one", "modded", "a", new("damage_mod", value)));
        Assert.Equal(accepted, result.Diagnostics.IsEmpty);
        Assert.Equal(accepted ? value : 0, result.ActiveSkillPreviews["one"].Outputs[0].Inputs["skill_mod_damage"]);
    }

    [Fact]
    public void OverflowWithholdsEntireInputAndLaterNegativeRollCannotRestoreIt()
    {
        var build = Configured.WithActiveSkillSelection("one", "overflow").WithSkillMod("one", "overflow", "a", new("large", 1e308))
            .WithSkillMod("one", "overflow", "b", new("large", 1e308)).WithSkillMod("one", "overflow", "c", new("large", -1e308));
        var result = BuildResolver.Resolve(Compiled, build);
        Assert.Equal(("error", "invalid-skill-mod", "one.b", ""), Assert.Single(result.Diagnostics).Key());
        Assert.Equal(0, Assert.Single(result.ActiveSkillPreviews["one"].Outputs).Value);
    }

    [Fact]
    public void NonfiniteStepArithmeticDoesNotMakeARepresentableRollValid()
    {
        var skill = Compiled.Pack.ActiveSkills.Single(skill => skill.ID == "overflow");
        skill = skill with
        {
            ModSlots = [skill.ModSlots[0] with { Choices = [skill.ModSlots[0].Choices[0] with { Step = 1e-308 }] }]
        };
        var accepted = ImmutableDictionary<string, ActiveSkillDefinition>.Empty.Add("one", skill);
        var build = Configured.WithSkillMod("one", "overflow", "a", new("large", 1e308));
        var result = SkillModInputSelection.Select(build, accepted);
        Assert.Equal("invalid-skill-mod", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(0, result.Inputs["one"]["skill_mod_damage"]);
    }

    [Fact]
    public void GradeErrorsPrecedeModErrorsAndBothUseZeroFallbackInputs()
    {
        var build = Configured.WithSkillUpgrade("one", "modded", 4).WithSkillMod("one", "modded", "a", new("damage_mod", 2));
        var result = BuildResolver.Resolve(Compiled, build);
        Assert.Equal(new[] { "invalid-skill-upgrade", "invalid-skill-mod" }, result.Diagnostics.Select(value => value.Code));
        Assert.Equal(100, result.ActiveSkillPreviews["one"].Outputs[0].Value);
    }

    [Fact]
    public void NullLegacyModMapIsInvalidWhileAnEmptyMapIsValid()
    {
        var selected = ActiveSkillSelection.Select(Compiled.Pack, Configured);
        var invalid = Configured with { SkillMods = ImmutableDictionary<string, SkillModSelection>.Empty.Add("one", new("modded", null!)) };
        Assert.Equal("one", Assert.Single(SkillModInputSelection.Select(invalid, selected.Accepted).Diagnostics).Subject);
        var empty = invalid with { SkillMods = invalid.SkillMods.SetItem("one", new("modded", ImmutableDictionary<string, SkillModRoll>.Empty)) };
        Assert.Empty(SkillModInputSelection.Select(empty, selected.Accepted).Diagnostics);
    }
}
