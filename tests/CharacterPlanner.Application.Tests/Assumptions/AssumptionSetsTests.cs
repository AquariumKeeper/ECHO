using System.Collections.Immutable;
using CharacterPlanner.Application.Assumptions;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Application.Tests.Commands;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Application.Tests.Assumptions;

/// <summary>Saved assumption sets (the Go app's G396): what one saves, applying one in one step, and which one matches.</summary>
public sealed class AssumptionSetsTests
{
    private static ImmutableDictionary<string, string> Map(params (string Id, string Value)[] values) =>
        values.ToImmutableDictionary(pair => pair.Id, pair => pair.Value, StringComparer.Ordinal);

    private static AssumptionSet Set(string name, params (string Id, string Value)[] values) => new(Division.Rules.ID, name, Map(values));

    [Fact]
    public void ASetSavesEveryAssumptionButTheWeaponInHand()
    {
        var assumptions = Map(("weapon_slot", "primary_weapon"), ("enemy_target", "2"), ("in_sync_weapon_hit_active", "true"));
        Assert.Equal(Map(("enemy_target", "2"), ("in_sync_weapon_hit_active", "true")), AssumptionSets.Values(assumptions));
    }

    [Fact]
    public void ApplyingReplacesEveryAssumptionButTheWeaponInHandInOneUndoStep()
    {
        var current = Map(("weapon_slot", "secondary_weapon"), ("enemy_target", "2"), ("practical_hit_rate", "70.5"), ("stale_input", "1"));
        var set = Set("Boss", ("practical_hit_rate", "50"), ("in_sync_weapon_hit_active", "true"), ("weapon_slot", "sidearm"));
        var application = AssumptionSets.Apply(Division.Rules, current, set);

        Assert.Empty(application.Skipped);
        // Ids the set lacks are cleared to their defaults, an id the pack no longer knows included; then the set's values.
        Assert.Equal(
            [ContextOperation.Cleared("enemy_target"), ContextOperation.Cleared("stale_input"), new ContextOperation("in_sync_weapon_hit_active", Text: "true"), new ContextOperation("practical_hit_rate", 50)],
            application.Operations);

        var session = new PlannerSession(Division.Pack, Division.State() with { Assumptions = current }, Division.Applier);
        var result = session.Apply(application.Operations, session.Snapshot.Revision, EditOrigin.Desktop);
        Assert.True(result.Succeeded);
        Assert.Equal(Map(("weapon_slot", "secondary_weapon"), ("practical_hit_rate", "50"), ("in_sync_weapon_hit_active", "true")), result.Snapshot.State.Assumptions);
        Assert.Single(result.Snapshot.History, entry => entry.Label is not null);
        Assert.Equal("Boss", AssumptionSets.Matching(Division.Rules, result.Snapshot.State.Assumptions, [set])?.Name);
    }

    [Fact]
    public void ValuesThePackNoLongerAcceptsAreReportedAndLeftOut()
    {
        var current = Map(("enemy_target", "2"));
        var set = Set("Old", ("enemy_target", "-1"), ("luck", "7"), ("in_sync_weapon_hit_active", "false"), ("practical_hit_rate", "lots"), ("practical_headshot_share", "25"));
        var application = AssumptionSets.Apply(Division.Rules, current, set);
        Assert.Equal(["enemy_target", "in_sync_weapon_hit_active", "luck", "practical_hit_rate"], application.Skipped);
        Assert.Equal([ContextOperation.Cleared("enemy_target"), new ContextOperation("practical_headshot_share", 25)], application.Operations);
        Assert.True(Division.Apply(Division.State() with { Assumptions = current }, [.. application.Operations]).Succeeded);
    }

    [Fact]
    public void ASetThatAlreadyMatchesNeedsNoOperations()
    {
        // Numbers are compared as the applier stores them, so "70.50" saved by hand still matches "70.5".
        var current = Map(("weapon_slot", "primary_weapon"), ("practical_hit_rate", "70.5"));
        var set = Set("Range", ("practical_hit_rate", "70.50"));
        Assert.Empty(AssumptionSets.Apply(Division.Rules, current, set).Operations);
        Assert.Same(set, AssumptionSets.Matching(Division.Rules, current, [set]));
    }

    [Fact]
    public void TheMatchingSetIsTheFirstTheAssumptionsEqual()
    {
        var current = Map(("weapon_slot", "primary_weapon"), ("enemy_target", "2"));
        var bigger = Set("Bigger", ("enemy_target", "2"), ("practical_hit_rate", "50"));
        // Values the pack no longer accepts don't stop a set matching: applying it would give these assumptions.
        var stale = Set("Stale", ("enemy_target", "2"), ("luck", "7"));
        var exact = Set("Exact", ("enemy_target", "2"));
        Assert.Same(stale, AssumptionSets.Matching(Division.Rules, current, [bigger, stale, exact]));
        Assert.Same(exact, AssumptionSets.Matching(Division.Rules, current, [bigger, exact]));
        Assert.Null(AssumptionSets.Matching(Division.Rules, current, [bigger]));
        Assert.Null(AssumptionSets.Matching(Division.Rules, Map(), [exact]));
        Assert.Equal("Defaults", AssumptionSets.Matching(Division.Rules, Map((GamePack.WeaponSlotSelector, "sidearm")), [Set("Defaults")])?.Name);
    }
}
