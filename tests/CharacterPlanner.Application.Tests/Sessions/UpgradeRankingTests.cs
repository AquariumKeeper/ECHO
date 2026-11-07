using System.Collections.Immutable;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Application.Tests.Commands;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Application.Tests.Sessions;

/// <summary>Ranking an item's changes by one stat (the Go app's Rank Upgrades): each tried alone, nothing applied.</summary>
public sealed class UpgradeRankingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static PlannerSession Session(BuildSnapshot build, IOperationApplier? applier = null) => new(Division.Pack, Division.State(build), applier ?? Division.Applier);

    private static double Resolved(PlannerState state, string stat) => BuildResolver.Resolve(Division.Pack, state.Build, state.Assumptions).Calculation.Stats[stat];

    [Fact]
    public void EveryChangeIsTriedAloneAndRankedByTheStat()
    {
        var session = Session(Division.Geared());
        var before = session.Snapshot;
        var ranking = session.RankUpgrades(before, "mask", "weapon_damage", Token);

        Assert.Null(ranking.Problem);
        Assert.Equal(Resolved(before.State, "weapon_damage"), ranking.Before);
        Assert.Contains(ranking.Upgrades, upgrade => upgrade.Kind == UpgradeKind.Attribute);
        Assert.Contains(ranking.Upgrades, upgrade => upgrade.Kind == UpgradeKind.Mod);
        Assert.Equal(ranking.Upgrades.Select(upgrade => upgrade.Delta).OrderDescending(), ranking.Upgrades.Select(upgrade => upgrade.Delta));
        // Each result is what its own operations make of the build as it is.
        foreach (var upgrade in ranking.Upgrades)
        {
            var edited = Division.Applied(before.State, [.. upgrade.Operations]);
            Assert.Equal(Resolved(edited, "weapon_damage"), upgrade.After);
            Assert.Equal(upgrade.After - ranking.Before, upgrade.Delta);
        }

        // The best is the weapon damage core at its highest roll, labelled as the Go app labelled it.
        var best = ranking.Upgrades[0];
        Assert.Equal((UpgradeKind.Attribute, "core_1", "Core 1", "weapon_damage_gear_core", "Weapon Damage (Offensive)", 0.15, "percent", false),
            (best.Kind, best.Target, best.TargetName, best.Id, best.Name, best.Value, best.ValueFormat, best.Reroll));
        Assert.Equal([new AttributeOperation("mask", "core_1", "weapon_damage_gear_core")], best.Operations);
        Assert.True(best.Delta > 0);

        Assert.Same(before, session.Snapshot);
    }

    [Fact]
    public void TheCurrentAttributeIsOfferedOnlyAsARerollBelowItsHighestRoll()
    {
        var low = Division.Applied(Division.Geared(), new AttributeOperation("mask", "core_1", "weapon_damage_gear_core", 0.015));
        var session = Session(low);
        var reroll = Assert.Single(session.RankUpgrades(session.Snapshot, "mask", "weapon_damage", Token).Upgrades, upgrade => upgrade.Id == "weapon_damage_gear_core");
        Assert.True(reroll.Reroll);
        Assert.Equal(0.15, reroll.Value);
        Assert.Equal(0.135, reroll.Delta, 9);

        var high = Division.Applied(low, new AttributeOperation("mask", "core_1", "weapon_damage_gear_core"));
        session = Session(high);
        var ranking = session.RankUpgrades(session.Snapshot, "mask", "weapon_damage", Token);
        Assert.DoesNotContain(ranking.Upgrades, upgrade => upgrade.Id == "weapon_damage_gear_core");
        // The other core choices replace it, and lose its weapon damage.
        Assert.Contains(ranking.Upgrades, upgrade => upgrade.Id == "armor_gear_core" && upgrade.Delta < 0);
    }

    [Fact]
    public void TheCurrentModIsOfferedOnlyAsARerollBelowItsDefaultRoll()
    {
        var low = Division.Applied(Division.Geared(), new ModOperation("mask", "mod_1", "critical_hit_chance_gear_mod"), new ModRollOperation("mask", "mod_1", 0.012));
        var session = Session(low);
        var reroll = Assert.Single(session.RankUpgrades(session.Snapshot, "mask", "critical_hit_chance", Token).Upgrades, upgrade => upgrade.Id == "critical_hit_chance_gear_mod");
        Assert.Equal((UpgradeKind.Mod, true, 0.06, "percent"), (reroll.Kind, reroll.Reroll, reroll.Value, reroll.ValueFormat));
        // Reselecting the mod would keep its roll; the reroll sets the roll.
        Assert.Equal([new ModRollOperation("mask", "mod_1")], reroll.Operations);
        Assert.Equal(0.048, reroll.Delta, 9);

        var high = Division.Applied(low, new ModRollOperation("mask", "mod_1"));
        session = Session(high);
        Assert.DoesNotContain(session.RankUpgrades(session.Snapshot, "mask", "critical_hit_chance", Token).Upgrades, upgrade => upgrade.Id == "critical_hit_chance_gear_mod");
    }

    [Fact]
    public void WeaponsAreRankedAsTheWeaponInHand()
    {
        var build = Division.Applied(Division.Empty(), new EquipmentOperation("primary_weapon", "primary_weapon_acr"));
        var session = Session(build);
        var ranking = session.RankUpgrades(session.Snapshot, "primary_weapon", "assault_rifle_damage", Token);

        var inHand = Division.State(build) with { Assumptions = ImmutableDictionary<string, string>.Empty.Add(GamePack.WeaponSlotSelector, "primary_weapon") };
        Assert.Equal(Resolved(inHand, "assault_rifle_damage"), ranking.Before);
        Assert.True(ranking.Before > 0);
        Assert.Contains(ranking.Upgrades, upgrade => upgrade.Kind == UpgradeKind.Talent);
        var talent = ranking.Upgrades.First(upgrade => upgrade.Kind == UpgradeKind.Talent);
        Assert.Equal(Resolved(Division.Applied(inHand, [.. talent.Operations]), "assault_rifle_damage"), talent.After);
        // The talent being tried is never the one already selected, and the session's assumptions are untouched.
        Assert.False(session.Snapshot.State.Assumptions.ContainsKey(GamePack.WeaponSlotSelector));
    }

    [Fact]
    public void ChangesThatAddABuildErrorAreLeftOut()
    {
        var build = Division.Applied(Division.Empty(), new EquipmentOperation("primary_weapon", "primary_weapon_acr"));
        var session = Session(build, new BreaksOn("weapon_talent_killer"));
        var talents = session.RankUpgrades(session.Snapshot, "primary_weapon", "weapon_damage", Token).Upgrades.Where(upgrade => upgrade.Kind == UpgradeKind.Talent).Select(upgrade => upgrade.Id).ToList();
        Assert.DoesNotContain("weapon_talent_killer", talents);
        Assert.Contains("weapon_talent_breadbasket", talents);
    }

    [Fact]
    public void ApplyingAnUpgradeIsOneUndoStep()
    {
        var session = Session(Division.Geared());
        var best = session.RankUpgrades(session.Snapshot, "mask", "weapon_damage", Token).Upgrades[0];
        var result = session.Apply(best.Operations, session.Snapshot.Revision, EditOrigin.Desktop);
        Assert.True(result.Succeeded);
        Assert.Equal(best.After, Resolved(result.Snapshot.State, "weapon_damage"));
        Assert.True(session.Undo(session.Snapshot.Revision).Succeeded);
        Assert.False(session.Snapshot.CanUndo);
    }

    [Fact]
    public void AnEmptySlotOrUnknownStatIsAProblemNotAnException()
    {
        var session = Session(Division.Geared());
        var empty = session.RankUpgrades(session.Snapshot, "backpack", "weapon_damage", Token);
        Assert.Equal("slot \"backpack\" has no item equipped", empty.Problem);
        Assert.Empty(empty.Upgrades);
        Assert.Equal("unknown stat \"luck\"", session.RankUpgrades(session.Snapshot, "mask", "luck", Token).Problem);
    }

    [Fact]
    public void ACancelledRankingStops()
    {
        var session = Session(Division.Geared());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => session.RankUpgrades(session.Snapshot, "mask", "weapon_damage", cancelled.Token));
    }

    /// <summary>The real applier, except that selecting one talent also equips an unknown item, an error diagnostic.</summary>
    private sealed class BreaksOn(string talent) : IOperationApplier
    {
        public OperationOutcome Apply(PlannerState state, ImmutableArray<BuildOperation> operations)
        {
            var outcome = Division.Applier.Apply(state, operations);
            if (!operations.Any(operation => operation is TalentOperation selected && selected.Id == talent)) { return outcome; }
            var broken = outcome.State.Build with { Equipment = outcome.State.Build.Equipment.SetItem("mask", "no_such_item") };
            return outcome with { State = outcome.State with { Build = broken } };
        }
    }
}
