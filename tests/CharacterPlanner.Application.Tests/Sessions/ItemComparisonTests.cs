using System.Collections.Immutable;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Application.Tests.Commands;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Application.Tests.Sessions;

/// <summary>Comparing a slot's items (F40): each is resolved as equipping it would make the build, and nothing is applied.</summary>
public sealed class ItemComparisonTests
{
    private readonly PlannerSession session = new(Division.Pack, Division.State(), Division.Applier);

    [Fact]
    public void ItemsAreComparedAsEquippingThemWouldMakeTheBuild()
    {
        var before = session.Snapshot;
        var comparison = session.CompareItems(before, "backpack", ["backpack_devil_s_due", "backpack_ceska_vyroba_s_r_o_backpack"], TestContext.Current.CancellationToken);

        // Equipping fills the item's fixed choices (Devil's Due's talent), so the comparison is the edit's build resolved.
        var edited = Division.Applier.Apply(before.State, [new EquipmentOperation("backpack", "backpack_devil_s_due")]);
        var expected = BuildResolver.Resolve(Division.Pack, edited.State.Build, edited.State.Assumptions);
        Assert.Equal(expected.Calculation.Stats, comparison.Candidates["backpack_devil_s_due"].Calculation.Stats);
        Assert.Equal(BuildResolver.Resolve(Division.Pack, before.State.Build, before.State.Assumptions).Calculation.Stats, comparison.Current!.Calculation.Stats);

        // Nothing was applied.
        Assert.Same(before, session.Snapshot);
    }

    [Fact]
    public void WeaponsAreComparedAsTheWeaponInHand()
    {
        var comparison = session.CompareItems(session.Snapshot, "primary_weapon", ["primary_weapon_acr"], TestContext.Current.CancellationToken);
        var inHand = comparison.Candidates["primary_weapon_acr"].Calculation.Stats;
        var assumptions = ImmutableDictionary<string, string>.Empty.Add(GamePack.WeaponSlotSelector, "primary_weapon");
        var edited = Division.Applier.Apply(session.Snapshot.State with { Assumptions = assumptions }, [new EquipmentOperation("primary_weapon", "primary_weapon_acr")]);
        Assert.Equal(BuildResolver.Resolve(Division.Pack, edited.State.Build, edited.State.Assumptions).Calculation.Stats, inHand);
        Assert.True(inHand["equipped_weapon_damage"] > 0);
        Assert.False(session.Snapshot.State.Assumptions.ContainsKey(GamePack.WeaponSlotSelector));
    }

    [Fact]
    public void ItemsThatCantBeEquippedThereAreLeftOut()
    {
        var comparison = session.CompareItems(session.Snapshot, "mask", ["mask_n_5_11_tactical_mask", "chest_n_5_11_tactical_chest", "no_such_item"], TestContext.Current.CancellationToken);
        Assert.Equal(["mask_n_5_11_tactical_mask"], comparison.Candidates.Keys);
    }

    [Fact]
    public void ACancelledComparisonStops()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => session.CompareItems(session.Snapshot, "mask", ["mask_n_5_11_tactical_mask"], cancelled.Token));
    }

    [Fact]
    public void APreviewIsTheEditsStateWithoutApplyingIt()
    {
        var before = session.Snapshot;
        var outcome = session.Preview(before, [new EquipmentOperation("mask", "mask_n_5_11_tactical_mask")]);
        Assert.True(outcome.Succeeded);
        Assert.Equal("mask_n_5_11_tactical_mask", outcome.State.Build.Equipment["mask"]);
        Assert.Same(before, session.Snapshot);
    }
}
