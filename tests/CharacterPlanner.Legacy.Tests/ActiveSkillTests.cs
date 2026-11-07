using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Legacy.Tests;

public sealed class ActiveSkillTests
{
    private static readonly CompiledPackExpressions Compiled = LegacyPacks.Load(Path.Combine("active-skill-resolution", "pack.json"), pack => SyntheticV1.Simplify(pack, 5));
    private static BuildSnapshot Configured => BuildSnapshot.Create(Compiled.Pack).WithActiveSkillSelection("one", "turret").WithActiveSkillSelection("two", "drone");

    [Fact]
    public void StaleGradeReportsErrorAndPreviewFallsBackToZeroWithoutEditingBuild()
    {
        var build = Configured.WithSkillUpgrade("one", "drone", 3);
        var result = BuildResolver.Resolve(Compiled, build);
        Assert.Equal("invalid-skill-upgrade", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(577, Assert.Single(result.ActiveSkillPreviews["one"].Outputs).Value);
        Assert.Equal("drone", build.SkillUpgrades["one"].SkillId);
        Assert.Equal(3, build.SkillUpgrades["one"].Rank);
    }

    [Fact]
    public void DuplicateFamilyRejectsLaterSkillAndItsGradeButKeepsFirstSkill()
    {
        var build = Configured.WithActiveSkillSelection("two", "turret_variant").WithSkillUpgrade("two", "turret_variant", 0) with
        { SkillUpgrades = ImmutableDictionary<string, SkillUpgrade>.Empty.Add("two", new("turret_variant", 0)) };
        var result = BuildResolver.Resolve(Compiled, build);
        Assert.Equal(new[] { "duplicate-active-skill-family", "invalid-skill-upgrade" }, result.Diagnostics.Select(value => value.Code));
        Assert.Equal("one", Assert.Single(result.ActiveSkillPreviews).Key);
        Assert.Equal(110, result.Calculation.Stats["damage"]);
    }

    [Theory]
    [InlineData("bad_formula", "one.infinite")]
    [InlineData("bad_condition", "one.conditional")]
    public void FailedOutputDoesNotDiscardOtherOutputsOrAcceptedSkill(string skill, string subject)
    {
        var result = BuildResolver.Resolve(Compiled, Configured.WithActiveSkillSelection("one", skill));
        Assert.Equal(("error", "active-skill-preview-eval", subject, ""), Assert.Single(result.Diagnostics).Key());
        Assert.Equal(2, result.ActiveSkillPreviews.Count);
        Assert.Equal("good", Assert.Single(result.ActiveSkillPreviews["one"].Outputs).Id);
        Assert.Equal(105, result.ActiveSkillPreviews["one"].Outputs[0].Value);
    }

    [Fact]
    public void PreviewInputsAreLocalAndIncludeConditionReadsButExcludeLevel()
    {
        var result = BuildResolver.Resolve(Compiled, Configured.WithSkillUpgrade("one", "turret", 2));
        Assert.Empty(result.Diagnostics);
        Assert.Equal(692, result.ActiveSkillPreviews["one"].Outputs[0].Value);
        var conditional = result.ActiveSkillPreviews["one"].Outputs[1];
        Assert.Equal(10, conditional.Inputs["power"]);
        Assert.Equal(2, conditional.Inputs["skill_upgrade_rank"]);
        Assert.DoesNotContain("level", result.ActiveSkillPreviews["one"].Outputs[0].Inputs.Keys);
        Assert.False(result.Calculation.Stats.ContainsKey("level"));
        Assert.False(result.Calculation.Stats.ContainsKey("skill_upgrade_rank"));
    }

    [Fact]
    public void FamilyAcceptanceUsesAuthoredSlotOrder()
    {
        var pack = Compiled.Pack with { ActiveSkillSlots = Compiled.Pack.ActiveSkillSlots.Reverse().ToImmutableArray() };
        var compiled = LegacyPacks.Compile(pack);
        var result = BuildResolver.Resolve(compiled, Configured.WithActiveSkillSelection("two", "turret_variant"));
        Assert.Equal("one", Assert.Single(result.Diagnostics).Subject);
        Assert.Equal("two", Assert.Single(result.ActiveSkillPreviews).Key);
        Assert.Equal(120, result.Calculation.Stats["damage"]);
    }

    [Fact]
    public void FalseConditionSkipsFormulaEvaluation()
    {
        var pack = Compiled.Pack with
        {
            ActiveSkills = Compiled.Pack.ActiveSkills.Select(skill => skill.ID == "turret" ? skill with
            {
                PreviewOutputs = [new() { ID = "skip", Name = "Skip", Formula = "sqrt(-1)", When = "0" }, new() { ID = "good", Name = "Good", Formula = "damage" }]
            } : skill).ToImmutableArray()
        };
        var compiled = LegacyPacks.Compile(pack);
        var result = BuildResolver.Resolve(compiled, Configured);
        Assert.Empty(result.Diagnostics);
        Assert.Equal("good", Assert.Single(result.ActiveSkillPreviews["one"].Outputs).Id);
    }

    [Fact]
    public void PreviewReadsThePackLevel()
    {
        var result = BuildResolver.Resolve(LegacyPacks.Compile(Compiled.Pack with { Level = 10 }), Configured);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(1152, result.ActiveSkillPreviews["one"].Outputs[0].Value);
    }
}
