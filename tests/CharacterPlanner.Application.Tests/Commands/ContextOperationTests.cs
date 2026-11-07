using CharacterPlanner.Application.Commands;
using CharacterPlanner.Core.Engine;
using static CharacterPlanner.Application.Tests.Commands.Division;
using Codes = CharacterPlanner.Application.Commands.OperationErrorCodes;

namespace CharacterPlanner.Application.Tests.Commands;

/// <summary>Context operations edit the view assumptions, never the build.</summary>
public sealed class ContextOperationTests
{
    [Fact]
    public void TheFrozenSessionContextEditSetsTheAssumptionOnly()
    {
        var fixture = Json("session", "apply-context.json");
        var state = State(Geared());
        var outcome = Apply(state, [.. Read(fixture.GetProperty("stdin").GetString()!)]);
        Assert.True(outcome.Succeeded);
        Assert.Same(state.Build, outcome.State.Build);
        var frozen = fixture.GetProperty("response");
        Assert.Equal(frozen.GetProperty("context").GetProperty("enemy_target").GetString(), outcome.State.Assumptions["enemy_target"]);
        var change = Assert.Single(outcome.Changes);
        var expected = frozen.GetProperty("result").GetProperty("changes")[0];
        Assert.Equal(expected.GetProperty("set").EnumerateArray().Select(path => path.GetString()), change.Set);
        Assert.Empty(change.Cleared);
    }

    [Fact]
    public void SetsNumericInputsSelectorsAndTheActiveWeaponAndClearRestoresTheDefault()
    {
        var state = Applied(State(),
            new ContextOperation("practical_hit_rate", 70.5),
            new ContextOperation("in_sync_weapon_hit_active", Text: "true"),
            new ContextOperation("weapon_slot", Text: "secondary_weapon"),
            new ContextOperation("enemy_target", 2));
        Assert.Equal(new Dictionary<string, string>
        {
            ["practical_hit_rate"] = "70.5",
            ["in_sync_weapon_hit_active"] = "true",
            ["weapon_slot"] = "secondary_weapon",
            ["enemy_target"] = "2"
        }, state.Assumptions);
        Assert.Equal(70.5, ResolveAssumptions.Resolve(Rules, state.Assumptions).Numeric["practical_hit_rate"]);

        var outcome = Apply(state, ContextOperation.Cleared("enemy_target"), ContextOperation.Cleared("weapon_slot"), ContextOperation.Cleared("practical_hit_rate"));
        Assert.True(outcome.Succeeded);
        Assert.Equal(["in_sync_weapon_hit_active"], outcome.State.Assumptions.Keys);
        Assert.Equal(["context.enemy_target"], outcome.Changes[0].Cleared);
        var input = Rules.NumericContextInputs.Single(input => input.ID == "enemy_target");
        Assert.Equal(input.Default, ResolveAssumptions.Resolve(Rules, outcome.State.Assumptions).Numeric["enemy_target"]);
    }

    [Theory]
    [InlineData(Codes.UnknownContext, "luck", 1d, null)]
    [InlineData(Codes.ContextValueInvalid, "practical_hit_rate", 100.5, null)]
    [InlineData(Codes.ContextValueInvalid, "practical_hit_rate", 70.55, null)]
    [InlineData(Codes.ContextValueInvalid, "enemy_target", -1d, null)]
    [InlineData(Codes.ContextNeedsValue, "enemy_target", null, "2")]
    [InlineData(Codes.ContextNeedsText, "in_sync_weapon_hit_active", 1d, null)]
    [InlineData(Codes.ContextValueInvalid, "in_sync_weapon_hit_active", null, "false")]
    [InlineData(Codes.ContextValueInvalid, "weapon_slot", null, "mask")]
    [InlineData(Codes.ContextNeedsText, "weapon_slot", 1d, null)]
    public void RejectsValuesThePackDoesNotDeclare(string code, string id, double? value, string? text)
    {
        var error = Rejected(State(), new ContextOperation(id, value, text));
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public void ClearingAnUnknownAssumptionIsAnError() =>
        Assert.Equal(Codes.UnknownContext, Rejected(State(), ContextOperation.Cleared("luck")).Code);
}
