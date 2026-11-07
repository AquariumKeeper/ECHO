using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

public sealed partial class DummyWeaponSimulatorTests
{
    private static (CompiledPackExpressions Pack, BuildSnapshot Build) RotatingBuff(string on = "hit", bool gear = false,
        string owner = "primary_weapon", double duration = 100,
        Func<GamePack, GamePack>? change = null)
    {
        var (original, _) = BuffBuild(on: on);
        var rules = original.Pack with
        {
            Slots = [.. original.Pack.Slots, new() { ID = "chest", Name = "Chest" }],
            Items = [.. original.Pack.Items.Select(item => item with { TalentSlots = [] }), new() { ID = "chest", Name = "Chest", Slot = "chest" }],
            Simulation = new() { Triggers = [original.Pack.Simulation.Triggers[0] with { Duration = duration }] }
        };
        var talentSlot = gear ? "chest" : owner;
        rules = rules with
        {
            Items = [.. rules.Items.Select(item => item.Slot == talentSlot ? item with { TalentSlots = [new() { ID = "talent", Choices = ["test_buff"] }] } : item)]
        };
        if (change is not null) { rules = change(rules); }
        var compiled = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithEquipment("secondary_weapon", "other")
            .WithEquipment("chest", "chest").WithTalentSelection(talentSlot, "talent", "test_buff");
        return (compiled, build);
    }

    private static DummyFightOptions DrivenRotation(double duration = 7) => new()
    {
        Duration = duration,
        Mode = SimulationMode.Random,
        Iterations = 1,
        DriveSimpleBuffs = true,
        CaptureFirstFightEvents = true,
        Rotation = new("secondary_weapon")
    };

    private static (CompiledPackExpressions Pack, BuildSnapshot Build) RotatingMagazines(double rounds) => RotatingBuff(change: rules => rules with
    {
        Stats = [.. rules.Stats.Select(stat => stat.ID == "equipped_weapon_magazine_size" ? stat with { Formula = rounds.ToString(System.Globalization.CultureInfo.InvariantCulture) } : stat)]
    });

