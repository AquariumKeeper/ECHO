using System.Collections.Immutable;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Application.Tests.Commands;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Application.Tests.Sessions;

public sealed class EquipmentLimitTests
{
    private const string Mask = "mask_striker_s_battlegear_mask";
    private const string Chest = "chest_striker_s_battlegear_chest";
    private static CompiledPackExpressions Pack => PackExpressionCompiler.Compile(Division.Rules with
    {
        Items = [.. Division.Rules.Items.Select(item => item.ID is Mask or Chest ? item with { Tags = [.. item.Tags, "limited"] } : item)],
        EquipmentLimits = [new() { ID = "test_allowance", Name = "Test allowance", Tag = "limited", Slots = ["mask", "chest"], Maximum = 1 }]
    }).Compiled!;

    [Fact]
    public void IllegalDraftsRemainEditableSerializableAndUndoableButCannotProduceComparisons()
    {
        var pack = Pack;
        var session = new PlannerSession(pack, Division.State(), new OperationApplier(pack));
        Assert.True(session.Apply([new EquipmentOperation("mask", Mask), new EquipmentOperation("chest", Chest)], 0, EditOrigin.Desktop).Succeeded);
        var invalid = session.Snapshot;
        Assert.Equal(2, BuildResolver.Resolve(pack, invalid.State.Build, invalid.State.Assumptions).Diagnostics.Count(LoadoutResolver.IsEquipmentLimitError));
        var read = BuildDocument.Decode(BuildDocument.Encode(invalid.State.Build));
        Assert.Equal(Mask, read.Equipment["mask"]);
        Assert.Equal(Chest, read.Equipment["chest"]);
        var imported = new PlannerSession(pack, PlannerState.Of(read), new OperationApplier(pack));
        Assert.NotNull(LoadoutResolver.EquipmentLimitProblem(pack.Pack, imported.Snapshot.State.Build));

        var comparison = session.CompareItems(invalid, "mask", [Mask], TestContext.Current.CancellationToken);
        Assert.NotNull(comparison.Problem);
        Assert.Null(comparison.Current);
        Assert.Empty(comparison.Candidates);
        var ranking = session.RankUpgrades(invalid, "mask", "weapon_damage", TestContext.Current.CancellationToken);
        Assert.NotNull(ranking.Problem);
        Assert.Empty(ranking.Upgrades);
        var builds = BuildComparison.Compare(pack, invalid.State.Build, BuildSnapshot.Create(pack.Pack), ImmutableDictionary<string, string>.Empty);
        Assert.False(builds.A.Valid);
        Assert.Contains(LoadoutResolver.EquipmentLimitExceeded, builds.A.Problem!, StringComparison.Ordinal);
        Assert.Empty(builds.Stats);

        Assert.True(session.Apply([EquipmentOperation.Cleared("chest")], 1, EditOrigin.Desktop).Succeeded);
        Assert.Null(LoadoutResolver.EquipmentLimitProblem(pack.Pack, session.Snapshot.State.Build));
        Assert.NotNull(session.CompareItems(session.Snapshot, "mask", [Mask], TestContext.Current.CancellationToken).Current);
        Assert.True(session.Undo(2).Succeeded);
        Assert.Same(invalid.State, session.Snapshot.State);
        Assert.NotNull(LoadoutResolver.EquipmentLimitProblem(pack.Pack, session.Snapshot.State.Build));
        Assert.True(session.Redo(3).Succeeded);
        Assert.Null(LoadoutResolver.EquipmentLimitProblem(pack.Pack, session.Snapshot.State.Build));
    }

    [Fact]
    public void CandidateThatWouldBreakALimitIsExcludedWithoutApplyingIt()
    {
        var pack = Pack;
        var session = new PlannerSession(pack, Division.State(Division.Empty().WithEquipment("mask", Mask)), new OperationApplier(pack));
        var comparison = session.CompareItems(session.Snapshot, "chest", [Chest, "chest_n_5_11_tactical_chest"], TestContext.Current.CancellationToken);
        Assert.NotNull(comparison.Current);
        Assert.DoesNotContain(Chest, comparison.Candidates.Keys);
        Assert.Contains("chest_n_5_11_tactical_chest", comparison.Candidates.Keys);
        Assert.Equal("", session.Snapshot.State.Build.Equipment["chest"]);
    }
}
