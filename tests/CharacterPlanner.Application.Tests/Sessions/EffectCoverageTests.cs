using System.Collections.Immutable;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Application.Tests.Commands;
using CharacterPlanner.Core.Builds;

namespace CharacterPlanner.Application.Tests.Sessions;

public sealed class EffectCoverageTests
{
    [Fact]
    public void CandidateAndBaselineCoverageBelongToTheirOwnImmutableStates()
    {
        var pack = Division.Rules;
        var host = pack.Items.First(item => item.TalentSlots.Any(slot => slot.Choices.Contains("gear_talent_resourceful")));
        var session = new PlannerSession(Division.Pack, Division.State(), Division.Applier);
        var snapshot = session.Snapshot;
        var compared = session.CompareItems(snapshot, host.Slot, [host.ID], TestContext.Current.CancellationToken);
        Assert.Empty(compared.Coverage.Talents);
        var candidate = compared.CandidateCoverage[host.ID];
        Assert.Contains(candidate.Talents, talent => talent.Talent == "gear_talent_resourceful" && talent.Channels.Any(channel => channel.Kind == "set-piece-credit"));
        Assert.Same(snapshot, session.Snapshot);
        Assert.True(session.Apply([new EquipmentOperation(host.Slot, host.ID)], 0, EditOrigin.Desktop).Succeeded);
        Assert.Empty(compared.Coverage.Talents);
        var both = BuildComparison.Compare(Division.Pack, snapshot.State.Build, session.Snapshot.State.Build, ImmutableDictionary<string, string>.Empty);
        Assert.Empty(both.A.Coverage.Talents);
        Assert.Contains(both.B.Coverage.Talents, talent => talent.Talent == "gear_talent_resourceful");
    }

    [Fact]
    public void RankedTalentChoicesRetainTheirOwnSelectedTalentAndInputs()
    {
        var state = Division.State(Division.Applied(Division.Empty(), new EquipmentOperation("primary_weapon", "primary_weapon_acr")));
        var session = new PlannerSession(Division.Pack, state, Division.Applier);
        var ranked = session.RankUpgrades(session.Snapshot, "primary_weapon", "weapon_damage", TestContext.Current.CancellationToken);
        Assert.Null(ranked.Problem);
        var candidates = ranked.Upgrades.Where(upgrade => upgrade.Kind == UpgradeKind.Talent).ToArray();
        Assert.NotEmpty(candidates);
        Assert.All(candidates, candidate =>
        {
            Assert.Equal("primary_weapon", candidate.Coverage.Inputs["weapon_slot"]);
            Assert.Contains(candidate.Coverage.Talents, talent => talent.Slot == "primary_weapon" && talent.Talent == candidate.Id);
        });
    }
}
