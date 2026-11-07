using System.Globalization;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Simulation;

namespace CharacterPlanner.Desktop.Tests;

/// <summary>Session-bound form defaults and validation.</summary>
public sealed class SimulateViewModelTests
{
    private static SimulateViewModel Create() => new(new PlannerSession(Division,
        PlannerState.Of(BuildSnapshot.Create(Division.Pack)), new OperationApplier(Division)), new QueueDispatcher());

    [Fact]
    public void TheSessionBoundFormKeepsDefaults()
    {
        using var simulate = Create();
        Assert.Equal(("60", "500", SimulateViewModel.NoSwaps, "1", ""), (simulate.FightLength, simulate.Fights, simulate.Swaps, simulate.SwapTime, simulate.Distance));
        Assert.Equal([SimulateViewModel.NoSwaps, SimulateViewModel.SwapWhenEmpty, SimulateViewModel.SwapAfterKill], simulate.SwapOptions);
        Assert.True(simulate.RandomFights);
        Assert.False(simulate.AverageFight);
        Assert.False(simulate.Incoming);
        Assert.Null(simulate.Problem);
        Assert.Equal(SimulateViewModel.Intro, simulate.Status);
        Assert.True(simulate.RunCommand.CanExecute(null));

        simulate.AverageFight = true;
        Assert.False(simulate.RandomFights);
        simulate.RandomFights = true;
        Assert.False(simulate.AverageFight);
    }

    [Theory]
    [InlineData("0", "500", "1", "", "Fight length must be a number of seconds from 1 to 600")]
    [InlineData("601", "500", "1", "", "Fight length must be a number of seconds from 1 to 600")]
    [InlineData("sixty", "500", "1", "", "Fight length must be a number of seconds from 1 to 600")]
    [InlineData("60", "0", "1", "", "Fights must be a whole number from 1 to 10,000")]
    [InlineData("60", "10001", "1", "", "Fights must be a whole number from 1 to 10,000")]
    [InlineData("60", "2.5", "1", "", "Fights must be a whole number from 1 to 10,000")]
    [InlineData("60", "500", "0", "", "Swap time must be a number of seconds above 0 and up to 10")]
    [InlineData("60", "500", "10.5", "", "Swap time must be a number of seconds above 0 and up to 10")]
    [InlineData("60", "500", "1", "0", "Target distance must be a number of meters above 0 and up to 100, or empty for none")]
    [InlineData("60", "500", "1", "101", "Target distance must be a number of meters above 0 and up to 100, or empty for none")]
    [InlineData(" 600 ", "10000", "10", "100", null)]
    [InlineData("0.5", "1", "0.1", " ", null)]
    public void ValuesAreCheckedAsGoCheckedThem(string length, string fights, string swap, string distance, string? expected) =>
        Assert.Equal(expected, SimulateViewModel.Validate(length, fights, swap, distance, CultureInfo.InvariantCulture));

    [Theory]
    [InlineData("-1", "100", "Trophy pickup delay must be a number of seconds from 0 to 60")]
    [InlineData("61", "100", "Trophy pickup delay must be a number of seconds from 0 to 60")]
    [InlineData("2", "101", "Trophies collected must be a percentage from 0 to 100")]
    [InlineData("2", "some", "Trophies collected must be a percentage from 0 to 100")]
    [InlineData("0", "0", null)]
    [InlineData("60", "100", null)]
    public void PickupAssumptionsAreChecked(string delay, string chance, string? expected) =>
        Assert.Equal(expected, SimulateViewModel.Validate("60", "500", "1", "", CultureInfo.InvariantCulture, delay, chance));

    [Theory]
    [InlineData("", null)]
    [InlineData("3", null)]
    [InlineData("10000", null)]
    [InlineData("10,000", null)]
    [InlineData("0", "Targets to clear must be a whole number from 1 to 10,000, or empty for none")]
    [InlineData("2.5", "Targets to clear must be a whole number from 1 to 10,000, or empty for none")]
    [InlineData("10001", "Targets to clear must be a whole number from 1 to 10,000, or empty for none")]
    public void TargetsToClearIsChecked(string targets, string? expected) =>
        Assert.Equal(expected, SimulateViewModel.Validate("60", "500", "1", "", CultureInfo.InvariantCulture, clearKills: targets));

