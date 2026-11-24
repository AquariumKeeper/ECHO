using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

/// <summary>
/// Behavior the committed pack's deliveries turn on: Rebalance cap candidate 2 (with R30 candidate 2 and P22), the
/// Unstoppable Force refresh, the R35 coverage audit, the stack, buff and graph-driven stacks refresh candidates and Dragon's
/// Glare.
/// </summary>
public sealed partial class DummyWeaponSimulatorTests
{
    private static CompiledPackExpressions CommittedPack()
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        return Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
    }

    // Equips items, selecting each item's first talent choice (the fixed talent of a named or exotic item).
    private static BuildSnapshot Equip(GamePack pack, params string[] items)
    {
        var build = BuildSnapshot.Create(pack);
        foreach (var id in items)
        {
            var item = pack.Items.Single(item => item.ID == id);
            build = build.WithEquipment(item.Slot, item.ID);
            foreach (var slot in item.TalentSlots) { build = build.WithTalentSelection(item.Slot, slot.ID, slot.Choices[0]); }
        }
        return build;
    }

    [Fact]
    public void CommittedIgnitedBurnsTickAtTheR30Damage()
    {
        // ShadeTech's R30 check: Ignited on an ACR at 100% hits; each burn tick is 33,181.126 at 0% Status Effects.
        var pack = CommittedPack();
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr")
            .WithTalentSelection("primary_weapon", "talent", "weapon_talent_ignited");
        var context = Context.Add("practical_hit_rate", "100").Add("enemy_target", "1").Add("enemy_difficulty", "1");
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, context,
            new() { Duration = 120, Mode = SimulationMode.Random, Iterations = 1, DriveSimpleBuffs = true, CaptureFirstFightEvents = true },
            cancellationToken: TestContext.Current.CancellationToken);
        var ticks = result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.BurnTick).ToArray();
        Assert.NotEmpty(ticks);
        // A tick on a nearly dead target deals only what is left, so the full tick is the largest.
        Assert.Equal(33181.126, ticks.Max(entry => entry.Damage), 3);
        Assert.True(result.StatusDps!.Mean > 0);
    }

    [Fact]
    public void CommittedRebalanceCapsWithOneColour()
    {
        // ShadeTech's red-only fight: no incoming hits land, so only Red builds. With a cap of 80 per colour Red banks
        // and clears; under the old 79 it stalled near the cap (mean 71.6 stacks over 600 s).
        var pack = CommittedPack();
        var build = Equip(pack.Pack, "primary_weapon_dread_edict", "backpack_harrier_pride");
        var context = Context.Add("practical_hit_rate", "100").Add("enemy_target", "1").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "0");
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, context,
            new() { Duration = 600, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true },
            cancellationToken: TestContext.Current.CancellationToken);
        var red = result.TimedTriggerUptime["rebalance_red_stacks"];
        Assert.InRange(red.MeanStacks!.Value, 1, 60);
    }

    // A key's uptime over 300 s at 100% hits with 6 s between targets, on the committed rules and on the rules as they were
    // before the refresh deliveries (the before function restores the old rule).
    private static (TimedTriggerUptime Committed, TimedTriggerUptime Before) Refreshed(BuildSnapshot build, string key, Func<SimTriggerDefinition, SimTriggerDefinition> before,
        string difficulty = "1")
    {
        var pack = CommittedPack();
        var old = pack.Pack with
        {
            Simulation = pack.Pack.Simulation with { Triggers = [.. pack.Pack.Simulation.Triggers.Select(trigger => trigger.Key == key ? before(trigger) : trigger)] }
        };
        var context = Context.Add("practical_hit_rate", "100").Add("enemy_target", "1").Add("enemy_difficulty", difficulty);
        TimedTriggerUptime Run(CompiledPackExpressions compiled) => DummyWeaponSimulator.RunAgainstTargets(compiled, build, context,
            new() { Duration = 300, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true }, retargetDelay: 6,
            cancellationToken: TestContext.Current.CancellationToken).TimedTriggerUptime[key];
        return (Run(pack), Run(Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(old).Compiled)));
    }

    // Mean stacks of a stack key whose committed rules refresh every stack on each trigger.
    private static (double Committed, double Before) RefreshedStacks(BuildSnapshot build, string key, Func<SimTriggerDefinition, SimTriggerDefinition> before)
    {
        Assert.All(CommittedPack().Pack.Simulation.Triggers.Where(trigger => trigger.Key == key), trigger => Assert.True(trigger.Refresh));
        var (committed, old) = Refreshed(build, key, before);
        return (committed.MeanStacks!.Value, old.MeanStacks!.Value);
    }

    private static BuildSnapshot Talented(string slot, string item, string talent) =>
        BuildSnapshot.Create(CommittedPack().Pack).WithEquipment("primary_weapon", "primary_weapon_acr").WithEquipment(slot, item).WithTalentSelection(slot, "talent", talent);

    [Fact]
    public void CommittedUnstoppableForceStacksShareOneTimerEveryKillRestarts()
    {
        // The Unstoppable Force candidate: one 15 s timer for all stacks, restarted by every kill. Per-stack timers expire
        // between kills while the shared timer holds near five (seed 1: 4.76 against 2.08).
        var (shared, perStack) = RefreshedStacks(Talented("backpack", "backpack_n_5_11_tactical_backpack", "gear_talent_unstoppable_force"),
            "unstoppable_force_stacks", trigger => trigger with { Refresh = false });
        Assert.InRange(shared, 4.5, 5);
        Assert.InRange(perStack, 1, 3);
    }

    [Fact]
    public void CommittedFuturePerfectStacksShareOneTimer()
    {
        // The stack refresh candidate: every kill restarts all of Future Perfect's stacks (ShadeTech's probe: 2.96 against 2.18).
        var (shared, perStack) = RefreshedStacks(Talented("primary_weapon", "primary_weapon_acr", "weapon_talent_future_perfect"),
            "future_perfect_stacks", trigger => trigger with { Refresh = false });
        Assert.InRange(shared, 2.5, 3);
        Assert.True(shared > perStack + 0.5, $"{shared} against {perStack}");
    }

    [Fact]
    public void CommittedStrainedStacksClearSoonAfterFiringStops()
    {
        // The stack refresh candidate: Strained's stacks last 0.8 s past the last gain instead of until reload, so they no
        // longer carry through the gaps between targets (ShadeTech's probe: 0.27 against 2.17).
        var (committed, before) = RefreshedStacks(Talented("primary_weapon", "primary_weapon_acr", "weapon_talent_strained"),
            "strained_stacks", trigger => trigger with { Refresh = false, Duration = 0 });
        Assert.True(committed < before / 2, $"{committed} against {before}");
    }

    [Fact]
    public void CommittedOutsiderIgnoresKillsWhileItsBuffRuns()
    {
        // The buff refresh candidate: a kill during Outsider's 10 s buff no longer restarts it (every: 10). ShadeTech's
        // 600 s probe: 99.9% uptime before, 75.0% after.
        var build = Talented("primary_weapon", "primary_weapon_mp5_st", "weapon_talent_outsider");
        Assert.All(CommittedPack().Pack.Simulation.Triggers.Where(trigger => trigger.Key == "outsider_active"), trigger => Assert.Equal(trigger.Duration, trigger.Every));
        // Before the candidate there was no cooldown, so no shared one either (R85 groups Outsider's rows).
        var (committed, before) = Refreshed(build, "outsider_active", trigger => trigger with { Every = 0, EveryGroup = "" });
        Assert.True(before.Fraction > 0.95, $"{before.Fraction}");
        Assert.InRange(committed.Fraction, 0.5, 0.9);
    }

    [Fact]
    public void CommittedDragonsGlareBurnsTheTargetOnItsCooldown()
    {
        // The Dragon's Glare candidate (R36): the default burn on the current target every 35 s, on whole seconds.
        var pack = CommittedPack();
        var glare = Assert.Single(pack.Pack.Simulation.Triggers, trigger => trigger.Talent == "gear_talent_dragon_s_glare");
        Assert.Equal(("second", "burning", 7.0, 35.0, 20.0), (glare.On, glare.Status, glare.Duration, glare.Every, glare.Within));
        var build = Equip(pack.Pack, "primary_weapon_acr", "holster_imperial_dynasty");
        var context = Context.Add("practical_hit_rate", "100").Add("enemy_target", "1").Add("enemy_difficulty", "1");
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, context,
            new() { Duration = 120, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, CaptureFirstFightEvents = true },
            cancellationToken: TestContext.Current.CancellationToken);
        var ticks = result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.BurnTick).ToArray();
        // The first burn lands on the first whole second with a living target (seed 1: the first target is dead at 1 s and the
        // next arrives later), with the R30 tick at 0% Status Effects. Nothing burns again until the 35 s cooldown ends; a kill
        // may cut a burn short.
        var first = ticks[0].Time;
        Assert.Equal(2, first);
        Assert.Equal(33181.126, ticks.Max(entry => entry.Damage), 3);
        Assert.All(ticks, entry => Assert.True(entry.Time < first + 7 || entry.Time >= first + 35, $"{entry.Time}"));
        Assert.Contains(ticks, entry => entry.Time >= first + 35);
        Assert.True(result.StatusDps!.Mean > 0);
    }

    [Theory]
    [InlineData(20.0, true)]
    [InlineData(25.0, false)]
    [InlineData(null, true)]
    public void CommittedDragonsGlareBurnsOnlyWithin20Metres(double? distance, bool burns)
    {
        // R101: the burn needs the target within 20 m of the player; a fight at a set distance beyond it has none, and one without a
        // distance takes the range as met and says so. The ACR has no burn of its own, so any tick is Dragon's Glare's.
        var pack = CommittedPack();
        var build = Equip(pack.Pack, "primary_weapon_acr", "holster_imperial_dynasty");
        var context = Context.Add("practical_hit_rate", "100").Add("enemy_target", "1").Add("enemy_difficulty", "1");
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, context,
            new() { Duration = 60, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, Distance = distance, CaptureFirstFightEvents = true },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(burns, result.FirstFightEvents.Any(entry => entry.Kind == DummyEventKind.BurnTick));
        Assert.Equal(distance is null, result.Limitations.Contains("With no fight distance, a status rule's range is taken as met"));
    }

    [Fact]
    public void CommittedPaymentInKindStacksShareOneTimer()
    {
        // The graph-driven stacks candidate: every hit restarts Payment in Kind's 10 s timer for all stacks (ShadeTech's
        // probe: 95.0 mean stacks against 14.8).
        var (shared, perStack) = RefreshedStacks(Equip(CommittedPack().Pack, "primary_weapon_backfire"),
            "payment_in_kind_stacks", trigger => trigger with { Refresh = false });
        Assert.True(shared > 2 * perStack, $"{shared} against {perStack}");
    }

    [Fact]
    public void CommittedStabilizeStacksClearSoonAfterFiringStops()
    {
        // The graph-driven stacks candidate: Stabilize's stacks last 4 s past the last shot instead of forever (ShadeTech's
        // probe: 6.5 against 58.3).
        var (committed, before) = RefreshedStacks(Talented("primary_weapon", "primary_weapon_acr", "weapon_talent_stabilize"),
            "stabilize_stacks", trigger => trigger with { Refresh = false, Duration = 0 });
        Assert.True(committed < before / 2, $"{committed} against {before}");
    }

    [Fact]
    public void CommittedFrenzyEndsWhenItsWeaponIsPutAway()
    {
        // The Frenzy weapon-switch candidate: cancelOn swap ends the buff when the Frenzy weapon is put away. Under rotation
        // the RPK empties and swaps away inside the 9 s buff, so uptime falls; with one weapon nothing swaps and the run
        // matches the old rule exactly (ShadeTech's probe: 375.95 s to 317.38 s, and identical single-weapon runs).
        var pack = CommittedPack();
        Assert.All(pack.Pack.Simulation.Triggers.Where(trigger => trigger.Key == "frenzy_active"), trigger => Assert.Equal("swap", trigger.CancelOn));
        var old = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
        {
            Simulation = pack.Pack.Simulation with
            {
                Triggers = [.. pack.Pack.Simulation.Triggers.Select(trigger => trigger.Key == "frenzy_active" ? trigger with { CancelOn = "" } : trigger)]
            }
        }).Compiled);
        var single = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_military_rpk_74")
            .WithTalentSelection("primary_weapon", "talent", "weapon_talent_frenzy");
        var rotated = single.WithEquipment("secondary_weapon", "secondary_weapon_acr");
        var context = Context.Add("practical_hit_rate", "100").Add("enemy_target", "1").Add("enemy_difficulty", "3");
        DummyFightResult Run(CompiledPackExpressions compiled, BuildSnapshot build, RotationOptions? rotation) =>
            DummyWeaponSimulator.RunAgainstTargets(compiled, build, context,
                new() { Duration = 300, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, Rotation = rotation },
                retargetDelay: 0, cancellationToken: TestContext.Current.CancellationToken);
        var rotation = new RotationOptions("secondary_weapon");
        var (now, before) = (Run(pack, rotated, rotation), Run(old, rotated, rotation));
        Assert.True(now.TimedTriggerUptime["frenzy_active"].Seconds < before.TimedTriggerUptime["frenzy_active"].Seconds,
            $"{now.TimedTriggerUptime["frenzy_active"].Seconds} against {before.TimedTriggerUptime["frenzy_active"].Seconds}");
        Assert.Equal(before.ShotsFired, now.ShotsFired);
        Assert.Equal(Run(old, single, null).TimedTriggerUptime["frenzy_active"], Run(pack, single, null).TimedTriggerUptime["frenzy_active"]);
    }

    [Theory]
    [InlineData("primary_weapon_acr_ss", true)]
    [InlineData("primary_weapon_acr", false)]
    public void CommittedResolvedForcesHeadshotsOnlyFromItsWeaponTypes(string weapon, bool eligible)
    {
        // The Resolved candidate (R36): with no natural headshots, a rifle's first hit becomes a headshot; an assault rifle's
        // never does (ShadeTech's probe: the ACR-SS's first shot 92,364 to 166,255, the ACR unchanged).
        var pack = CommittedPack();
        var old = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
        {
            Simulation = pack.Pack.Simulation with { Triggers = [.. pack.Pack.Simulation.Triggers.Where(trigger => trigger.Talent != "gear_talent_resolved")] }
        }).Compiled);
        var build = Equip(pack.Pack, weapon, "chest_iron_will");
        var context = Context.Add("practical_hit_rate", "100").Add("practical_headshot_share", "0").Add("enemy_target", "1").Add("enemy_difficulty", "1");
        double[] Shots(CompiledPackExpressions compiled) => [.. DummyWeaponSimulator.RunAgainstTargets(compiled, build, context,
            new() { Duration = 10, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, CaptureFirstFightEvents = true },
            cancellationToken: TestContext.Current.CancellationToken).FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.Shot).Select(entry => entry.Damage)];
        var (now, before) = (Shots(pack), Shots(old));
        if (eligible) { Assert.True(now[0] > before[0], $"{now[0]} against {before[0]}"); }
        else { Assert.Equal(before, now); }
    }

    [Theory]
    [InlineData("primary_weapon_model_700", "concussion_mmr_headshot_active", "concussion_headshot_active")]
    [InlineData("primary_weapon_acr", "concussion_headshot_active", "concussion_mmr_headshot_active")]
    public void CommittedConcussionHeadshotBuffFollowsTheWeaponType(string weapon, string gains, string idle)
    {
        // The Concussion weapon-tag candidate: a marksman rifle's headshots drive the 5 s bonus, other weapons' the 1.5 s one.
        var pack = CommittedPack();
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", weapon)
            .WithEquipment("backpack", "backpack_n_5_11_tactical_backpack").WithTalentSelection("backpack", "talent", "gear_talent_concussion");
        var context = Context.Add("practical_hit_rate", "100").Add("practical_headshot_share", "100").Add("enemy_target", "1").Add("enemy_difficulty", "1");
        var uptime = DummyWeaponSimulator.RunAgainstTargets(pack, build, context,
            new() { Duration = 60, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true },
            cancellationToken: TestContext.Current.CancellationToken).TimedTriggerUptime;
        Assert.True(uptime[gains].Seconds > 0);
        Assert.Equal(0, uptime[idle].Seconds);
    }

    [Fact]
    public void CommittedSteadyHandedRefillsAndClearsOnTheHundredthHit()
    {
        // The Steady Handed candidate: every 100th hit refills the magazine and clears the stacks (ShadeTech's probe on an ACR:
        // 92 to 84 reloads, mean stacks 97.4 to 48.9). Before, the stacks sat at 99 and nothing refilled.
        var pack = CommittedPack();
        var old = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
        {
            Simulation = pack.Pack.Simulation with
            {
                Triggers = [.. pack.Pack.Simulation.Triggers.Where(trigger => trigger.Key != "steady_handed_refill")
                    .Select(trigger => trigger.Key == "steady_handed_stacks" ? trigger with { Purge = false } : trigger)]
            }
        }).Compiled);
        var build = Talented("primary_weapon", "primary_weapon_acr", "weapon_talent_steady_handed");
        var context = Context.Add("practical_hit_rate", "100").Add("enemy_target", "1").Add("enemy_difficulty", "1");
        DummyFightResult Run(CompiledPackExpressions compiled, RotationOptions? rotation = null) => DummyWeaponSimulator.RunAgainstTargets(compiled,
            rotation is null ? build : build.WithEquipment("secondary_weapon", "secondary_weapon_acr_ss"), context,
            new() { Duration = 300, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, Rotation = rotation },
            cancellationToken: TestContext.Current.CancellationToken);
        var (now, before) = (Run(pack), Run(old));
        Assert.True(now.Reloads < before.Reloads, $"{now.Reloads} against {before.Reloads}");
        Assert.True(now.TimedTriggerUptime["steady_handed_stacks"].MeanStacks < 60);
        Assert.True(before.TimedTriggerUptime["steady_handed_stacks"].MeanStacks > 90);
        // The refill lands within the hit's own events, before an emptied magazine queues a swap, so rotation runs.
        foreach (var mode in new[] { WeaponSwapMode.WhenEmpty, WeaponSwapMode.AfterKill })
        { Assert.True(Run(pack, new RotationOptions("secondary_weapon", mode)).Swaps > 0); }
    }

    [Fact]
    public void CommittedAblativeGrantsTheWearerTemporaryArmorOnArmorBreak()
    {
        // The Ablative self-grant candidate (R36): an armor break grants 80% of max armor for 10 s, once per 45 s (ShadeTech's
        // probe: time to down 8.61 s to 11.58 s).
        var pack = CommittedPack();
        var build = Equip(pack.Pack, "primary_weapon_acr", "chest_tardigrade_armor_system");
        var context = Context.Add("practical_hit_rate", "100").Add("enemy_target", "1").Add("enemy_difficulty", "1");
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, context,
            new() { Duration = 120, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, CaptureFirstFightEvents = true },
            cancellationToken: TestContext.Current.CancellationToken);
        var grants = result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.BonusArmorGranted).ToArray();
        Assert.NotEmpty(grants);
        Assert.Equal(grants.Select(grant => grant.Time), result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.ArmorBreak)
            .Select(entry => entry.Time).Where(time => grants.Any(grant => grant.Time == time)));
        Assert.All(grants.Zip(grants.Skip(1)), pair => Assert.True(pair.Second.Time - pair.First.Time >= 45));
    }

    [Fact]
    public void CommittedTraumaBlindsAndBleedsOnItsCooldowns()
    {
        // The Trauma candidate: a headshot arms a 7 s blind and a body hit a 10 s bleed, each at most every 30 s (ShadeTech's
        // probe at difficulty 4: status DPS 0 to 4,381). From the Quick Draw and Trauma timing candidate (R38) each status lands
        // on the hit that triggers it.
        var pack = CommittedPack();
        Assert.All(pack.Pack.Simulation.Triggers.Where(trigger => trigger.Talent is "gear_talent_trauma" or "gear_talent_perfect_trauma"),
            trigger => Assert.True(trigger.ApplyOnHit));
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr")
            .WithEquipment("chest", "chest_n_5_11_tactical_chest").WithTalentSelection("chest", "talent", "gear_talent_trauma");
        var context = Context.Add("practical_hit_rate", "100").Add("practical_headshot_share", "20").Add("enemy_target", "1").Add("enemy_difficulty", "4");
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, context,
            new() { Duration = 300, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, CaptureFirstFightEvents = true },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.StatusDps!.Mean > 0);
        Assert.True(result.TargetStatusUptime["blinded"].Seconds > 0);
        Assert.True(result.TargetStatusUptime["bleeding"].Seconds > 0);
        // A 10 s bleed at most every 30 s: never more than a third of the fight, plus one bleed the last cooldown cuts short.
        Assert.InRange(result.TargetStatusUptime["bleeding"].Seconds, 0, 300 / 3.0 + 10);
    }

    [Fact]
    public void CommittedFrenzyStartsWhenTheReloadCompletes()
    {
        // R37: Frenzy's 9 s buff starts at the reload's completion, so it runs while firing instead of partly during the reload
        // (ShadeTech's probe on a Stoner LMG: uptime 261 s to 288 s, total DPS 754,406 to 907,917).
        var pack = CommittedPack();
        Assert.All(pack.Pack.Simulation.Triggers.Where(trigger => trigger.Key == "frenzy_active"), trigger => Assert.Equal("reload_end", trigger.On));
        var old = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
        {
            Simulation = pack.Pack.Simulation with
            {
                Triggers = [.. pack.Pack.Simulation.Triggers.Select(trigger => trigger.Key == "frenzy_active" ? trigger with { On = "reload" } : trigger)]
            }
        }).Compiled);
        var build = Talented("primary_weapon", "primary_weapon_stoner_lamg", "weapon_talent_frenzy");
        var context = Context.Add("practical_hit_rate", "100").Add("enemy_target", "1").Add("enemy_difficulty", "3");
        DummyFightResult Run(CompiledPackExpressions compiled) => DummyWeaponSimulator.RunAgainstTargets(compiled, build, context,
            new() { Duration = 300, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true }, retargetDelay: 0,
            cancellationToken: TestContext.Current.CancellationToken);
        var (now, before) = (Run(pack), Run(old));
        Assert.True(now.TotalDps!.Mean > before.TotalDps!.Mean, $"{now.TotalDps.Mean} against {before.TotalDps.Mean}");
    }

    [Fact]
    public void CommittedPummelCountsKillsAcrossReloads()
    {
        // The Pummel candidate: the source graph has no reload listener, so the kill count survives reloads. On an ACS-12 at
        // difficulty 4, three kills rarely fit one magazine, so the old reset kept Pummel from firing (ShadeTech's probe:
        // uptime 0 s to 548.2 s).
        var pack = CommittedPack();
        Assert.All(pack.Pack.Simulation.Triggers.Where(trigger => trigger.Key == "pummel_active"), trigger => Assert.Equal("", trigger.ResetOn));
        var (now, before) = Refreshed(Talented("primary_weapon", "primary_weapon_acs_12", "weapon_talent_pummel"), "pummel_active",
            trigger => trigger with { ResetOn = "reload" }, difficulty: "4");
        Assert.True(now.Seconds > before.Seconds, $"{now.Seconds} against {before.Seconds}");
    }

    [Fact]
    public void CommittedChemicalAgentReactantSpeedsReloads()
    {
        // The Chemical Agent candidate: Reactant adds 20% reload speed beside its bonus armor (ShadeTech's probe: an ACR's reload
        // 1.91 s to 1.592 s).
        var pack = CommittedPack();
        var build = Equip(pack.Pack, "primary_weapon_acr", "mask_catalyst");
        double Reload(bool reactant) => BuildResolver.Resolve(pack, build, reactant ? Context.Add("reactant_active", "true") : Context)
            .Calculation.Stats["equipped_weapon_reload_time"];
        Assert.Equal(Reload(false) / 1.2, Reload(true), 6);
    }

    [Fact]
    public void CommittedQuickDrawStocksWhileHolsteredAndSpendsOnTheFirstPistolShot()
    {
        // The Quick Draw candidate (R38): +1 stack per 0.3 s while no pistol is in hand, +10% Pistol Damage per stack, and the
        // first pistol shot is a headshot that spends them (ShadeTech's probe: the first D50 shot after a swap 467,024 instead of
        // 189,172). With an ACR and a D50 swapping when empty, that shot beats the same shot without the rule; the next doesn't.
        var committed = CommittedPack();
        var rule = Assert.Single(committed.Pack.Simulation.Triggers, trigger => trigger.Talent == "gear_talent_quick_draw");
        Assert.Equal((0.3, 100L, "shot", true), (rule.GainWhileHolstered, rule.Max, rule.ConsumeOn, rule.ForceHeadshot));
        var without = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(committed.Pack with
        {
            Simulation = committed.Pack.Simulation with { Triggers = [.. committed.Pack.Simulation.Triggers.Where(trigger => trigger != rule)] }
        }).Compiled);
        var context = Context.Add("practical_hit_rate", "100").Add("practical_headshot_share", "0");
        var build = Equip(committed.Pack, "primary_weapon_acr", "holster_dodge_city_gunslinger_s_holster").WithEquipment("sidearm", "sidearm_d50");
        DummyEvent[] PistolShots(CompiledPackExpressions pack) => [.. DummyWeaponSimulator.Run(pack, build, context, new()
        {
            Duration = 30, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, CaptureFirstFightEvents = true,
            Rotation = new RotationOptions("sidearm")
        }, TestContext.Current.CancellationToken).FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.Shot && entry.WeaponSlot == "sidearm")];
        var (now, before) = (PistolShots(committed), PistolShots(without));
        Assert.Equal(before.Select(entry => entry.Time), now.Select(entry => entry.Time));
        Assert.True(now[0].Damage > 2 * before[0].Damage, $"{now[0].Damage} against {before[0].Damage}");
        Assert.Equal(before[1].Damage, now[1].Damage);
    }

    [Fact]
    public void CommittedPummelCountDropsAfterTwentyQuietSeconds()
    {
        // The Pummel count-window candidate (R38): a partial count clears 20 s after the last kill. With 15 s between targets
        // at difficulty 4, kills land about 17–20 s apart, so a gap just over 20 s drops the count and three kills rarely chain
        // (ShadeTech's probe on an ACS-12: uptime 110 s to 10 s).
        var pack = CommittedPack();
        Assert.All(pack.Pack.Simulation.Triggers.Where(trigger => trigger.Key == "pummel_active"), trigger => Assert.Equal(20, trigger.CountWindow));
        var old = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
        {
            Simulation = pack.Pack.Simulation with
            {
                Triggers = [.. pack.Pack.Simulation.Triggers.Select(trigger => trigger.Key == "pummel_active" ? trigger with { CountWindow = 0 } : trigger)]
            }
        }).Compiled);
        var build = Talented("primary_weapon", "primary_weapon_acs_12", "weapon_talent_pummel");
        var context = Context.Add("practical_hit_rate", "100").Add("enemy_target", "1").Add("enemy_difficulty", "4");
        double Uptime(CompiledPackExpressions compiled) => DummyWeaponSimulator.RunAgainstTargets(compiled, build, context,
            new() { Duration = 600, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true }, retargetDelay: 15,
            cancellationToken: TestContext.Current.CancellationToken).TimedTriggerUptime["pummel_active"].Seconds;
        var (now, before) = (Uptime(pack), Uptime(old));
        Assert.True(now < before, $"{now} against {before}");
    }

    [Fact]
    public void CommittedToxicDeliveryTotalDamageIsHalfAConcussionGrenade()
    {
        // The Toxic Delivery stat candidate (R38): 2120 × the status tick skill curve × 0.5 with no grenade, explosive or
        // Status Effects bonus (ShadeTech's reading: 703,439.9). Nothing reads it until the talent damage channel exists.
        var pack = CommittedPack();
        var stats = BuildResolver.Resolve(pack, BuildSnapshot.Create(pack.Pack), Context).Calculation.Stats;
        Assert.Equal(2120 * stats["status_tick_skill_curve"] * 0.5, stats["toxic_delivery_total_damage"], 6);
        Assert.Equal(703439.9, stats["toxic_delivery_total_damage"], 1);
    }

    [Fact]
    public void CommittedSlottedCountsTheInvestorsBonusAttributesByColour()
    {
        // The Slotted candidate (R38): ShadeTech's reading, with Critical Hit Damage, Critical Hit Chance and Skill Haste in the
        // Investor's bonus slots, is red 2, blue 0, yellow 1: Critical Hit Damage 0.12 -> 0.32 and Skill Efficiency 0 -> 0.05.
        var pack = CommittedPack();
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("mask", "mask_investor")
            .WithAttributeRoll("mask", "core_1", new("weapon_damage_gear_core", 0.15))
            .WithAttributeRoll("mask", "minor_1", new("critical_hit_damage_gear_minor", 0.12))
            .WithAttributeRoll("mask", "minor_2", new("critical_hit_chance_gear_minor", 0.06))
            .WithAttributeRoll("mask", "minor_3", new("skill_haste_gear_minor", 0.12));
        var without = BuildResolver.Resolve(pack, build, Context).Calculation.Stats;
        var stats = BuildResolver.Resolve(pack, build.WithTalentSelection("mask", "talent", "gear_talent_slotted"), Context).Calculation.Stats;
        Assert.Equal((2.0, 0.0, 1.0), (stats["slotted_offensive_count"], stats["slotted_defensive_count"], stats["slotted_skill_count"]));
        Assert.Equal((0.12, 0.0), (without["critical_hit_damage"], without["skill_efficiency"]));
        Assert.Equal(0.32, stats["critical_hit_damage"], 9);
        Assert.Equal(0.05, stats["skill_efficiency"], 9);
        // A blue bonus attribute in place of the Critical Hit Chance counts blue and adds 1% armor regeneration.
        var regeneration = pack.Pack.Attributes.Single(attribute => attribute.ID == "armor_regeneration_gear_minor");
        var blue = build.WithAttributeRoll("mask", "minor_2", new(regeneration.ID, regeneration.Max));
        var plain = BuildResolver.Resolve(pack, blue, Context).Calculation.Stats;
        var slotted = BuildResolver.Resolve(pack, blue.WithTalentSelection("mask", "talent", "gear_talent_slotted"), Context).Calculation.Stats;
        Assert.Equal((1.0, 1.0, 1.0), (slotted["slotted_offensive_count"], slotted["slotted_defensive_count"], slotted["slotted_skill_count"]));
        Assert.Equal(0.01, slotted["armor_regeneration_percent"] - plain["armor_regeneration_percent"], 9);
        // Another mask's bonus attributes don't count: its template declares no count stats.
        var effects = pack.Pack.Attributes.Single(attribute => attribute.ID == "status_effects_gear_minor");
        var other = BuildSnapshot.Create(pack.Pack).WithEquipment("mask", "mask_vile")
            .WithAttributeRoll("mask", "minor_1", new(effects.ID, effects.Max));
        Assert.Equal(0, BuildResolver.Resolve(pack, other, Context).Calculation.Stats["slotted_skill_count"]);
    }

    [Fact]
    public void CommittedSepticShockAddsItsBonusOnTheEighthHit()
    {
        // The Septic Shock candidate: Scorpio's graph adds the damage-taken modifier on the 8th hitting shot (the stack display
        // then reads 9). Its statuses and once-per-cycle end stay omitted, so the talent is partial.
        var pack = CommittedPack();
        var rule = pack.Pack.Simulation.Triggers.Single(rule => rule.Key == "septic_shock_nine_stacks");
        Assert.Equal(("weapon_talent_septic_shock", 8L, 10.0), (rule.Talent, rule.Count, rule.Duration));
        Assert.Equal("partial", pack.Pack.Talents.Single(talent => talent.ID == "weapon_talent_septic_shock").Coverage!.Status);
    }

    [Fact]
    public void CommittedArdentBurnsOnlyOnceItsHalfMagazineMeterIsFull()
    {
        // The Ardent candidate (R39): the Iron Lung's meter holds half its magazine, and only bullets fired from a full meter burn,
        // so the first burn starts with the bullet after the one that fills it. Without the gate the first hit burns.
        var pack = CommittedPack();
        var rules = pack.Pack.Simulation.Triggers.Where(rule => rule.Talent == "weapon_talent_ardent").ToArray();
        Assert.Equal([("stacks", 0.5, "", false), ("status", 0, "ardent_heat_stacks", true)], rules.Select(rule => (rule.Kind, rule.MaxMagazine, rule.WhileKey, rule.WhileFull)));
        Assert.Equal("complete", pack.Pack.Talents.Single(talent => talent.ID == "weapon_talent_ardent").Coverage!.Status);
        var build = Equip(pack.Pack, "primary_weapon_iron_lung");
        var context = Context.Add("practical_hit_rate", "100").Add("enemy_target", "1").Add("enemy_difficulty", "4");
        var magazine = BuildResolver.Resolve(pack, build, context).Calculation.Stats["equipped_weapon_magazine_size"];
        DummyEvent[] Events(CompiledPackExpressions compiled) => [.. DummyWeaponSimulator.Run(compiled, build, context, new()
        {
            Duration = 60, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, CaptureFirstFightEvents = true
        }, TestContext.Current.CancellationToken).FirstFightEvents];
        // A burn's first tick lands with the bullet that applies it.
        double FirstBurnShot(DummyEvent[] events)
        {
            var applied = events.First(entry => entry.Kind == DummyEventKind.BurnTick).Time;
            return events.Count(entry => entry.Kind == DummyEventKind.Shot && entry.Time <= applied);
        }
        // The 50-round magazine's meter holds 25: the 25th bullet fills it and the 26th burns.
        Assert.Equal(50, magazine);
        Assert.Equal(26, FirstBurnShot(Events(pack)));
        var ungated = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
        {
            Simulation = pack.Pack.Simulation with
            {
                Triggers = [.. pack.Pack.Simulation.Triggers.Select(rule => rule.Talent == "weapon_talent_ardent" && rule.Kind == "status" ? rule with { WhileKey = "", WhileFull = false } : rule)]
            }
        }).Compiled);
        Assert.Equal(1, FirstBurnShot(Events(ungated)));
    }

    [Theory]
    [InlineData(10, "Enemy Hit Rate (%) follows the fight distance: 70 at 10 m (up to 15 m)", 0.7)]
    [InlineData(15, "Enemy Hit Rate (%) follows the fight distance: 70 at 15 m (up to 15 m)", 0.7)]
    [InlineData(30, "Enemy Hit Rate (%) follows the fight distance: 30 at 30 m (beyond the pack's bands)", 0.3)]
    public void CommittedEnemyHitRateFollowsTheFightDistance(double meters, string note, double share)
    {
        // R44: a normal enemy lands 70% up to 15 m and 30% beyond, and a rate the user sets wins.
        var pack = CommittedPack();
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr");
        var context = Context.Add("practical_hit_rate", "100").Add("enemy_target", "1").Add("enemy_difficulty", "1").Add("enemy_firing_share", "100");
        var options = new DummyFightOptions { Duration = 60, Mode = SimulationMode.Random, Iterations = 40, DriveSimpleBuffs = true, IncludeIncoming = true, Distance = meters };
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, context, options, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(note, result.Limitations);
        Assert.InRange(result.Incoming!.Hits / result.Incoming.Attempts, share - 0.05, share + 0.05);
        var set = DummyWeaponSimulator.RunAgainstTargets(pack, build, context.Add("enemy_hit_rate", "50"), options, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(set.Limitations, text => text.StartsWith("Enemy Hit Rate (%) is set to 50", StringComparison.Ordinal));
        Assert.InRange(set.Incoming!.Hits / set.Incoming.Attempts, 0.45, 0.55);
        // With no enemy fire the rate isn't read, so the result doesn't claim it.
        var quiet = DummyWeaponSimulator.RunAgainstTargets(pack, build, context, options with { IncludeIncoming = false, Iterations = 1 }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(quiet.Limitations, text => text.StartsWith("Enemy Hit Rate", StringComparison.Ordinal));
        Assert.DoesNotContain("Enemy Hit Rate", quiet.Coverage.InputBasis, StringComparison.Ordinal);
    }

    // One item for each slot a set covers: its four-piece talent and more.
    private static string[] SetPieces(GamePack pack, string set) =>
        [.. pack.Sets.Single(entry => entry.ID == set).Items.Select(id => pack.Items.Single(item => item.ID == id)).GroupBy(item => item.Slot).Select(group => group.First().ID)];

    private static DummyFightResult SetFightUnderFire(string set, int? kits = null, string weapon = "primary_weapon_acr", string? headshots = null)
    {
        var pack = CommittedPack();
        var context = Context.Add("enemy_target", "1").Add("enemy_difficulty", "1");
        if (headshots is not null) { context = context.Add("practical_hit_rate", "100").Add("practical_headshot_share", headshots); }
        return DummyWeaponSimulator.RunAgainstTargets(pack, Equip(pack.Pack, [.. SetPieces(pack.Pack, set), weapon]), context,
            new() { Duration = 30, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, ArmorKits = kits, ArmorKitAtPercent = kits is null ? null : 50 },
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("gear_set_future_initiative", "Ground Control's repair doesn't happen in this fight: it repairs only when you repair an ally, and fights have no allies")]
    [InlineData("gear_set_umbra_initiative", "From the Shadows Into the Light's repair doesn't happen in this fight: Shadow stacks built in the open repair only as they're spent in cover, and fights never move between the two")]
    [InlineData("gear_set_system_corruption", "Hackstep Protocol's repair doesn't happen in this fight: it is the build's armor kit, and this fight uses no kits")]
    public void CommittedSetRepairsNoFightReachesStepAsideUnderFire(string set, string note)
    {
        // ADR 0021: these sets refused every fight under enemy fire for their repairs; the fight now runs without them and says why.
        var result = SetFightUnderFire(set);
        Assert.Contains(note, result.Limitations);
        Assert.NotNull(result.Incoming);
    }

    [Fact]
    public void CommittedSetRepairsFightsReachRunUnderFire()
    {
        // ADR 0021, r127.candidate.1: Hotshot and System Corruption with kits refused every fight under fire. Now Headache's cycle repairs
        // with a marksman rifle landing headshots, and Hackstep's ability is the kit.
        var hotshot = SetFightUnderFire("gear_set_hotshot", weapon: "primary_weapon_m700_carbon", headshots: "100");
        Assert.Contains(hotshot.Limitations, note => note.Contains("counts consecutive headshots with weapons tagged weapon_type_marksman_rifle", StringComparison.Ordinal));
        // The repair is the cycle's: with no headshots there's none.
        Assert.True(hotshot.Incoming!.ArmorRepaired > 0);
        Assert.Equal(0, SetFightUnderFire("gear_set_hotshot", weapon: "primary_weapon_m700_carbon", headshots: "0").Incoming!.ArmorRepaired);
        var hackstep = SetFightUnderFire("gear_set_system_corruption", kits: 3);
        Assert.True(hackstep.Incoming!.ArmorKitAbilityUses > 0);
    }

    private static string[] ApexPieces(int pieces) => [.. new[] { "mask_zenith_mask", "chest_apogee_chest_plate", "backpack_wolf_pack", "gloves_pinnacle_gloves" }.Take(pieces)];

    [Theory]
    [InlineData(3, false)]
    [InlineData(4, true)]
    public void CommittedApexEngineBuildsStacksOnAFullSet(int pieces, bool builds)
    {
        // The set's own talent has no slot, so a fight reaches it through the top bonus: a stack each 100 pellets, from none.
        var pack = CommittedPack();
        var build = Equip(pack.Pack, [.. ApexPieces(pieces), "primary_weapon_acr"]);
        var context = Context.Add("practical_hit_rate", "100").Add("enemy_target", "5").Add("enemy_difficulty", "4");
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, context, new()
        {
            Duration = 40,
            Mode = SimulationMode.Random,
            Iterations = 1,
            DriveSimpleBuffs = true,
            CaptureFirstFightEvents = true
        }, cancellationToken: TestContext.Current.CancellationToken);
        var shots = result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.Shot).Select(entry => entry.Damage).ToArray();
        Assert.True(shots.Length > 100);
        // Each stack is +65% weapon damage on the set's top row. Crits vary single shots, so this compares the first 100 hits with the
        // last 100: only a built stack lifts them.
        Assert.Equal(builds, shots.TakeLast(100).Average() > shots.Take(100).Average() * 1.3);
        Assert.Equal(builds, result.TimedTriggerUptime.ContainsKey("apex_engine_bonus_stacks"));
    }

    [Fact]
    public void ApexEngineStacksReachLaterSkillUses()
    {
        // Its stacks raise Skill Tier, and each use reads the tier as it deploys, so the fourth piece lifts the biggest impact.
        var pack = CommittedPack();
        var context = Context.Add("practical_hit_rate", "100").Add("enemy_target", "5").Add("enemy_difficulty", "4").Add("enemy_hit_rate", "0");
        double Fight(int pieces) => DummyWeaponSimulator.RunAgainstTargets(pack,
            Equip(pack.Pack, [.. ApexPieces(pieces), "primary_weapon_acr"]).WithActiveSkillSelection("skill_slot_1", "skill_explosive_sticky_bomb"), context, new()
            {
                Duration = 120,
                Mode = SimulationMode.Random,
                Iterations = 1,
                Seed = 1,
                DriveSimpleBuffs = true,
                IncludeIncoming = true,
                IncludeSkills = true,
                CaptureFirstFightEvents = true
            }, cancellationToken: TestContext.Current.CancellationToken).FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.SkillImpact).Max(entry => entry.Damage);
        Assert.True(Fight(4) > Fight(3));
    }

    [Fact]
    public void ApexEngineWithAShieldIsRefusedIfItChangesTheShield()
    {
        var pack = CommittedPack();
        var build = Equip(pack.Pack, [.. ApexPieces(4), "primary_weapon_acr"]).WithActiveSkillSelection("skill_slot_1", "skill_striker_shield");
        var context = Context.Add("practical_hit_rate", "100").Add("enemy_target", "1").Add("enemy_difficulty", "1");
        Assert.Equal("shield-state-unsupported", Assert.Throws<SimulationException>(() => DummyWeaponSimulator.RunAgainstTargets(pack, build, context, new()
        {
            Duration = 3,
            Mode = SimulationMode.Random,
            Iterations = 1,
            DriveSimpleBuffs = true,
            IncludeIncoming = true,
            IncludeShield = true
        }, cancellationToken: TestContext.Current.CancellationToken)).Code);
    }

    [Fact]
    public void CommittedAfterburnCountsLandedPellets()
    {
        // A stack per pellet, each worth 13% of a pellet's damage (build 6615452; it was 100%), up to 30 (was 20): as many stacks as a
        // shot has pellets make 13% of one shot's weapon damage.
        var pack = CommittedPack();
        var rule = Assert.Single(pack.Pack.Simulation.Triggers, candidate => candidate.Talent == "gear_talent_afterburn");
        Assert.Equal(("hit_pellet", 30L), (rule.On, rule.Max));
        var stats = BuildResolver.Resolve(pack, Equip(pack.Pack, "primary_weapon_acs_12"), Context.Add("afterburn_stacks", "8")).Calculation.Stats;
        Assert.Equal(8, stats["equipped_weapon_pellets"]);
        Assert.Equal(0.13 * stats["equipped_weapon_damage"], stats["afterburn_detonation_damage"], 6);
        // The input takes the new cap: 30 stacks are 30 x 13% of a pellet's damage, beyond the old 20.
        var full = BuildResolver.Resolve(pack, Equip(pack.Pack, "primary_weapon_acs_12"), Context.Add("afterburn_stacks", "30")).Calculation.Stats;
        Assert.Equal(30 * 0.13 * full["equipped_weapon_damage"] / 8, full["afterburn_detonation_damage"], 6);
    }

    [Fact]
    public void CommittedVoraciousBuildsStacksOverAHundredHits()
    {
        // The Ratel's Voracious: a stack for every 100 hits, up to 4. A long fight against a tough target reaches them.
        var pack = CommittedPack();
        var build = Equip(pack.Pack, "primary_weapon_the_ratel");
        var context = Context.Add("practical_hit_rate", "100").Add("enemy_target", "5").Add("enemy_difficulty", "4");
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, context, new()
        {
            Duration = 60,
            Mode = SimulationMode.Random,
            Iterations = 1,
            DriveSimpleBuffs = true
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.ShotsFired >= 100);
        Assert.True(result.TimedTriggerUptime["voracious_stacks"].MeanStacks > 0);
    }

    [Fact]
    public void CommittedAfterburnDetonatesAHalfSecondAfterAReload()
    {
        // Loaded for Bear: stacks build on hits and detonate once, 0.5 s after the reload starts, for damage that grows with them.
        var pack = CommittedPack();
        var build = Equip(pack.Pack, "primary_weapon_acr", "gloves_loaded_for_bear");
        var context = Context.Add("practical_hit_rate", "100").Add("enemy_target", "5").Add("enemy_difficulty", "4");
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, context, new()
        {
            Duration = 30,
            Mode = SimulationMode.Random,
            Iterations = 1,
            DriveSimpleBuffs = true,
            CaptureFirstFightEvents = true
        }, cancellationToken: TestContext.Current.CancellationToken);
        var reloads = result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.ReloadStarted).Select(entry => entry.Time).ToArray();
        var ticks = result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.TalentTick).ToArray();
        Assert.NotEmpty(ticks);
        Assert.All(ticks, tick => Assert.Contains(reloads, reload => Math.Abs(tick.Time - reload - 0.5) < 1e-9));
        Assert.All(ticks, tick => Assert.True(tick.Damage > 0));
    }

    [Theory]
    [InlineData("primary_weapon_the_ratel")]
    [InlineData("secondary_weapon_the_ratel")]
    [InlineData("primary_weapon_malinois")]
    [InlineData("secondary_weapon_malinois")]
    [InlineData("primary_weapon_clockwork")]
    [InlineData("secondary_weapon_clockwork")]
    public void CommittedY8S33WeaponsFightInEitherSlot(string item)
    {
        var pack = CommittedPack();
        var slot = pack.Pack.Items.Single(candidate => candidate.ID == item).Slot;
        var build = Equip(pack.Pack, item);
        var context = Context.SetItem(GamePack.WeaponSlotSelector, slot).Add("practical_hit_rate", "100").Add("enemy_target", "5").Add("enemy_difficulty", "4");
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, context, new()
        {
            Duration = 15,
            Mode = SimulationMode.Random,
            Iterations = 2,
            DriveSimpleBuffs = true,
            Distance = 20
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.ShotsFired > 0);
        Assert.True(result.WeaponDps.Mean > 0);
    }

    [Theory]
    [InlineData("skill_leech_hive_charge_capacity_tiers", new double[] { 0, 2, 3, 5, 7, 9, 11, 15 })]
    [InlineData("skill_corrosive_trap_trap_count_tiers", new double[] { 0, 1, 2, 3, 4, 5, 6, 8 })]
    [InlineData("skill_stinger_hive_charge_capacity_tiers", new double[] { 0, 1, 2, 3, 4, 5, 6, 6 })]
    public void CommittedCountCurvesAreThePveTierUnlocks(string curve, double[] bonuses)
    {
        // R48: Leech Hive and Corrosive Trap had the tier files' PvP values. Stinger Hive's reported difference was the same misreading,
        // so it stays.
        Assert.Equal(bonuses, CommittedPack().Pack.Curves[curve].Select(point => point.Y));
    }

    [Theory]
    [InlineData("skill_leech_hive", "overcharge_charge_capacity", 20)]
    [InlineData("skill_corrosive_trap", "overcharge_trap_count", 16)]
    public void CommittedOverchargeCountsAreThePveValues(string skill, string output, double count)
    {
        var pack = CommittedPack();
        var build = BuildSnapshot.Create(pack.Pack).WithActiveSkillSelection("skill_slot_1", skill);
        var resolved = BuildResolver.Resolve(pack, build, Context.Add("overcharge_capacity_preview", "1"));
        Assert.Equal(count, resolved.ActiveSkillPreviews["skill_slot_1"].Outputs.Single(entry => entry.Id == output).Value);
    }

    [Theory]
    [InlineData("skill_explosive_sticky_bomb", 22120.75)]
    [InlineData("skill_burster_firefly", 22120.77)]
    [InlineData("skill_cluster_seeker_mine", 43799.10)]
    [InlineData("skill_burn_sticky_bomb", 27208.57)]
    [InlineData("skill_artillery_turret", 109497.73)]
    public void CommittedCycleDpsAtTierZero(string skill, double dps)
    {
        // R15's three impact skills are one hit per use (per mine, for Cluster) over the cooldown. Burn Sticky Bomb moved with its burn base (R20).
        // Artillery Turret is ShadeTech's hand figure (R66): three shells 2 s apart, 2 s to the turret's end and the 20 s cooldown, so 26 s.
        var pack = CommittedPack();
        var build = BuildSnapshot.Create(pack.Pack).WithActiveSkillSelection("skill_slot_1", skill);
        var output = BuildResolver.Resolve(pack, build, Context).ActiveSkillPreviews["skill_slot_1"].Outputs.Single(entry => entry.Id == "assumed_cycle_dps");
        Assert.Equal(dps, Math.Round(output.Value, 2));
    }

    [Theory]
    [InlineData("skill_explosive_sticky_bomb", 22120.75)]
    [InlineData("skill_burster_firefly", 22120.77)]
    [InlineData("skill_cluster_seeker_mine", 54748.88)]
    public void CommittedImpactSkillsFight(string skill, double dps)
    {
        // ShadeTech's figures over 120 s: a fixed fight catches Cluster's third use at 100 s.
        var pack = CommittedPack();
        var build = Equip(pack.Pack, "primary_weapon_acr").WithActiveSkillSelection("skill_slot_1", skill);
        var result = DummyWeaponSimulator.Run(pack, build, Context, new() { Duration = 120, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, IncludeSkills = true },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(dps, Math.Round(result.SkillDps!.Total.Mean, 2));
        Assert.Equal(0, result.SkillDps.ContinuousWindow.Mean);
    }

    [Theory]
    [InlineData(3, 10, 0.0403)]
    [InlineData(6, 10, 0.0314)]
    [InlineData(7, 10, 0.0314)]
    [InlineData(3, 14, 0.0403)]
    [InlineData(3, 16, 0.0706)]
    public void CommittedChatterCountsAtTheReloadsEnd(int enemies, double distance, double gap)
    {
        // ShadeTech's hand figures: 3 within 15 m, the cap of 5 for more, and none beyond. Each stack adds rate of fire to the next magazine.
        var pack = CommittedPack();
        var context = Context.Add("enemy_target", "1").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "3");
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, Equip(pack.Pack, "primary_weapon_the_chatterbox"), context,
            new() { Duration = 30, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, EnemiesAtOnce = enemies, Distance = distance, CaptureFirstFightEvents = true },
            cancellationToken: TestContext.Current.CancellationToken);
        var reloaded = result.FirstFightEvents.First(entry => entry.Kind == DummyEventKind.ReloadCompleted).Time;
        var shots = result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.Shot && entry.Time >= reloaded).Take(3).Select(entry => entry.Time).ToArray();
        Assert.Equal(gap, Math.Round(shots[2] - shots[1], 4));
    }

    [Fact]
    public void CommittedAdrenalineRushCountsWithin10M()
    {
        // Bonus armor from enemies within 10 m keeps the player up longer than at 11 m, where the talent reads none.
        var pack = CommittedPack();
        const string Rush = "gear_talent_adrenaline_rush";
        var item = pack.Pack.Items.First(item => item.TalentSlots.Any(slot => slot.Choices.Contains(Rush)));
        var build = Equip(pack.Pack, "primary_weapon_acr").WithEquipment(item.Slot, item.ID).WithTalentSelection(item.Slot, item.TalentSlots.First(slot => slot.Choices.Contains(Rush)).ID, Rush);
        var context = Context.Add("enemy_target", "1").Add("enemy_difficulty", "1");
        double Down(double distance, ImmutableDictionary<string, string>? at = null, BuildSnapshot? worn = null) => DummyWeaponSimulator.RunAgainstTargets(pack, worn ?? build, at ?? context,
            new() { Duration = 60, Mode = SimulationMode.Random, Iterations = 20, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, EnemiesAtOnce = 3, Distance = distance },
            cancellationToken: TestContext.Current.CancellationToken).Incoming!.TimeToDown!.Mean;
        var (near, far) = (Down(10), Down(11));
        Assert.True(near > far * 1.1, $"{near} against {far}");
        // Perfect counts the same way for a bigger bonus (R53): at least as long within 10 m, where hits land in whole numbers here, and the same beyond.
        const string Perfect = "gear_talent_perfect_adrenaline_rush";
        var perfectItem = pack.Pack.Items.First(item => item.TalentSlots.Any(slot => slot.Choices.Contains(Perfect)));
        var perfect = Equip(pack.Pack, "primary_weapon_acr").WithEquipment(perfectItem.Slot, perfectItem.ID)
            .WithTalentSelection(perfectItem.Slot, perfectItem.TalentSlots.First(slot => slot.Choices.Contains(Perfect)).ID, Perfect);
        Assert.True(Down(10, worn: perfect) >= near);
        Assert.Equal(far, Down(11, worn: perfect));
        // The count replaces what's assumed, so setting those inputs changes nothing here.
        var assumed = context.Add("adrenaline_rush_active", "true").Add("adrenaline_rush_stacks", "3");
        Assert.Equal(far, Down(11, assumed, perfect));
    }

    [Fact]
    public void CommittedBloodsuckerGainsBonusArmorFromItsKillsUnderFire()
    {
        // ADR 0020: each kill's stack raises bonus_armor_percent, and the player gains the rise. Before, the first kill's stack was refused
        // under enemy fire.
        var pack = CommittedPack();
        const string Talent = "gear_talent_bloodsucker";
        var item = pack.Pack.Items.First(item => item.TalentSlots.Any(slot => slot.Choices.Contains(Talent)));
        var build = Equip(pack.Pack, "primary_weapon_acr").WithEquipment(item.Slot, item.ID).WithTalentSelection(item.Slot, item.TalentSlots.First(slot => slot.Choices.Contains(Talent)).ID, Talent);
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, Context.Add("enemy_target", "1").Add("enemy_difficulty", "1"),
            new() { Duration = 60, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, CaptureFirstFightEvents = true },
            cancellationToken: TestContext.Current.CancellationToken);
        var kill = result.FirstFightEvents.First(entry => entry.Kind == DummyEventKind.Kill).Time;
        Assert.Contains(result.FirstFightEvents, entry => entry.Kind == DummyEventKind.BonusArmorGranted && entry.Time == kill && entry.Damage > 0);
    }

    [Fact]
    public void CommittedStickyBombModsApply()
    {
        // ShadeTech's fight: a +7.5% Damage roll adds inside the tier bracket, so at tier 0 the skill does 1.075 times its 22,120.75.
        var pack = CommittedPack();
        const string Skill = "skill_explosive_sticky_bomb";
        var build = Equip(pack.Pack, "primary_weapon_acr").WithActiveSkillSelection("skill_slot_1", Skill).WithSkillMod("skill_slot_1", Skill, "payload", new("payload_damage", 0.075));
        var result = DummyWeaponSimulator.Run(pack, build, Context, new() { Duration = 120, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, IncludeSkills = true },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(23779.81, Math.Round(result.SkillDps!.Total.Mean, 2));
        // A Cooldown roll joins the tier bracket: 60 / 1.05.
        var cooled = BuildSnapshot.Create(pack.Pack).WithActiveSkillSelection("skill_slot_1", Skill).WithSkillMod("skill_slot_1", Skill, "launcher", new("launcher_cooldown", 0.05));
        Assert.Equal(60 / 1.05, BuildResolver.Resolve(pack, cooled, Context).ActiveSkillPreviews["skill_slot_1"].Outputs.Single(entry => entry.Id == "cooldown_seconds").Value, 9);
        // No roll reaches EMP's Payload socket, so it has no such slot.
        var emp = pack.Pack.ActiveSkills.Single(skill => skill.ID == "skill_emp_sticky_bomb");
        Assert.DoesNotContain(emp.ModSlots, slot => slot.ID == "payload");
    }

    private static BuildSnapshot WithNode(GamePack pack, BuildSnapshot build, string specialization, string node)
    {
        var tree = pack.Specializations.Single(entry => entry.ID == specialization);
        BuildSnapshot Allocate(BuildSnapshot current, string id) =>
            tree.Nodes.Single(entry => entry.ID == id).Prerequisites.Aggregate(current, Allocate).WithSpecializationNode(id, true);
        return Allocate(build.WithSpecialization(specialization), node);
    }

    [Theory]
    [InlineData("gunner", "gunner_hardened_armor_kits_tier_1", "hardened_armor_kits_active")]
    [InlineData("demolitionist", "demolitionist_stimulant_patch_tier_1", "stimulant_patch_active")]
    [InlineData("sharpshooter", "sharpshooter_emergency_cleanse_tier_1", "emergency_cleanse_active")]
    public void CommittedSpecializationNodesFightOnAKit(string specialization, string node, string key)
    {
        var pack = CommittedPack();
        var gear = Equip(pack.Pack, "primary_weapon_acr", "mask_badger_tuff_mask", "chest_badger_tuff_chest", "gloves_badger_tuff_gloves", "holster_badger_tuff_holster", "knees_badger_tuff_kneepads");
        var context = Context.Add("enemy_target", "1").Add("enemy_difficulty", "1");
        DummyFightResult Fight(BuildSnapshot build) => DummyWeaponSimulator.RunAgainstTargets(pack, build, context,
            new() { Duration = 120, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, ArmorKits = 3, ArmorKitAtPercent = 50 },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(Fight(WithNode(pack.Pack, gear, specialization, node)).TimedTriggerUptime[key].Seconds > 0);
        // Without the node, the rule doesn't run.
        Assert.DoesNotContain(key, Fight(gear.WithSpecialization(specialization)).TimedTriggerUptime.Keys);
    }

    [Theory]
    [InlineData("demolitionist", "demolitionist_stimulant_patch_tier_1", "stimulant_patch_active", "weapon_handling")]
    [InlineData("sharpshooter", "sharpshooter_emergency_cleanse_tier_1", "emergency_cleanse_active", "burn_resistance")]
    public void CommittedNodeEffectsReadTheirRulesKey(string specialization, string node, string key, string stat)
    {
        var pack = CommittedPack();
        var build = WithNode(pack.Pack, BuildSnapshot.Create(pack.Pack), specialization, node);
        double Value(ImmutableDictionary<string, string> context) => BuildResolver.Resolve(pack, build, context).Calculation.Stats[stat];
        Assert.Equal(Value(Context) + 0.3, Value(Context.Add(key, "true")), 9);
    }

    [Fact]
    public void CommittedHardenedArmorKitsGrantBonusArmor()
    {
        var rule = CommittedPack().Pack.Simulation.Triggers.Single(entry => entry.Talent == "gunner_hardened_armor_kits_tier_1");
        // R85: the share is a stat, 0.3 scaled by the specialization kit bonus; its 10 s isn't.
        Assert.Equal(("armor_kit", "temporary_armor", 0d, "gunner_hardened_armor_kits_bonus_armor", 10d), (rule.On, rule.Grant, rule.GrantAmount, rule.GrantAmountStat, rule.GrantDuration));
    }

    // A build with the talent on the first item that offers it.
    private static BuildSnapshot WithTalent(GamePack pack, BuildSnapshot build, string talent)
    {
        var item = pack.Items.First(entry => entry.TalentSlots.Any(slot => slot.Choices.Contains(talent)));
        var slot = item.TalentSlots.First(entry => entry.Choices.Contains(talent));
        return build.WithEquipment(item.Slot, item.ID).WithTalentSelection(item.Slot, slot.ID, talent);
    }

    [Theory]
    [InlineData("gear_talent_efficient", 0.5)]
    [InlineData("gear_talent_perfect_efficient", 0.75)]
    public void CommittedEfficientKeepsKitsAfterTenSecondsAndDoublesTheKitBonus(string talent, double chance)
    {
        // R85: a kit used from 10 s in comes back at the talent's chance, and the specialization kit bonus is 1.
        var pack = CommittedPack();
        var rule = Assert.Single(pack.Pack.Simulation.Triggers, entry => entry.Talent == talent);
        Assert.Equal(("efficient_kept", "armor_kit", 10d, chance, "armor_kit_refund", 1d), (rule.Key, rule.On, rule.ActiveAfter, rule.Chance, rule.Grant, rule.GrantAmount));
        Assert.Equal("partial", pack.Pack.Talents.Single(entry => entry.ID == talent).Coverage!.Status);
        var gear = Equip(pack.Pack, "primary_weapon_acr", "mask_badger_tuff_mask", "chest_badger_tuff_chest", "gloves_badger_tuff_gloves", "holster_badger_tuff_holster", "knees_badger_tuff_kneepads");
        var gunner = WithNode(pack.Pack, gear, "gunner", "gunner_hardened_armor_kits_tier_1");
        var efficient = WithTalent(pack.Pack, gunner, talent);
        double Stat(BuildSnapshot build, string stat) => BuildResolver.Resolve(pack, build, Context).Calculation.Stats[stat];
        Assert.Equal((0d, 0.3), (Stat(gunner, "specialization_kit_bonus"), Stat(gunner, "gunner_hardened_armor_kits_bonus_armor")));
        Assert.Equal((1d, 0.6), (Stat(efficient, "specialization_kit_bonus"), Stat(efficient, "gunner_hardened_armor_kits_bonus_armor")));
        Assert.Equal((15d, 30d), (Stat(gunner, "demolitionist_stimulant_patch_duration"), Stat(efficient, "demolitionist_stimulant_patch_duration")));
        Assert.Equal((20d, 40d), (Stat(gunner, "sharpshooter_emergency_cleanse_duration"), Stat(efficient, "sharpshooter_emergency_cleanse_duration")));
        // ShadeTech's setup: 60% enemy hits, two kits used below half armor. Kits kept mean more used, and more bonus armor.
        var context = Context.Add("enemy_target", "1").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "60");
        DummyFightResult Fight(CompiledPackExpressions fought) => DummyWeaponSimulator.RunAgainstTargets(fought, efficient, context,
            new() { Duration = 180, Mode = SimulationMode.Random, Iterations = 20, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, ArmorKits = 2, ArmorKitAtPercent = 50 },
            cancellationToken: TestContext.Current.CancellationToken);
        var kept = Fight(pack);
        var spent = Fight(Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
        { Simulation = pack.Pack.Simulation with { Triggers = [.. pack.Pack.Simulation.Triggers.Where(entry => entry.Talent != talent)] } }).Compiled));
        Assert.True(kept.Incoming!.ArmorKitsUsed > spent.Incoming!.ArmorKitsUsed, $"{kept.Incoming.ArmorKitsUsed} vs {spent.Incoming.ArmorKitsUsed}");
    }

    [Theory]
    [InlineData("weapon_talent_outsider", "outsider_active")]
    [InlineData("weapon_talent_perfect_outsider", "outsider_active")]
    [InlineData("weapon_talent_preservation", "preservation_repair")]
    [InlineData("weapon_talent_perfect_preservation", "preservation_repair")]
    public void CommittedWeaponTalentsTakingAnySourcesKillHaveASkillKillRow(string talent, string key)
    {
        // R85: beside its kill row, a skill_kill row on the same key; Outsider's rows share one cooldown.
        var pack = CommittedPack();
        var rows = pack.Pack.Simulation.Triggers.Where(entry => entry.Talent == talent && entry.Key == key).ToArray();
        Assert.Contains(rows, entry => entry.On == "kill");
        Assert.Contains(rows, entry => entry.On == "skill_kill");
        if (key == "outsider_active") { Assert.All(rows, entry => Assert.Equal("outsider", entry.EveryGroup)); }
        Assert.Equal("partial", pack.Pack.Talents.Single(entry => entry.ID == talent).Coverage!.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ACountedStatusRulesAlsoAddsToItsOneCount(bool shared)
    {
        // Ignited's shape (R86, now the committed row): one kill count of 4 taking weapon and skill kills. Two of each arm the next hit's
        // burn; as two rules, each with its own count of 4, the same four kills burn nothing. The committed also is stripped first, so
        // each case builds its own.
        var pack = CommittedPack();
        var committed = pack.Pack.Simulation.Triggers.Single(entry => entry.Talent == "weapon_talent_ignited");
        var ignited = committed with { Also = [] };
        SimTriggerDefinition[] rules = shared ? [ignited with { Also = [new() { On = "skill_kill" }] }] : [ignited, ignited with { On = "skill_kill" }];
        var changed = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
        { Simulation = pack.Pack.Simulation with { Triggers = [.. pack.Pack.Simulation.Triggers.Where(entry => entry != committed), .. rules] } }).Compiled);
        var statuses = new StatusTriggerState(changed, rules);
        statuses.Hold(new TargetStatuses());
        statuses.Fire("kill", 0);
        statuses.Fire("skill_kill", 1);
        statuses.Fire("kill", 2);
        statuses.Fire("skill_kill", 3);
        statuses.Fire("hit", 4);
        Assert.Equal(shared, statuses.Active(4.5).Contains("burning"));
    }

    [Theory]
    [InlineData("weapon_talent_ignited", 4)]
    [InlineData("weapon_talent_perfect_ignited", 3)]
    public void CommittedIgnitedRowsCountSkillKills(string talent, long count)
    {
        // R86: both rows count weapon and skill kills toward one count, and stay partial.
        var pack = CommittedPack();
        var row = Assert.Single(pack.Pack.Simulation.Triggers, entry => entry.Talent == talent);
        var also = Assert.Single(row.Also);
        Assert.Equal(("kill", count, "skill_kill", 1L, 0L), (row.On, row.Count, also.On, also.Gain, also.Count));
        Assert.Equal("partial", pack.Pack.Talents.Single(entry => entry.ID == talent).Coverage!.Status);
    }

    [Fact]
    public void IgnitedsAlsoCountsASkillsKillsInAFight()
    {
        // The committed Ignited row, on an ACR with Cluster Seeker Mine: the mine's kills count too, so it burns longer than without the also.
        var pack = CommittedPack();
        var ignited = pack.Pack.Simulation.Triggers.Single(entry => entry.Talent == "weapon_talent_ignited");
        var changed = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
        { Simulation = pack.Pack.Simulation with { Triggers = [.. pack.Pack.Simulation.Triggers.Select(entry => entry == ignited ? entry with { Also = [] } : entry)] } }).Compiled);
        var build = SixTierCores(pack.Pack, "skill_cluster_seeker_mine").WithTalentSelection("primary_weapon", "talent", "weapon_talent_ignited");
        var context = Context.Add("enemy_target", "1").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "0");
        double Burning(CompiledPackExpressions fought) => DummyWeaponSimulator.RunAgainstTargets(fought, build, context,
            new() { Duration = 120, Mode = SimulationMode.Random, Iterations = 5, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, IncludeSkills = true },
            cancellationToken: TestContext.Current.CancellationToken).TargetStatusUptime["burning"].Seconds;
        var counted = Burning(pack);
        var weaponOnly = Burning(changed);
        Assert.True(counted > weaponOnly, $"{counted} vs {weaponOnly}");
    }

    [Fact]
    public void AGearTalentsCountCantNameASkillsKillTwice()
    {
        // A skill's kill is already a kill to gear talents, so on and also naming both would count it twice: refused.
        var pack = CommittedPack();
        var ignited = pack.Pack.Simulation.Triggers.Single(entry => entry.Talent == "weapon_talent_ignited");
        var gearTalent = "gear_talent_skilled";
        var rule = ignited with { Talent = gearTalent, Also = [new() { On = "skill_kill" }] };
        var changed = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
        { Simulation = pack.Pack.Simulation with { Triggers = [.. pack.Pack.Simulation.Triggers.Where(entry => entry.Talent != gearTalent), rule] } }).Compiled);
        var build = WithTalent(pack.Pack, SixTierCores(pack.Pack, "skill_cluster_seeker_mine"), gearTalent);
        Assert.Equal("trigger-unsupported", Assert.Throws<SimulationException>(() => DummyWeaponSimulator.RunAgainstTargets(changed, build, Context,
            new() { Duration = 10, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, IncludeSkills = true },
            cancellationToken: TestContext.Current.CancellationToken)).Code);
    }

    [Fact]
    public void CommittedPerfectPreservationRepairsOnSkillKills()
    {
        // R85, ShadeTech's figures: under full enemy fire, Cluster Seeker Mine's kills repair too (12.55M to 16.12M).
        var pack = CommittedPack();
        var talent = "weapon_talent_perfect_preservation";
        var build = WithTalent(pack.Pack, SixTierCores(pack.Pack, "skill_cluster_seeker_mine"), talent);
        var context = Context.Add("enemy_target", "1").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "100");
        DummyFightResult Fight(CompiledPackExpressions fought) => DummyWeaponSimulator.RunAgainstTargets(fought, build, context,
            new() { Duration = 180, Mode = SimulationMode.Random, Iterations = 20, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, IncludeSkills = true },
            cancellationToken: TestContext.Current.CancellationToken);
        var both = Fight(pack);
        var weaponOnly = Fight(Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
        { Simulation = pack.Pack.Simulation with { Triggers = [.. pack.Pack.Simulation.Triggers.Where(entry => !(entry.Talent == talent && entry.On == "skill_kill"))] } }).Compiled));
        Assert.True(both.Incoming!.ArmorRepaired > weaponOnly.Incoming!.ArmorRepaired, $"{both.Incoming.ArmorRepaired} vs {weaponOnly.Incoming.ArmorRepaired}");
        // Without skills the skill row never fires, and the fight says so.
        var plain = DummyWeaponSimulator.RunAgainstTargets(pack, build, context,
            new() { Duration = 30, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(plain.Limitations, note => note.StartsWith("This fight has no skills", StringComparison.Ordinal) && note.Contains("Perfect Preservation", StringComparison.Ordinal));
    }

    [Theory]
    // Incinerator Turret's rate became its burn's statusRate (R68), and its live read went with it.
    [InlineData("skill_incinerator_turret", "")]
    [InlineData("skill_stinger_hive", "live")]
    // The owner chose live reads for the Assault Turret and Oxidizer after R151, so the Artificer's bonus reaches them out (R152).
    [InlineData("skill_assault_turret", "live")]
    [InlineData("skill_oxidizer_chem_launcher", "live")]
    [InlineData("skill_burn_sticky_bomb", "")]
    public void CommittedRatesFollowOrHoldAsR57Found(string skill, string rateRead) =>
        Assert.Equal(rateRead, CommittedPack().Pack.Simulation.Skills.Single(rule => rule.Skill == skill).RateRead);

    [Fact]
    public void CommittedConeSizeReachesBansheesRangeOnce()
    {
        // ShadeTech's own check caught this roll reaching the range twice.
        var pack = CommittedPack();
        const string Skill = "skill_banshee_pulse";
        var build = BuildSnapshot.Create(pack.Pack).WithActiveSkillSelection("skill_slot_1", Skill).WithSkillMod("skill_slot_1", Skill, "coil", new("coil_cone", 0.05));
        var outputs = BuildResolver.Resolve(pack, build, Context).ActiveSkillPreviews["skill_slot_1"].Outputs;
        Assert.Equal(31.5, outputs.Single(entry => entry.Id == "radius_meters").Value, 9);
        Assert.Equal(21, outputs.Single(entry => entry.Id == "cone_angle_degrees").Value, 9);
    }

    [Theory]
    [InlineData("weapon_talent_spike")]
    [InlineData("weapon_talent_in_sync")]
    [InlineData("weapon_talent_future_perfect")]
    public void CommittedSkillTalentsFightWithADamagingSkill(string talent)
    {
        // These were refused: their rules change what a skill reads.
        var pack = CommittedPack();
        var build = Equip(pack.Pack, "primary_weapon_acr").WithTalentSelection("primary_weapon", "talent", talent).WithActiveSkillSelection("skill_slot_1", "skill_explosive_sticky_bomb");
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, Context.Add("enemy_target", "1").Add("enemy_difficulty", "1"),
            new() { Duration = 60, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, IncludeSkills = true },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.SkillDps!.Total.Mean > 0);
    }

    [Theory]
    [InlineData("skill_cluster_seeker_mine", "payload", "payload_damage", 0.05, "damage")]
    [InlineData("skill_oxidizer_chem_launcher", "agitator", "agitator_damage", 0.05, "cloud_damage")]
    [InlineData("skill_artillery_turret", "firing_mechanism", "firing_damage", 0.05, "damage")]
    [InlineData("skill_stinger_hive", "drones", "drones_damage", 0.05, "damage")]
    [InlineData("skill_burster_firefly", "payload", "payload_damage", 0.075, "damage")]
    [InlineData("skill_striker_drone", "feed", "feed_damage", 0.05, "damage")]
    [InlineData("skill_achilles_pulse", "housing", "housing_zone_health", 0.1, "zone_health_reference")]
    [InlineData("skill_scanner_pulse", "housing", "housing_effect_duration", 0.08, "effect_duration_seconds")]
    [InlineData("skill_fortified_smart_cover", "launcher", "launcher_radius", 0.05, "radius_meters")]
    [InlineData("skill_decoy", "projector", "decoy_projector_internals", 0.05, "duration_seconds")]
    public void CommittedSkillFamilyRollsAddInsideTheTierBracket(string skill, string slot, string choice, double roll, string output)
    {
        var pack = CommittedPack();
        double Value(BuildSnapshot build) => BuildResolver.Resolve(pack, build, Context).ActiveSkillPreviews["skill_slot_1"].Outputs.Single(entry => entry.Id == output).Value;
        var plain = BuildSnapshot.Create(pack.Pack).WithActiveSkillSelection("skill_slot_1", skill);
        Assert.Equal(Value(plain) * (1 + roll), Value(plain.WithSkillMod("skill_slot_1", skill, slot, new(choice, roll))), 6);
    }

    [Fact]
    public void CommittedPlagueStacksItsDebuffInAFight()
    {
        // R75: each hit starts a 10 s debuff of a body hit's damage, ticking each second, up to 50 on a target.
        var pack = CommittedPack();
        var row = Assert.Single(pack.Pack.Simulation.TalentDamage, rule => rule.Talent == "weapon_talent_plague_of_the_outcasts");
        Assert.Equal(("hit", 10.0, 1.0, 50), (row.On, row.Duration, row.Period, row.MaxStacks));
        Assert.Equal("partial", pack.Pack.Talents.Single(talent => talent.ID == row.Talent).Coverage!.Status);
        var build = Equip(pack.Pack, "primary_weapon_pestilence");
        var context = Context.Add("enemy_target", "5").Add("enemy_difficulty", "4");
        DummyFightResult Fight(CompiledPackExpressions fought) => DummyWeaponSimulator.RunAgainstTargets(fought, build, context,
            new() { Duration = 60, Mode = SimulationMode.Random, Iterations = 3, Seed = 1, DriveSimpleBuffs = true, CaptureFirstFightEvents = true },
            cancellationToken: TestContext.Current.CancellationToken);
        var plague = Fight(pack);
        Assert.True(plague.TalentDps!.Mean > 0);
        // Stacks: more than one debuff ticks in some second.
        Assert.Contains(plague.FirstFightEvents.Where(e => e.Kind == DummyEventKind.TalentTick).GroupBy(e => Math.Floor(e.Time)), second => second.Count() > 1);
        var without = Fight(Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
        { Simulation = pack.Pack.Simulation with { TalentDamage = [.. pack.Pack.Simulation.TalentDamage.Where(rule => rule != row)] } }).Compiled));
        Assert.Null(without.TalentDps);
        Assert.True(plague.TotalDps!.Mean > without.TotalDps!.Mean);
    }

    [Fact]
    public void CommittedReachingRowsAreTheR88ToR91AndR100Values()
    {
        // ShadeTech's R88 to R91 candidate: Regicide's 400% blast within 5 m of a headshot kill, with no bleed; the two Creeping Deaths'
        // spreads; Plague's pass-on within 25 m; Indirect Transmission's statuses to all in reach for a share of their duration, both read
        // from the four-piece stats: 10 m and half, or 15 m and three quarters with Proliferation (R100).
        var pack = CommittedPack().Pack;
        var blast = Assert.Single(pack.Simulation.Blasts, rule => rule.Talent == "weapon_talent_regicide");
        Assert.Equal(("weapon_talent_regicide", "headshot_kill", "regicide_blast_damage", 5.0, "", ""), (blast.Talent, blast.On, blast.Amount, blast.Within, blast.Status, blast.Mark));
        Assert.Equal("4 * equipped_weapon_damage / equipped_weapon_pellets", pack.Stats.Single(stat => stat.ID == blast.Amount).Formula);
        Assert.Equal([("gear_talent_creeping_death", 8.0, 15.0), ("gear_talent_perfect_creeping_death", 10.0, 10.0)],
            pack.Simulation.Spreads.Select(spread => (spread.Talent, spread.Within, spread.Every)));
        Assert.Equal([("weapon_talent_plague_of_the_outcasts", 25.0, "", "", 0.0, "", ""),
            ("gear_talent_indirect_transmission", 0.0, "indirect_transmission_radius", "any", 0.0, "indirect_transmission_refresh", "all")],
            pack.Simulation.PassOns.Select(passOn => (passOn.Talent, passOn.Within, passOn.WithinStat, passOn.Status, passOn.Share, passOn.ShareStat, passOn.To)));
        foreach (var talent in new[] { "weapon_talent_regicide", "gear_talent_creeping_death", "gear_talent_perfect_creeping_death", "weapon_talent_plague_of_the_outcasts",
            "gear_talent_indirect_transmission" })
        { Assert.Equal("partial", pack.Talents.Single(entry => entry.ID == talent).Coverage!.Status); }
        Assert.Equal("complete", pack.Talents.Single(entry => entry.ID == "gear_talent_proliferation").Coverage!.Status);
        // R105: all three take a skill's own statuses (ADR 0015).
        foreach (var talent in new[] { "gear_talent_creeping_death", "gear_talent_perfect_creeping_death" })
        { Assert.True(pack.Simulation.Spreads.Single(spread => spread.Talent == talent).SkillStatuses, talent); }
        Assert.True(pack.Simulation.PassOns.Single(passOn => passOn.Talent == "gear_talent_indirect_transmission").SkillStatuses);
        Assert.False(pack.Simulation.PassOns.Single(passOn => passOn.Talent == "weapon_talent_plague_of_the_outcasts").SkillStatuses);
        // R106: their coverage says what fights now do. Copies carry the skill's damage and a pass-on isn't capped, so neither is an
        // omission; what stays open is named.
        foreach (var talent in new[] { "gear_talent_creeping_death", "gear_talent_perfect_creeping_death", "gear_talent_indirect_transmission" })
        {
            var omits = string.Join(" ", pack.Talents.Single(entry => entry.ID == talent).Coverage!.Omits);
            Assert.DoesNotContain("player's statusDamage rate (R105)", omits, StringComparison.Ordinal);
            Assert.DoesNotContain("fights cap it", omits, StringComparison.Ordinal);
            Assert.Contains("A copy's armor bonus", omits, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CommittedAreaRowsAreTheR107AndR108Values()
    {
        // R107: six skills reach an area; the Airburst Seeker's cone takes none. R108: four fall off.
        var simulation = CommittedPack().Pack.Simulation;
        string Row(string skill)
        {
            var rule = simulation.Skills.Single(entry => entry.Skill == skill);
            return $"{rule.Area}/{rule.AreaFull}/{rule.AreaEdge}";
        }
        Assert.Equal("radius_meters/area_full_meters/0.1", Row("skill_explosive_sticky_bomb"));
        Assert.Equal("radius_meters/area_full_meters/0.1", Row("skill_burn_sticky_bomb"));
        Assert.Equal("radius_meters/area_full_meters/0.5", Row("skill_explosive_seeker_mine"));
        Assert.Equal("radius_meters/area_full_meters/0.5", Row("skill_cluster_seeker_mine"));
        Assert.Equal("radius_meters//0", Row("skill_firestarter_chem_launcher"));
        Assert.Equal("radius_meters//0", Row("skill_artillery_turret"));
        Assert.Equal("//0", Row("skill_airburst_seeker_mine"));
        // R109: both Creeping Deaths say a refresh sets off no spread in fights, and cite R107's review.
        foreach (var id in new[] { "gear_talent_creeping_death", "gear_talent_perfect_creeping_death" })
        {
            var coverage = CommittedPack().Pack.Talents.Single(talent => talent.ID == id).Coverage!;
            Assert.Contains(coverage.Omits, omit => omit.Contains("refresh", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("R107_SOURCE_REVIEW.md", coverage.Evidence, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CommittedCoverShooterCountsShotsInCoverOnly()
    {
        // R112: a stack per bullet fired in cover, up to 100, each held 15 s, cleared by a completed reload or a swap. In a cover
        // cycle Big Alejandro gains over the rule left out; in the open the key is held at 0 (#243), so nothing moves.
        var pack = CommittedPack();
        var rule = Assert.Single(pack.Pack.Simulation.Triggers, entry => entry.Talent == "weapon_talent_cover_shooter");
        Assert.Equal(("cover_shooter_stacks", "shot", "stacks", 100.0, 15.0, "reload_end", "swap", "true"),
            (rule.Key, rule.On, rule.Kind, rule.Max, rule.Duration, rule.ResetOn, rule.AlsoResetOn, rule.RequiresContext!["in_cover"]));
        var build = Equip(pack.Pack, "primary_weapon_big_alejandro");
        var without = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
        { Simulation = pack.Pack.Simulation with { Triggers = [.. pack.Pack.Simulation.Triggers.Where(entry => entry != rule)] } }).Compiled);
        double Dps(CompiledPackExpressions fought, bool cover) => DummyWeaponSimulator.RunAgainstTargets(fought, build,
            Context.SetItem(GamePack.WeaponSlotSelector, "primary_weapon").Add("practical_hit_rate", "100").Add("practical_headshot_share", "30").Add("enemy_difficulty", "4"),
            new()
            {
                Duration = 60,
                Mode = SimulationMode.Random,
                Iterations = 2,
                Seed = 1,
                DriveSimpleBuffs = true,
                PeekSeconds = cover ? 3 : null,
                CoverSeconds = cover ? 1 : null,
                PeekHitShare = cover ? 1 : null
            },
            cancellationToken: TestContext.Current.CancellationToken).TotalDps!.Mean;
        Assert.True(Dps(pack, true) > Dps(without, true));
        Assert.Equal(Dps(without, false), Dps(pack, false));
    }

    [Fact]
    public void CommittedVersatileGainsOnlyInItsWeaponsBand()
    {
        // Adopted with r116.candidate.2 (R117): Versatile on the 5.11 backpack, an SMG and an ACR SS rifle swapping when empty. The swap
        // between their types drives the bonus, but each class has its band: at 20 m neither the SMG (close) nor the rifle (far) is in
        // it, so the talent adds nothing; at 10 m the SMG's bonus holds.
        var pack = CommittedPack();
        var bare = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_converted_smg_9")
            .WithEquipment("secondary_weapon", "secondary_weapon_acr_ss").WithEquipment("backpack", "backpack_n_5_11_tactical_backpack");
        var versatile = bare.WithTalentSelection("backpack", "talent", "gear_talent_versatile");
        double Dps(BuildSnapshot build, double meters) => DummyWeaponSimulator.RunAgainstTargets(pack, build,
            Context.SetItem(GamePack.WeaponSlotSelector, "primary_weapon").Add("practical_hit_rate", "100").Add("enemy_difficulty", "4"),
            new() { Duration = 60, Mode = SimulationMode.Random, Iterations = 2, Seed = 1, DriveSimpleBuffs = true, Rotation = new("secondary_weapon"), Distance = meters },
            cancellationToken: TestContext.Current.CancellationToken).WeaponDps.Mean;
        Assert.Equal(Dps(bare, 20), Dps(versatile, 20), 6);
        Assert.True(Dps(versatile, 10) > Dps(bare, 10));
        // Beyond 25 m the rifle's band holds; without a distance the band reads "any" and both keep the bonus.
        Assert.True(Dps(versatile, 30) > Dps(bare, 30));
        double Unplaced(BuildSnapshot build) => DummyWeaponSimulator.RunAgainstTargets(pack, build,
            Context.SetItem(GamePack.WeaponSlotSelector, "primary_weapon").Add("practical_hit_rate", "100").Add("enemy_difficulty", "4"),
            new() { Duration = 60, Mode = SimulationMode.Random, Iterations = 2, Seed = 1, DriveSimpleBuffs = true, Rotation = new("secondary_weapon") },
            cancellationToken: TestContext.Current.CancellationToken).WeaponDps.Mean;
        Assert.True(Unplaced(versatile) > Unplaced(bare));
    }

    [Fact]
    public void CommittedChemicalAgentsTwoPartsGainIntoOneCount()
    {
        // Adopted with r118.candidate.1 (R118, ADR 0018 point 7): +1 a second while the target has a status, +2 while one is on the player,
        // into one count. The target's part alone, one; both, three; the player's part alone, two.
        var rules = CommittedPack().Pack.Simulation.Triggers.Where(rule => rule.Talent == "gear_talent_chemical_agent" && rule.Key == "catalysis_stacks").ToArray();
        Assert.Equal(2, rules.Length);
        var enemy = Assert.Single(rules, rule => rule.WhileStatus == "any");
        var player = Assert.Single(rules, rule => rule.WhilePlayerStatus == "any");
        Assert.Equal(2, player.Gain);
        Assert.True(enemy.SharesStacksWith(player));
        var onPlayer = false;
        var state = new TimedBuffState(rules) { PlayerStatusOn = (_, _) => onPlayer };
        var burning = new HashSet<string> { "burning" };
        string Count(double time) => state.Context(ImmutableDictionary<string, string>.Empty, time)["catalysis_stacks"];
        state.Fire("second", 1, burning);
        Assert.Equal("1", Count(1));
        onPlayer = true;
        state.Fire("second", 2, burning);
        Assert.Equal("4", Count(2));
        state.Fire("second", 3, new HashSet<string>());
        Assert.Equal("6", Count(3));
    }

    [Fact]
    public void CommittedReactantFiresOnAKillAtFullCatalysisOfAnEnemyWithAStatus()
    {
        // Adopted with r120.candidate.2 (R120, R121): a kill at 12 stacks of an enemy with a status turns Reactant on for 5 s, and a later
        // such kill restarts it. No grant: its bonus armor is the talent's modifier, following the fight's state (ADR 0020).
        var rules = CommittedPack().Pack.Simulation.Triggers.Where(rule => rule.Talent == "gear_talent_chemical_agent").ToArray();
        var reactant = Assert.Single(rules, rule => rule.Key == "reactant_active");
        Assert.Equal(("kill", "buff", 5.0, ""), (reactant.On, reactant.Kind, reactant.Duration, reactant.Grant));
        var burning = new HashSet<string> { "burning" };
        var none = new HashSet<string>();
        TimedBuffState Filled(int seconds)
        {
            var state = new TimedBuffState(rules);
            for (var second = 1; second <= seconds; second++) { state.Fire("second", second, burning); }
            return state;
        }
        bool On(TimedBuffState state, double time) => state.Context(ImmutableDictionary<string, string>.Empty, time).GetValueOrDefault("reactant_active") == "true";
        var below = Filled(11);
        below.Fire("kill", 11.5, burning);
        Assert.False(On(below, 11.5));
        var bare = Filled(12);
        bare.Fire("kill", 12.5, none);
        Assert.False(On(bare, 12.5));
        var full = Filled(12);
        full.Fire("kill", 12.5, burning);
        Assert.True(On(full, 17.4));
        Assert.False(On(full, 17.6));
        full.Fire("kill", 17.0, burning);
        Assert.True(On(full, 21.9));
    }

    [Fact]
    public void CommittedChemicalAgentLeavesOutEnsnared()
    {
        // Adopted with r123.candidate.1 (R122, R123): the source's check leaves out ensnare, so an enemy only ensnared gains no Catalysis,
        // and killing one at 12 stacks doesn't turn Reactant on.
        var rules = CommittedPack().Pack.Simulation.Triggers.Where(rule => rule.Talent == "gear_talent_chemical_agent").ToArray();
        Assert.Equal(3, rules.Length);
        Assert.All(rules, rule => Assert.Equal(["ensnared"], rule.AnyStatusExcept));
        // The player part: a player only ensnared gains nothing either, and bleeding gains 2.
        var onPlayer = new HashSet<string> { "ensnared" };
        var state = new TimedBuffState(rules) { PlayerStatusOn = (status, _) => status == "any" ? onPlayer.Count > 0 : onPlayer.Contains(status) };
        var ensnared = new HashSet<string> { "ensnared" };
        state.Fire("second", 1, ensnared);
        Assert.Equal("0", state.Context(ImmutableDictionary<string, string>.Empty, 1).GetValueOrDefault("catalysis_stacks", "0"));
        onPlayer.Add("bleeding");
        state.Fire("second", 1.5, ensnared);
        Assert.Equal("2", state.Context(ImmutableDictionary<string, string>.Empty, 1.5)["catalysis_stacks"]);
        onPlayer.Clear();
        for (var second = 2; second <= 13; second++) { state.Fire("second", second, new HashSet<string> { "burning" }); }
        state.Fire("kill", 13.5, ensnared);
        Assert.NotEqual("true", state.Context(ImmutableDictionary<string, string>.Empty, 13.5).GetValueOrDefault("reactant_active"));
        state.Fire("kill", 13.6, new HashSet<string> { "ensnared", "shocked" });
        Assert.Equal("true", state.Context(ImmutableDictionary<string, string>.Empty, 13.6)["reactant_active"]);
    }

    [Fact]
    public void CommittedReactantDrivesAFightThatFillsCatalysis()
    {
        // ShadeTech's R121 setup without enemy fire: Iron Lung's Ardent burns once its heat is full, filling Catalysis, and kills of the
        // burning target turn Reactant on for part of the fight. Its setting is ignored there: driven fights read the kills.
        var pack = CommittedPack();
        var build = Equip(pack.Pack, "primary_weapon_iron_lung", "mask_catalyst");
        var context = Context.Add("enemy_target", "5").Add("enemy_difficulty", "4");
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, context,
            new() { Duration = 120, Mode = SimulationMode.Random, Iterations = 2, Seed = 1, DriveSimpleBuffs = true },
            cancellationToken: TestContext.Current.CancellationToken);
        var uptime = result.TimedTriggerUptime["reactant_active"].Seconds;
        Assert.InRange(uptime, 1, 119);
    }

    [Fact]
    public void CommittedVersatilesBandHoldsWithoutARotation()
    {
        // The ledger's exception (r116.candidate.2): without a rotation Versatile reads the user's setting, but its band follows any
        // fight with a set distance. With the setting on, a rifle gains beyond 25 m and not at 10 m, where it gained before.
        var pack = CommittedPack();
        var bare = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr_ss")
            .WithEquipment("backpack", "backpack_n_5_11_tactical_backpack");
        var versatile = bare.WithTalentSelection("backpack", "talent", "gear_talent_versatile");
        double Dps(BuildSnapshot build, double meters) => DummyWeaponSimulator.RunAgainstTargets(pack, build,
            Context.SetItem(GamePack.WeaponSlotSelector, "primary_weapon").Add("practical_hit_rate", "100").Add("enemy_difficulty", "4").Add("versatile_active", "true"),
            new() { Duration = 30, Mode = SimulationMode.Random, Iterations = 2, Seed = 1, DriveSimpleBuffs = true, Distance = meters },
            cancellationToken: TestContext.Current.CancellationToken).WeaponDps.Mean;
        Assert.Equal(Dps(bare, 10), Dps(versatile, 10), 6);
        Assert.True(Dps(versatile, 30) > Dps(bare, 30));
    }

    [Fact]
    public void CommittedBackAndForthOnBothWeaponsGivesOnlyTheSwapToGroup()
    {
        // Adopted with r116.candidate.2: both groups shared, so with the talent on both ACRs each draw's swap-to group replaces the
        // swap-from group, as in the source, and the fight runs where it was refused.
        var pack = CommittedPack();
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr").WithEquipment("secondary_weapon", "secondary_weapon_acr")
            .WithTalentSelection("primary_weapon", "talent", "weapon_talent_back_and_forth").WithTalentSelection("secondary_weapon", "talent", "weapon_talent_back_and_forth");
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build,
            Context.SetItem(GamePack.WeaponSlotSelector, "primary_weapon").Add("practical_hit_rate", "100").Add("enemy_difficulty", "4"),
            new() { Duration = 60, Mode = SimulationMode.Random, Iterations = 2, Seed = 1, DriveSimpleBuffs = true, Rotation = new("secondary_weapon") },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.TimedTriggerUptime["back_and_forth_swap_to_active"].Seconds > 0);
        Assert.Equal(0, result.TimedTriggerUptime["back_and_forth_swap_from_active"].Seconds);
    }

    [Fact]
    public void CommittedSwapTalentRowsTakeTheirSourceShapes()
    {
        // The adopted rows (R116, R117): Unwavering's full lockout, Perturb's and Faster Than Reloading's primes, and the latter's refill
        // of both weapons, ended by the pistol's reload completing.
        var triggers = CommittedPack().Pack.Simulation.Triggers;
        Assert.All(triggers.Where(rule => rule.Key == "unwavering_active"), rule =>
            Assert.Equal((true, true, true, "swap", 5.0), (rule.LockoutAlways, rule.GainAtLockoutEnd, rule.ShareCopies, rule.CancelOn, rule.CancelFor)));
        Assert.Equal(10, Assert.Single(triggers, rule => rule.Key == "perturb_primed").PrimeHolstered);
        Assert.Equal(5, Assert.Single(triggers, rule => rule.Key == "faster_than_reloading_primed").PrimeHolstered);
        var faster = Assert.Single(triggers, rule => rule.Key == "faster_than_reloading_active");
        Assert.Equal((true, true, "reload_end", "faster_than_reloading_primed"), (faster.Refill, faster.RefillBoth, faster.ConsumeOn, faster.WhileKey));
    }

    [Fact]
    public void CoverShootersSourceTimerRunsInACommittedFight()
    {
        // The owner's call (2026-11-14), adopted with R115: Cover Shooter's row has the source's single timer and kill refresh. With the
        // timer cut to 2 s so it ends before a reload clears it, the single timer holds fewer stacks than each stack's own 2 s.
        var pack = CommittedPack();
        var rule = Assert.Single(pack.Pack.Simulation.Triggers, entry => entry.Talent == "weapon_talent_cover_shooter");
        double MeanStacks(SimTriggerDefinition changed)
        {
            var fought = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
            {
                Simulation = pack.Pack.Simulation with { Triggers = [.. pack.Pack.Simulation.Triggers.Select(entry => entry == rule ? changed : entry)] }
            }).Compiled);
            var result = DummyWeaponSimulator.RunAgainstTargets(fought, Equip(pack.Pack, "primary_weapon_big_alejandro"),
                Context.SetItem(GamePack.WeaponSlotSelector, "primary_weapon").Add("practical_hit_rate", "100").Add("enemy_difficulty", "4"),
                new() { Duration = 60, Mode = SimulationMode.Random, Iterations = 2, Seed = 1, DriveSimpleBuffs = true, PeekSeconds = 3, CoverSeconds = 1, PeekHitShare = 1 },
                cancellationToken: TestContext.Current.CancellationToken);
            return result.TimedTriggerUptime["cover_shooter_stacks"].MeanStacks!.Value;
        }
        Assert.Equal((true, "kill"), (rule.FromFirstGain, rule.RefreshOn));
        var sourced = MeanStacks(rule with { Duration = 2 });
        Assert.True(sourced > 0);
        Assert.True(sourced < MeanStacks(rule with { Duration = 2, FromFirstGain = false, RefreshOn = "" }), $"{sourced}");
    }

    [Fact]
    public void CommittedNailGunBleedsThePlayer()
    {
        // R115: target 5's hits put a 2 s bleed on the player, ticking 35 x the status curve every 0.5 s from the hit, shortened by
        // hazard protection and bleed resistance down to 0.5 s.
        var pack = CommittedPack().Pack;
        var row = Assert.Single(pack.Simulation.EnemyStatuses);
        Assert.Equal((5, "bleeding", 2.0, "npc_bleed_tick_damage", 0.5, true, 0.5), (row.Target, row.Status, row.Duration, row.Tick, row.Period, row.TickOnApply, row.MinDuration));
        Assert.Equal(["hazard_protection", "bleed_resistance"], row.Resistances);
        var build = Equip(pack, "primary_weapon_acr");
        ImmutableDictionary<string, string> Against(string target) =>
            Context.SetItem(GamePack.WeaponSlotSelector, "primary_weapon").Add("enemy_target", target).Add("enemy_difficulty", "1").Add("enemy_hit_rate", "20");
        DummyFightResult Fight(string target) => DummyWeaponSimulator.RunAgainstTargets(CommittedPack(), build, Against(target),
            new() { Duration = 20, Mode = SimulationMode.Random, Iterations = 2, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, CaptureFirstFightEvents = true },
            cancellationToken: TestContext.Current.CancellationToken);
        var nails = Fight("5");
        Assert.True(nails.Incoming!.StatusDamageTaken > 0);
        // Each tick deals the resolved stat while the pools last, so the first does.
        var tick = BuildResolver.Resolve(CommittedPack(), build, Against("5")).Calculation.Stats["npc_bleed_tick_damage"];
        Assert.InRange(tick, 20000, 30000);
        Assert.Equal(tick, nails.FirstFightEvents.First(e => e.Kind == DummyEventKind.PlayerStatusTick).Damage, 6);
        Assert.True(nails.Incoming.PlayerStatusUptime!["bleeding"].Seconds > 0);
        Assert.Equal(0, Fight("3").Incoming!.StatusDamageTaken);
    }

    [Fact]
    public void CommittedEnemyFireDefaultIsProfileThree()
    {
        // R113: fights with enemy fire start from profile 3 (normal, 1,200 health, no armor), the ordinary default staying 6.
        var pack = CommittedPack().Pack;
        Assert.Equal(("enemy_target", 3.0), Assert.Single(pack.Simulation.IncomingDefaults) is var entry ? (entry.Input, entry.Value) : default);
        Assert.Equal(6, pack.NumericContextInputs.Single(input => input.ID == "enemy_target").Default);
    }

    [Fact]
    public void CommittedControlRowsAreTheR110Values()
    {
        // R110 with ADR 0017: all four control skills put their status on; the Shock Trap's and Riot Foam's spheres take an area, the
        // Banshee Pulse's and Blinder Firefly's pies don't.
        var simulation = CommittedPack().Pack.Simulation;
        string Row(string skill)
        {
            var rule = simulation.Skills.Single(entry => entry.Skill == skill);
            return $"{rule.Status}/{rule.ControlStatus}/{rule.Area}";
        }
        Assert.Equal("shocked/True/radius_meters", Row("skill_shock_trap"));
        Assert.Equal("ensnared/True/radius_meters", Row("skill_riot_foam_chem_launcher"));
        Assert.Equal("disoriented/True/", Row("skill_banshee_pulse"));
        Assert.Equal("blinded/True/", Row("skill_blinder_firefly"));
    }

    [Fact]
    public void CommittedRiotFoamFeedsWicked()
    {
        // R110's figures: with an ACR and Wicked, the Riot Foam's ensnare is a status Wicked fires on, so the build gains; without the
        // field it doesn't. Four enemies at once at 3 m with no enemy hits, as ShadeTech's setup.
        var pack = CommittedPack();
        var item = pack.Pack.Items.First(entry => entry.TalentSlots.Any(slot => slot.Choices.Contains("gear_talent_wicked")));
        var build = Equip(pack.Pack, ["primary_weapon_acr"]).WithEquipment(item.Slot, item.ID)
            .WithTalentSelection(item.Slot, item.TalentSlots.First(slot => slot.Choices.Contains("gear_talent_wicked")).ID, "gear_talent_wicked")
            .WithActiveSkillSelection("skill_slot_1", "skill_riot_foam_chem_launcher");
        DummyFightResult Fight(CompiledPackExpressions fightPack) => DummyWeaponSimulator.RunAgainstTargets(fightPack, build,
            Context.SetItem(GamePack.WeaponSlotSelector, "primary_weapon").Add("practical_hit_rate", "100").Add("practical_headshot_share", "30").Add("enemy_target", "1").Add("enemy_difficulty", "4").Add("enemy_hit_rate", "0"),
            new() { Duration = 60, Mode = SimulationMode.Random, Iterations = 4, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, IncludeSkills = true, EnemiesAtOnce = 4, EnemySpacing = 3 },
            cancellationToken: TestContext.Current.CancellationToken);
        var foamed = Fight(pack);
        var plain = Fight(Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
        {
            Simulation = pack.Pack.Simulation with { Skills = [.. pack.Pack.Simulation.Skills.Select(rule => rule with { ControlStatus = false })] }
        }).Compiled));
        Assert.True(foamed.TargetStatusUptime["ensnared"].Seconds > 0);
        Assert.False(plain.TargetStatusUptime.TryGetValue("ensnared", out var none) && none.Seconds > 0);
        Assert.True(foamed.TotalDps!.Mean > plain.TotalDps!.Mean, $"{foamed.TotalDps.Mean} against {plain.TotalDps.Mean}");
    }

    [Fact]
    public void CommittedSeekerMineReachesItsNeighbourWithFalloff()
    {
        // R107 and R108: an Explosive Seeker Mine's 4 m blast reaches an enemy 3 m away at about two thirds of the impact (full within
        // 1 m, half at 4 m), and nothing reaches it without a spacing.
        var pack = CommittedPack();
        var build = Equip(pack.Pack, ["primary_weapon_acr"]).WithActiveSkillSelection("skill_slot_1", "skill_explosive_seeker_mine");
        DummyFightResult Fight(double? spacing) => DummyWeaponSimulator.RunAgainstTargets(pack, build,
            Context.SetItem(GamePack.WeaponSlotSelector, "primary_weapon").Add("practical_hit_rate", "100").Add("practical_headshot_share", "30").Add("enemy_target", "1").Add("enemy_difficulty", "4").Add("enemy_hit_rate", "0"),
            new() { Duration = 30, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, IncludeSkills = true, EnemiesAtOnce = 4, EnemySpacing = spacing, CaptureFirstFightEvents = true },
            cancellationToken: TestContext.Current.CancellationToken);
        var spaced = Fight(3);
        var impact = spaced.FirstFightEvents.First(entry => entry.Kind == DummyEventKind.SkillImpact);
        var area = spaced.FirstFightEvents.First(entry => entry.Kind == DummyEventKind.SkillAreaHit && entry.Time == impact.Time);
        Assert.Equal(1 - 0.5 * (3 - 1) / (4 - 1.0), area.Damage / impact.Damage, 3);
        Assert.DoesNotContain(Fight(null).FirstFightEvents, entry => entry.Kind == DummyEventKind.SkillAreaHit);
    }

    [Fact]
    public void CommittedCreepingDeathSpreadsABurnStickyBombsBurn()
    {
        // R105 with ADR 0015: with an ACR, a Burn Sticky Bomb and Creeping Death, the bomb's burn spreads to a neighbour 3 m away and
        // ticks there. Without the row's field it doesn't, and the burn and bleed DPS has nothing to count. With the player's own burn
        // tick at 0 the copies still deal damage: they carry the bomb's rate, not the player's. The bomb's own blast (R107) burns its
        // neighbours as well, so every fight here takes the skills' areas away, leaving the spread as the only way a burn moves.
        var pack = CommittedPack();
        var item = pack.Pack.Items.First(entry => entry.TalentSlots.Any(slot => slot.Choices.Contains("gear_talent_creeping_death")));
        var build = Equip(pack.Pack, ["primary_weapon_acr"]).WithEquipment(item.Slot, item.ID)
            .WithTalentSelection(item.Slot, item.TalentSlots.First(slot => slot.Choices.Contains("gear_talent_creeping_death")).ID, "gear_talent_creeping_death")
            .WithActiveSkillSelection("skill_slot_1", "skill_burn_sticky_bomb");
        DummyFightResult Fight(CompiledPackExpressions fightPack) => DummyWeaponSimulator.RunAgainstTargets(fightPack, build,
            Context.SetItem(GamePack.WeaponSlotSelector, "primary_weapon").Add("practical_hit_rate", "100").Add("practical_headshot_share", "30").Add("enemy_target", "1").Add("enemy_difficulty", "4").Add("enemy_hit_rate", "0"),
            new() { Duration = 60, Mode = SimulationMode.Random, Iterations = 2, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, IncludeSkills = true, EnemiesAtOnce = 4, EnemySpacing = 3 },
            cancellationToken: TestContext.Current.CancellationToken);
        var noAreas = pack.Pack with
        {
            Simulation = pack.Pack.Simulation with { Skills = [.. pack.Pack.Simulation.Skills.Select(rule => rule with { Area = "", AreaFull = "", AreaEdge = 0 })] }
        };
        var copied = Fight(Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(noAreas).Compiled));
        var simulation = noAreas.Simulation;
        var plain = Fight(Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(noAreas with
        {
            Simulation = simulation with { Spreads = [.. simulation.Spreads.Select(spread => spread with { SkillStatuses = false })] }
        }).Compiled));
        Assert.True(copied.StatusDps!.Mean > 0);
        Assert.True(plain.StatusDps is null || plain.StatusDps.Mean == 0, $"{plain.StatusDps?.Mean}");
        var noPlayerBurn = Fight(Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(noAreas with
        {
            Stats = [.. noAreas.Stats.Select(stat => stat.ID == "burn_tick_damage" ? stat with { Formula = "0" } : stat)]
        }).Compiled));
        Assert.True(noPlayerBurn.StatusDps!.Mean > 0);
    }

    [Fact]
    public void CommittedIndirectTransmissionStatsSayFightsReadThem()
    {
        // R102: the two stats Indirect Transmission's pass-on reads describe that, and no longer say nothing is simulated.
        var pack = CommittedPack().Pack;
        foreach (var id in new[] { "indirect_transmission_radius", "indirect_transmission_refresh" })
        {
            var description = pack.Stats.Single(stat => stat.ID == id).Description;
            Assert.Contains("Driven fights read it at the fight's start", description, StringComparison.Ordinal);
            Assert.DoesNotContain("No events", description, StringComparison.Ordinal);
            Assert.DoesNotContain("No kills, spread", description, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CommittedUnnerveMarksAreTheR92Values()
    {
        // ShadeTech's R92 candidate: a kill with the Strega marks each enemy within 20 m, up to 5 marks held per enemy for 10 s from
        // the last, and each mark adds 15% to the player's shots on it.
        var pack = CommittedPack().Pack;
        var mark = Assert.Single(pack.Simulation.Blasts, rule => rule.Talent == "weapon_talent_unnerve");
        Assert.Equal(("kill", "", 20.0, "unnerve_marks"), (mark.On, mark.Amount, mark.Within, mark.Mark));
        var rule = Assert.Single(pack.Simulation.Triggers, entry => entry.Talent == mark.Talent);
        Assert.Equal(("unnerve_marks", "stacks", "", 5L, 10.0, true, "target_death"), (rule.Key, rule.Kind, rule.On, rule.Max, rule.Duration, rule.Refresh, rule.ResetOn));
        Assert.Equal("partial", pack.Talents.Single(entry => entry.ID == mark.Talent).Coverage!.Status);
        Assert.Equal(0, pack.Stats.Single(stat => stat.ID == "regicide_blast_damage").Min);
    }

    [Fact]
    public void CommittedTheTrapSpringsOnlyWhenThePlayerMovesBetweenEnemies()
    {
        // ShadeTech's R97 candidate: The Trap tags after 0.5 s of hits, up to 10, and a headshot kill burns the other tags for 10 s,
        // then waits 30 s, cut 10 s per headshot kill.
        var pack = CommittedPack();
        var row = Assert.Single(pack.Pack.Simulation.Traps);
        Assert.Equal(("weapon_talent_the_trap", 0.5, 10, "burning", 10.0, 30.0, 10.0), (row.Talent, row.TagAfter, row.MaxTags, row.Status, row.StatusDuration, row.Every, row.Cut));
        Assert.Equal("partial", pack.Pack.Talents.Single(entry => entry.ID == row.Talent).Coverage!.Status);
        var build = Equip(pack.Pack, "primary_weapon_sacrum_imperium");
        DummyFightResult Fight(double? focus) => DummyWeaponSimulator.RunAgainstTargets(pack, build,
            Context.SetItem(GamePack.WeaponSlotSelector, "primary_weapon").Add("practical_hit_rate", "100").Add("practical_headshot_share", "30")
                .Add("enemy_target", "1").Add("enemy_difficulty", "4").Add("enemy_hit_rate", "0"),
            new() { Duration = 60, Mode = SimulationMode.Random, Iterations = 2, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, EnemiesAtOnce = 4, FocusSeconds = focus, CaptureFirstFightEvents = true },
            cancellationToken: TestContext.Current.CancellationToken);
        // Shooting each enemy until it dies tags only the one being shot, so it never springs; moving every second, it does.
        Assert.DoesNotContain(Fight(null).FirstFightEvents, entry => entry.Kind == DummyEventKind.TrapSprung);
        var moving = Fight(1);
        Assert.Contains(moving.FirstFightEvents, entry => entry.Kind == DummyEventKind.TrapSprung);
        // And the burns it puts on the other tags tick.
        var sprung = moving.FirstFightEvents.First(entry => entry.Kind == DummyEventKind.TrapSprung).Time;
        Assert.Contains(moving.FirstFightEvents, entry => entry.Kind == DummyEventKind.BurnTick && entry.Time > sprung);
    }

    [Fact]
    public void CommittedBewilderedRedirectsHalfOfEachHitWithin30M()
    {
        // ShadeTech's R95-R96 candidate: half of each weapon hit goes to a random enemy within 30 m of the one hit.
        var pack = CommittedPack();
        var row = Assert.Single(pack.Pack.Simulation.Redirects);
        Assert.Equal(("gear_talent_bewildered", 0.5, 30.0), (row.Talent, row.Share, row.Within));
        Assert.Equal("partial", pack.Pack.Talents.Single(entry => entry.ID == row.Talent).Coverage!.Status);
        var item = pack.Pack.Items.First(entry => entry.TalentSlots.Any(slot => slot.Choices.Contains(row.Talent)));
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr").WithEquipment(item.Slot, item.ID)
            .WithTalentSelection(item.Slot, item.TalentSlots.First(slot => slot.Choices.Contains(row.Talent)).ID, row.Talent);
        // At 20 m the next enemy is in reach and takes shares; at 31 m nobody is, and with no spacing nothing moves.
        Assert.Contains(ReachingFight(pack, build, "primary_weapon", 20).FirstFightEvents, entry => entry.Kind == DummyEventKind.DamageRedirected);
        Assert.DoesNotContain(ReachingFight(pack, build, "primary_weapon", 31).FirstFightEvents, entry => entry.Kind == DummyEventKind.DamageRedirected);
        var unspaced = ReachingFight(pack, build, "primary_weapon", null);
        Assert.DoesNotContain(unspaced.FirstFightEvents, entry => entry.Kind == DummyEventKind.DamageRedirected);
        // A redirect moves damage, not adds it: out of reach the fight is the unspaced one.
        Assert.Equal(unspaced.TotalDps!.Mean, ReachingFight(pack, build, "primary_weapon", 31).TotalDps!.Mean, 6);
    }

    [Fact]
    public void CommittedUnnerveFightsReadTheirOwnMarksNotTheAssumption()
    {
        // The marks are now driven, so a driven fight ignores the "Unnerve Target Marks" assumption: with one enemy at a time nobody is
        // marked, whatever the assumption says. A fight without talent triggers still reads it, as static comparisons do.
        var pack = CommittedPack();
        var build = Equip(pack.Pack, "primary_weapon_strega");
        double Fight(string marks, bool driven) => DummyWeaponSimulator.RunAgainstTargets(pack, build,
            Context.Add("practical_hit_rate", "100").Add("enemy_target", "1").Add("enemy_difficulty", "4").Add("unnerve_marks", marks),
            new() { Duration = 30, Mode = SimulationMode.Random, Iterations = 2, Seed = 1, DriveSimpleBuffs = driven },
            cancellationToken: TestContext.Current.CancellationToken).TotalDps!.Mean;
        Assert.Equal(Fight("0", driven: true), Fight("5", driven: true), 6);
        // Unnerve is on the build: without talent triggers, five assumed marks raise the damage.
        Assert.True(Fight("5", driven: false) > Fight("0", driven: false));
    }

    // ShadeTech's R88 to R91 setup, shortened to two fights of 60 s: four enemies at once with no enemy hits, 100% hits, 30% headshots,
    // difficulty 4, against the lightest enemy so kills come often.
    private static DummyFightResult ReachingFight(CompiledPackExpressions pack, BuildSnapshot build, string weapon, double? spacing) =>
        DummyWeaponSimulator.RunAgainstTargets(pack, build,
            Context.SetItem(GamePack.WeaponSlotSelector, weapon).Add("practical_hit_rate", "100").Add("practical_headshot_share", "30").Add("enemy_target", "1").Add("enemy_difficulty", "4").Add("enemy_hit_rate", "0"),
            new() { Duration = 60, Mode = SimulationMode.Random, Iterations = 2, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, EnemiesAtOnce = 4, EnemySpacing = spacing, CaptureFirstFightEvents = true },
            cancellationToken: TestContext.Current.CancellationToken);

    public static TheoryData<string, string[], DummyEventKind?, double> ReachingTalents => new()
    {
        // Each reaches a neighbour at 3 m. Regicide's 5 m reaches none at 6 m, Plague's 25 m none at 30 m, Creeping Death's 8 m none at
        // 9 m, Perfect Creeping Death's 10 m none at 11 m, and Indirect Transmission with the chest's Proliferation, 15 m (R100), none at
        // 16 m. A spread records no event of its own.
        { "weapon_talent_regicide", ["sidearm_regulus"], DummyEventKind.TalentBlast, 6 },
        { "weapon_talent_plague_of_the_outcasts", ["primary_weapon_pestilence"], DummyEventKind.DebuffPassedOn, 30 },
        { "gear_talent_indirect_transmission", ["mask_eclipse_protocol_mask", "chest_eclipse_protocol_chest", "gloves_eclipse_protocol_gloves", "holster_eclipse_protocol_holster"], DummyEventKind.StatusPassedOn, 16 },
        { "gear_talent_perfect_creeping_death", ["backpack_the_courier"], null, 11 },
        { "gear_talent_creeping_death", [], null, 9 },
        // Unnerve's 20 m reaches none at 21 m.
        { "weapon_talent_unnerve", ["primary_weapon_strega"], DummyEventKind.EnemyMarked, 21 },
    };

    [Theory]
    [MemberData(nameof(ReachingTalents))]
    public void CommittedReachingTalentsRaiseDamageOnlyWithANeighbourInReach(string talent, string[] items, DummyEventKind? kind, double outOfReach)
    {
        var pack = CommittedPack();
        var build = Equip(pack.Pack, items);
        if (items.Length == 0)
        {
            // A gear talent with no fixed item goes on the first item offering it.
            var item = pack.Pack.Items.First(entry => entry.TalentSlots.Any(slot => slot.Choices.Contains(talent)));
            build = build.WithEquipment(item.Slot, item.ID).WithTalentSelection(item.Slot, item.TalentSlots.First(slot => slot.Choices.Contains(talent)).ID, talent);
        }
        var slot = items.Length == 0 ? "" : pack.Pack.Items.Single(item => item.ID == items[0]).Slot;
        var weapon = pack.Pack.Slots.Any(entry => entry.ID == slot && entry.Weapon) ? slot : "primary_weapon";
        // The gear talents ride on an ACR with Ignited, for statuses to spread and pass on.
        if (weapon != slot) { build = build.WithEquipment("primary_weapon", "primary_weapon_acr").WithTalentSelection("primary_weapon", "talent", "weapon_talent_ignited"); }
        var near = ReachingFight(pack, build, weapon, 3);
        var far = ReachingFight(pack, build, weapon, outOfReach);
        var unspaced = ReachingFight(pack, build, weapon, null);
        Assert.True(near.TotalDps!.Mean > unspaced.TotalDps!.Mean, talent);
        Assert.Equal(unspaced.TotalDps.Mean, far.TotalDps!.Mean, 6);
        // The gain is the talent's own row's: without it, the spaced fight is the unspaced one.
        var simulation = pack.Pack.Simulation;
        var rowless = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
        {
            Simulation = simulation with
            {
                Blasts = [.. simulation.Blasts.Where(rule => rule.Talent != talent)],
                Spreads = [.. simulation.Spreads.Where(rule => rule.Talent != talent)],
                PassOns = [.. simulation.PassOns.Where(rule => rule.Talent != talent)]
            }
        }).Compiled);
        Assert.Equal(unspaced.TotalDps.Mean, ReachingFight(rowless, build, weapon, 3).TotalDps!.Mean, 6);
        if (kind is { } reached)
        {
            Assert.Contains(near.FirstFightEvents, entry => entry.Kind == reached);
            Assert.DoesNotContain(far.FirstFightEvents, entry => entry.Kind == reached);
        }
        Assert.Contains(unspaced.Limitations, note => note.Contains("enemy spacing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CommittedProliferationPassesOnWithinFifteenMetres()
    {
        // R100: with the Eclipse Protocol chest's Proliferation, Indirect Transmission reaches a neighbour 12 m away. Four pieces without
        // the chest keep the 10 m, and reach nobody there.
        var pack = CommittedPack();
        BuildSnapshot Wearing(string[] items) => Equip(pack.Pack, items)
            .WithEquipment("primary_weapon", "primary_weapon_acr").WithTalentSelection("primary_weapon", "talent", "weapon_talent_ignited");
        var chest = Wearing(["mask_eclipse_protocol_mask", "chest_eclipse_protocol_chest", "gloves_eclipse_protocol_gloves", "holster_eclipse_protocol_holster"]);
        var at12 = ReachingFight(pack, chest, "primary_weapon", 12);
        Assert.Contains(at12.FirstFightEvents, entry => entry.Kind == DummyEventKind.StatusPassedOn);
        Assert.True(at12.TotalDps!.Mean > ReachingFight(pack, chest, "primary_weapon", null).TotalDps!.Mean);
        var noChest = Wearing(["mask_eclipse_protocol_mask", "backpack_eclipse_protocol_backpack", "gloves_eclipse_protocol_gloves", "holster_eclipse_protocol_holster"]);
        Assert.Contains(ReachingFight(pack, noChest, "primary_weapon", 3).FirstFightEvents, entry => entry.Kind == DummyEventKind.StatusPassedOn);
        var noChestAt12 = ReachingFight(pack, noChest, "primary_weapon", 12);
        Assert.DoesNotContain(noChestAt12.FirstFightEvents, entry => entry.Kind == DummyEventKind.StatusPassedOn);
        Assert.Equal(ReachingFight(pack, noChest, "primary_weapon", null).TotalDps!.Mean, noChestAt12.TotalDps!.Mean, 6);
    }

    [Theory]
    [InlineData("gear_talent_tag_team", 6.0)]
    [InlineData("gear_talent_perfect_tag_team", 12.0)]
    public void CommittedTagTeamCutsCooldownsInAFight(string talent, double seconds)
    {
        // R76: a skill hit marks the enemy and the next weapon hit takes seconds off every running cooldown, at most every 4 s.
        var pack = CommittedPack();
        var rules = pack.Pack.Simulation.Triggers.Where(rule => rule.Talent == talent).ToArray();
        Assert.Equal(["tag_team_mark", "tag_team_cut"], rules.Select(rule => rule.Key));
        Assert.Equal(("ready", "skill_hit", "hit"), (rules[0].Kind, rules[0].On, rules[0].ConsumeOn));
        Assert.Equal(("skill_cooldown_seconds", seconds, 4.0, "tag_team_mark"), (rules[1].Grant, rules[1].GrantAmount, rules[1].Every, rules[1].WhileKey));
        Assert.Equal("partial", pack.Pack.Talents.Single(entry => entry.ID == talent).Coverage!.Status);
        var item = pack.Pack.Items.First(entry => entry.TalentSlots.Any(slot => slot.Choices.Contains(talent)));
        var slot = item.TalentSlots.First(entry => entry.Choices.Contains(talent));
        var build = SixTierCores(pack.Pack, "skill_stinger_hive").WithEquipment(item.Slot, item.ID).WithTalentSelection(item.Slot, slot.ID, talent);
        // As ShadeTech ran it, with no enemy hits, so the player stays up all fight.
        var context = Context.Add("enemy_target", "5").Add("enemy_difficulty", "4").Add("enemy_hit_rate", "0");
        DummyFightResult Fight(CompiledPackExpressions fought) => DummyWeaponSimulator.RunAgainstTargets(fought, build, context,
            new() { Duration = 180, Mode = SimulationMode.Random, Iterations = 3, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, IncludeSkills = true, CaptureFirstFightEvents = true },
            cancellationToken: TestContext.Current.CancellationToken);
        var tagged = Fight(pack);
        var untagged = Fight(Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
        { Simulation = pack.Pack.Simulation with { Triggers = [.. pack.Pack.Simulation.Triggers.Where(rule => rule.Talent != talent)] } }).Compiled));
        int Deployments(DummyFightResult result) => result.FirstFightEvents.Count(e => e.Kind == DummyEventKind.SkillImpact);
        Assert.True(tagged.TimedTriggerUptime["tag_team_cut"].Seconds > 0);
        string Kinds(DummyFightResult result) => string.Join(",", result.FirstFightEvents.Where(e => e.Kind is DummyEventKind.SkillImpact or DummyEventKind.SkillTick).GroupBy(e => e.Kind).Select(g => g.Key + ":" + g.Count() + "@" + string.Join("/", g.Take(6).Select(e => e.Time.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)))));
        Assert.True(tagged.SkillDps!.Total.Mean > untagged.SkillDps!.Total.Mean, $"{tagged.SkillDps.Total.Mean} vs {untagged.SkillDps.Total.Mean} | {Kinds(tagged)} | {Kinds(untagged)} | down {tagged.Incoming?.TimeToDown?.Mean}");
        Assert.True(Deployments(tagged) >= Deployments(untagged));
    }

    [Theory]
    [InlineData("gear_talent_skilled", 0.25)]
    [InlineData("gear_talent_perfect_skilled", 0.3)]
    public void CommittedSkilledResetsCooldownsOnSkillKills(string talent, double chance)
    {
        // R77: a skill kill has a chance to reset every skill cooldown. Cluster Seeker Mine's spaced impacts kill after its use has
        // started its cooldown, so a reset reaches it; ShadeTech's figures show its skill DPS rise.
        var pack = CommittedPack();
        var rule = Assert.Single(pack.Pack.Simulation.Triggers, entry => entry.Talent == talent);
        Assert.Equal(("skilled_reset", "skill_kill", chance, "skill_cooldown", 1.0, 1.0), (rule.Key, rule.On, rule.Chance, rule.Grant, rule.GrantAmount, rule.GrantHoldDelay));
        Assert.Equal("partial", pack.Pack.Talents.Single(entry => entry.ID == talent).Coverage!.Status);
        // R83: the banked reset is modeled now, so only the status kills are left out.
        Assert.DoesNotContain(pack.Pack.Talents.Single(entry => entry.ID == talent).Coverage!.Omits, omit => omit.Contains("banks the reset", StringComparison.Ordinal));
        var item = pack.Pack.Items.First(entry => entry.TalentSlots.Any(slot => slot.Choices.Contains(talent)));
        var slot = item.TalentSlots.First(entry => entry.Choices.Contains(talent));
        var build = SixTierCores(pack.Pack, "skill_cluster_seeker_mine").WithEquipment(item.Slot, item.ID).WithTalentSelection(item.Slot, slot.ID, talent);
        var context = Context.Add("enemy_target", "5").Add("enemy_difficulty", "4").Add("enemy_hit_rate", "0");
        DummyFightResult Fight(CompiledPackExpressions fought) => DummyWeaponSimulator.RunAgainstTargets(fought, build, context,
            new() { Duration = 180, Mode = SimulationMode.Random, Iterations = 20, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, IncludeSkills = true },
            cancellationToken: TestContext.Current.CancellationToken);
        var skilled = Fight(pack);
        var without = Fight(Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
        { Simulation = pack.Pack.Simulation with { Triggers = [.. pack.Pack.Simulation.Triggers.Where(entry => entry.Talent != talent)] } }).Compiled));
        Assert.True(skilled.SkillDps!.Total.Mean > without.SkillDps!.Total.Mean, $"{skilled.SkillDps.Total.Mean} vs {without.SkillDps.Total.Mean}");
    }

    [Theory]
    [InlineData("gear_talent_skilled")]
    [InlineData("gear_talent_perfect_skilled")]
    public void CommittedSkilledHoldsTheResetForAStickyBombsImpactKill(string talent)
    {
        // R83: an Explosive Sticky Bomb kills as its use lands, before its cooldown is running, so the reset is held and lands 1 s after.
        // ShadeTech's figures show its skill DPS rise; without the hold the start's guard left it alone. At difficulty 1 its blast kills
        // often enough for every seed to draw resets (at 4 a few fights may draw none).
        var pack = CommittedPack();
        var item = pack.Pack.Items.First(entry => entry.TalentSlots.Any(slot => slot.Choices.Contains(talent)));
        var slot = item.TalentSlots.First(entry => entry.Choices.Contains(talent));
        var build = SixTierCores(pack.Pack, "skill_explosive_sticky_bomb").WithEquipment(item.Slot, item.ID).WithTalentSelection(item.Slot, slot.ID, talent);
        var context = Context.Add("enemy_target", "5").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "0");
        DummyFightResult Fight(CompiledPackExpressions fought) => DummyWeaponSimulator.RunAgainstTargets(fought, build, context,
            new() { Duration = 180, Mode = SimulationMode.Random, Iterations = 20, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, IncludeSkills = true },
            cancellationToken: TestContext.Current.CancellationToken);
        CompiledPackExpressions With(IEnumerable<SimTriggerDefinition> triggers) => Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
        { Simulation = pack.Pack.Simulation with { Triggers = [.. triggers] } }).Compiled);
        var heldFight = Fight(pack);
        var held = heldFight.SkillDps!.Total.Mean;
        var without = Fight(With(pack.Pack.Simulation.Triggers.Where(entry => entry.Talent != talent))).SkillDps!.Total.Mean;
        var unheld = Fight(With(pack.Pack.Simulation.Triggers.Select(entry => entry.Talent == talent ? entry with { GrantHoldDelay = 0 } : entry))).SkillDps!.Total.Mean;
        Assert.True(heldFight.TimedTriggerUptime["skilled_reset"].Seconds > 0);
        Assert.True(held > without, $"{held} vs {without}");
        Assert.Equal(without, unheld);
    }

    [Theory]
    [InlineData("gear_talent_calculated", 0.1)]
    [InlineData("gear_talent_perfect_calculated", 0.15)]
    public void CommittedCalculatedCutsCooldownsOnKillsInCover(string talent, double share)
    {
        // R80: a kill in cover takes a share of each skill's cooldown off. A cover cycle sets in_cover for the fight; in the open nothing moves.
        var pack = CommittedPack();
        var rule = Assert.Single(pack.Pack.Simulation.Triggers, entry => entry.Talent == talent);
        Assert.Equal(("calculated_cut", "kill", "skill_cooldown", share, "true"), (rule.Key, rule.On, rule.Grant, rule.GrantAmount, rule.RequiresContext!["in_cover"]));
        Assert.Equal("partial", pack.Pack.Talents.Single(entry => entry.ID == talent).Coverage!.Status);
        var item = pack.Pack.Items.First(entry => entry.TalentSlots.Any(slot => slot.Choices.Contains(talent)));
        var slot = item.TalentSlots.First(entry => entry.Choices.Contains(talent));
        var build = SixTierCores(pack.Pack, "skill_stinger_hive").WithEquipment(item.Slot, item.ID).WithTalentSelection(item.Slot, slot.ID, talent);
        var context = Context.Add("enemy_target", "5").Add("enemy_difficulty", "4").Add("enemy_hit_rate", "0");
        var without = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
        { Simulation = pack.Pack.Simulation with { Triggers = [.. pack.Pack.Simulation.Triggers.Where(entry => entry.Talent != talent)] } }).Compiled);
        double SkillDps(CompiledPackExpressions fought, bool cover) => DummyWeaponSimulator.RunAgainstTargets(fought, build, context,
            new()
            {
                Duration = 180,
                Mode = SimulationMode.Random,
                Iterations = 20,
                Seed = 1,
                DriveSimpleBuffs = true,
                IncludeIncoming = true,
                IncludeSkills = true,
                PeekSeconds = cover ? 3 : null,
                CoverSeconds = cover ? 1 : null,
                PeekHitShare = cover ? 1 : null
            },
            cancellationToken: TestContext.Current.CancellationToken).SkillDps!.Total.Mean;
        Assert.True(SkillDps(pack, true) > SkillDps(without, true));
        Assert.Equal(SkillDps(without, false), SkillDps(pack, false));
    }

    [Fact]
    public void CommittedSafeguardIsComplete()
    {
        // The unmodeled survey: the talents' rule reads 1.3 (1.6 Perfect) while armor is full, as the pack has it; the bundle's 1.5 is unread.
        var pack = CommittedPack();
        Assert.All(new[] { "gear_talent_safeguard", "gear_talent_perfect_safeguard" },
            id => Assert.Equal("complete", pack.Pack.Talents.Single(talent => talent.ID == id).Coverage!.Status));
    }

    [Fact]
    public void CommittedEnergizeReachesTierSevenInAFight()
    {
        // R64: at tier 6 a kit's Energize used to add nothing. A full six now gets tier 7 for a skill deployed under it.
        var pack = CommittedPack();
        var build = SixTierCores(pack.Pack, "skill_stinger_hive");
        var context = Context.Add("enemy_target", "5").Add("enemy_difficulty", "4");
        Assert.Equal(6, BuildResolver.Resolve(pack, build, context).Calculation.Stats["skill_tier"]);
        DummyFightResult Fight(BuildSnapshot fought) => DummyWeaponSimulator.RunAgainstTargets(pack, fought, context,
            new() { Duration = 180, Mode = SimulationMode.Random, Iterations = 5, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, IncludeSkills = true, ArmorKits = 10, ArmorKitAtPercent = 95 },
            cancellationToken: TestContext.Current.CancellationToken);
        var energized = Fight(build.WithTalentSelection("backpack", "talent", "gear_talent_energize"));
        Assert.True(energized.TimedTriggerUptime["energize_skill_tier_active"].Seconds > 0);
        Assert.True(energized.SkillDps!.Total.Mean > Fight(build).SkillDps!.Total.Mean);
    }

    // The committed pack without its stat-gated rows (R70) or the modifiers they turn on: the field's tests add their own.
    private static readonly Lazy<CompiledPackExpressions> UngatedPack = new(() =>
    {
        var pack = CommittedPack().Pack;
        var keys = pack.Simulation.Triggers.Where(rule => rule.WhileStatAtLeast is not null).Select(rule => rule.Key).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(keys);
        return Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack with
        {
            Talents = [.. pack.Talents.Select(talent => talent with { Modifiers = [.. talent.Modifiers.Where(modifier => modifier.RequiresContext?.Keys.Any(keys.Contains) != true)] })],
            Simulation = pack.Simulation with { Triggers = [.. pack.Simulation.Triggers.Where(rule => rule.WhileStatAtLeast is null)] }
        }).Compiled);
    });

    private static CompiledPackExpressions Ungated() => UngatedPack.Value;

    // The field's own row on that pack, as ShadeTech first proposed it: a kill at a tier grants Overcharge.
    private static CompiledPackExpressions GatedFuturePerfect(double tier, double every = 90, string on = "kill")
    {
        const string talent = "weapon_talent_future_perfect", key = "future_perfect_overcharge";
        var pack = Ungated().Pack;
        return Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack with
        {
            Talents = [.. pack.Talents.Select(entry => entry.ID != talent ? entry : entry with
            {
                Modifiers = [.. entry.Modifiers, new() { Stat = "skill_overcharge", Op = "flat", Value = new(1), RequiresContext = ImmutableDictionary<string, string>.Empty.Add(key, "true") }]
            })],
            Selectors = [.. pack.Selectors, new() { ID = key, Name = "Future Perfect Overcharge", Values = ["true"] }],
            Simulation = pack.Simulation with
            {
                Triggers = [.. pack.Simulation.Triggers, new()
                {
                    Talent = talent, Key = key, On = on, Kind = "buff", Duration = 15, Every = every,
                    WhileStatAtLeast = new() { Stat = "skill_tier", Value = tier }
                }]
            }
        }).Compiled);
    }

    private static double Overcharged(CompiledPackExpressions pack, double headshots = 0) =>
        DummyWeaponSimulator.RunAgainstTargets(pack, Equip(pack.Pack, "primary_weapon_acr").WithTalentSelection("primary_weapon", "talent", "weapon_talent_future_perfect"),
            Context.Add("practical_hit_rate", "100").Add("practical_headshot_share", headshots.ToString(System.Globalization.CultureInfo.InvariantCulture)).Add("enemy_target", "1").Add("enemy_difficulty", "1"),
            new() { Duration = 120, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true }, retargetDelay: 6,
            cancellationToken: TestContext.Current.CancellationToken).TimedTriggerUptime.GetValueOrDefault("future_perfect_overcharge")?.Seconds ?? 0;

    [Fact]
    public void AStatGateReadsTheTiersStacksBeforeItsKill()
    {
        // At tier 0 Future Perfect's three stacks are all it has. Without a cooldown each tier opens a kill later, and a kill doesn't open its own gate.
        var first = Overcharged(GatedFuturePerfect(0, every: 0));
        var second = Overcharged(GatedFuturePerfect(1, every: 0));
        var fourth = Overcharged(GatedFuturePerfect(3, every: 0));
        Assert.True(first > second, $"{first} {second}");
        Assert.True(second > fourth, $"{second} {fourth}");
        Assert.True(fourth > 0);
        // Three stacks never make four.
        Assert.Equal(0, Overcharged(GatedFuturePerfect(4, every: 0)));
    }

    [Fact]
    public void AStatGateDoesntSeeTheStackItsOwnBulletEarned()
    {
        // Every bullet a headshot: its kill stacks first, then its headshot kill reads the tier, without that stack.
        var open = Overcharged(GatedFuturePerfect(0, every: 0, on: "headshot_kill"), headshots: 100);
        var waiting = Overcharged(GatedFuturePerfect(1, every: 0, on: "headshot_kill"), headshots: 100);
        Assert.True(waiting > 0);
        Assert.True(open > waiting, $"{open} {waiting}");
    }

    [Fact]
    public void AStatGatedOverchargeReachesASkillInAFight()
    {
        var pack = GatedFuturePerfect(6);
        var build = SixTierCores(pack.Pack, "skill_stinger_hive").WithTalentSelection("primary_weapon", "talent", "weapon_talent_future_perfect");
        var context = Context.Add("practical_hit_rate", "100").Add("enemy_target", "1").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "0");
        DummyFightResult Fight(CompiledPackExpressions fought) => DummyWeaponSimulator.RunAgainstTargets(fought, build, context,
            new() { Duration = 180, Mode = SimulationMode.Random, Iterations = 5, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, IncludeSkills = true },
            retargetDelay: 6, cancellationToken: TestContext.Current.CancellationToken);
        var gated = Fight(pack);
        // The 90 s cooldown holds it to two 15 s windows in a fight of 180, or three if the first comes early enough.
        Assert.InRange(gated.TimedTriggerUptime["future_perfect_overcharge"].Seconds, 1, 5 * 15 * 3);
        Assert.True(gated.SkillDps!.Total.Mean > Fight(Ungated()).SkillDps!.Total.Mean);
    }

    [Fact]
    public void CommittedFuturePerfectGrantsOverchargeOnAKillAtTierSix()
    {
        // R70: a kill at tier 6, counting Overcharge, grants it for 15 s, every 90 s from the last grant.
        var pack = CommittedPack();
        foreach (var talent in new[] { "weapon_talent_future_perfect", "weapon_talent_future_perfection" })
        {
            var rule = pack.Pack.Simulation.Triggers.Single(entry => entry.Talent == talent && entry.WhileStatAtLeast is not null);
            Assert.Equal(("future_perfect_overcharge_active", "kill", 15d, 90d, "overcharged_skill_tier", 6d, "future_perfect_overcharge"),
                (rule.Key, rule.On, rule.Duration, rule.Every, rule.WhileStatAtLeast!.Stat, rule.WhileStatAtLeast.Value, rule.EveryGroup));
        }
        var build = SixTierCores(pack.Pack, "skill_stinger_hive").WithTalentSelection("primary_weapon", "talent", "weapon_talent_future_perfect");
        var context = Context.Add("practical_hit_rate", "100").Add("enemy_target", "1").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "0");
        DummyFightResult Fight(CompiledPackExpressions fought) => DummyWeaponSimulator.RunAgainstTargets(fought, build, context,
            new() { Duration = 180, Mode = SimulationMode.Random, Iterations = 5, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, IncludeSkills = true },
            retargetDelay: 6, cancellationToken: TestContext.Current.CancellationToken);
        var committed = Fight(pack);
        Assert.InRange(committed.TimedTriggerUptime["future_perfect_overcharge_active"].Seconds, 1, 5 * 15 * 3);
        Assert.True(committed.SkillDps!.Total.Mean > Fight(Ungated()).SkillDps!.Total.Mean);
    }

    [Fact]
    public void FuturePerfectWithFuturePerfectionIsRefused()
    {
        // Their Overcharge rows share `future_perfect_overcharge` (R73). Their Skill Tier stacks share a key too, across two talents,
        // so a build with both doesn't fight. The source never has both active (R73), so this blocks no build the game allows.
        var pack = CommittedPack();
        var build = SixTierCores(pack.Pack, "skill_stinger_hive").WithTalentSelection("primary_weapon", "talent", "weapon_talent_future_perfect")
            .WithEquipment("secondary_weapon", "secondary_weapon_mechanical_animal").WithTalentSelection("secondary_weapon", "talent", "weapon_talent_future_perfection");
        var refused = Assert.Throws<SimulationException>(() => DummyWeaponSimulator.RunAgainstTargets(pack, build, Context.Add("enemy_target", "1").Add("enemy_difficulty", "1"),
            new() { Duration = 10, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, Rotation = new("secondary_weapon") },
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("Stacks rules on one key must be one talent's", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CommittedFuturePerfectsGateCountsEnergizesOvercharge()
    {
        // Tier 2 and three stacks make 5, which never opens the gate; Energize's Overcharge makes it 6 (R70).
        var pack = CommittedPack();
        var build = TierCores(pack.Pack, "skill_stinger_hive", 2).WithTalentSelection("primary_weapon", "talent", "weapon_talent_future_perfect");
        var context = Context.Add("practical_hit_rate", "100").Add("enemy_target", "1").Add("enemy_difficulty", "1");
        Assert.Equal(2, BuildResolver.Resolve(pack, build, context).Calculation.Stats["skill_tier"]);
        double Uptime(BuildSnapshot fought, CompiledPackExpressions? fights = null) => DummyWeaponSimulator.RunAgainstTargets(fights ?? pack, fought, context,
            new() { Duration = 180, Mode = SimulationMode.Random, Iterations = 5, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, ArmorKits = 10, ArmorKitAtPercent = 95 },
            retargetDelay: 6, cancellationToken: TestContext.Current.CancellationToken).TimedTriggerUptime.GetValueOrDefault("future_perfect_overcharge_active")?.Seconds ?? 0;
        var energized = build.WithTalentSelection("backpack", "talent", "gear_talent_energize");
        Assert.Equal(0, Uptime(build));
        Assert.True(Uptime(energized) > 0);
        // A gate on skill_tier, as first proposed, doesn't count Overcharge, so it stays shut.
        var onTier = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
        {
            Simulation = pack.Pack.Simulation with { Triggers = [.. pack.Pack.Simulation.Triggers.Select(rule => rule.WhileStatAtLeast is null ? rule : rule with { WhileStatAtLeast = rule.WhileStatAtLeast with { Stat = "skill_tier" } })] }
        }).Compiled);
        Assert.Equal(0, Uptime(energized, onTier));
    }

    [Fact]
    public void AStatGateFollowsTheWeaponInHandUnderRotation()
    {
        var build = Equip(CommittedPack().Pack, "primary_weapon_acr").WithEquipment("sidearm", "sidearm_d50").WithTalentSelection("primary_weapon", "talent", "weapon_talent_future_perfect");
        double Uptime(CompiledPackExpressions pack, WeaponSwapMode mode) => DummyWeaponSimulator.RunAgainstTargets(pack, build, Context.Add("practical_hit_rate", "100").Add("enemy_target", "1").Add("enemy_difficulty", "1"),
            new() { Duration = 120, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, Rotation = new("sidearm", mode) }, retargetDelay: 6,
            cancellationToken: TestContext.Current.CancellationToken).TimedTriggerUptime.GetValueOrDefault("future_perfect_overcharge")?.Seconds ?? 0;
        Assert.True(Uptime(GatedFuturePerfect(1, every: 0), WeaponSwapMode.WhenEmpty) > 0);
        // The swap's rule belongs to the weapon leaving the hand, whose stacks count though the drawn weapon has none.
        Assert.True(Uptime(GatedFuturePerfect(1, every: 0, on: "swap"), WeaponSwapMode.AfterKill) > 0);
    }

    [Fact]
    public void AStatGatedRuleWithAShieldIsRefusedBeforeTheFight()
    {
        // A second is too short for a kill, so the rolls can't be what decides.
        var pack = GatedFuturePerfect(6);
        var build = SixTierCores(pack.Pack, "skill_striker_shield").WithTalentSelection("primary_weapon", "talent", "weapon_talent_future_perfect");
        var context = Context.Add("enemy_target", "1").Add("enemy_difficulty", "4");
        DummyFightResult Fight(CompiledPackExpressions fought, BuildSnapshot used) => DummyWeaponSimulator.RunAgainstTargets(fought, used, context,
            new() { Duration = 1, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, IncludeShield = true },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("shield-state-unsupported", Assert.Throws<SimulationException>(() => Fight(pack, build)).Code);
        Assert.True(Fight(Ungated(), build).Duration > 0);
        Assert.True(Fight(pack, SixTierCores(pack.Pack, "skill_striker_shield")).Duration > 0);
        // Future Perfect's own row (R70) is refused the same way.
        Assert.Equal("shield-state-unsupported", Assert.Throws<SimulationException>(() => Fight(CommittedPack(), build)).Code);
    }

    [Fact]
    public void CommittedFuturePerfectWithAShieldRunsWhereItsGateCantOpen()
    {
        // Tier 2 and three stacks make 5, so Overcharge never comes. At tier 3 the stacks reach 6, so it can.
        var pack = CommittedPack();
        var build = TierCores(pack.Pack, "skill_striker_shield", 2).WithTalentSelection("primary_weapon", "talent", "weapon_talent_future_perfect");
        DummyFightResult Fight(BuildSnapshot used, int? kits) => DummyWeaponSimulator.RunAgainstTargets(pack, used, Context.Add("enemy_target", "1").Add("enemy_difficulty", "4"),
            new() { Duration = 1, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, IncludeShield = true, ArmorKits = kits, ArmorKitAtPercent = kits is null ? null : 50 },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(Fight(build, null).Duration > 0);
        var three = TierCores(pack.Pack, "skill_striker_shield", 3).WithTalentSelection("primary_weapon", "talent", "weapon_talent_future_perfect");
        Assert.Equal("shield-state-unsupported", Assert.Throws<SimulationException>(() => Fight(three, null)).Code);
    }

    [Fact]
    public void AStatGatedRuleOnTheSecondWeaponIsRefusedWithAShieldToo()
    {
        var pack = GatedFuturePerfect(6);
        var build = SixTierCores(pack.Pack, "skill_striker_shield").WithEquipment("sidearm", "sidearm_d50").WithTalentSelection("sidearm", "talent", "weapon_talent_future_perfect");
        Assert.Equal("shield-state-unsupported", Assert.Throws<SimulationException>(() => DummyWeaponSimulator.RunAgainstTargets(pack, build, Context.Add("enemy_target", "1").Add("enemy_difficulty", "4"),
            new() { Duration = 1, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, IncludeShield = true, Rotation = new("sidearm") },
            cancellationToken: TestContext.Current.CancellationToken)).Code);
    }

    [Fact]
    public void AStatGatedRuleThatCantFireIsNoReasonToRefuse()
    {
        // No kits in the fight, so a rule waiting on one is gone, and its talent is an assumption.
        var pack = GatedFuturePerfect(6, on: "armor_kit");
        var build = SixTierCores(pack.Pack, "skill_striker_shield").WithTalentSelection("primary_weapon", "talent", "weapon_talent_future_perfect");
        Assert.True(DummyWeaponSimulator.RunAgainstTargets(pack, build, Context.Add("enemy_target", "1").Add("enemy_difficulty", "4"),
            new() { Duration = 1, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, IncludeShield = true },
            cancellationToken: TestContext.Current.CancellationToken).Duration > 0);
    }

    private static BuildSnapshot SixTierCores(GamePack pack, string skill) => TierCores(pack, skill, 6);

    // Every gear slot filled, with a skill tier core on the first `cores` of them.
    private static BuildSnapshot TierCores(GamePack pack, string skill, int cores)
    {
        const string core = "skill_tier_gear_core";
        var max = pack.Attributes.Last(attribute => attribute.ID == core).Max;
        var build = Equip(pack, "primary_weapon_acr").WithActiveSkillSelection("skill_slot_1", skill);
        var slots = new[] { "mask", "chest", "backpack", "gloves", "holster", "knees" };
        for (var index = 0; index < slots.Length; index++)
        {
            var slot = slots[index];
            var item = pack.Items.First(item => item.Slot == slot && item.AttributeSlots.Any(host => host.Choices.Any(choice => choice.AttributeID == core)));
            var host = item.AttributeSlots.Last(host => host.Choices.Any(choice => choice.AttributeID == core));
            build = build.WithEquipment(slot, item.ID);
            if (index < cores) { build = build.WithAttributeRoll(slot, host.ID, new(core, max)); }
        }
        return build;
    }

    [Fact]
    public void CommittedEnergizeWithAShieldAtTierSixIsRefusedBeforeTheFight()
    {
        // A fight can't follow a shield, and tier 7 lifts one. A second is too short to reach a kit, so the rolls can't be what decides.
        var pack = CommittedPack();
        var build = SixTierCores(pack.Pack, "skill_striker_shield");
        var context = Context.Add("enemy_target", "1").Add("enemy_difficulty", "1");
        DummyFightResult Fight(BuildSnapshot fought, int? kits) => DummyWeaponSimulator.RunAgainstTargets(pack, fought, context,
            new() { Duration = 1, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, IncludeShield = true, ArmorKits = kits, ArmorKitAtPercent = kits is null ? null : 50 },
            cancellationToken: TestContext.Current.CancellationToken);
        var energized = build.WithTalentSelection("backpack", "talent", "gear_talent_energize");
        Assert.Equal("shield-state-unsupported", Assert.Throws<SimulationException>(() => Fight(energized, 3)).Code);
        // No kits in the fight, or no Energize, and nothing changes the shield.
        Assert.True(Fight(energized, null).Duration > 0);
        Assert.True(Fight(build, 3).Duration > 0);
    }

    [Fact]
    public void CommittedSniperTurretFights()
    {
        // R15: refused until its rule. Three shots at tier 0, each a reload of 3 s after the first, then 2 s and the 20 s cooldown.
        var pack = CommittedPack();
        var build = Equip(pack.Pack, "primary_weapon_acr").WithActiveSkillSelection("skill_slot_1", "skill_sniper_turret");
        var context = Context.Add("enemy_target", "1").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "0");
        var outputs = BuildResolver.Resolve(pack, build, context).ActiveSkillPreviews["skill_slot_1"].Outputs;
        double Output(string id) => outputs.Single(entry => entry.Id == id).Value;
        Assert.Equal(28, Output("use_cycle_seconds"));
        Assert.Equal(Output("damage") * 3 / 28, Output("assumed_cycle_dps"), 6);
        var fight = DummyWeaponSimulator.RunAgainstTargets(pack, build, context,
            new() { Duration = 60, Mode = SimulationMode.Random, Iterations = 2, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, IncludeSkills = true },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(fight.SkillDps!.Total.Mean > 0);
    }

    private static DummyEvent[] ArtilleryTicks(string target, string difficulty) => [.. DummyWeaponSimulator.RunAgainstTargets(CommittedPack(),
        Equip(CommittedPack().Pack, "primary_weapon_acr").WithActiveSkillSelection("skill_slot_1", "skill_artillery_turret"),
        Context.Add("enemy_target", target).Add("enemy_difficulty", difficulty).Add("enemy_hit_rate", "0"),
        new() { Duration = 20, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, IncludeSkills = true, CaptureFirstFightEvents = true },
        retargetDelay: 1, cancellationToken: TestContext.Current.CancellationToken).FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.SkillTick)];

    [Fact]
    public void CommittedArtilleryBleedsFromItsFirstShellToSevenSecondsAfterItsLast()
    {
        // Three shells 2 s apart at tier 0, each putting the bleed on, so it runs to 11 s, as the preview's cycle DPS counts it (R66, R69).
        var ticks = ArtilleryTicks("6", "5");
        Assert.InRange(ticks.Max(entry => entry.Time), 10.5, 11);
    }

    [Fact]
    public void CommittedArtilleryBleedsAnEnemyArrivingBetweenItsShells()
    {
        // The first shell kills a weak enemy, and the next arrives before the second shell, which puts the bleed on it.
        Assert.Contains(ArtilleryTicks("1", "1"), entry => entry.Time > 2 && entry.Damage > 0);
    }

    [Fact]
    public void CommittedSniperTurretReachesMoreEnemiesWithItsShotsSpaced()
    {
        // Its reload spaces the shots (R69). This takes the spacing out: a use all at once spends itself on the first enemy.
        var spaced = CommittedPack();
        var pack = spaced.Pack;
        var skill = pack.ActiveSkills.Single(entry => entry.ID == "skill_sniper_turret");
        var rule = pack.Simulation.Skills.Single(entry => entry.Skill == skill.ID);
        Assert.Equal("reload_seconds", rule.ImpactInterval);
        var compiled = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack with
        {
            Simulation = pack.Simulation with { Skills = [.. pack.Simulation.Skills.Select(entry => entry == rule ? rule with { ImpactInterval = "" } : entry)] }
        }).Compiled);
        var context = Context.Add("enemy_target", "1").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "0");
        double Fight(CompiledPackExpressions fought) => DummyWeaponSimulator.RunAgainstTargets(fought, Equip(fought.Pack, "primary_weapon_acr").WithActiveSkillSelection("skill_slot_1", skill.ID), context,
            new() { Duration = 120, Mode = SimulationMode.Random, Iterations = 20, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, IncludeSkills = true },
            cancellationToken: TestContext.Current.CancellationToken).SkillDps!.Total.Mean;
        Assert.True(Fight(spaced) > Fight(compiled));
    }

    [Fact]
    public void CommittedEnergizeFiresOnAKit()
    {
        var pack = CommittedPack();
        var build = Equip(pack.Pack, "primary_weapon_acr", "mask_badger_tuff_mask", "chest_badger_tuff_chest", "gloves_badger_tuff_gloves", "holster_badger_tuff_holster", "knees_badger_tuff_kneepads", "backpack_n_5_11_tactical_backpack")
            .WithTalentSelection("backpack", "talent", "gear_talent_energize");
        var context = Context.Add("enemy_target", "1").Add("enemy_difficulty", "1");
        DummyFightResult Fight(int? kits) => DummyWeaponSimulator.RunAgainstTargets(pack, build, context,
            new() { Duration = 120, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, ArmorKits = kits, ArmorKitAtPercent = kits is null ? null : 50 },
            cancellationToken: TestContext.Current.CancellationToken);
        var kits = Fight(3);
        Assert.True(kits.Incoming!.ArmorKitsUsed > 0);
        Assert.True(kits.TimedTriggerUptime["energize_skill_tier_active"].Seconds > 0);
        // With no kits the fight runs on the user's setting, and says so.
        var none = Fight(null);
        Assert.DoesNotContain("energize_skill_tier_active", none.TimedTriggerUptime.Keys);
        // A kit's tier reaches a skill deployed under it.
        Assert.True(DummyWeaponSimulator.RunAgainstTargets(pack, build.WithActiveSkillSelection("skill_slot_1", "skill_explosive_sticky_bomb"), context,
            new() { Duration = 120, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, IncludeSkills = true, ArmorKits = 3, ArmorKitAtPercent = 50 },
            cancellationToken: TestContext.Current.CancellationToken).SkillDps!.Total.Mean > 0);
        Assert.Contains("This fight carries no armor kits, so these read your setting and never react to one: Energize", none.Limitations);
    }

    [Theory]
    [InlineData("primary_weapon_steel_sons_acr", "shakedown_stacks")]
    [InlineData("primary_weapon_underboss", "gangland_hit_active")]
    public void CommittedTargetTalentsFightByTheirRules(string item, string key)
    {
        var pack = CommittedPack();
        var build = Equip(pack.Pack, item);
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, Context.Add("enemy_target", "1"),
            new() { Duration = 30, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.TimedTriggerUptime[key].Seconds > 0);
    }

    [Fact]
    public void CommittedBleedingEdgeCountNeedsADistance()
    {
        // A count with a range: in a group a fight must say how far enemies are, as for Chatter and Adrenaline Rush.
        var pack = CommittedPack();
        const string Edge = "gear_talent_bleeding_edge";
        var item = pack.Pack.Items.First(item => item.TalentSlots.Any(slot => slot.Choices.Contains(Edge)));
        var build = Equip(pack.Pack, "primary_weapon_acr").WithEquipment(item.Slot, item.ID).WithTalentSelection(item.Slot, item.TalentSlots.First(slot => slot.Choices.Contains(Edge)).ID, Edge);
        var context = Context.Add("enemy_target", "1").Add("enemy_difficulty", "1");
        DummyFightResult Fight(double? distance) => DummyWeaponSimulator.RunAgainstTargets(pack, build, context,
            new() { Duration = 5, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, EnemiesAtOnce = 2, Distance = distance },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("distance-required", Assert.Throws<SimulationException>(() => Fight(null)).Code);
        Assert.True(Fight(10).ShotsFired > 0);
    }

    [Fact]
    public void CommittedArmorKitIsTheNormalKit()
    {
        var pack = CommittedPack();
        var kit = pack.Pack.Simulation.ArmorKit!;
        Assert.Equal((1d, 2.5, 0.3, 4.9), (kit.Repair, kit.UseSeconds, kit.ExtendSeconds, kit.MaxUseSeconds));
        Assert.Equal(["firewall", "survivalist"], kit.ReplacedBySpecializations);
        Assert.Equal(["gear_talent_hackstep_protocol"], kit.ReplacedByTalents);
        var context = Context.Add("enemy_target", "1").Add("enemy_difficulty", "1");
        DummyFightResult Fight(string specialization) => DummyWeaponSimulator.RunAgainstTargets(pack,
            Equip(pack.Pack, "primary_weapon_acr", "mask_badger_tuff_mask", "chest_badger_tuff_chest", "gloves_badger_tuff_gloves", "holster_badger_tuff_holster", "knees_badger_tuff_kneepads").WithSpecialization(specialization), context,
            new() { Duration = 120, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, ArmorKits = 3, ArmorKitAtPercent = 50 },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(Fight("gunner").Incoming!.ArmorKitsUsed > 0);
        Assert.Equal("armor-kits-unsupported", Assert.Throws<SimulationException>(() => Fight("firewall")).Code);
        Assert.Equal("armor-kits-unsupported", Assert.Throws<SimulationException>(() => Fight("survivalist")).Code);
    }

    [Theory]
    [InlineData("skill_corrosive_trap", "damage")]
    [InlineData("skill_striker_drone", "damage")]
    public void CommittedY8S33SkillsPreviewButDontFight(string skill, string output)
    {
        // The Y8S3.3 skills with no rule are previews only: they resolve in both slots, and a fight with damaging skills is refused
        // rather than run without them, as for any skill with no rule. Blackout Seeker has a rule since r131.candidate.1 (R131),
        // Gas Bomber Drone since r136.candidate.1 (R136), Leech Hive since r145.candidate.1 (R145), and The Howl since
        // r147.candidate.1 (R147); the Trophy System Drone, once here, since r159.candidate.1 (R159), the Jammer Pulse since
        // r161.candidate.1 (R161), the Achilles Pulse since r163.candidate.1 (R163), and the Reviver Hive since r166.candidate.1 (R166).
        var pack = CommittedPack();
        foreach (var slot in new[] { "skill_slot_1", "skill_slot_2" })
        {
            var build = BuildSnapshot.Create(pack.Pack).WithActiveSkillSelection(slot, skill);
            var resolved = BuildResolver.Resolve(pack, build, Context);
            Assert.DoesNotContain(resolved.Diagnostics, diagnostic => diagnostic.Severity == "error");
            Assert.True(resolved.ActiveSkillPreviews[slot].Outputs.Single(entry => entry.Id == output).Value > 0);
        }
        Assert.DoesNotContain(pack.Pack.Simulation.Skills, rule => rule.Skill == skill);
        var selected = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr").WithActiveSkillSelection("skill_slot_1", skill);
        var context = Context.Add("enemy_target", "1").Add("enemy_difficulty", "1");
        var refusal = Assert.Throws<SimulationException>(() => DummyWeaponSimulator.RunAgainstTargets(pack, selected, context,
            new() { Duration = 5, IncludeSkills = true }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("skill-rule-required", refusal.Code);
        // Without them the same build fights.
        Assert.True(DummyWeaponSimulator.RunAgainstTargets(pack, selected, context, new() { Duration = 5 }, cancellationToken: TestContext.Current.CancellationToken).ShotsFired > 0);
    }

    [Fact]
    public void ASetTalentWhoseOnlyRuleIsAHeadshotCycleRunsInFights()
    {
        // R127: a slotless set talent joins fights at its set's top bonus when it has a rule, a headshot cycle included.
        var committed = CommittedPack().Pack;
        var pack = committed with { Simulation = committed.Simulation with { HeadshotCycles = [] } };
        var headache = pack.Talents.Single(talent => talent.ID == "gear_talent_headache");
        Assert.False(SetBonusSelection.RunsAtTopBonus(pack, headache));
        var cycled = pack with
        {
            Simulation = pack.Simulation with
            {
                HeadshotCycles = [new() { Talent = "gear_talent_headache", WeaponTag = "weapon_type_marksman_rifle", Key = "x", Repair = "x", BonusArmorCap = "x", Allowance = "x", KillFrom = 3 }]
            }
        };
        Assert.True(SetBonusSelection.RunsAtTopBonus(cycled, headache));
    }

    [Fact]
    public void CommittedHeadScratcherConfusesAfterItsKills()
    {
        // R132 (r132.candidate.1): Thunder Strike's shape with Confuse (disoriented) for 10 s after 4 kills, so its 30% now reads the
        // fight's own confusion where it read the user's setting.
        var pack = CommittedPack();
        const string Talent = "weapon_talent_head_scratcher";
        var rule = pack.Pack.Simulation.Triggers.Single(entry => entry.Talent == Talent);
        Assert.Equal(("target_confused", "kill", "status", "disoriented", 4L, 10.0), (rule.Key, rule.On, rule.Kind, rule.Status, rule.Count, rule.Duration));
        Assert.Equal(rule with { Talent = "weapon_talent_perfect_head_scratcher", Count = 3 },
            pack.Pack.Simulation.Triggers.Single(entry => entry.Talent == "weapon_talent_perfect_head_scratcher"));
        var item = pack.Pack.Items.First(entry => entry.TalentSlots.Any(slot => slot.Choices.Contains(Talent)));
        var build = Equip(pack.Pack, item.ID).WithTalentSelection(item.Slot, item.TalentSlots.First(slot => slot.Choices.Contains(Talent)).ID, Talent);
        var context = Context.SetItem(GamePack.WeaponSlotSelector, item.Slot).Add("practical_hit_rate", "90").Add("enemy_target", "3").Add("enemy_difficulty", "1");
        DummyFightResult Fight(CompiledPackExpressions fought) => DummyWeaponSimulator.RunAgainstTargets(fought, build, context,
            new() { Duration = 120, Mode = SimulationMode.Random, Iterations = 2, Seed = 1, DriveSimpleBuffs = true },
            cancellationToken: TestContext.Current.CancellationToken);
        var confused = Fight(pack);
        Assert.True(confused.TargetStatusUptime.TryGetValue("disoriented", out var uptime) && uptime.Seconds > 0, "no confusion");
        var without = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
        {
            Simulation = pack.Pack.Simulation with { Triggers = [.. pack.Pack.Simulation.Triggers.Where(entry => entry.Talent != Talent)] }
        }).Compiled);
        Assert.True(confused.WeaponDps.Mean > Fight(without).WeaponDps.Mean);
    }

    [Fact]
    public void CommittedBlackoutSeekerBlastsAndBlinds()
    {
        // R131 (r131.candidate.1): one blast per use, full within 1 m and half at the radius, and a blind on each enemy in it.
        var pack = CommittedPack();
        var rule = pack.Pack.Simulation.Skills.Single(entry => entry.Skill == "skill_blackout_seeker");
        Assert.Equal(("damage", "radius_meters", "area_full_meters", 0.5, "blinded", true),
            (rule.Impact, rule.Area, rule.AreaFull, rule.AreaEdge, rule.Status, rule.ControlStatus));
        var build = Equip(pack.Pack, "primary_weapon_acr").WithActiveSkillSelection("skill_slot_1", "skill_blackout_seeker");
        var fight = DummyWeaponSimulator.RunAgainstTargets(pack, build, Context.Add("enemy_target", "1").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "0"),
            new() { Duration = 60, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, IncludeSkills = true },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(fight.SkillDps!.Total.Mean > 0);
        Assert.True(fight.TargetStatusUptime.TryGetValue("blinded", out var blind) && blind.Seconds > 0, "no blind");
        // In a group 3 m apart the blast reaches a neighbour on the falloff line: full to 1 m, half at the radius.
        var radius = BuildResolver.Resolve(pack, build, Context).ActiveSkillPreviews["skill_slot_1"].Outputs.Single(entry => entry.Id == "radius_meters").Value;
        Assert.True(radius > 3, $"radius {radius}");
        var group = DummyWeaponSimulator.RunAgainstTargets(pack, build,
            Context.SetItem(GamePack.WeaponSlotSelector, "primary_weapon").Add("practical_hit_rate", "100").Add("enemy_target", "1").Add("enemy_difficulty", "4").Add("enemy_hit_rate", "0"),
            new() { Duration = 30, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, IncludeSkills = true, EnemiesAtOnce = 4, EnemySpacing = 3, CaptureFirstFightEvents = true },
            cancellationToken: TestContext.Current.CancellationToken);
        var impact = group.FirstFightEvents.First(entry => entry.Kind == DummyEventKind.SkillImpact);
        var area = group.FirstFightEvents.First(entry => entry.Kind == DummyEventKind.SkillAreaHit && entry.Time == impact.Time);
        Assert.Equal(1 - 0.5 * (3 - 1) / (radius - 1), area.Damage / impact.Damage, 3);
    }

    [Fact]
    public void AStaticValueGatedOutOfFightsLetsARulePlayIt()
    {
        // ADR 0023 stage 1, with one row and gate of our own in place of the pack's (r133 has its own, on kills and skill kills):
        // Salvage's static rounds-returned value requires fight_context static, and a kill rule refills in its place. Outside fights the value holds; driven fights set it aside and
        // run where they refused; a fight without talent triggers keeps it, and still refuses.
        var committed = CommittedPack();
        const string Salvage = "weapon_talent_salvage";
        var gate = ImmutableDictionary<string, string>.Empty.Add(GamePack.FightContextSelector, GamePack.FightContextStatic);
        var rules = committed.Pack with
        {
            Selectors = [.. committed.Pack.Selectors, new() { ID = "salvage_refill", Name = "Salvage refill", Values = ["true"], Section = "Talents and gear" }],
            Talents = [.. committed.Pack.Talents.Select(talent => talent.ID != Salvage ? talent : talent with
            {
                Modifiers = [.. talent.Modifiers.Select(modifier => modifier with { RequiresContext = gate })]
            })],
            Simulation = committed.Pack.Simulation with
            {
                Triggers = [.. committed.Pack.Simulation.Triggers.Where(rule => rule.Talent != Salvage),
                    new() { Talent = Salvage, Key = "salvage_refill", On = "kill", Kind = "buff", Duration = 0.001, Chance = 0.7, Refill = true }]
            }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var item = pack.Pack.Items.First(entry => entry.TalentSlots.Any(slot => slot.Choices.Contains(Salvage)));
        var build = Equip(pack.Pack, item.ID).WithTalentSelection(item.Slot, item.TalentSlots.First(slot => slot.Choices.Contains(Salvage)).ID, Salvage);
        var context = Context.SetItem(GamePack.WeaponSlotSelector, item.Slot).Add("enemy_target", "1").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "0");
        double Returned(ImmutableDictionary<string, string> at) => BuildResolver.Resolve(pack, build, at).Calculation.Stats.GetValueOrDefault("practical_rounds_returned_per_shot");
        Assert.True(Returned(context) > 0, "the static value holds outside fights");
        Assert.Equal(0, Returned(context.Add(GamePack.FightContextSelector, GamePack.FightContextFight)));
        DummyFightResult Fight(bool driven, CompiledPackExpressions? fought = null) => DummyWeaponSimulator.RunAgainstTargets(fought ?? pack, build,
            context.Add(GamePack.FightContextSelector, GamePack.FightContextFight),
            new() { Duration = 60, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = driven },
            cancellationToken: TestContext.Current.CancellationToken);
        var played = Fight(true);
        Assert.True(played.Kills > 0);
        // It's no input, so the result's coverage doesn't list it; and the static value it set aside isn't described as part of the fight.
        Assert.DoesNotContain(GamePack.FightContextSelector, played.Coverage.Inputs.Keys);
        bool Averaged(EffectCoverageReport report) => report.Talents.Single(row => row.Talent == Salvage).Channels.Any(channel => channel.Kind == EffectCoverage.ExpectedValueModifiers);
        Assert.False(Averaged(played.Coverage));
        Assert.True(Averaged(EffectCoverage.Describe(pack.Pack, build, context)));
        // The rule refills: the same gate with no rule reloads more.
        var unplayed = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules with
        {
            Simulation = rules.Simulation with { Triggers = [.. rules.Simulation.Triggers.Where(rule => rule.Talent != Salvage)] }
        }).Compiled);
        Assert.True(played.Reloads < Fight(true, unplayed).Reloads, $"reloads {played.Reloads} with the rule");
        // The user's setting doesn't reach a fight: an undriven one reads static, and the value still refuses there.
        Assert.Equal("shot-mechanic-unsupported", Assert.Throws<SimulationException>(() => Fight(false)).Code);
    }

    [Theory]
    [InlineData("weapon_talent_salvage")]
    [InlineData("weapon_talent_perfect_salvage")]
    [InlineData("weapon_talent_determined")]
    [InlineData("weapon_talent_perfect_determined")]
    [InlineData("gear_talent_headhunter")]
    [InlineData("gear_talent_perfect_headhunter")]
    public void CommittedShotMechanicTalentsPlayInDrivenFights(string talent)
    {
        // R133 (r133.candidate.1, ADR 0023): each static value is gated on fight_context: static, and a fight rule plays the mechanic.
        // Outside fights the value holds; a driven fight with targets runs where it refused; one without talent triggers still refuses.
        var pack = CommittedPack();
        var item = pack.Pack.Items.First(entry => entry.TalentSlots.Any(slot => slot.Choices.Contains(talent)));
        var weapon = pack.Pack.Slots.Single(slot => slot.ID == item.Slot).Weapon;
        var build = Equip(pack.Pack, weapon ? [item.ID] : [item.ID, "primary_weapon_acr"])
            .WithTalentSelection(item.Slot, item.TalentSlots.First(slot => slot.Choices.Contains(talent)).ID, talent);
        var context = Context.SetItem(GamePack.WeaponSlotSelector, weapon ? item.Slot : "primary_weapon").Add("practical_hit_rate", "90").Add("practical_headshot_share", "30")
            .Add("enemy_target", "3").Add("enemy_difficulty", "1");
        var stats = BuildResolver.Resolve(pack, build, context).Calculation.Stats;
        Assert.True(stats.GetValueOrDefault("practical_shot_bonus_damage") + stats.GetValueOrDefault("practical_rounds_returned_per_shot") > 0, "the static value");
        Assert.Contains(pack.Pack.Talents.Single(entry => entry.ID == talent).Modifiers,
            modifier => modifier.RequiresContext?.GetValueOrDefault(GamePack.FightContextSelector) == GamePack.FightContextStatic);
        // Long enough for a forced shot to save whole shots: a target dies at its health either way.
        var options = new DummyFightOptions { Duration = 120, Mode = SimulationMode.Random, Iterations = 5, Seed = 1, DriveSimpleBuffs = true };
        DummyFightResult Fight(bool driven) => DummyWeaponSimulator.RunAgainstTargets(pack, build, context, options with { DriveSimpleBuffs = driven },
            cancellationToken: TestContext.Current.CancellationToken);
        var played = Fight(true);
        Assert.True(played.Kills > 0);
        Assert.Equal("shot-mechanic-unsupported", Assert.Throws<SimulationException>(() => Fight(false)).Code);
        // The mechanic adds something: without the talent's rows, the same gated fight over the same draws deals less.
        var unplayed = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
        {
            Simulation = pack.Pack.Simulation with
            {
                Triggers = [.. pack.Pack.Simulation.Triggers.Where(rule => rule.Talent != talent)],
                NextHitBonuses = [.. pack.Pack.Simulation.NextHitBonuses.Where(rule => rule.Talent != talent)]
            }
        }).Compiled);
        var without = DummyWeaponSimulator.RunAgainstTargets(unplayed, build, context, options,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(played.WeaponDps.Mean > without.WeaponDps.Mean, $"{played.WeaponDps.Mean} against {without.WeaponDps.Mean}");
    }

    [Fact]
    public void ANextHitBonusAddsToTheHitAfterAHeadshotKill()
    {
        // ADR 0023 stage 3, with Headhunter's row and gate of our own until ShadeTech's: a headshot kill arms 125% of its killing pellet
        // as dealt, capped, for the next landed hit. The draws don't change, so the same fight deals more with it, and nothing with a
        // cap of 0.
        var committed = CommittedPack();
        const string Headhunter = "gear_talent_headhunter";
        var gate = ImmutableDictionary<string, string>.Empty.Add(GamePack.FightContextSelector, GamePack.FightContextStatic);
        var item = committed.Pack.Items.First(entry => entry.TalentSlots.Any(slot => slot.Choices.Contains(Headhunter)));
        var build = Equip(committed.Pack, [item.ID, "primary_weapon_acr"])
            .WithTalentSelection(item.Slot, item.TalentSlots.First(slot => slot.Choices.Contains(Headhunter)).ID, Headhunter);
        var context = Context.SetItem(GamePack.WeaponSlotSelector, "primary_weapon").Add("practical_hit_rate", "100").Add("practical_headshot_share", "40")
            .Add("enemy_target", "1").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "0");
        DummyFightResult Fight(string? cap)
        {
            var rules = committed.Pack with
            {
                Stats = [.. committed.Pack.Stats, new() { ID = "test_headhunter_cap", Formula = "0" }],
                Talents = [.. committed.Pack.Talents.Select(talent => talent.ID != Headhunter ? talent : talent with
                {
                    Modifiers = [.. talent.Modifiers.Select(modifier => modifier with { RequiresContext = gate })]
                })],
                Simulation = committed.Pack.Simulation with
                {
                    NextHitBonuses = cap is null ? [] : [new() { Talent = Headhunter, Share = 1.25, Cap = cap, Window = 30 }]
                }
            };
            var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
            return DummyWeaponSimulator.RunAgainstTargets(pack, build, context,
                new() { Duration = 60, Mode = SimulationMode.Random, Iterations = 2, Seed = 1, DriveSimpleBuffs = true },
                cancellationToken: TestContext.Current.CancellationToken);
        }
        var without = Fight(null);
        Assert.True(without.Kills > 0);
        var with = Fight("equipped_weapon_damage");
        Assert.True(with.WeaponDps.Mean > without.WeaponDps.Mean, $"the bonus: {with.WeaponDps.Mean} against {without.WeaponDps.Mean}, kills {with.Kills} against {without.Kills}; notes: {string.Join(" | ", with.Limitations.Where(note => note.Contains("headshot", StringComparison.Ordinal)))}");
        Assert.Contains(with.Limitations, note => note.Contains("of a headshot kill's killing pellet", StringComparison.Ordinal));
        Assert.Equal(without.WeaponDps.Mean, Fight("test_headhunter_cap").WeaponDps.Mean);
    }

    [Fact]
    public void CommittedGasBomberPoisonsInFightsWithSkills()
    {
        // R136 (r136.candidate.1, ADR 0024): Gas Bomber Drone's rule poisons the enemy being shot through its 8 s cloud, at poison_dps
        // (the per-tick damage at one tick per 0.5 s), and it runs where every fight with skills refused it. A poison skill needs the
        // poisoned statusDamage row, for its copies on other enemies: without it the pack is refused.
        var committed = CommittedPack();
        const string Skill = "skill_gas_bomber_drone";
        var rule = committed.Pack.Simulation.Skills.Single(entry => entry.Skill == Skill);
        Assert.True(rule.IsCloud);
        Assert.Equal(("poisoned", "poison_dps", "poison_duration_seconds"), (rule.Status, rule.StatusRate, rule.StatusWindow));
        var row = committed.Pack.Simulation.StatusDamage.Single(entry => entry.Status == "poisoned");
        Assert.Equal(("poison_tick_damage", 0.5), (row.Tick, row.Period));
        Assert.Contains(PackValidator.Validate(committed.Pack with
        {
            Simulation = committed.Pack.Simulation with { StatusDamage = [.. committed.Pack.Simulation.StatusDamage.Where(entry => entry.Status != "poisoned")] }
        }).Diagnostics, diagnostic => diagnostic.Code == "bad-sim-skill" && diagnostic.Path.EndsWith(".status", StringComparison.Ordinal));
        var build = Equip(committed.Pack, "primary_weapon_acr").WithActiveSkillSelection("skill_slot_1", Skill);
        var context = Context.Add("enemy_target", "1").Add("enemy_difficulty", "4").Add("enemy_hit_rate", "0");
        var fight = DummyWeaponSimulator.RunAgainstTargets(committed, build, context,
            new() { Duration = 60, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, IncludeSkills = true, CaptureFirstFightEvents = true },
            cancellationToken: TestContext.Current.CancellationToken);
        // The rule has no rate and no impact, so its skill damage is the poison's.
        Assert.True(fight.SkillDps!.Total.Mean > 0);
        // Each use poisons for its 8 s cloud and 5 s after, at no more than the build's poison_dps (armor and the enemy's health can only
        // lower it), and the next use comes a 30 s cooldown after the use.
        var rate = BuildResolver.Resolve(committed, build, context).ActiveSkillPreviews["skill_slot_1"].Outputs.Single(output => output.Id == "poison_dps").Value;
        Assert.True(rate > 0);
        var ticks = fight.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.SkillTick).ToArray();
        var first = ticks[0].Time;
        var early = ticks.Where(entry => entry.Time < first + 20).ToArray();
        Assert.All(early, entry => Assert.True(entry.Time <= first + 13.1, $"a tick at {entry.Time}"));
        Assert.True(early.Sum(entry => entry.Damage) <= rate * 13.1, $"{early.Sum(entry => entry.Damage)} against {rate}");
        Assert.DoesNotContain(ticks, entry => entry.Time > first + 13.1 && entry.Time < first + 29.9);
        Assert.Contains(ticks, entry => entry.Time >= first + 29.9);
    }

    [Fact]
    public void CommittedGasBomberCloudReachesEnemiesWithinItsRadius()
    {
        // ADR 0024 stage 2b on the committed rule: the cloud's poison goes on every other enemy within its 3 m of the enemy being shot
        // while it applies, and ticks there by the poisoned row, carrying the skill's rate. Neighbours 3 m apart are in it; without a
        // spacing nobody else is. An area with a rate and no status window isn't a cloud, and is refused.
        var pack = CommittedPack();
        const string Skill = "skill_gas_bomber_drone";
        var cloud = pack.Pack.Simulation.Skills.Single(entry => entry.Skill == Skill);
        Assert.Contains(PackValidator.Validate(pack.Pack with
        {
            Simulation = pack.Pack.Simulation with
            {
                Skills = [.. pack.Pack.Simulation.Skills.Select(entry => entry.Skill == Skill ? cloud with { Status = "", StatusRate = "", StatusWindow = "", Rate = "poison_dps" } : entry)]
            }
        }).Diagnostics, diagnostic => diagnostic.Code == "bad-sim-skill" && diagnostic.Path.EndsWith(".area", StringComparison.Ordinal));
        var build = Equip(pack.Pack, "primary_weapon_acr").WithActiveSkillSelection("skill_slot_1", Skill);
        var context = Context.Add("enemy_target", "1").Add("enemy_difficulty", "4").Add("enemy_hit_rate", "0");
        DummyFightResult Fight(double? spacing) => DummyWeaponSimulator.RunAgainstTargets(pack, build, context,
            new() { Duration = 30, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, IncludeSkills = true, EnemiesAtOnce = 4, EnemySpacing = spacing, CaptureFirstFightEvents = true },
            cancellationToken: TestContext.Current.CancellationToken);
        var spaced = Fight(3);
        Assert.Contains(spaced.FirstFightEvents, entry => entry.Kind == DummyEventKind.PoisonTick && entry.Damage > 0);
        Assert.True(spaced.StatusDps!.Mean > 0);
        Assert.Contains(spaced.Limitations, note => note.Contains("walk out of a cloud", StringComparison.Ordinal));
        var alone = Fight(null);
        Assert.DoesNotContain(alone.FirstFightEvents, entry => entry.Kind == DummyEventKind.PoisonTick);
        Assert.DoesNotContain(alone.Limitations, note => note.Contains("walk out of a cloud", StringComparison.Ordinal));
        // The enemy being shot takes the poison as skill damage, so the status damage is the neighbours' alone.
        Assert.True(spaced.StatusDps.Mean > (alone.StatusDps?.Mean ?? 0), $"{spaced.StatusDps.Mean} against {alone.StatusDps?.Mean}");
        // A neighbour keeps it no longer than the window's end plus one application: 8 s and 5 s from a use, within a tick.
        var ticks = spaced.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.PoisonTick).Select(entry => entry.Time).ToArray();
        var firstUse = spaced.FirstFightEvents.First(entry => entry.Kind == DummyEventKind.SkillTick).Time;
        Assert.All(ticks.Where(time => time < firstUse + 20), time => Assert.True(time <= firstUse + 13.1, $"a tick at {time}"));
        // An impact's area (the Burn Sticky Bomb's) isn't a cloud: its status goes on once per impact, and no cloud note.
        var bomb = DummyWeaponSimulator.RunAgainstTargets(pack, Equip(pack.Pack, "primary_weapon_acr").WithActiveSkillSelection("skill_slot_1", "skill_burn_sticky_bomb"), context,
            new() { Duration = 30, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, IncludeSkills = true, EnemiesAtOnce = 4, EnemySpacing = 3 },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(bomb.Limitations, note => note.Contains("walk out of a cloud", StringComparison.Ordinal));
        Assert.False(pack.Pack.Simulation.Skills.Single(rule => rule.Skill == "skill_artillery_turret").IsCloud);
    }

    [Fact]
    public void ATalentsPoisonTicksAsPoisonInStatusDamage()
    {
        // ADR 0024: a talent's status rule can put poison on, and the committed poisoned row (R136) ticks it as PoisonTick events in
        // status DPS. Sadist's kill rule, made to poison for the test.
        var committed = CommittedPack();
        const string Talent = "weapon_talent_sadist";
        var rules = committed.Pack with
        {
            Simulation = committed.Pack.Simulation with
            {
                Triggers = [.. committed.Pack.Simulation.Triggers.Select(rule => rule.Talent == Talent ? rule with { Status = "poisoned" } : rule)]
            }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var item = pack.Pack.Items.First(entry => entry.TalentSlots.Any(slot => slot.Choices.Contains(Talent)));
        var build = Equip(pack.Pack, item.ID).WithTalentSelection(item.Slot, item.TalentSlots.First(slot => slot.Choices.Contains(Talent)).ID, Talent);
        var fight = DummyWeaponSimulator.RunAgainstTargets(pack, build,
            Context.SetItem(GamePack.WeaponSlotSelector, item.Slot).Add("practical_hit_rate", "100").Add("enemy_target", "1").Add("enemy_difficulty", "1"),
            new() { Duration = 60, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, CaptureFirstFightEvents = true },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(fight.FirstFightEvents, entry => entry.Kind == DummyEventKind.PoisonTick && entry.Damage > 0);
        Assert.DoesNotContain(fight.FirstFightEvents, entry => entry.Kind == DummyEventKind.BleedTick);
        Assert.True(fight.StatusDps!.Mean > 0);
        Assert.True(fight.TargetStatusUptime["poisoned"].Seconds > 0);
    }

    [Theory]
    [InlineData("weapon_talent_overflowing", 3)]
    [InlineData("weapon_talent_perfect_overflowing", 2)]
    public void CommittedOverflowingDoublesTheMagazineEveryFewReloads(string talent, int every)
    {
        // ADR 0025, stage 1 with the committed row: the counted reload's start turns Overflowing on, and that reload loads the doubled
        // magazine; the next reload's start spends it, and that one loads the usual size. A driven fight used to refuse it
        // (buff-timing-unsupported), as did a rotation.
        var pack = CommittedPack();
        var item = pack.Pack.Items.First(entry => entry.TalentSlots.Any(slot => slot.Choices.Contains(talent)));
        var build = Equip(pack.Pack, item.ID).WithTalentSelection(item.Slot, item.TalentSlots.First(slot => slot.Choices.Contains(talent)).ID, talent);
        var context = Context.SetItem(GamePack.WeaponSlotSelector, item.Slot).Add("practical_hit_rate", "100").Add("enemy_target", "1").Add("enemy_difficulty", "4");
        var fight = DummyWeaponSimulator.RunAgainstTargets(pack, build, context,
            new() { Duration = 120, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, CaptureFirstFightEvents = true },
            cancellationToken: TestContext.Current.CancellationToken);
        // Shots between reload completions: each magazine's rounds.
        var magazines = new List<int>();
        var shots = 0;
        var loaded = false;
        foreach (var entry in fight.FirstFightEvents)
        {
            if (entry.Kind == DummyEventKind.Shot) { shots++; }
            else if (entry.Kind == DummyEventKind.ReloadCompleted)
            {
                if (loaded) { magazines.Add(shots); }
                (shots, loaded) = (0, true);
            }
        }
        Assert.True(magazines.Count >= 2 * every, $"{magazines.Count} magazines");
        var usual = magazines.Min();
        Assert.Contains(2 * usual, magazines);
        // magazines[i] is reload i + 1's, so the doubled magazine is each count-th reload's.
        for (var index = 0; index < magazines.Count; index++)
        {
            Assert.Equal((index + 1) % every == 0 ? 2 * usual : usual, magazines[index]);
        }
        Assert.True(fight.TimedTriggerUptime["overflowing_active"].Seconds > 0);
        // Under a rotation it runs too (ADR 0025 stage 2b): each weapon fills to its own state's magazine.
        var rotated = DummyWeaponSimulator.RunAgainstTargets(pack,
            build.WithEquipment("sidearm", pack.Pack.Items.First(entry => entry.Slot == "sidearm").ID), context,
            new() { Duration = 120, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, Rotation = new("sidearm"), CaptureFirstFightEvents = true },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(rotated.TimedTriggerUptime["overflowing_active"].Seconds > 0);
        // The Overflowing weapon's own magazines, between its reloads: the usual size, and twice it.
        var own = new List<int>();
        (shots, loaded) = (0, false);
        foreach (var entry in rotated.FirstFightEvents.Where(entry => entry.WeaponSlot == item.Slot))
        {
            if (entry.Kind == DummyEventKind.Shot) { shots++; }
            else if (entry.Kind == DummyEventKind.ReloadCompleted)
            {
                if (loaded) { own.Add(shots); }
                (shots, loaded) = (0, true);
            }
        }
        Assert.True(own.Count >= every, string.Join(", ", own));
        Assert.Equal(new[] { usual, 2 * usual }, own.Distinct().Order());
        // With a cover cycle under a rotation (after kills, so cover reloads top up), it runs.
        var covered = DummyWeaponSimulator.RunAgainstTargets(pack,
            build.WithEquipment("sidearm", pack.Pack.Items.First(entry => entry.Slot == "sidearm").ID), context.Add("enemy_hit_rate", "0"),
            new()
            {
                Duration = 120,
                Mode = SimulationMode.Random,
                Iterations = 1,
                Seed = 1,
                DriveSimpleBuffs = true,
                IncludeIncoming = true,
                Rotation = new("sidearm", WeaponSwapMode.AfterKill),
                PeekSeconds = 2,
                CoverSeconds = 1,
                PeekHitShare = 0.5
            },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(covered.ShotsFired > 0);
    }

    [Fact]
    public void CommittedPakhanStacksFromKillsAndSizesTheNextMagazine()
    {
        // R139 (r139.candidate.1): Pakhan's stacks come from kills with an LMG, up to 4, and a finished reload clears them after
        // loading the magazine they size (ADR 0025). A driven fight builds them, where they stayed at the setting, and the enlarged
        // magazines mean fewer reloads than with the rule taken away.
        const string Talent = "weapon_talent_pakhan";
        var committed = CommittedPack();
        var item = committed.Pack.Items.First(entry => entry.Slot == "primary_weapon" && entry.TalentSlots.Any(slot => slot.Choices.Contains(Talent)));
        var build = Equip(committed.Pack, item.ID).WithTalentSelection(item.Slot, item.TalentSlots.First(slot => slot.Choices.Contains(Talent)).ID, Talent);
        var context = Context.SetItem(GamePack.WeaponSlotSelector, item.Slot).Add("practical_hit_rate", "90").Add("practical_headshot_share", "30")
            .Add("enemy_target", "3").Add("enemy_difficulty", "1");
        var options = new DummyFightOptions { Duration = 120, Mode = SimulationMode.Random, Iterations = 2, Seed = 1, DriveSimpleBuffs = true };
        var driven = DummyWeaponSimulator.RunAgainstTargets(committed, build, context, options, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(driven.TimedTriggerUptime["pakhan_stacks"].Seconds > 0);
        var without = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(committed.Pack with
        {
            Simulation = committed.Pack.Simulation with { Triggers = [.. committed.Pack.Simulation.Triggers.Where(rule => rule.Talent != Talent)] }
        }).Compiled);
        var still = DummyWeaponSimulator.RunAgainstTargets(without, build, context, options, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(driven.Reloads < still.Reloads, $"{driven.Reloads} against {still.Reloads}");
    }

    [Fact]
    public void AnEmptyReloadIsReloadEmptyAndAPartialOneIsNot()
    {
        // R138: Overflowing counts only reloads from empty, and its committed row counts reload_empty since R139. reload_empty fires
        // after reload when the magazine was empty, so against the same row on reload, a fight where every reload is from empty plays
        // the same; with a cover cycle, whose reloads behind cover can start with rounds left, the partial ones don't count.
        const string Talent = "weapon_talent_overflowing";
        var committed = CommittedPack();
        var moved = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(committed.Pack with
        {
            Simulation = committed.Pack.Simulation with
            {
                Triggers = [.. committed.Pack.Simulation.Triggers.Select(rule => rule.Talent == Talent ? rule with { On = "reload" } : rule)]
            }
        }).Compiled);
        var item = committed.Pack.Items.First(entry => entry.TalentSlots.Any(slot => slot.Choices.Contains(Talent)));
        var build = Equip(committed.Pack, item.ID).WithTalentSelection(item.Slot, item.TalentSlots.First(slot => slot.Choices.Contains(Talent)).ID, Talent);
        var context = Context.SetItem(GamePack.WeaponSlotSelector, item.Slot).Add("practical_hit_rate", "100").Add("enemy_target", "1").Add("enemy_difficulty", "4")
            .Add("enemy_hit_rate", "0");
        double Uptime(CompiledPackExpressions pack, DummyFightOptions options) => DummyWeaponSimulator.RunAgainstTargets(pack, build, context, options,
            cancellationToken: TestContext.Current.CancellationToken).TimedTriggerUptime["overflowing_active"].Seconds;
        var open = new DummyFightOptions { Duration = 120, Mode = SimulationMode.Random, Iterations = 2, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true };
        Assert.Equal(Uptime(committed, open), Uptime(moved, open));
        var covered = open with { PeekSeconds = 2, CoverSeconds = 1, PeekHitShare = 0.5 };
        Assert.NotEqual(Uptime(committed, covered), Uptime(moved, covered));
    }

    [Fact]
    public void CommittedRestrainedNeverFiresInAFightWithoutAShield()
    {
        // Restrained's rule is on shield_break. A fight without a shield, whether the build has none or the fight leaves it out, has
        // none to break, so the rule never fires and the result names it, as skill events in a fight without skills; it was refused
        // (trigger-event-unsupported).
        const string Talent = "weapon_talent_restrained";
        var pack = CommittedPack();
        var item = pack.Pack.Items.First(entry => entry.TalentSlots.Any(slot => slot.Choices.Contains(Talent)));
        var build = Equip(pack.Pack, item.ID).WithTalentSelection(item.Slot, item.TalentSlots.First(slot => slot.Choices.Contains(Talent)).ID, Talent);
        var context = Context.SetItem(GamePack.WeaponSlotSelector, item.Slot).Add("practical_hit_rate", "90").Add("enemy_target", "1").Add("enemy_difficulty", "1");
        var options = new DummyFightOptions { Duration = 30, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true };
        var plain = DummyWeaponSimulator.RunAgainstTargets(pack, build, context, options, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(plain.ShotsFired > 0);
        Assert.Equal(0, plain.TimedTriggerUptime["restrained_active"].Seconds);
        Assert.Contains(plain.Limitations, note => note.StartsWith("This fight has no shield", StringComparison.Ordinal) && note.Contains("Restrained", StringComparison.Ordinal));
        var shielded = DummyWeaponSimulator.RunAgainstTargets(pack, build.WithActiveSkillSelection("skill_slot_1", "skill_striker_shield"), context, options,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, shielded.TimedTriggerUptime["restrained_active"].Seconds);
        Assert.Contains(shielded.Limitations, note => note.StartsWith("This fight has no shield", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("weapon_talent_close_personal")]
    [InlineData("weapon_talent_perfect_close_personal")]
    public void CommittedClosePersonalGainsOnKillsWithinItsRange(string talent)
    {
        // Close & Personal's row is a buff on kill within 7 m. Driven fights used to refuse the range (trigger-unsupported); a kill
        // now gains it where the enemy is within 7 m, not at 10 m, and with no fight distance the range is taken as met, with a note.
        var pack = CommittedPack();
        var item = pack.Pack.Items.First(entry => entry.TalentSlots.Any(slot => slot.Choices.Contains(talent)));
        var build = Equip(pack.Pack, item.ID).WithTalentSelection(item.Slot, item.TalentSlots.First(slot => slot.Choices.Contains(talent)).ID, talent);
        var context = Context.SetItem(GamePack.WeaponSlotSelector, item.Slot).Add("practical_hit_rate", "90").Add("practical_headshot_share", "30")
            .Add("enemy_target", "1").Add("enemy_difficulty", "1");
        DummyFightResult Fight(double? distance) => DummyWeaponSimulator.RunAgainstTargets(pack, build, context,
            new() { Duration = 60, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, Distance = distance },
            cancellationToken: TestContext.Current.CancellationToken);
        const string Note = "a talent rule's range";
        var near = Fight(5);
        Assert.True(near.Kills > 0);
        Assert.True(near.TimedTriggerUptime["close_personal_active"].Seconds > 0);
        Assert.DoesNotContain(near.Limitations, note => note.Contains(Note, StringComparison.Ordinal));
        var far = Fight(10);
        Assert.True(far.Kills > 0);
        Assert.Equal(0, far.TimedTriggerUptime["close_personal_active"].Seconds);
        var anywhere = Fight(null);
        Assert.True(anywhere.TimedTriggerUptime["close_personal_active"].Seconds > 0);
        Assert.Contains(anywhere.Limitations, note => note.Contains(Note, StringComparison.Ordinal));
    }

    [Fact]
    public void CommittedDeterminedAndItsPerfectRotateOnTheirOwnKeys()
    {
        // R135 (r135.candidate.1): Perfect Determined arms its own key, as each talent's script holds its own flag, so a rotation with
        // both runs where r133's one shared key refused it (trigger-key-conflict). Each weapon's headshot kill arms only its own talent.
        var pack = CommittedPack();
        var rules = pack.Pack.Simulation.Triggers.Where(rule => rule.Talent is "weapon_talent_determined" or "weapon_talent_perfect_determined").ToArray();
        Assert.Equal(["determined_armed", "perfect_determined_armed"], rules.Select(rule => rule.Key).Order(StringComparer.Ordinal));
        var perfect = pack.Pack.Items.First(entry => entry.Slot == "primary_weapon" && entry.TalentSlots.Any(slot => slot.Choices.Contains("weapon_talent_perfect_determined")));
        var plain = pack.Pack.Items.First(entry => entry.Slot == "sidearm" && entry.TalentSlots.Any(slot => slot.Choices.Contains("weapon_talent_determined")));
        var build = Equip(pack.Pack, perfect.ID, plain.ID)
            .WithTalentSelection(perfect.Slot, perfect.TalentSlots.First(slot => slot.Choices.Contains("weapon_talent_perfect_determined")).ID, "weapon_talent_perfect_determined")
            .WithTalentSelection(plain.Slot, plain.TalentSlots.First(slot => slot.Choices.Contains("weapon_talent_determined")).ID, "weapon_talent_determined");
        var context = Context.SetItem(GamePack.WeaponSlotSelector, "primary_weapon").Add("practical_hit_rate", "90").Add("practical_headshot_share", "30")
            .Add("enemy_target", "3").Add("enemy_difficulty", "1");
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, context,
            new() { Duration = 120, Mode = SimulationMode.Random, Iterations = 2, Seed = 1, DriveSimpleBuffs = true, Rotation = new("sidearm") },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Kills > 0);
        // Each key is armed for some of the fight, by its own weapon's headshot kills.
        Assert.True(result.TimedTriggerUptime["determined_armed"].Seconds > 0, "Determined armed");
        Assert.True(result.TimedTriggerUptime["perfect_determined_armed"].Seconds > 0, "Perfect Determined armed");
    }

    [Fact]
    public void AForcedShotCanTakeTheWholeShotAndACrit()
    {
        // ADR 0023 stage 2, in Determined's shape on a shotgun talent of our own choosing (no shotgun offers Determined, and the shape is
        // generic): a headshot kill arms one stack, and the next hit spends it as a headshot. By the owner's call every landed pellet of
        // that shot is forced, and Perfect Determined's also crits. The random draws don't change, so the same fight deals more with each.
        var committed = CommittedPack();
        var ruled = committed.Pack.Simulation.RuleTalents.ToHashSet(StringComparer.Ordinal);
        var item = committed.Pack.Items.First(entry => entry.Tags.Contains("weapon_type_shotgun") && entry.TalentSlots.Any(slot => slot.Choices.Any(Plain)));
        bool Plain(string id) => !ruled.Contains(id) && committed.Pack.Talents.Single(talent => talent.ID == id).Modifiers.All(modifier => modifier.Value.Formula is null || !modifier.Value.Formula.Contains("practical_", StringComparison.Ordinal));
        var slot = item.TalentSlots.First(entry => entry.Choices.Any(Plain));
        var Determined = slot.Choices.First(Plain);
        var build = Equip(committed.Pack, item.ID).WithTalentSelection(item.Slot, slot.ID, Determined);
        var context = Context.SetItem(GamePack.WeaponSlotSelector, item.Slot).Add("practical_hit_rate", "100").Add("practical_headshot_share", "20")
            .Add("enemy_target", "1").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "0");
        DummyFightResult Fight(bool whole, bool critical)
        {
            var rules = committed.Pack with
            {
                Simulation = committed.Pack.Simulation with
                {
                    Triggers = [.. committed.Pack.Simulation.Triggers, new()
                    {
                        Talent = Determined, Key = "determined_armed", On = "headshot_kill", Kind = "stacks", Max = 1, ConsumeOn = "hit",
                        ForceHeadshot = true, ForceWholeShot = whole, ForceCritical = critical
                    }]
                }
            };
            var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
            return DummyWeaponSimulator.RunAgainstTargets(pack, build, context,
                new() { Duration = 60, Mode = SimulationMode.Random, Iterations = 2, Seed = 1, DriveSimpleBuffs = true },
                cancellationToken: TestContext.Current.CancellationToken);
        }
        var first = Fight(false, false);
        Assert.True(first.Kills > 0);
        Assert.True(Fight(true, false).WeaponDps.Mean > first.WeaponDps.Mean, "the whole shot");
        Assert.True(Fight(false, true).WeaponDps.Mean > first.WeaponDps.Mean, "the crit");
    }

    [Fact]
    public void ASkillMarkedAsideStepsAsideInFightsWithSkills()
    {
        // ADR 0022: a skill the pack marks as playing no part in a fight is never used, so a fight with skills runs without it rather
        // than refusing the build, and a use condition on its slot is dropped. A skill with a rule beside it still fights.
        var committed = CommittedPack();
        var rules = committed.Pack with
        {
            Simulation = committed.Pack.Simulation with { SkillsAside = [new() { Skill = "skill_striker_drone", Why = "it waits on a rate" }] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "primary_weapon_acr").WithActiveSkillSelection("skill_slot_1", "skill_striker_drone");
        var context = Context.Add("enemy_target", "1").Add("enemy_difficulty", "1");
        var later = ImmutableDictionary<string, SkillUseCondition>.Empty.Add("skill_slot_1", new() { When = SkillUseCondition.AfterSeconds, Seconds = 2 });
        DummyFightResult Run(BuildSnapshot fighting, DummyFightOptions options) => DummyWeaponSimulator.RunAgainstTargets(pack, fighting, context, options,
            cancellationToken: TestContext.Current.CancellationToken);
        var alone = Run(build, new() { Duration = 5, IncludeSkills = true, SkillUse = later });
        Assert.Equal(Run(build, new() { Duration = 5 }).TotalDps, alone.TotalDps);
        Assert.DoesNotContain(alone.FirstFightEvents, e => e.Kind is DummyEventKind.SkillImpact or DummyEventKind.SkillTick);
        Assert.Contains("Run without skills that play no part in fights: Striker Drone (it waits on a rate). Use conditions dropped for: skill_slot_1",
            alone.Limitations);
        // Beside a turret, which has a rule, the turret fights and its own condition stays.
        var paired = build.WithActiveSkillSelection("skill_slot_2", "skill_assault_turret");
        var turret = Run(paired, new() { Duration = 5, IncludeSkills = true, SkillUse = later.Add("skill_slot_2", new() { When = SkillUseCondition.AfterSeconds, Seconds = 1 }) });
        Assert.True(turret.SkillDps!.Total.Mean > 0);
        Assert.Contains(turret.Limitations, note => note.StartsWith("Assumed skill use: skill_slot_2 after 1 s.", StringComparison.Ordinal));
        // With only that skill, a talent reacting to a skill's kill never does, and the fight says so.
        var preserving = Run(WithTalent(rules, build, "weapon_talent_perfect_preservation"), new()
        {
            Duration = 5,
            Mode = SimulationMode.Random,
            Iterations = 1,
            Seed = 1,
            DriveSimpleBuffs = true,
            IncludeIncoming = true,
            IncludeSkills = true
        });
        Assert.Contains(preserving.Limitations, note => note.StartsWith("No skill in this fight deals damage, so these never react to a skill's hit or kill", StringComparison.Ordinal) &&
            note.Contains("Perfect Preservation", StringComparison.Ordinal));
        Assert.DoesNotContain(turret.Limitations, note => note.StartsWith("No skill in this fight deals damage", StringComparison.Ordinal));
        // Without the mark the build is still refused, as before.
        Assert.Equal("skill-rule-required", Assert.Throws<SimulationException>(() => DummyWeaponSimulator.RunAgainstTargets(committed, BuildSnapshot.Create(committed.Pack)
            .WithEquipment("primary_weapon", "primary_weapon_acr").WithActiveSkillSelection("skill_slot_1", "skill_striker_drone"), context,
            new() { Duration = 5, IncludeSkills = true }, cancellationToken: TestContext.Current.CancellationToken)).Code);
    }

    [Fact]
    public void AGatedSkillStepsAsideUntilItsSettingIsOn()
    {
        // ADR 0027: a skill rule gated on a setting (as the Firefly's "Target has a weakpoint or is a mech" is) steps aside while the
        // setting is off, so the build runs, and fights as an ungated rule does once it's on. Shown here on the Sticky Bomb with a test gate.
        var committed = CommittedPack();
        const string Gate = "target_has_weakpoint";
        var rules = committed.Pack with
        {
            Selectors = [.. committed.Pack.Selectors, new() { ID = Gate, Name = "Target has a weakpoint", Values = ["true"], Section = "Enemy target" }],
            Simulation = committed.Pack.Simulation with
            {
                Skills = [.. committed.Pack.Simulation.Skills.Select(rule => rule.Skill != "skill_explosive_sticky_bomb" ? rule :
                    rule with { RequiresContext = ImmutableDictionary<string, string>.Empty.Add(Gate, "true") })]
            }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "primary_weapon_acr").WithActiveSkillSelection("skill_slot_1", "skill_explosive_sticky_bomb");
        var context = Context.Add("enemy_target", "1").Add("enemy_difficulty", "1");
        var later = ImmutableDictionary<string, SkillUseCondition>.Empty.Add("skill_slot_1", new() { When = SkillUseCondition.AfterSeconds, Seconds = 2 });
        DummyFightResult Run(CompiledPackExpressions fighting, ImmutableDictionary<string, string> at, DummyFightOptions options) =>
            DummyWeaponSimulator.RunAgainstTargets(fighting, build, at, options, cancellationToken: TestContext.Current.CancellationToken);
        var off = Run(pack, context, new() { Duration = 10, IncludeSkills = true, SkillUse = later });
        Assert.Equal(Run(pack, context, new() { Duration = 10 }).TotalDps, off.TotalDps);
        Assert.Contains("Run without skills whose setting is off: Explosive Sticky Bomb (needs Target has a weakpoint set). Use conditions dropped for: skill_slot_1",
            off.Limitations);
        Assert.DoesNotContain(off.Limitations, note => note.StartsWith("Run without skills that play no part", StringComparison.Ordinal));
        // On, it fights exactly as without the gate.
        var on = Run(pack, context.Add(Gate, "true"), new() { Duration = 10, IncludeSkills = true });
        Assert.True(on.SkillDps!.Total.Mean > 0);
        Assert.Equal(Run(committed, context, new() { Duration = 10, IncludeSkills = true }).TotalDps, on.TotalDps);
        Assert.DoesNotContain(on.Limitations, note => note.StartsWith("Run without skills", StringComparison.Ordinal));
        // In a group with a spacing, a gated-off area skill reaches nobody: the fight is the one without skills, and no area note.
        var group = new DummyFightOptions { Duration = 10, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, EnemiesAtOnce = 3, EnemySpacing = 3 };
        var grouped = Run(pack, context, group with { IncludeSkills = true });
        Assert.Equal(Run(pack, context, group).TotalDps, grouped.TotalDps);
        Assert.DoesNotContain(grouped.Limitations, note => note.StartsWith("Skills with an area", StringComparison.Ordinal));
        Assert.Contains(Run(pack, context.Add(Gate, "true"), group with { IncludeSkills = true }).Limitations,
            note => note.StartsWith("Skills with an area", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("skill_scanner_pulse")]
    [InlineData("skill_remote_pulse")]
    [InlineData("skill_tactician_drone")]
    public void CommittedMarkingSkillsStepAsideBesideATurret(string skill)
    {
        // R128 (r128.candidate.1): these only mark enemies, which the "Target is pulsed" input covers, so a fight with skills runs
        // without them where it refused the build. Beside an Assault Turret the fight is the turret's alone, as ShadeTech's figures show.
        var pack = CommittedPack();
        var why = pack.Pack.Simulation.SkillsAside.Single(entry => entry.Skill == skill).Why;
        var context = Context.Add("practical_hit_rate", "90").Add("practical_headshot_share", "30").Add("enemy_target", "3").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "0");
        var turret = Equip(pack.Pack, "primary_weapon_acr").WithActiveSkillSelection("skill_slot_1", "skill_assault_turret");
        DummyFightResult Fight(BuildSnapshot build) => DummyWeaponSimulator.RunAgainstTargets(pack, build, context,
            new() { Duration = 30, Mode = SimulationMode.Random, Iterations = 2, Seed = 1, DriveSimpleBuffs = true, IncludeIncoming = true, IncludeSkills = true },
            cancellationToken: TestContext.Current.CancellationToken);
        var alone = Fight(turret);
        var paired = Fight(turret.WithActiveSkillSelection("skill_slot_2", skill));
        Assert.True(paired.SkillDps!.Total.Mean > 0);
        Assert.Equal(alone.SkillDps!.Total.Mean, paired.SkillDps.Total.Mean);
        Assert.Equal(alone.WeaponDps.Mean, paired.WeaponDps.Mean);
        Assert.Equal(alone.Kills, paired.Kills);
        Assert.Contains(paired.Limitations, note => note.StartsWith("Run without skills that play no part in fights: ", StringComparison.Ordinal)
            && note.Contains("(" + why + ")", StringComparison.Ordinal));
    }

    [Fact]
    public void DistanceRulesWorkWithTargetDistancesUnderIncomingFire()
    {
        // Ranger reads the fight distance. With target distances it reads each target's, and enemy fire follows that distance too, so
        // the two agree.
        var pack = CommittedPack();
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr").WithTalentSelection("primary_weapon", "talent", "weapon_talent_ranger");
        var context = Context.Add("practical_hit_rate", "100").Add("enemy_target", "1").Add("enemy_difficulty", "1");
        DummyFightResult Run(bool incoming) => DummyWeaponSimulator.RunAgainstTargets(pack, build, context, new()
        {
            Duration = 5,
            Mode = SimulationMode.Random,
            Iterations = 1,
            DriveSimpleBuffs = true,
            IncludeIncoming = incoming,
            TargetDistances = [10, 40]
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("Enemy Hit Rate (%) follows each target's distance: 70 at 10 m, 30 at 40 m", Run(true).Limitations);
        Assert.Contains(Run(false).Limitations, note => note.StartsWith("Targets cycle through 2 distances", StringComparison.Ordinal));
    }

    [Fact]
    public void CommittedFalloffFloorsMatchTheSourceAtADistance()
    {
        // The falloff-floor candidate (R42): each weapon's myDamageMin / myDamageMax. ShadeTech's probe: the ACR's optimal
        // range is 31 m and its long range 105 m, so at 68 m, halfway, a shot deals 1 - 0.5 x (1 - 0.518838) of full damage.
        var pack = CommittedPack();
        var context = Context.Add("practical_hit_rate", "100");
        Assert.Equal(0.40032, BuildResolver.Resolve(pack, BuildSnapshot.Create(pack.Pack).WithEquipment("sidearm", "sidearm_quickstep"),
            context.SetItem(GamePack.WeaponSlotSelector, "sidearm")).Calculation.Stats["weapon_falloff_floor"], 9);
        double[] Shots(string item, double distance) => [.. DummyWeaponSimulator.Run(pack, BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", item), context, new()
        {
            Duration = 10, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, CaptureFirstFightEvents = true, Distance = distance
        }, TestContext.Current.CancellationToken).FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.Shot).Select(entry => entry.Damage)];
        var (close, middle, beyond) = (Shots("primary_weapon_acr", 0), Shots("primary_weapon_acr", 68), Shots("primary_weapon_acr", 106));
        Assert.NotEmpty(close);
        Assert.Equal(close.Select(shot => shot * (1 - 0.5 * (1 - 0.518838))), middle, (a, b) => Math.Abs(a - b) < 1e-6 * Math.Max(1, a));
        // Past the ACR's maximum range (105 m) nothing lands.
        Assert.All(beyond, shot => Assert.Equal(0, shot));
        // A weapon with no ranges and no floor of its own has the default 1: it fights at range without falloff. The pack's last such
        // weapons got their ranges in R168, so here the ACR loses its own.
        pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
        {
            Items = [.. pack.Pack.Items.Select(item => item.ID != "primary_weapon_acr" ? item
                : item with { Weapon = [.. item.Weapon.Where(stat => stat.Stat is not ("weapon_long_range" or "weapon_max_range" or "weapon_falloff_floor"))] })]
        }).Compiled);
        Assert.Equal(close, Shots("primary_weapon_acr", 300));
    }

    [Fact]
    public void CommittedToxicDeliveryTicksAShareOfItsTotalOnEachStatus()
    {
        // The Toxic Delivery candidate (R38): each status the player applies starts a 10 s debuff whose total is split into 20
        // ticks (ShadeTech's probe: 35,172 a tick, 703,440 / 20, with Trauma supplying the statuses at difficulty 4).
        var pack = CommittedPack();
        var rule = Assert.Single(pack.Pack.Simulation.TalentDamage, candidate => candidate.Talent == "gear_talent_toxic_delivery");
        Assert.Equal(("status_applied", "toxic_delivery_total_damage", 10.0, 0.5), (rule.On, rule.Total, rule.Duration, rule.Period));
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr")
            .WithEquipment("mask", "mask_vile").WithTalentSelection("mask", "talent", "gear_talent_toxic_delivery")
            .WithEquipment("chest", "chest_n_5_11_tactical_chest").WithTalentSelection("chest", "talent", "gear_talent_trauma");
        var context = Context.Add("practical_hit_rate", "100").Add("practical_headshot_share", "20").Add("enemy_target", "1").Add("enemy_difficulty", "4");
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, context,
            new() { Duration = 120, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, CaptureFirstFightEvents = true },
            cancellationToken: TestContext.Current.CancellationToken);
        var total = BuildResolver.Resolve(pack, build, context).Calculation.Stats["toxic_delivery_total_damage"];
        var ticks = result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.TalentTick).ToArray();
        Assert.NotEmpty(ticks);
        // A tick on a nearly dead target deals only what is left, so the full tick is the largest.
        Assert.Equal(total / 20, ticks.Max(entry => entry.Damage), 3);
        Assert.True(result.TalentDps!.Mean > 0);
    }

    // Abridged (the Tinkerer mask) copies the secondary weapon's talent onto the primary when both are the same ordinary type.
    private static BuildSnapshot AbridgedCopying(GamePack pack, string talent) => Equip(pack, "mask_tinkerer")
        .WithEquipment("primary_weapon", "primary_weapon_acr").WithEquipment("secondary_weapon", "secondary_weapon_acr")
        .WithTalentSelection("secondary_weapon", "talent", talent);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbridgedCopyingATalentWithoutCombatRulesSimulates(bool rotation)
    {
        // Near Sighted has stat modifiers only, which Core copies for the weapon in hand; driven simulations used to refuse any
        // build with Abridged.
        var pack = CommittedPack();
        var build = AbridgedCopying(pack.Pack, "weapon_talent_near_sighted");
        var copied = BuildResolver.Resolve(pack, build, Context).Calculation.Stats["stability"];
        Assert.True(copied > BuildResolver.Resolve(pack, build.WithTalentSelection("secondary_weapon", "talent", ""), Context).Calculation.Stats["stability"]);
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, Context.Add("practical_hit_rate", "100").Add("enemy_target", "1"),
            new() { Duration = 30, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, Rotation = rotation ? new RotationOptions("secondary_weapon") : null },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.WeaponDps.Mean > 0);
    }

    [Fact]
    public void AbridgedCopyingATalentWithCombatRulesIsStillRefused()
    {
        // Strained has its own stack rules, which would need copied trigger ownership.
        var pack = CommittedPack();
        var error = Assert.Throws<SimulationException>(() => DummyWeaponSimulator.RunAgainstTargets(pack, AbridgedCopying(pack.Pack, "weapon_talent_strained"),
            Context.Add("practical_hit_rate", "100").Add("enemy_target", "1"),
            new() { Duration = 30, Mode = SimulationMode.Random, Iterations = 1, DriveSimpleBuffs = true }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("buff-copy-unsupported", error.Code);
    }

    [Fact]
    public void CommittedTalentsCarryTheR35SourceAudit()
    {
        var pack = CommittedPack();
        Assert.All(pack.Pack.Talents, talent => Assert.NotNull(talent.Coverage));
        Assert.Contains(PackCapabilities.TalentCoverage, pack.Pack.Capabilities);
        var build = Equip(pack.Pack, "backpack_backbone");
        var row = EffectCoverage.Describe(pack.Pack, build, Context).Talents.Single(talent => talent.Talent == "gear_talent_perfect_unstoppable_force");
        Assert.Equal("partial", row.AuditStatus);
        Assert.True(row.Modeled);
        Assert.Contains("Leaves out: grenade kills add 2 stacks", row.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void CommittedExoticGearLimitAllowsOne()
    {
        var pack = CommittedPack();
        var context = Context.Add("enemy_target", "1");
        var one = BuildResolver.Resolve(pack, Equip(pack.Pack, "primary_weapon_acr", "mask_vile"), context);
        Assert.DoesNotContain(one.Diagnostics, diagnostic => LoadoutResolver.IsEquipmentLimitError(diagnostic));
        var two = BuildResolver.Resolve(pack, Equip(pack.Pack, "primary_weapon_acr", "mask_vile", "chest_collector"), context);
        Assert.Equal(["chest", "mask"], two.Diagnostics.Where(LoadoutResolver.IsEquipmentLimitError).Select(diagnostic => diagnostic.Subject).Order(StringComparer.Ordinal));
        Assert.All(two.Diagnostics.Where(LoadoutResolver.IsEquipmentLimitError), diagnostic => Assert.Equal("exotic_gear", diagnostic.Target));
    }
}