    // Swapping after kills against a target that outlives the fight: no swap comes, and cover tops up the weapon in hand.
    private static DummyFightResult RunToppingUp((CompiledPackExpressions Pack, BuildSnapshot Build) fixture, double duration, double peek, double cover)
    {
        var rules = fixture.Pack.Pack with { Stats = [.. fixture.Pack.Pack.Stats.Select(stat => stat.ID == "enemy_target_health" ? stat with { Formula = "100000" } : stat)] };
        return DummyWeaponSimulator.RunAgainstTargets(Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled), fixture.Build, Context, DrivenRotation(duration) with
        {
            Rotation = new("secondary_weapon", WeaponSwapMode.AfterKill),
            PeekSeconds = peek,
            CoverSeconds = cover
        }, retargetDelay: 0, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static double[] Times(DummyFightResult result, DummyEventKind kind) => [.. result.FirstFightEvents.Where(entry => entry.Kind == kind).Select(entry => entry.Time)];

    [Fact]
    public void AnEmptyWeaponsSwapStartsAtTheCoverBoundaryAndCarriesOn()
    {
        // ADR 0009: three rounds, a shot a second, 1 s swaps; 2.5 s peeks and 2 s behind cover. The shot at 2 s empties the rifle,
        // so its swap starts at the boundary (2.5 s, not 3 s) and ends at 3.5 s behind cover. The sidearm is full and waits for
        // the next peek. Its last round at 6.5 s swaps at the next boundary (7 s), and the empty rifle reloads on arrival.
        var (pack, build) = RotatingMagazines(3);
        var result = Run(pack, build, Context, DrivenRotation(9.5) with { PeekSeconds = 2.5, CoverSeconds = 2, PeekHitShare = 1 });
        Assert.Equal(new[] { 0, 1, 2, 4.5, 5.5, 6.5 }, Times(result, DummyEventKind.Shot));
        Assert.Equal(new[] { 2.5, 7 }, Times(result, DummyEventKind.SwapStarted));
        Assert.Equal(new double[] { 8 }, Times(result, DummyEventKind.ReloadStarted));
        Assert.StartsWith("d2p-simple-buffs-v61/", result.Contract, StringComparison.Ordinal);
    }

    [Fact]
    public void SwappingWhenEmptyCoverKeepsAPartUsedMagazineForTheSwap()
    {
        // Three rounds; 1.5 s peeks and 2 s behind cover. The shots at 0 and 1 s leave a round, which cover doesn't top up. The next
        // peek's shot at 3.5 s empties the rifle, and its swap at 4.5 s draws the full sidearm, which shoots at the next peek.
        var (pack, build) = RotatingMagazines(3);
        var result = Run(pack, build, Context, DrivenRotation(8.2) with { PeekSeconds = 1.5, CoverSeconds = 2 });
        Assert.Equal(new[] { 0, 1, 3.5, 7, 8 }, Times(result, DummyEventKind.Shot));
        Assert.Empty(Times(result, DummyEventKind.ReloadStarted));
        Assert.Equal(new[] { 4.5 }, Times(result, DummyEventKind.SwapStarted));
        Assert.Contains(result.Limitations, note => note.Contains("Behind cover the weapon doesn't fire. Swaps and reloads under way carry on behind cover, and a magazine is kept for the swap when empty", StringComparison.Ordinal));
    }

    [Fact]
    public void SwappingAfterKillsCoverStillTopsUpTheWeaponInHand()
    {
        // The same fight swapping after kills: each cover phase reloads the rifle after two shots, and no swap comes.
        var result = RunToppingUp(RotatingMagazines(3), 8.2, 1.5, 2);
        Assert.Equal(new[] { 0, 1, 3.5, 4.5, 7, 8 }, Times(result, DummyEventKind.Shot));
        Assert.Equal(new[] { 1.5, 5 }, Times(result, DummyEventKind.ReloadStarted));
        Assert.Empty(Times(result, DummyEventKind.SwapStarted));
        Assert.Contains(result.Limitations, note => note.Contains("a magazine that isn't full reloads. Swaps and reloads under way carry on behind cover, and only the weapon in hand reloads there", StringComparison.Ordinal));
    }

    [Fact]
    public void ASwapAndItsReloadCarryOnAcrossACoverBoundary()
    {
        // One round each: rifle at 0 s, sidearm at 2 s. The sidearm's swap back (3 to 4 s) and the rifle's reload on arrival run to
        // 6 s, past the boundary at 5 s, and the rifle is full, so it waits for the peek at 7 s rather than reloading again.
        var (pack, build) = RotatingMagazines(1);
        var result = Run(pack, build, Context, DrivenRotation(10) with { PeekSeconds = 5, CoverSeconds = 2, PeekHitShare = 1 });
        Assert.Equal(new double[] { 0, 2, 7 }, Times(result, DummyEventKind.Shot));
        Assert.Equal(new double[] { 4, 9 }, Times(result, DummyEventKind.ReloadStarted));
    }

    [Fact]
    public void ACoverReloadNeverHastensTheNextShotUnderRotation()
    {
        // 1.5 s peeks, 0.2 s behind cover and 0.1 s reloads: the rifle reloads in each cover phase, then waits out its 1 s
        // interval rather than shooting as the peek starts.
        var (original, build) = RotatingMagazines(5);
        var rules = original.Pack with { Stats = [.. original.Pack.Stats.Select(stat => stat.ID == "equipped_weapon_reload_time" ? stat with { Formula = "0.1" } : stat)] };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var result = RunToppingUp((pack, build), 4.5, 1.5, 0.2);
        Assert.Equal(new double[] { 0, 1, 2, 3, 4 }, Times(result, DummyEventKind.Shot));
        Assert.Equal(2, result.Reloads);
    }

    // A magazine rule: "top" while under half of the magazine is spent, as DrivenMagazineReadings' fixture.
    private static (CompiledPackExpressions Pack, BuildSnapshot Build) RotatingPhases(string rounds, string reload = "2") => RotatingBuff(change: rules => rules with
    {
        Selectors = [new() { ID = "phase", Name = "Phase", Values = ["top", "bottom"] }],
        Stats = [.. rules.Stats.Select(stat => stat.ID == "equipped_weapon_magazine_size" ? stat with { Formula = rounds } :
            stat.ID == "equipped_weapon_reload_time" ? stat with { Formula = reload } : stat)],
        Talents = [rules.Talents[0] with { Modifiers = [rules.Talents[0].Modifiers[0] with { RequiresContext = ImmutableDictionary<string, string>.Empty.Add("phase", "top") }] }],
        Simulation = new()
        {
            Triggers = [new() { Talent = "test_buff", Key = "phase", Kind = "magazine", Phases = [new() { Until = 0.5, Value = "top" }, new() { Until = 1, Value = "bottom" }] }]
        }
    });

    [Fact]
    public void ACoverReloadKeepsReadingTheRoundsItHeldUntilItCompletes()
    {
        // Eight rounds: two shots leave six, which stays "top" through the top-up from 1.5 s, as a single weapon's does. Reading an
        // empty magazine during it would show "bottom".
        var result = RunToppingUp(RotatingPhases("8"), 4, 1.5, 2);
        Assert.Equal(new[] { 1.5 }, Times(result, DummyEventKind.ReloadStarted));
        Assert.Equal(4, result.MagazinePhaseUptime["phase"]["top"].Seconds, 10);
    }

    [Fact]
    public void AReloadCompletingBehindCoverChangesReadingsThen()
    {
        // Four rounds: two shots leave half, "bottom" from the 1.5 s boundary (under rotation a spent round reads at the next
        // decision). The 1 s top-up fills it at 2.5 s, with nothing else happening until the peek at 3.5 s, and the reading
        // follows at 2.5 s rather than at the peek.
        var result = RunToppingUp(RotatingPhases("4", reload: "1"), 3.5, 1.5, 2);
        Assert.Equal(2.5, result.MagazinePhaseUptime["phase"]["top"].Seconds, 10);
        Assert.Equal(1, result.MagazinePhaseUptime["phase"]["bottom"].Seconds, 10);
    }

    [Fact]
    public void ACoverReloadReadsItsTimeAtTheBoundary()
    {
        // Each hit halves the reload (2 s to 1 s) for 0.3 s. The buff from the 2 s shot has expired by the boundary at 2.5 s, with
        // nothing else happening between, so the reload takes the full 2 s.
        var fixture = RotatingBuff(duration: 0.3, change: rules => rules with
        {
            Stats = [.. rules.Stats.Select(stat => stat.ID == "equipped_weapon_magazine_size" ? stat with { Formula = "5" } :
                stat.ID == "equipped_weapon_reload_time" ? stat with { Formula = "2 - buff_damage / 10" } : stat)]
        });
        var result = RunToppingUp(fixture, 5, 2.5, 2);
        Assert.Equal(new[] { 2.5 }, Times(result, DummyEventKind.ReloadStarted));
        Assert.Equal(new[] { 4.5 }, Times(result, DummyEventKind.ReloadCompleted));
    }

    private static (CompiledPackExpressions Pack, BuildSnapshot Build) RotatingPelletReading(string owner) => RotatingBuff(owner: owner, change: rules => rules with
    {
        NumericContextInputs = [new() { ID = "prior", Name = "Prior pellets", Min = 0, Max = 8, Step = 1 }],
        Stats = [.. rules.Stats.Select(stat => stat.ID == "equipped_weapon_pellets" ? stat with { Formula = "weapon_pellets" } : stat),
            new() { ID = "weapon_pellets", Formula = "0" }],
        Items = [.. rules.Items.Select(item => item.Slot is "primary_weapon" or "secondary_weapon" ? item with
        { Weapon = [.. item.Weapon, new("weapon_pellets", item.Slot == owner ? 6 : 1)] } : item)],
        Talents = [rules.Talents[0] with { Modifiers = [rules.Talents[0].Modifiers[0] with
        { Value = new("context_value('prior') * 10"), RequiresContext = ImmutableDictionary<string, string>.Empty }] }],
        Simulation = new() { Triggers = [new() { Talent = "test_buff", Key = "prior", Kind = "pellets" }] }
    });

    [Theory]
    [InlineData("primary_weapon", new double[] { 10, 100, 70, 100, 70 })]
    [InlineData("secondary_weapon", new double[] { 10, 100, 10, 160, 10 })]
    public void PriorPelletReadingsBelongToEachWeaponsPreviousShot(string owner, double[] expected)
    {
        var (pack, build) = RotatingPelletReading(owner);
        var result = Run(pack, build, Context, DrivenRotation(15));
        Assert.Equal(expected, result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.Shot).Select(entry => Math.Round(entry.Damage, 8)));
        Assert.Equal(expected.Sum(), result.Timeline.Sum(), 8);
    }