    [Theory]
    [InlineData("", "", "", null)]
    [InlineData("2", "1", "25", null)]
    [InlineData("0.1", "60", "0", null)]
    [InlineData("60", "0.1", "100", null)]
    [InlineData("0.05", "1", "25", "Peek time must be a number of seconds from 0.1 to 60, or empty for none")]
    [InlineData("soon", "1", "25", "Peek time must be a number of seconds from 0.1 to 60, or empty for none")]
    [InlineData("2", "61", "25", "Cover time must be a number of seconds from 0.1 to 60, or empty for none")]
    [InlineData("2", "1", "101", "Hit chance while peeking must be a percentage from 0 to 100, or empty for none")]
    [InlineData("2", "1", "", null)]
    [InlineData("2", "", "25", "A cover cycle needs the peek time and the cover time")]
    [InlineData("", "1", "25", "A cover cycle needs the peek time and the cover time")]
    [InlineData("", "", "25", "The hit chance while peeking needs a cover cycle: set the peek time and the cover time")]
    public void CoverCycleSettingsAreChecked(string peek, string cover, string hit, string? expected) =>
        Assert.Equal(expected, SimulateViewModel.Validate("60", "500", "1", "", CultureInfo.InvariantCulture,
            peekSeconds: peek, coverSeconds: cover, peekHitPercent: hit));

    [Fact]
    public void TheHitChanceIsNeededOnlyWithIncomingFire() =>
        Assert.Equal("A cover cycle under incoming fire needs the hit chance while peeking", SimulateViewModel.Validate("60", "500", "1", "",
            CultureInfo.InvariantCulture, peekSeconds: "2", coverSeconds: "1", incoming: true));

    [Fact]
    public void CoverMessagesFollowTheFormsCulture() =>
        Assert.Equal("Peek time must be a number of seconds from 0,1 to 60, or empty for none",
            SimulateViewModel.Validate("60", "500", "1", "", CultureInfo.GetCultureInfo("de-DE"), peekSeconds: "0,05", coverSeconds: "1", peekHitPercent: "25"));

    [Fact]
    public void ACoverCycleIsCheckedLikeTheEngine()
    {
        // ADR 0009: talent triggers, as the engine does; an average fight names the cycle.
        using var simulate = Create();
        simulate.PeekSeconds = "2";
        Assert.Equal("A cover cycle needs the peek time and the cover time", simulate.Problem);
        simulate.CoverSeconds = "1";
        Assert.Null(simulate.Problem);
        // Turning incoming fire on asks for the hit chance, and turning it off again doesn't.
        simulate.Incoming = true;
        Assert.Equal("A cover cycle under incoming fire needs the hit chance while peeking", simulate.Problem);
        simulate.Incoming = false;
        Assert.Null(simulate.Problem);
        simulate.DriveBuffs = false;
        Assert.Equal("A cover cycle requires talent triggers", simulate.Problem);
        simulate.AverageFight = true;
        Assert.Equal("A cover cycle requires random fights", simulate.Problem);
        simulate.RandomFights = true;
        simulate.DriveBuffs = true;
        // Swaps combine with a cycle: only the ordinary swap checks speak (here, no second weapon is equipped).
        simulate.Swaps = SimulateViewModel.SwapWhenEmpty;
        Assert.Equal("Equip and choose another weapon for swaps", simulate.Problem);
        simulate.Swaps = SimulateViewModel.NoSwaps;
        Assert.Null(simulate.Problem);
    }

