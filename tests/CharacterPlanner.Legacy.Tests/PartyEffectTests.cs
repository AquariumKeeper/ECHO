using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Expressions;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Legacy.Tests;

public sealed class PartyEffectTests
{
    private static readonly CompiledPackExpressions Compiled = LegacyPacks.Load(Path.Combine("party-resolution", "pack.json"), SyntheticV1.Simplify);
    private static BuildSnapshot Giver => BuildSnapshot.Create(Compiled.Pack).WithEquipment("chest", "giver_gear").WithTalentSelection("chest", "talent", "grant");
    private static PartyEffect Effect(string id, double damage) => new(id, ImmutableDictionary<string, double>.Empty.Add("damage", damage), true);

    [Fact]
    public void GiverSnapshotsIgnoreItsOwnPartyWithoutChangingSavedBuild()
    {
        var saved = new PartyEffect("alpha", ImmutableDictionary<string, double>.Empty.Add("armor", 100000), true);
        var giver = Giver.WithParty([new("External ally", "opaque code", 50000, [saved])]);
        Assert.Equal(101500, BuildResolver.Resolve(Compiled, giver).Calculation.Stats["armor"]);
        var effects = PartyEffectSelection.EffectsOf(Compiled, giver);
        Assert.Equal(new[] { "alpha", "beta", "gamma", "delta" }, effects.Select(effect => effect.Id));
        Assert.Equal(150, effects[0].Modifiers.Single(modifier => modifier.Stat == "armor").Value);
        Assert.Equal("Grant", effects[0].Source);
        Assert.Equal(100000, giver.Party[0].Effects[0].Values["armor"]);
    }

    [Fact]
    public void GiverEffectsRequireTheirWeaponToBeActive()
    {
        var pack = Compiled.Pack with { Slots = [Compiled.Pack.Slots[0] with { Weapon = true }] };
        var compiled = LegacyPacks.Compile(pack);
        Assert.Empty(PartyEffectSelection.EffectsOf(compiled, Giver));
        Assert.Equal(4, PartyEffectSelection.EffectsOf(compiled, Giver, ImmutableDictionary<string, string>.Empty.Add("weapon_slot", "chest")).Length);
    }

    [Fact]
    public void ManualGiverScopeEvaluatesOnlyTheSuppliedStats()
    {
        var talent = Compiled.Pack.Talents[0];
        var values = PartyEffectSelection.Evaluate(Compiled, talent, talent.AllyEffects[0], new EvaluationScope { Stats = ImmutableDictionary<string, double>.Empty.Add("armor", 2500) });
        Assert.Equal(new[] { new AllyModifier("damage", 3), new AllyModifier("armor", 250) }, values);
    }

    [Fact]
    public void AppliedBuffsUseStackOrderAndEarliestTiesRatherThanMemberNames()
    {
        var build = BuildSnapshot.Create(Compiled.Pack).WithParty([
            new("Zulu", "", 0, [Effect("delta", 7), Effect("alpha", 10)]),
            new("Alpha", "", 0, [Effect("beta", 10), Effect("gamma", 5)])]);
        var buffs = PartyEffectSelection.Select(Compiled.Pack, build);
        Assert.Equal(new[] { "gamma", "alpha", "delta" }, buffs.Select(buff => buff.Effect.Id));
        Assert.Equal("Zulu", buffs[1].Member);
    }

    [Fact]
    public void EmptySavedBuffCanBeatNegativeValuesWithoutAddingContributions()
    {
        var build = BuildSnapshot.Create(Compiled.Pack).WithParty([
            new("Negative", "", 0, [Effect("alpha", -10)]),
            new("Empty", "", 0, [new("beta", ImmutableDictionary<string, double>.Empty, true)])]);
        var selected = Assert.Single(PartyEffectSelection.Select(Compiled.Pack, build));
        Assert.Equal("Empty", selected.Member);
        Assert.Empty(selected.Effect.Modifiers);
        var result = BuildResolver.Resolve(Compiled, build);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.Calculation.Breakdowns["damage"].Contributions);
        Assert.Equal(100, result.Calculation.Stats["damage"]);
    }
}
