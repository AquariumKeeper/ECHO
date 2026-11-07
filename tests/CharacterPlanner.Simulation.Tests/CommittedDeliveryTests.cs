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
        var (committed, before) = Refreshed(build, "outsider_active", trigger => trigger with { Every = 0 });
        Assert.True(before.Fraction > 0.95, $"{before.Fraction}");
        Assert.InRange(committed.Fraction, 0.5, 0.9);
    }

    [Fact]
    public void CommittedDragonsGlareBurnsTheTargetOnItsCooldown()
    {
        // The Dragon's Glare candidate (R36): the default burn on the current target every 35 s, on whole seconds.
        var pack = CommittedPack();
        var glare = Assert.Single(pack.Pack.Simulation.Triggers, trigger => trigger.Talent == "gear_talent_dragon_s_glare");
        Assert.Equal(("second", "burning", 7.0, 35.0), (glare.On, glare.Status, glare.Duration, glare.Every));
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
        // A stack per pellet, each worth a pellet's damage: as many stacks as a shot has pellets make one shot's weapon damage.
        var pack = CommittedPack();
        var rule = Assert.Single(pack.Pack.Simulation.Triggers, candidate => candidate.Talent == "gear_talent_afterburn");
        Assert.Equal("hit_pellet", rule.On);
        var stats = BuildResolver.Resolve(pack, Equip(pack.Pack, "primary_weapon_acs_12"), Context.Add("afterburn_stacks", "8")).Calculation.Stats;
        Assert.Equal(8, stats["equipped_weapon_pellets"]);
        Assert.Equal(stats["equipped_weapon_damage"], stats["afterburn_detonation_damage"], 6);
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
        Assert.Equal(("armor_kit", "temporary_armor", 0.3, 10d), (rule.On, rule.Grant, rule.GrantAmount, rule.GrantDuration));
    }

    [Theory]
    // Incinerator Turret's rate became its burn's statusRate (R68), and its live read went with it.
    [InlineData("skill_incinerator_turret", "")]
    [InlineData("skill_stinger_hive", "live")]
    [InlineData("skill_assault_turret", "")]
    [InlineData("skill_oxidizer_chem_launcher", "")]
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
        // Their Overcharge rows share `future_perfect_overcharge` (R73). Their Skill Tier stacks share a key too, which may have one owner,
        // so a build with both doesn't fight. The source never has both active (R73), so this blocks no build the game allows.
        var pack = CommittedPack();
        var build = SixTierCores(pack.Pack, "skill_stinger_hive").WithTalentSelection("primary_weapon", "talent", "weapon_talent_future_perfect")
            .WithEquipment("secondary_weapon", "secondary_weapon_mechanical_animal").WithTalentSelection("secondary_weapon", "talent", "weapon_talent_future_perfection");
        var refused = Assert.Throws<SimulationException>(() => DummyWeaponSimulator.RunAgainstTargets(pack, build, Context.Add("enemy_target", "1").Add("enemy_difficulty", "1"),
            new() { Duration = 10, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true, Rotation = new("secondary_weapon") },
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("one owner rule", refused.Message, StringComparison.Ordinal);
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
    [InlineData("skill_leech_hive", "damage")]
    [InlineData("skill_the_howl", "armor_damage")]
    [InlineData("skill_blackout_seeker", "damage")]
    [InlineData("skill_corrosive_trap", "damage")]
    [InlineData("skill_gas_bomber_drone", "damage")]
    [InlineData("skill_trophy_system_drone", "cooldown_seconds")]
    public void CommittedY8S33SkillsPreviewButDontFight(string skill, string output)
    {
        // The Y8S3.3 skills are previews only: they resolve in both slots, and a fight with damaging skills is refused rather than
        // run without them, as for any skill with no rule.
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
        // The Cooler has no source identity, so its floor is the default 1: it fights at range without falloff.
        Assert.Equal(Shots("primary_weapon_cooler", 0), Shots("primary_weapon_cooler", 300));
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
