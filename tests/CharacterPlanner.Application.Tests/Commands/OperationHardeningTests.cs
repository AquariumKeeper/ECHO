using System.Collections.Immutable;
using System.Text;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using static CharacterPlanner.Application.Tests.Commands.Division;
using Codes = CharacterPlanner.Application.Commands.OperationErrorCodes;

namespace CharacterPlanner.Application.Tests.Commands;

/// <summary>Regressions for the operations review: undecodable text, grid snapping and stale-data cleanup.</summary>
public sealed class OperationHardeningTests
{
    [Theory]
    [InlineData("""[{"kind":"name","text":"\ud800"}]""")]
    [InlineData("""[{"kind":"\ud800"}]""")]
    [InlineData("""[{"kind":"name","\ud800":1}]""")]
    public void UndecodableTextIsAnErrorNotACrash(string json)
    {
        var read = OperationJson.Read(json);
        Assert.Empty(read.Operations);
        Assert.Equal(Codes.FieldType, Assert.Single(read.Errors).Code);
    }

    [Fact]
    public void InvalidUtf8IsAnErrorNotACrash()
    {
        byte[] bytes = [.. Encoding.UTF8.GetBytes("""[{"kind":"name","text":"a"""), 0xC3, 0x28, .. Encoding.UTF8.GetBytes("\"}]")];
        var read = OperationJson.Read(bytes);
        Assert.Empty(read.Operations);
        Assert.NotEmpty(read.Errors);
    }

    [Fact]
    public void NearGridValuesAreStoredOnTheGrid()
    {
        // 0.9999999995 is accepted within the step tolerance but must be stored as the toggle's 1.
        var state = Applied(State(), new ContextOperation("prototype_activity", 0.9999999995));
        Assert.Equal("1", state.Assumptions["prototype_activity"]);

        var bounds = new AttributeRollBounds(0, 170000, 1);
        Assert.True(bounds.Accepts(169999.9999999));
        Assert.Equal(170000, bounds.Snap(169999.9999999));
        Assert.Equal(0.3, new AttributeRollBounds(0, 1, 0.1).Snap(0.30000000000000004));
        Assert.Equal(1, new AttributeRollBounds(0, 1, 3e-13).Highest(), 1e-9);
        Assert.True(new AttributeRollBounds(0, 1, 3e-13).Accepts(new AttributeRollBounds(0, 1, 3e-13).Highest()));
    }

    [Fact]
    public void AStaleAssumptionCanStillBeCleared()
    {
        var state = new PlannerState(Empty(), ImmutableDictionary<string, string>.Empty.Add("no_longer_in_the_pack", "1"));
        Assert.Empty(Applied(state, ContextOperation.Cleared("no_longer_in_the_pack")).Assumptions);
        Assert.Equal(Codes.UnknownContext, Rejected(State(), new ContextOperation("no_longer_in_the_pack", 1)).Code);
    }

    [Fact]
    public void AnEffectThePackNoLongerHasCanStillBeTurnedOff()
    {
        var gone = new PartyEffect("gone", ImmutableDictionary<string, double>.Empty, true);
        var handEntered = Empty().WithParty([new("Ally", "", 0, [gone])]);
        Assert.Empty(Applied(handEntered, new PartyBuffOperation("Ally", "gone", false)).Party.Single().Effects);
        var coded = Empty().WithParty([new("Coded", "share-code", 0, [gone])]);
        Assert.False(Applied(coded, new PartyBuffOperation("Coded", "gone", false)).Party.Single().Effects.Single().Active);
        Assert.Equal(Codes.UnknownAllyEffect, Rejected(handEntered, new PartyBuffOperation("Ally", "gone", true)).Code);
    }

    [Fact]
    public void AMissingOperationRejectsTheBatch()
    {
        var state = State();
        var outcome = Applier.Apply(state, [new NameOperation("x"), null!]);
        Assert.False(outcome.Succeeded);
        Assert.Same(state, outcome.State);
        Assert.Equal(2, Assert.Single(outcome.Errors).Operation);
    }
}