    [Fact]
    public async Task ACoverCycleRunPassesItsSettingsToTheEngine()
    {
        var build = BuildSnapshot.Create(Division.Pack).WithEquipment("primary_weapon", "primary_weapon_acr");
        var state = PlannerState.Of(build) with { Assumptions = PlannerState.Of(build).Assumptions.Add("weapon_slot", "primary_weapon") };
        var dispatcher = new QueueDispatcher();
        using var simulate = new SimulateViewModel(new PlannerSession(Division, state, new OperationApplier(Division)), dispatcher);
        simulate.FightLength = "5";
        simulate.Fights = "2";
        // Trailing no-break spaces, as pasted: the form trims them, so the run must too.
        simulate.PeekSeconds = "3\u00A0";
        simulate.CoverSeconds = "1\u00A0";
        simulate.PeekHitPercent = "12\u00A0";
        simulate.Distance = "20\u00A0";
        simulate.Incoming = true;
        simulate.RunCommand.Execute(null);
        await simulate.Running;
        dispatcher.RunPending();
        Assert.Contains(simulate.Result!.Limitations, note => note.StartsWith("Assumed cover cycle: 3 s peeking from cover, then 1 s behind it (20% of this fight)", StringComparison.Ordinal) &&
            note.Contains("enemies land 12% of their normal hit chance", StringComparison.Ordinal));
        // The form says which assumption the cycle chose, in the limitations shown beside the result.
        Assert.Contains("Set by the cover cycle, not chosen: In cover.", simulate.Limitations, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("2", null)]
    [InlineData("10", null)]
    [InlineData("1", "Enemies at once must be a whole number from 2 to 10, or empty for one at a time")]
    [InlineData("11", "Enemies at once must be a whole number from 2 to 10, or empty for one at a time")]
    [InlineData("2.5", "Enemies at once must be a whole number from 2 to 10, or empty for one at a time")]
    [InlineData("-3", "Enemies at once must be a whole number from 2 to 10, or empty for one at a time")]
    [InlineData("many", "Enemies at once must be a whole number from 2 to 10, or empty for one at a time")]
    public void EnemiesAtOnceIsChecked(string enemies, string? expected) =>
        Assert.Equal(expected, SimulateViewModel.Validate("60", "500", "1", "", CultureInfo.InvariantCulture, enemiesAtOnce: enemies));

    [Theory]
    [InlineData("", null)]
    [InlineData("0.1", null)]
    [InlineData("60", null)]
    [InlineData("0.05", "Time on each enemy must be a number of seconds from 0.1 to 60, or empty to shoot each until it dies")]
    [InlineData("61", "Time on each enemy must be a number of seconds from 0.1 to 60, or empty to shoot each until it dies")]
    [InlineData("Infinity", "Time on each enemy must be a number of seconds from 0.1 to 60, or empty to shoot each until it dies")]
    [InlineData("NaN", "Time on each enemy must be a number of seconds from 0.1 to 60, or empty to shoot each until it dies")]
    [InlineData("soon", "Time on each enemy must be a number of seconds from 0.1 to 60, or empty to shoot each until it dies")]
    public void TimeOnEachEnemyIsChecked(string focus, string? expected) =>
        Assert.Equal(expected, SimulateViewModel.Validate("60", "500", "1", "", CultureInfo.InvariantCulture, focusSeconds: focus));

    [Fact]
    public void TimeOnEachEnemyReadsTheUsersDecimals() =>
        Assert.Null(SimulateViewModel.Validate("60", "500", "1", "", CultureInfo.GetCultureInfo("de-DE"), focusSeconds: "2,5"));

    [Fact]
    public void TimeOnEachEnemyNeedsSeveralEnemies()
    {
        using var simulate = Create();
        simulate.FocusSeconds = "2";
        Assert.Equal("Time on each enemy needs several enemies at once", simulate.Problem);
        simulate.Incoming = true;
        simulate.EnemiesAtOnce = "3";
        Assert.Null(simulate.Problem);
    }

    [Fact]
    public async Task ARunTakesItsTimeOnEachEnemy()
    {
        var build = BuildSnapshot.Create(Division.Pack).WithEquipment("primary_weapon", "primary_weapon_acr");
        var state = PlannerState.Of(build) with { Assumptions = PlannerState.Of(build).Assumptions.Add("weapon_slot", "primary_weapon").Add("enemy_target", "1") };
        var dispatcher = new QueueDispatcher();
        using var simulate = new SimulateViewModel(new PlannerSession(Division, state, new OperationApplier(Division)), dispatcher);
        simulate.FightLength = "5";
        simulate.Fights = "2";
        simulate.Incoming = true;
        simulate.EnemiesAtOnce = "3";
        simulate.FocusSeconds = " 2\u00A0";
        Assert.Null(simulate.Problem);
        simulate.RunCommand.Execute(null);
        await simulate.Running;
        dispatcher.RunPending();
        Assert.Contains(simulate.Result!.Limitations, note => note.StartsWith("Assumed shooting pattern: 2 s on an enemy", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("", "", null)]
    [InlineData("1", "50", null)]
    [InlineData("10", "100", null)]
    [InlineData("3", "1", null)]
    [InlineData("3", "", "Armor kits need the number of kits and the armor percentage")]
    [InlineData("", "50", "Armor kits need the number of kits and the armor percentage")]
    [InlineData("0", "50", "Armor kits must be a whole number from 1 to 10, or empty for none")]
    [InlineData("11", "50", "Armor kits must be a whole number from 1 to 10, or empty for none")]
    [InlineData("2.5", "50", "Armor kits must be a whole number from 1 to 10, or empty for none")]
    [InlineData("3", "0.5", "Use a kit below must be a percentage from 1 to 100, or empty for none")]
    [InlineData("3", "101", "Use a kit below must be a percentage from 1 to 100, or empty for none")]
    [InlineData("3", "NaN", "Use a kit below must be a percentage from 1 to 100, or empty for none")]
    [InlineData("3", "half", "Use a kit below must be a percentage from 1 to 100, or empty for none")]
    public void ArmorKitsAreChecked(string kits, string percent, string? expected) =>
        Assert.Equal(expected, SimulateViewModel.Validate("60", "500", "1", "", CultureInfo.InvariantCulture, armorKits: kits, armorKitAtPercent: percent));

    [Fact]
    public void ArmorKitsAreCheckedLikeTheEngine()
    {
        using var simulate = Create();
        simulate.ArmorKits = "3";
        simulate.ArmorKitAtPercent = "50";
        Assert.Equal("Armor kits require incoming fire", simulate.Problem);
        simulate.Incoming = true;
        Assert.Null(simulate.Problem);
        simulate.DriveBuffs = false;
        Assert.Equal("Armor kits require talent triggers", simulate.Problem);
        simulate.AverageFight = true;
        Assert.Equal("Armor kits require random fights", simulate.Problem);
    }

    [Fact]
    public async Task ARunTakesItsArmorKitsAndReportsThem()
    {
        var build = BuildSnapshot.Create(Division.Pack).WithEquipment("primary_weapon", "primary_weapon_acr").WithEquipment("chest", "chest_badger_tuff_chest")
            .WithEquipment("mask", "mask_badger_tuff_mask").WithEquipment("gloves", "gloves_badger_tuff_gloves")
            .WithEquipment("holster", "holster_badger_tuff_holster").WithEquipment("knees", "knees_badger_tuff_kneepads");
        var state = PlannerState.Of(build) with { Assumptions = PlannerState.Of(build).Assumptions.Add("weapon_slot", "primary_weapon").Add("enemy_target", "1").Add("enemy_difficulty", "1") };
        var dispatcher = new QueueDispatcher();
        using var simulate = new SimulateViewModel(new PlannerSession(Division, state, new OperationApplier(Division)), dispatcher);
        simulate.FightLength = "120";
        simulate.Fights = "5";
        simulate.Incoming = true;
        // Both are read trimmed, as the check reads them.
        simulate.ArmorKits = " 3\u00A0";
        simulate.ArmorKitAtPercent = "50\u00A0";
        Assert.Null(simulate.Problem);
        simulate.RunCommand.Execute(null);
        await simulate.Running;
        dispatcher.RunPending();
        Assert.Contains(simulate.Result!.Limitations, note => note.Contains("Assumed armor kits: 3 carried, each used if armor is below 50%", StringComparison.Ordinal));
        Assert.True(simulate.Result.Incoming!.ArmorKitsUsed > 0);
        Assert.Contains("Armor kits/fight:", SimulationPresentation.Summary(simulate.Result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABuildThatReplacesTheKitIsRefusedWhenItRuns()
    {
        var build = BuildSnapshot.Create(Division.Pack).WithEquipment("primary_weapon", "primary_weapon_acr").WithSpecialization("survivalist");
        var state = PlannerState.Of(build) with { Assumptions = PlannerState.Of(build).Assumptions.Add("weapon_slot", "primary_weapon").Add("enemy_target", "1") };
        var dispatcher = new QueueDispatcher();
        using var simulate = new SimulateViewModel(new PlannerSession(Division, state, new OperationApplier(Division)), dispatcher);
        simulate.FightLength = "5";
        simulate.Fights = "2";
        simulate.Incoming = true;
        simulate.ArmorKits = "3";
        simulate.ArmorKitAtPercent = "50";
        simulate.RunCommand.Execute(null);
        await simulate.Running;
        dispatcher.RunPending();
        Assert.Null(simulate.Result);
        Assert.Contains("This build replaces the armor kit", simulate.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void SeveralEnemiesAreCheckedLikeTheEngine()
    {
        // ADR 0008: incoming fire and talent triggers; an average fight names the option.
        using var simulate = Create();
        simulate.EnemiesAtOnce = "3";
        Assert.Equal("Several enemies at once require incoming fire", simulate.Problem);
        simulate.Incoming = true;
        Assert.Null(simulate.Problem);
        simulate.DriveBuffs = false;
        Assert.Equal("Several enemies at once require talent triggers", simulate.Problem);
        simulate.AverageFight = true;
        Assert.Equal("Several enemies at once require random fights", simulate.Problem);
    }

    [Fact]
    public void AShrinkingGroupIsCheckedLikeTheEngine()
    {
        using var simulate = Create();
        simulate.Incoming = true;
        simulate.EnemiesShrink = true;
        Assert.Equal("A shrinking group needs several enemies at once", simulate.Problem);
        simulate.EnemiesAtOnce = "3";
        Assert.Equal("A shrinking group needs a number of targets to clear: how many enemies there are in all", simulate.Problem);
        simulate.ClearKills = "2";
        Assert.Equal("A shrinking group needs at least as many targets to clear as enemies at once", simulate.Problem);
        simulate.ClearKills = "3";
        Assert.Null(simulate.Problem);
        simulate.EnemiesShrink = false;
        simulate.ClearKills = "2";
        Assert.Null(simulate.Problem);
    }

    [Fact]
    public async Task AShrinkingRunPassesItsSettingsToTheEngine()
    {
        var build = BuildSnapshot.Create(Division.Pack).WithEquipment("primary_weapon", "primary_weapon_acr");
        var state = PlannerState.Of(build) with { Assumptions = PlannerState.Of(build).Assumptions.Add("weapon_slot", "primary_weapon") };
        var dispatcher = new QueueDispatcher();
        using var simulate = new SimulateViewModel(new PlannerSession(Division, state, new OperationApplier(Division)), dispatcher);
        simulate.FightLength = "5";
        simulate.Fights = "2";
        simulate.Incoming = true;
        simulate.EnemiesAtOnce = "3";
        simulate.ClearKills = "4";
        simulate.EnemiesShrink = true;
        simulate.RunCommand.Execute(null);
        await simulate.Running;
        dispatcher.RunPending();
        Assert.Contains(simulate.Result!.Limitations, note => note.Contains("There are 4 in all", StringComparison.Ordinal));
    }

    [Fact]
    public void EnemiesAttackingConflictsAndClearsWithoutARun()
    {
        // The form says so before a run, and follows the assumption as it changes, so a fixed conflict doesn't leave Run disabled.
        var dispatcher = new QueueDispatcher();
        var session = new PlannerSession(Division, PlannerState.Of(BuildSnapshot.Create(Division.Pack)), new OperationApplier(Division));
        using var simulate = new SimulateViewModel(session, dispatcher);
        simulate.EnemiesAtOnce = "3";
        simulate.Incoming = true;
        Assert.Null(simulate.Problem);
        Assert.True(session.Apply([new ContextOperation(DummyFightOptions.AttackersInput, Value: 2)], session.Snapshot.Revision, EditOrigin.Desktop).Succeeded);
        dispatcher.RunPending();
        Assert.Equal("Several enemies at once replace Enemies Attacking; set that to 1", simulate.Problem);
        Assert.False(simulate.RunCommand.CanExecute(null));
        Assert.True(session.Apply([new ContextOperation(DummyFightOptions.AttackersInput, Value: 1)], session.Snapshot.Revision, EditOrigin.Desktop).Succeeded);
        dispatcher.RunPending();
        Assert.Null(simulate.Problem);
    }

    [Fact]
    public void EnemiesAttackingLeavesTheFormAloneWithoutSeveralEnemies()
    {
        // With the field blank the assumption doesn't matter here, so a change to it doesn't reset the form's status.
        var dispatcher = new QueueDispatcher();
        var session = new PlannerSession(Division, PlannerState.Of(BuildSnapshot.Create(Division.Pack)), new OperationApplier(Division));
        using var simulate = new SimulateViewModel(session, dispatcher);
        var changes = 0;
        simulate.PropertyChanged += (_, change) => changes += change.PropertyName == nameof(SimulateViewModel.Status) ? 1 : 0;
        Assert.True(session.Apply([new ContextOperation(DummyFightOptions.AttackersInput, Value: 2)], session.Snapshot.Revision, EditOrigin.Desktop).Succeeded);
        dispatcher.RunPending();
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task ASeveralEnemiesRunPassesItsSizeToTheEngine()
    {
        var build = BuildSnapshot.Create(Division.Pack).WithEquipment("primary_weapon", "primary_weapon_acr");
        var state = PlannerState.Of(build) with { Assumptions = PlannerState.Of(build).Assumptions.Add("weapon_slot", "primary_weapon") };
        var dispatcher = new QueueDispatcher();
        using var simulate = new SimulateViewModel(new PlannerSession(Division, state, new OperationApplier(Division)), dispatcher);
        simulate.FightLength = "5";
        simulate.Fights = "2";
        simulate.Incoming = true;
        // A trailing no-break space, as pasted: the form trims it, so the run must too.
        simulate.EnemiesAtOnce = "3\u00A0";
        simulate.RunCommand.Execute(null);
        await simulate.Running;
        dispatcher.RunPending();
        Assert.Contains(simulate.Result!.Limitations, note => note.StartsWith("3 enemies fire at once", StringComparison.Ordinal));
    }

    [Fact]
    public void MixedTargetsOfferThePacksProfilesAndAreChecked()
    {
        // P10: the pack's enemy target profiles, without "no target"; the sequence needs talent triggers, no swaps and no
        // damaging skills, as the engine does.
        using var simulate = Create();
        Assert.True(simulate.HasTargetProfiles);
        Assert.Equal(Enumerable.Range(1, 20).Select(value => value.ToString(CultureInfo.InvariantCulture)), simulate.TargetProfiles.Select(choice => choice.Id));
        Assert.StartsWith("No mixed targets", simulate.TargetSequenceText, StringComparison.Ordinal);
        Assert.False(simulate.ClearTargetsCommand.CanExecute(null));
        Assert.False(simulate.RemoveLastTargetCommand.CanExecute(null));
        simulate.SequenceProfile = simulate.TargetProfiles.Single(choice => choice.Id == "2");
        simulate.AddTargetCommand.Execute(null);
        Assert.StartsWith("1 target, repeating: Normal", simulate.TargetSequenceText, StringComparison.Ordinal);
        simulate.SequenceProfile = simulate.TargetProfiles.Single(choice => choice.Id == "5");
        simulate.AddTargetCommand.Execute(null);
        Assert.Equal(["2", "5"], simulate.TargetSequence.Select(choice => choice.Id));
        Assert.StartsWith("2 targets, then repeating: Normal", simulate.TargetSequenceText, StringComparison.Ordinal);
        Assert.Contains("; Elite", simulate.TargetSequenceText, StringComparison.Ordinal);
        Assert.Null(simulate.Problem);
        // Mixed targets now combine with damaging skills (the engine refuses only skills whose damage follows the target).
        simulate.Incoming = true;
        simulate.IncludeSkills = true;
        Assert.Null(simulate.Problem);
        // And with swaps: only the ordinary swap checks speak (here, no second weapon is equipped).
        simulate.Swaps = SimulateViewModel.SwapWhenEmpty;
        Assert.Equal("Equip and choose another weapon for swaps", simulate.Problem);
        simulate.Swaps = SimulateViewModel.NoSwaps;
        simulate.IncludeSkills = false;
        simulate.Incoming = false;
        simulate.DriveBuffs = false;
        Assert.Equal("Mixed targets require talent triggers", simulate.Problem);
        // An average fight with a sequence names the sequence, rather than asking for triggers to be turned off.
        simulate.AverageFight = true;
        Assert.Equal("Mixed targets require random fights", simulate.Problem);
        simulate.RandomFights = true;
        simulate.DriveBuffs = true;
        simulate.RemoveLastTargetCommand.Execute(null);
        Assert.Equal(["2"], simulate.TargetSequence.Select(choice => choice.Id));
        simulate.ClearTargetsCommand.Execute(null);
        Assert.Empty(simulate.TargetSequence);
        Assert.Null(simulate.Problem);
    }

    [Fact]
    public void TargetDistancesAreTypedOneAtATimeAndChecked()
    {
        // P10: distances are added one at a time (a decimal comma never splits a list), above 0 and up to 100 m like the target
        // distance; they need talent triggers and random fights, as the engine does.
        using var simulate = Create();
        Assert.StartsWith("No target distances", simulate.TargetDistancesText, StringComparison.Ordinal);
        foreach (var entry in new[] { "", "0", "150", "far" })
        {
            simulate.DistanceEntry = entry;
            Assert.False(simulate.AddDistanceCommand.CanExecute(null));
        }
        simulate.DistanceEntry = "10";
        // A distance typed but not added blocks the run, so it can't be silently left out.
        Assert.Equal("Add the typed target distance, or clear the box", simulate.Problem);
        simulate.AddDistanceCommand.Execute(null);
        Assert.Equal("", simulate.DistanceEntry);
        Assert.Null(simulate.Problem);
        Assert.Equal("1 distance, repeating: 10 m", simulate.TargetDistancesText);
        simulate.DistanceEntry = "40";
        simulate.AddDistanceCommand.Execute(null);
        Assert.Equal([10d, 40], simulate.TargetDistances);
        Assert.Equal("2 distances, then repeating: 10 m; 40 m", simulate.TargetDistancesText);
        Assert.Null(simulate.Problem);
        simulate.DriveBuffs = false;
        Assert.Equal("Target distances require talent triggers", simulate.Problem);
        simulate.AverageFight = true;
        Assert.Equal("Target distances require random fights", simulate.Problem);
        simulate.RandomFights = true;
        simulate.DriveBuffs = true;
        simulate.RemoveLastDistanceCommand.Execute(null);
        Assert.Equal([10d], simulate.TargetDistances);
        simulate.ClearDistancesCommand.Execute(null);
        Assert.Empty(simulate.TargetDistances);
        Assert.False(simulate.ClearDistancesCommand.CanExecute(null));
    }

    [Fact]
    public async Task ATargetDistanceRunPassesTheDistancesToTheEngine()
    {
        var build = BuildSnapshot.Create(Division.Pack).WithEquipment("primary_weapon", "primary_weapon_acr");
        var state = PlannerState.Of(build) with { Assumptions = PlannerState.Of(build).Assumptions.Add("weapon_slot", "primary_weapon") };
        var dispatcher = new QueueDispatcher();
        using var simulate = new SimulateViewModel(new PlannerSession(Division, state, new OperationApplier(Division)), dispatcher);
        simulate.FightLength = "5";
        simulate.Fights = "2";
        foreach (var meters in new[] { "10", "40" })
        {
            simulate.DistanceEntry = meters;
            simulate.AddDistanceCommand.Execute(null);
        }
        simulate.RunCommand.Execute(null);
        await simulate.Running;
        dispatcher.RunPending();
        Assert.Contains(simulate.Result!.Limitations, note => note.StartsWith("Targets cycle through 2 distances (10, 40 m)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AMixedTargetRunPassesTheSequenceToTheEngine()
    {
        var build = BuildSnapshot.Create(Division.Pack).WithEquipment("primary_weapon", "primary_weapon_acr");
        var state = PlannerState.Of(build) with { Assumptions = PlannerState.Of(build).Assumptions.Add("weapon_slot", "primary_weapon") };
        var dispatcher = new QueueDispatcher();
        using var simulate = new SimulateViewModel(new PlannerSession(Division, state, new OperationApplier(Division)), dispatcher);
        simulate.FightLength = "5";
        simulate.Fights = "2";
        simulate.SequenceProfile = simulate.TargetProfiles.Single(choice => choice.Id == "1");
        simulate.AddTargetCommand.Execute(null);
        simulate.SequenceProfile = simulate.TargetProfiles.Single(choice => choice.Id == "5");
        simulate.AddTargetCommand.Execute(null);
        simulate.RunCommand.Execute(null);
        await simulate.Running;
        dispatcher.RunPending();
        Assert.Contains(simulate.Result!.Limitations, note => note.StartsWith("Targets cycle through a sequence of 2 (profiles 1, 5)", StringComparison.Ordinal));
    }

    [Fact]
    public void AProblemReplacesTheStatusUntilItIsFixed()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            using var simulate = Create();
            simulate.Distance = "200";
            Assert.True(simulate.HasProblem);
            Assert.Equal("Target distance must be a number of meters above 0 and up to 100, or empty for none", simulate.Status);
            simulate.Distance = "";
            Assert.False(simulate.HasProblem);
            Assert.Equal(SimulateViewModel.Intro, simulate.Status);
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }
}