    [Fact]
    public void TargetDeathClearsPelletHistoryForHolsteredWeaponsAsWell()
    {
        var (original, build) = RotatingPelletReading("primary_weapon");
        var rules = original.Pack with { Stats = [.. original.Pack.Stats.Select(stat => stat.ID == "enemy_target_health" ? stat with { Formula = "50" } : stat)] };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, Context, DrivenRotation(),
            retargetDelay: 0, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new double[] { 10, 40, 10 }, result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.Shot).Select(entry => Math.Round(entry.Damage, 8)));
        Assert.Equal(1, result.Kills);
    }

    [Theory]
    [InlineData("primary_weapon", new double[] { 10, 100, 20 })]
    [InlineData("secondary_weapon", new double[] { 10, 100, 10 })]
    public void OnlyTheWeaponInHandGainsHitTriggers(string owner, double[] expected)
    {
        var (pack, build) = RotatingBuff(owner: owner);
        var result = Run(pack, build, Context, DrivenRotation());
        Assert.Equal(expected, result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.Shot).Select(entry => entry.Damage));
        Assert.StartsWith("d2p-simple-buffs-v61/", result.Contract, StringComparison.Ordinal);
    }

    [Fact]
    public void GearSwapBuffCancelsAndLocksOutOnTheNextSwap()
    {
        var (pack, build) = RotatingBuff("swapped", gear: true, duration: 5, change: rules => rules with
        {
            Simulation = new() { Triggers = [rules.Simulation.Triggers[0] with { CancelOn = "swapped", CancelFor = 5 }] }
        });
        var result = Run(pack, build, Context, DrivenRotation(15));
        // Draws at 1, 3, 7, 11: gain, cancel/lock until 8, locked, gain.
        Assert.Equal(new double[] { 10, 110, 10, 100, 20 }, result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.Shot).Select(entry => entry.Damage));
        Assert.Equal(6, result.TimedTriggerUptime["buff"].Seconds);
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(1, 0)]
    public void SwapTriggersUseTheStartBoundaryAndRespectTheHalfOpenFight(double duration, double expectedUptime)
    {
        var (pack, build) = RotatingBuff("swapped", gear: true);
        var result = Run(pack, build, Context, DrivenRotation(duration));
        Assert.Equal(expectedUptime, result.TimedTriggerUptime["buff"].Seconds);
        Assert.Single(result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.Shot));
    }

    [Fact]
    public void SwappingAwayCanGrantAHolsteredWeaponBuffToTheDestination()
    {
        var (pack, build) = RotatingBuff("swap", change: rules => rules with
        {
            Talents = [rules.Talents[0] with { Modifiers = [rules.Talents[0].Modifiers[0] with { WhileHolstered = true }] }]
        });
        var result = Run(pack, build, Context, DrivenRotation());
        Assert.Equal(new double[] { 10, 110, 10 }, result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.Shot).Select(entry => entry.Damage));
    }

    [Fact]
    public void AHolsteredFinisherDoesNotClaimTheOtherWeaponsKill()
    {
        var (pack, build) = RotatingBuff("swap_after_kill", change: rules => rules with
        {
            Stats = [.. rules.Stats.Select(stat => stat.ID == "enemy_target_health" ? stat with { Formula = "50" } : stat)],
            Talents = [rules.Talents[0] with { Modifiers = [rules.Talents[0].Modifiers[0] with { WhileHolstered = true }] }]
        });
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, Context, DrivenRotation(8),
            retargetDelay: 0, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(result.KillTimes);
        Assert.Equal(0, result.TimedTriggerUptime["buff"].Seconds);
    }

    [Theory]
    [InlineData(1, 0.5)]
    [InlineData(10, 0.5)]
    [InlineData(10.01, 0)]
    public void FinisherUsesItsOwnKillAndTheTenSecondSwapStartWindow(double interval, double seconds)
    {
        var (pack, build) = RotatingBuff("swap_after_kill", change: rules => rules with
        {
            Stats = [.. rules.Stats.Select(stat => stat.ID == "equipped_weapon_rpm" ? stat with { Formula = (60 / interval).ToString("R", System.Globalization.CultureInfo.InvariantCulture) } : stat)],
            Talents = [rules.Talents[0] with { Modifiers = [rules.Talents[0].Modifiers[0] with { WhileHolstered = true }] }]
        });
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, Context, DrivenRotation(interval + 0.5),
            retargetDelay: 0, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(seconds, result.TimedTriggerUptime["buff"].Seconds, 8);
    }

    [Fact]
    public void HolsteredWeaponStatusesWaitForThatWeaponsNextHit()
    {
        var (pack, build) = RotatingBuff(owner: "secondary_weapon", change: rules => rules with
        {
            Simulation = new() { Triggers = [rules.Simulation.Triggers[0] with { Kind = "status", Status = "burning", Duration = 2 }] }
        });
        var result = Run(pack, build, Context, DrivenRotation(11));
        // Its hit at 2 arms the status; the primary hit at 6 must not apply it.
        // Its own next hit at 10 applies the status for the final second.
        Assert.Equal(1, result.TargetStatusUptime["burning"].Seconds);
    }

    [Fact]
    public void DrivenRotationReplaysAndConservesDamageWithoutTraceCapture()
    {
        var (pack, build) = RotatingBuff("swapped", gear: true);
        var options = DrivenRotation(20) with { Iterations = 4 };
        var first = Run(pack, build, Context, options);
        var second = Run(pack, build, Context, options with { CaptureFirstFightEvents = false });
        Assert.Equal(first.WeaponDps, second.WeaponDps);
        Assert.Equal(first.TimedTriggerUptime, second.TimedTriggerUptime);
        Assert.Equal(first.Timeline, second.Timeline);
        Assert.Equal(first.WeaponDps.Mean * options.Duration, first.Timeline.Sum(), 8);
        Assert.Empty(second.FirstFightEvents);
    }

    [Fact]
    public void CommittedGunslingerAffectsRotatedWeaponDamageThroughCore()
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var chest = pack.Pack.Items.First(item => item.TalentSlots.Any(slot => slot.Choices.Contains("gear_talent_gunslinger")));
        var slot = chest.TalentSlots.First(slot => slot.Choices.Contains("gear_talent_gunslinger"));
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr")
            .WithEquipment("secondary_weapon", "secondary_weapon_police_m4").WithEquipment(chest.Slot, chest.ID)
            .WithTalentSelection(chest.Slot, slot.ID, "gear_talent_gunslinger");
        var context = Context.Add("practical_hit_rate", "100").Add("practical_headshot_share", "0");
        var options = DrivenRotation(15);
        var baseline = Run(pack, build, context, options with { DriveSimpleBuffs = false });
        var driven = Run(pack, build, context, options);
        Assert.True(driven.WeaponDps.Mean > baseline.WeaponDps.Mean);
        Assert.True(driven.TimedTriggerUptime["gunslinger_active"].Seconds > 0);
        Assert.Equal(baseline.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.Shot).Select(entry => entry.Time),
            driven.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.Shot).Select(entry => entry.Time));
    }

    [Fact]
    public void CommittedFinisherCanBeEarnedOnASidearmAndCarriedToTheRifle()
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr")
            .WithEquipment("sidearm", "sidearm_m1911").WithTalentSelection("sidearm", "talent", "weapon_talent_finisher");
        var context = Context.SetItem(GamePack.WeaponSlotSelector, "sidearm").Add("practical_hit_rate", "100")
            .Add("practical_headshot_share", "100").Add("enemy_target", "1").Add("enemy_difficulty", "1").Add("enemy_players", "1");
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, context,
            DrivenRotation(15) with { Rotation = new("primary_weapon", WeaponSwapMode.WhenEmpty, 0.1) },
            retargetDelay: 0, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.TimedTriggerUptime["finisher_swap_buff"].Seconds > 0);
        Assert.Contains(result.FirstFightEvents, entry => entry.Kind == DummyEventKind.Shot && entry.WeaponSlot == "primary_weapon");
    }

    [Fact]
    public void ATriggeredShotRefillKeepsTheWeaponFullAndAvoidsAnEmptySwap()
    {
        var (pack, build) = RotatingBuff(change: rules => rules with
        {
            Simulation = new() { Triggers = [rules.Simulation.Triggers[0] with { Refill = true }] }
        });
        var result = Run(pack, build, Context, DrivenRotation(4));
        Assert.Equal(new double[] { 0, 1, 2, 3 }, result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.Shot).Select(entry => entry.Time));
        Assert.Equal(0, result.Swaps);
    }

    [Fact]
    public void TheOtherWeaponsShotDoesNotConsumeAHolsteredWeaponsBuff()
    {
        var (pack, build) = RotatingBuff(change: rules => rules with
        {
            Simulation = new() { Triggers = [rules.Simulation.Triggers[0] with { ConsumeOn = "shot" }] }
        });
        var result = Run(pack, build, Context, DrivenRotation());
        Assert.Equal(new double[] { 10, 100, 20 }, result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.Shot).Select(entry => entry.Damage));
    }

    [Fact]
    public void MagazinePhasesReadTheRiflesOwnMagazineOnlyWhileItIsInHand()
    {
        // Two-round magazines. The rifle's talent adds 10 damage in the top half of its magazine: it fires at 0 (top) and
        // 1 (bottom), the sidearm at 3 and 4, then the emptied rifle reloads on arrival until 8 and fires at 8 (top) and 9.
        // Its reading is unset while the sidearm is in hand ([2, 5)) and reads empty during the reload ([5, 8)).
        var (pack, build) = RotatingBuff(change: rules => rules with
        {
            Selectors = [new() { ID = "phase", Name = "Phase", Values = ["top", "bottom"] }],
            Stats = [.. rules.Stats.Select(stat => stat.ID == "equipped_weapon_magazine_size" ? stat with { Formula = "2" } : stat)],
            Talents = [rules.Talents[0] with { Modifiers = [rules.Talents[0].Modifiers[0] with
                { RequiresContext = ImmutableDictionary<string, string>.Empty.Add("phase", "top") }] }],
            Simulation = new()
            {
                Triggers = [new() { Talent = "test_buff", Key = "phase", Kind = "magazine",
                Phases = [new() { Until = 0.5, Value = "top" }, new() { Until = 1, Value = "bottom" }] }]
            }
        });
        var result = Run(pack, build, Context, DrivenRotation(10));
        var shots = result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.Shot).ToArray();
        Assert.Equal(new double[] { 0, 1, 3, 4, 8, 9 }, shots.Select(entry => entry.Time));
        Assert.Equal(new double[] { 20, 10, 100, 100, 20, 10 }, shots.Select(entry => entry.Damage));
        Assert.Equal(2, result.MagazinePhaseUptime["phase"]["top"].Seconds, 10);
        Assert.Equal(5, result.MagazinePhaseUptime["phase"]["bottom"].Seconds, 10);
    }

    [Fact]
    public void CycleReadingsFollowFightTimeAcrossSwaps()
    {
        // The rifle gains 10 damage in the first two seconds of each four-second cycle: on at 0, off at 6.
        var (pack, build) = RotatingBuff(change: rules => rules with
        {
            Selectors = [new() { ID = "cycle", Name = "Cycle", Values = ["on", "off"] }],
            Talents = [rules.Talents[0] with { Modifiers = [rules.Talents[0].Modifiers[0] with
                { RequiresContext = ImmutableDictionary<string, string>.Empty.Add("cycle", "on") }] }],
            Simulation = new()
            {
                Triggers = [new() { Talent = "test_buff", Key = "cycle", Kind = "cycle",
                Phases = [new() { Until = 2, Value = "on" }, new() { Until = 4, Value = "off" }] }]
            }
        });
        var result = Run(pack, build, Context, DrivenRotation(7));
        Assert.Equal(new[] { (0d, 20d), (2d, 100d), (6d, 10d) },
            result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.Shot).Select(entry => (entry.Time, entry.Damage)));
    }

    [Fact]
    public void CommittedMeasuredRunsUnderDrivenRotation()
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr").WithEquipment("secondary_weapon", "secondary_weapon_police_m4")
            .WithTalentSelection("primary_weapon", "talent", "weapon_talent_measured");
        var result = Run(pack, build, Context.Add("practical_hit_rate", "100"), DrivenRotation(30));
        var phases = result.MagazinePhaseUptime["measured_phase"];
        // Both halves are measured, and only while the rifle is in hand.
        Assert.True(phases["top"].Seconds > 0 && phases["bottom"].Seconds > 0);
        Assert.True(phases["top"].Seconds + phases["bottom"].Seconds < 30);
        Assert.Contains(result.FirstFightEvents, entry => entry.Kind == DummyEventKind.Shot && entry.WeaponSlot == "secondary_weapon");
    }

    [Fact]
    public void ADrivenReloadTimeAppliesToTheDrawnWeaponsReloadOnArrival()
    {
        // The rifle's first hit gains +10 s reload time while the rifle is in hand. Swapping back from the sidearm at 3 s
        // draws the empty rifle, whose reload on arrival is resolved with it in hand: 12 s instead of 2.
        var (pack, build) = RotatingBuff(change: rules => rules with
        {
            Talents = [rules.Talents[0] with { Modifiers = [rules.Talents[0].Modifiers[0] with { Stat = "equipped_weapon_reload_time" }] }]
        });
        var result = Run(pack, build, Context, DrivenRotation(17));
        Assert.Equal(new double[] { 0, 2, 16 }, result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.Shot).Select(entry => entry.Time));
    }

    [Fact]
    public void ADrivenSwapSpeedShortensOnlyItsOwnersHalfOfEachSwap()
    {
        // +10 swap speed on the rifle once it hits: its holster and draw halves take 0.5/11 s; the sidearm's stay 0.5 s.
        var (pack, build) = RotatingBuff(change: rules => rules with
        {
            Stats = [.. rules.Stats, new() { ID = "effective_swap_speed", Formula = "0" }],
            Talents = [rules.Talents[0] with { Modifiers = [rules.Talents[0].Modifiers[0] with { Stat = "effective_swap_speed" }] }]
        });
        var result = Run(pack, build, Context, DrivenRotation(7));
        var fast = 0.5 / 11;
        var expected = new[] { 0, 1 + fast + 0.5, 1 + fast + 0.5 + 1 + 0.5 + fast + 2 };
        var shots = result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.Shot).Select(entry => entry.Time).ToArray();
        Assert.Equal(expected.Length, shots.Length);
        Assert.All(expected.Zip(shots), pair => Assert.Equal(pair.First, pair.Second, 10));
    }

    [Fact]
    public void CommittedOnEmptyRunsUnderDrivenRotation()
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var weapon = pack.Pack.Items.First(item => item.Slot == "primary_weapon" && item.TalentSlots.Any(slot => slot.Choices.Contains("weapon_talent_on_empty")));
        var slot = weapon.TalentSlots.First(slot => slot.Choices.Contains("weapon_talent_on_empty"));
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", weapon.ID).WithEquipment("secondary_weapon", "secondary_weapon_police_m4")
            .WithTalentSelection("primary_weapon", slot.ID, "weapon_talent_on_empty");
        var driven = Run(pack, build, Context, DrivenRotation(60));
        var fixedTiming = Run(pack, build, Context, DrivenRotation(60) with { DriveSimpleBuffs = false });
        // Reloading the emptied rifle on arrival triggers On Empty; its handling then speeds the rifle's later swaps and reloads.
        Assert.True(driven.TimedTriggerUptime["on_empty_active"].Seconds > 0);
        Assert.True(driven.ShotsFired > fixedTiming.ShotsFired);
    }

    [Fact]
    public void ADrivenFireRatePacesTheRotationWhileItsOwnerIsInHand()
    {
        // The primary's +10 RPM from its first hit shortens its later interval, which brings the next swap and the sidearm's
        // reload on arrival forward by 1/7 s; the sidearm's own interval and reload are unchanged.
        var (pack, build) = RotatingBuff(change: rules => rules with
        {
            Talents = [rules.Talents[0] with { Modifiers = [rules.Talents[0].Modifiers[0] with { Stat = "equipped_weapon_rpm" }] }]
        });
        var result = Run(pack, build, Context, DrivenRotation(10));
        Assert.Equal(new[] { 0, 2, 6, 6 + 60 / 70d + 1 + 2 }, result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.Shot).Select(entry => entry.Time));
    }

    [Fact]
    public void ARefillTakesBackAnObsoleteEmptySwap()
    {
        var (pack, build) = RotatingBuff("pickup", gear: true, change: rules => rules with
        {
            Simulation = new() { Triggers = [rules.Simulation.Triggers[0] with { Refill = true }] }
        });
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, Context,
            DrivenRotation() with { PickupDelay = 0.5, PickupChance = 1 }, retargetDelay: 0, cancellationToken: TestContext.Current.CancellationToken);
        // Each shot empties the magazine and queues a swap, and each pickup comes first and fills it, so the weapon just keeps firing.
        Assert.Equal([0d, 1, 2, 3, 4, 5, 6], Shots(result));
        Assert.Equal((0d, 0d), (result.Swaps, result.Reloads));
        Assert.DoesNotContain(result.FirstFightEvents, entry => entry.Kind is DummyEventKind.SwapStarted or DummyEventKind.SwapCompleted);
    }

    private static double[] Shots(DummyFightResult result) => [.. result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.Shot).Select(entry => entry.Time)];

    // One round each, a shot every 1.5 s, and a refill on some whole seconds.
    private static (CompiledPackExpressions Pack, BuildSnapshot Build) SecondRefill(long count) => RotatingBuff("second", gear: true, change: rules => rules with
    {
        Stats = [.. rules.Stats.Select(stat => stat.ID switch
        {
            "equipped_weapon_magazine_size" => stat with { Formula = "1" },
            "equipped_weapon_rpm" => stat with { Formula = "40" },
            "enemy_target_health" => stat with { Formula = "100000" },
            _ => stat
        })],
        Simulation = new() { Triggers = [rules.Simulation.Triggers[0] with { Refill = true, Count = count }] }
    });

    [Fact]
    public void ARefillTakesBackAReloadNotYetBegun()
    {
        // Swapping only after kills, each empty magazine queues a reload at the next decision, and the second before it fills the magazine.
        var (pack, build) = SecondRefill(1);
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, Context, DrivenRotation() with { Rotation = new("secondary_weapon", WeaponSwapMode.AfterKill) },
            retargetDelay: 0, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal([0, 1.5, 3, 4.5, 6], Shots(result));
        Assert.Equal(0, result.Reloads);
        Assert.DoesNotContain(result.FirstFightEvents, entry => entry.Kind is DummyEventKind.ReloadStarted or DummyEventKind.ReloadCompleted);
    }

    [Fact]
    public void ARefillTakesBackAnEmptySwapAndTheReloadItWouldEndIn()
    {
        // Shot at 0, swap 1.5 to 3, and the shot at 3 empties the second weapon, queuing a swap at 4.5 onto the empty first, with a reload on arrival.
        // The fourth second refills it, so it fires again at 4.5. Its next empty swap starts at 6 and arrives after the fight.
        var (pack, build) = SecondRefill(4);
        var result = Run(pack, build, Context, DrivenRotation() with { Rotation = new("secondary_weapon", SwapTime: 1.5) });
        Assert.Equal([0, 3, 4.5], Shots(result));
        Assert.Equal((2d, 0d), (result.Swaps, result.Reloads));
        Assert.Equal([1.5, 6], result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.SwapStarted).Select(entry => entry.Time));
    }

    [Fact]
    public void ARefillTakesBackAReloadWaitingAtTheEndOfASwap()
    {
        // Shot at 0, swap 1.5 to 3, shot at 3, swap 4.5 to 6 onto the empty first weapon, and a reload due then. The fifth second refills it mid-swap.
        var (pack, build) = SecondRefill(5);
        var result = Run(pack, build, Context, DrivenRotation() with { Rotation = new("secondary_weapon", SwapTime: 1.5) });
        Assert.Equal([0d, 3, 6], Shots(result));
        Assert.Equal((2d, 0d), (result.Swaps, result.Reloads));
        Assert.DoesNotContain(result.FirstFightEvents, entry => entry.Kind is DummyEventKind.ReloadStarted or DummyEventKind.ReloadCompleted);
        Assert.Equal(2, result.FirstFightEvents.Count(entry => entry.Kind == DummyEventKind.SwapCompleted));
    }

    private static (CompiledPackExpressions Pack, BuildSnapshot Build) OnBothWeapons(Func<GamePack, GamePack>? change = null)
    {
        var (pack, build) = RotatingBuff(change: rules =>
        {
            rules = rules with
            {
                Items = [.. rules.Items.Select(item => item.Slot == "secondary_weapon" ? item with
                { TalentSlots = [new() { ID = "talent", Choices = ["test_buff"] }] } : item)]
            };
            return change is null ? rules : change(rules);
        });
        return (pack, build.WithTalentSelection("secondary_weapon", "talent", "test_buff"));
    }

    [Fact]
    public void EachWeaponsCopyOfATalentKeepsItsOwnTriggerState()
    {
        // The same +10-damage-on-hit talent is on both weapons. The rifle's first hit at 0 s gains only the rifle's copy,
        // so the sidearm's first shot at 2 s is unbuffed (one shared state would have made it 110); the rifle's copy still
        // holds at 6 s. Each copy's uptime is reported separately.
        var (pack, build) = OnBothWeapons();
        var result = Run(pack, build, Context, DrivenRotation(7));
        Assert.Equal(new double[] { 10, 100, 20 }, result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.Shot).Select(entry => entry.Damage));
        Assert.Equal(7, result.TimedTriggerUptime["buff@primary_weapon"].Seconds, 10);
        Assert.Equal(5, result.TimedTriggerUptime["buff@secondary_weapon"].Seconds, 10);
        Assert.DoesNotContain("buff", result.TimedTriggerUptime.Keys);
    }

    [Fact]
    public void ATalentOnBothWeaponsIsRefusedWhenItsCopiesWouldCollide()
    {
        // Holstered effects need both copies' state under one key; a status rule would apply to one shared target.
        var holstered = OnBothWeapons(rules => rules with
        {
            Talents = [rules.Talents[0] with { Modifiers = [rules.Talents[0].Modifiers[0] with { WhileHolstered = true }] }]
        });
        var status = OnBothWeapons(rules => rules with
        {
            Selectors = [.. rules.Selectors, new() { ID = "burn", Name = "Burn", Values = ["true"] }],
            Simulation = new() { Triggers = [.. rules.Simulation.Triggers, new() { Talent = "test_buff", Key = "burn", Kind = "status", Status = "burning", On = "hit", Duration = 1 }] }
        });
        var sharedKey = OnBothWeapons(rules => rules with
        {
            Slots = [.. rules.Slots, new() { ID = "mask", Name = "Mask" }],
            Talents = [.. rules.Talents, new() { ID = "gear_buff", Name = "Gear buff" }],
            Items = [.. rules.Items, new() { ID = "mask", Name = "Mask", Slot = "mask", TalentSlots = [new() { ID = "talent", Choices = ["gear_buff"] }] }],
            Simulation = new() { Triggers = [.. rules.Simulation.Triggers, rules.Simulation.Triggers[0] with { Talent = "gear_buff" }] }
        });
        sharedKey = (sharedKey.Pack, sharedKey.Build.WithEquipment("mask", "mask").WithTalentSelection("mask", "talent", "gear_buff"));
        foreach (var (pack, build) in new[] { holstered, status, sharedKey })
        { Assert.Equal("rotation-shared-talent-unsupported", Assert.Throws<SimulationException>(() => Run(pack, build, Context, DrivenRotation())).Code); }
    }

    [Fact]
    public void CommittedTalentOnBothRiflesRunsAsTwoCopies()
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var rifle = pack.Pack.Items.Single(item => item.ID == "primary_weapon_acr");
        var sidearm = pack.Pack.Items.Single(item => item.ID == "secondary_weapon_police_m4");
        string Slot(ItemDefinition item, string talent) => item.TalentSlots.First(slot => slot.Choices.Contains(talent)).ID;
        // Strained: stacking buff rules, no holstered effects, offered on both assault rifles.
        const string talent = "weapon_talent_strained";
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", rifle.ID).WithEquipment("secondary_weapon", sidearm.ID)
            .WithTalentSelection("primary_weapon", Slot(rifle, talent), talent).WithTalentSelection("secondary_weapon", Slot(sidearm, talent), talent);
        var result = Run(pack, build, Context.Add("practical_hit_rate", "100"), DrivenRotation(30));
        var keys = pack.Pack.Simulation.Triggers.Where(rule => rule.Talent == talent).Select(rule => rule.Key).Distinct().ToArray();
        Assert.All(keys, key =>
        {
            Assert.True(result.TimedTriggerUptime[key + "@primary_weapon"].Seconds > 0);
            Assert.True(result.TimedTriggerUptime[key + "@secondary_weapon"].Seconds > 0);
        });
    }

    [Fact]
    public void WithoutRotationASwapThatOnlyEndsOrClearsARuleNeverFires()
    {
        // Frenzy's removal on weapon switch would be cancelOn swapped. A single-weapon fight never swaps, so the rule runs
        // exactly as without it; here on committed Outsider (cancel) and Strained (consume; it already resets on reload).
        var pack = CommittedPack();
        var context = Context.Add("practical_hit_rate", "100").Add("enemy_target", "1").Add("enemy_difficulty", "1");
        foreach (var (item, talent, key, change) in new (string, string, string, Func<SimTriggerDefinition, SimTriggerDefinition>)[]
        {
            ("primary_weapon_mp5_st", "weapon_talent_outsider", "outsider_active", rule => rule with { CancelOn = "swapped" }),
            ("primary_weapon_acr", "weapon_talent_strained", "strained_stacks", rule => rule with { ConsumeOn = "swapped" })
        })
        {
            var changed = pack.Pack with
            {
                Simulation = pack.Pack.Simulation with { Triggers = [.. pack.Pack.Simulation.Triggers.Select(rule => rule.Key == key ? change(rule) : rule)] }
            };
            var build = Talented("primary_weapon", item, talent);
            TimedTriggerUptime Run(CompiledPackExpressions compiled) => DummyWeaponSimulator.RunAgainstTargets(compiled, build, context,
                new() { Duration = 120, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true }, retargetDelay: 6,
                cancellationToken: TestContext.Current.CancellationToken).TimedTriggerUptime[key];
            Assert.Equal(Run(pack), Run(Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(changed).Compiled)));
        }
    }

    [Fact]
    public void WithoutRotationARuleThatGainsOnASwapIsStillRefused()
    {
        // Committed Gunslinger gains on swapped: with one weapon it could never trigger, so the run says it needs rotation.
        var pack = CommittedPack();
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr")
            .WithEquipment("chest", "chest_n_5_11_tactical_chest").WithTalentSelection("chest", "talent", "gear_talent_gunslinger");
        Assert.Equal("trigger-event-unsupported", Assert.Throws<SimulationException>(() => DummyWeaponSimulator.RunAgainstTargets(pack, build, Context,
            new() { Duration = 10, Mode = SimulationMode.Random, Iterations = 1, DriveSimpleBuffs = true },
            cancellationToken: TestContext.Current.CancellationToken)).Code);
    }

    // Shots alternate gun (10), other (100), gun; the gear buff adds 10. Untagged or gun-tagged, the gun's first hit buffs the
    // other's shot; tagged for the other weapon, only the other's hit gains, so only the third shot is buffed.
    [Theory]
    [InlineData("", new double[] { 10, 110, 20 })]
    [InlineData("tag_gun", new double[] { 10, 110, 20 })]
    [InlineData("tag_other", new double[] { 10, 100, 20 })]
    public void WeaponTagsLimitAGearRuleToHitsFromTaggedWeapons(string tag, double[] expected)
    {
        var (pack, build) = RotatingBuff(gear: true, change: rules => rules with
        {
            Items = [.. rules.Items.Select(item => item.ID is "gun" or "other" ? item with { Tags = [.. item.Tags, "tag_" + item.ID] } : item)],
            Simulation = new() { Triggers = [rules.Simulation.Triggers[0] with { WeaponTags = tag.Length == 0 ? [] : [tag] }] }
        });
        var result = Run(pack, build, Context, DrivenRotation());
        Assert.Equal(expected, result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.Shot).Select(entry => entry.Damage));
    }
}
