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
        Assert.StartsWith("d2p-simple-buffs-v62/", result.Contract, StringComparison.Ordinal);
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
        Assert.StartsWith("d2p-simple-buffs-v62/", result.Contract, StringComparison.Ordinal);
    }

    // ADR 0019, Perturb's shape: the rifle's talent is primed once the rifle has been away its seconds, and drawing it while primed
    // gains the buff, which reads the prime before the draw spends it. The rifle goes away at 1 and 7 s and is drawn at 3 and 11 s,
    // 2 and 4 s later: with 3 s only the draw at 11 s is primed, with 1 s both are.
    [Theory]
    [InlineData(3, 4)]
    [InlineData(1, 12)]
    public void AWeaponPutAwayLongEnoughPrimesItsDraw(double prime, double uptime)
    {
        var (pack, build) = RotatingBuff("swapped", change: rules => rules with
        {
            Selectors = [.. rules.Selectors, new() { ID = "primed", Name = "Primed", Values = ["true"] }],
            Simulation = new()
            {
                Triggers = [new() { Talent = "test_buff", Key = "primed", Kind = "ready", PrimeHolstered = prime },
                    rules.Simulation.Triggers[0] with { WhileKey = "primed" }]
            }
        });
        var result = Run(pack, build, Context, DrivenRotation(15));
        Assert.Equal(new[] { 1.0, 3, 7, 11 }, Times(result, DummyEventKind.SwapStarted));
        Assert.Equal(uptime, result.TimedTriggerUptime["buff"].Seconds);
        // The prime is ready from its seconds after each put-away until the draw: 3 s, from 10 s to 11 s; 1 s, 2 to 3 s and 8 to 11 s.
        Assert.Equal(prime == 3 ? 1 : 4, result.TimedTriggerUptime["primed"].Seconds, 9);
    }

    [Fact]
    public void APrimedTalentOnBothWeaponsPrimesEachCopyByItsOwnWeapon()
    {
        // The talent on both weapons: each copy is primed by its own weapon's time away and its draw reads its own copy's prime. The
        // sidearm is away from 0 s, so with 1 s its draw at 1 s is primed; so is the rifle's at 3 s (away from 1 s).
        var (pack, build) = RotatingBuff("swapped", change: rules => rules with
        {
            Selectors = [.. rules.Selectors, new() { ID = "primed", Name = "Primed", Values = ["true"] }],
            Items = [.. rules.Items.Select(item => item.Slot is "primary_weapon" or "secondary_weapon" ? item with { TalentSlots = [new() { ID = "talent", Choices = ["test_buff"] }] } : item)],
            Simulation = new()
            {
                Triggers = [new() { Talent = "test_buff", Key = "primed", Kind = "ready", PrimeHolstered = 1 },
                    rules.Simulation.Triggers[0] with { WhileKey = "primed" }]
            }
        });
        var result = Run(pack, build.WithTalentSelection("secondary_weapon", "talent", "test_buff"), Context, DrivenRotation(15));
        Assert.Equal(new[] { 1.0, 3, 7, 11 }, Times(result, DummyEventKind.SwapStarted));
        Assert.True(result.TimedTriggerUptime.Where(pair => pair.Key.StartsWith("buff", StringComparison.Ordinal)).Sum(pair => pair.Value.Seconds) > 0);
    }

    [Fact]
    public void APrimedGearTalentIsRefused()
    {
        var (pack, build) = RotatingBuff("swapped", gear: true, change: rules => rules with
        {
            Selectors = [.. rules.Selectors, new() { ID = "primed", Name = "Primed", Values = ["true"] }],
            Simulation = new() { Triggers = [new() { Talent = "test_buff", Key = "primed", Kind = "ready", PrimeHolstered = 1 }] }
        });
        Assert.Equal("trigger-unsupported", Assert.Throws<SimulationException>(() => Run(pack, build, Context, DrivenRotation(15))).Code);
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
    private static (CompiledPackExpressions Pack, BuildSnapshot Build) SecondRefill(long count, bool both = false) => RotatingBuff("second", gear: true, change: rules => rules with
    {
        Stats = [.. rules.Stats.Select(stat => stat.ID switch
        {
            "equipped_weapon_magazine_size" => stat with { Formula = "1" },
            "equipped_weapon_rpm" => stat with { Formula = "40" },
            "enemy_target_health" => stat with { Formula = "100000" },
            _ => stat
        })],
        Simulation = new() { Triggers = [rules.Simulation.Triggers[0] with { Refill = true, RefillBoth = both, Count = count }] }
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARefillOfBothWeaponsFillsTheHolsteredOneToo(bool both)
    {
        // As above: the fourth second refills the second weapon, which fires again at 4.5 and swaps 6 to 7.5 onto the first. With the
        // first emptied at 0, a refill of the hand alone leaves it to reload on arrival, 7.5 to 9.5; a refill of both filled it at 4 s.
        var (pack, build) = SecondRefill(4, both);
        var result = Run(pack, build, Context, DrivenRotation(10) with { Rotation = new("secondary_weapon", SwapTime: 1.5) });
        Assert.Equal([1.5, 6], Times(result, DummyEventKind.SwapStarted));
        Assert.Equal(both ? 0 : 1, result.Reloads);
        Assert.Equal(both ? [0, 3, 4.5, 7.5, 9] : [0, 3, 4.5, 9.5], Shots(result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AShotsRefillOfBothWeaponsFillsTheHolsteredOneBeforeTheRotationDecides(bool both)
    {
        // One round each, a shot every 1.5 s, and every second shot refills. The shot at 0 empties the first weapon and swaps 1.5 to 3; the
        // shot at 3 refills the second, and with both the first too. The shot at 4.5 empties the second and swaps 6 to 7.5 onto the first:
        // refilled, it fires at once; otherwise it reloads 7.5 to 9.5 first.
        var (pack, build) = RotatingBuff("shot", gear: true, change: rules => rules with
        {
            Stats = [.. rules.Stats.Select(stat => stat.ID switch
            {
                "equipped_weapon_magazine_size" => stat with { Formula = "1" },
                "equipped_weapon_rpm" => stat with { Formula = "40" },
                "enemy_target_health" => stat with { Formula = "100000" },
                _ => stat
            })],
            Simulation = new() { Triggers = [rules.Simulation.Triggers[0] with { Refill = true, RefillBoth = both, Count = 2 }] }
        });
        var result = Run(pack, build, Context, DrivenRotation(10) with { Rotation = new("secondary_weapon", SwapTime: 1.5) });
        Assert.Equal([1.5, 6], Times(result, DummyEventKind.SwapStarted));
        Assert.Equal(both ? 0 : 1, result.Reloads);
        Assert.Equal(both ? [0, 3, 4.5, 7.5, 9] : [0, 3, 4.5, 9.5], Shots(result));
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

    // ADR 0019, Unwavering's shape on both weapons: the draw gains for 5 s, and a swap away cancels it and locks it out for 5 s.
    // Swaps start at 1, 3 and 7 s. Shared: the draw at 1 gains; the swap at 3 cancels and locks the one key to 8, so the draws at 3
    // and 7 gain nothing: 2 s. Copied: the swap at 3 locks only the sidearm's copy, so the rifle's draw at 3 gains until the swap at 7
    // cancels it: 2 + 4 s, under two keys.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARuleSharedByBothWeaponsCopiesDrivesOneKeyThatASwapAwayLocksForTheDraw(bool share)
    {
        var (pack, build) = OnBothWeapons(rules => rules with
        {
            Simulation = new()
            {
                Triggers = [rules.Simulation.Triggers[0] with { On = "swapped", Duration = 5, CancelOn = "swap", CancelFor = 5, ShareCopies = share }]
            }
        });
        var result = Run(pack, build, Context, DrivenRotation(10));
        Assert.Equal(new[] { 1.0, 3, 7 }, Times(result, DummyEventKind.SwapStarted));
        if (share)
        {
            Assert.Equal(2, result.TimedTriggerUptime["buff"].Seconds, 9);
            Assert.DoesNotContain(result.TimedTriggerUptime.Keys, key => key.Contains('@', StringComparison.Ordinal));
            Assert.Contains(result.Limitations, note => note.StartsWith("Buff is on both weapons, and its shared rules aren't copied", StringComparison.Ordinal) &&
                note.EndsWith("a cancel on the weapon a swap leaves holds for the one drawn", StringComparison.Ordinal));
        }
        else
        {
            Assert.Equal(2, result.TimedTriggerUptime["buff@secondary_weapon"].Seconds, 9);
            Assert.Equal(4, result.TimedTriggerUptime["buff@primary_weapon"].Seconds, 9);
            Assert.DoesNotContain(result.Limitations, note => note.Contains("shared rules", StringComparison.Ordinal));
        }
    }

    // Versatile's shape (ADR 0019) on gear: the draw gains for 1 s, with the rifle typed and the sidearm an SMG or a rifle. Swaps at 1
    // (the SMG drawn), 3 (the rifle) and 7 s (the SMG).
    private static (CompiledPackExpressions Pack, BuildSnapshot Build) Versatile(string sidearm, double every = 0, bool perType = false, bool different = false) =>
        RotatingBuff("swapped", gear: true, change: rules => rules with
        {
            Items = [.. rules.Items.Select(item => item.ID switch
            {
                "gun" => item with { Tags = ["weapon_type_rifle"] },
                "other" => item with { Tags = [sidearm] },
                _ => item
            })],
            Simulation = new()
            {
                Triggers = [rules.Simulation.Triggers[0] with { Duration = 1, Every = every, WeaponTypes = ["weapon_type_smg", "weapon_type_rifle"],
                    DifferentTypes = different, EveryPerType = perType }]
            }
        });

    [Theory]
    [InlineData("weapon_type_smg", 3)]
    [InlineData("weapon_type_rifle", 0)]
    public void AGainOnDifferentWeaponTypesNeedsTheTwoWeaponsUnalike(string sidearm, double uptime)
    {
        var (pack, build) = Versatile(sidearm, different: true);
        var result = Run(pack, build, Context, DrivenRotation(10));
        Assert.Equal(new[] { 1.0, 3, 7 }, Times(result, DummyEventKind.SwapStarted));
        Assert.Equal(uptime, result.TimedTriggerUptime["buff"].Seconds, 9);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 3)]
    public void ACooldownPerWeaponTypeLetsTheOtherTypesDrawGain(bool perType, double uptime)
    {
        // Every 5 s: one cooldown blocks the rifle's draw at 3 s after the SMG's at 1 s; one per type lets it gain.
        var (pack, build) = Versatile("weapon_type_smg", every: 5, perType: perType);
        Assert.Equal(uptime, Run(pack, build, Context, DrivenRotation(10)).TimedTriggerUptime["buff"].Seconds, 9);
    }

    [Fact]
    public void ADistanceRuleThatMayAssumeStepsAsideWithoutADistance()
    {
        // Versatile's bands: without a fight distance the rule steps aside, its key reads the user's setting and the result says so; a
        // distance rule that may not assume still refuses; with a distance the reading runs.
        (CompiledPackExpressions, BuildSnapshot) Banded(bool assume) => RotatingBuff(change: rules => rules with
        {
            Selectors = [.. rules.Selectors, new() { ID = "range", Name = "Range", Values = ["close", "far"] }],
            Simulation = new()
            {
                Triggers = [.. rules.Simulation.Triggers, new() { Talent = "test_buff", Key = "range", Kind = "distance",
                    Phases = [new() { Until = 15, Value = "close" }, new() { Until = 1000, Value = "far" }], AssumeWithoutDistance = assume }]
            }
        });
        var (pack, build) = Banded(true);
        var result = Run(pack, build, Context, DrivenRotation(4) with { Rotation = null });
        Assert.Contains("This fight has no distance, so these read your setting, or the pack's default, for their range: Buff", result.Limitations);
        var (strict, strictBuild) = Banded(false);
        Assert.Equal("distance-required", Assert.Throws<SimulationException>(() => Run(strict, strictBuild, Context, DrivenRotation(4) with { Rotation = null })).Code);
        var placed = Run(pack, build, Context, DrivenRotation(4) with { Rotation = null, Distance = 10 });
        Assert.DoesNotContain(placed.Limitations, note => note.StartsWith("This fight has no distance", StringComparison.Ordinal));
        // Stepping aside doesn't skip the checks: a band reading on a key a buff rule drives is refused with or without a distance.
        var (clash, clashBuild) = RotatingBuff(change: rules => rules with
        {
            Selectors = [.. rules.Selectors.Select(selector => selector.ID == "buff" ? selector with { Values = ["true", "far"] } : selector)],
            Simulation = new()
            {
                Triggers = [.. rules.Simulation.Triggers, new() { Talent = "test_buff", Key = "buff", Kind = "distance",
                    Phases = [new() { Until = 15, Value = "true" }, new() { Until = 1000, Value = "far" }], AssumeWithoutDistance = true }]
            }
        });
        Assert.Equal("trigger-key-conflict", Assert.Throws<SimulationException>(() => Run(clash, clashBuild, Context, DrivenRotation(4) with { Rotation = null })).Code);
        Assert.Equal("trigger-key-conflict", Assert.Throws<SimulationException>(() => Run(clash, clashBuild, Context, DrivenRotation(4) with { Rotation = null, Distance = 10 })).Code);
    }

    [Fact]
    public void ABandRuleSteppingAsideLeavesTheSelectorAtItsDefault()
    {
        // Versatile's bands (ADR 0019): the talent's +10 is gated on the band "any", the selector's default. Without a distance the
        // band rule steps aside and the key reads "any", so the bonus holds; at 10 m it reads "close", and the bonus is off.
        var (pack, build) = RotatingBuff(change: rules => rules with
        {
            Selectors = [.. rules.Selectors, new() { ID = "range", Name = "Range", Values = ["any", "close", "far"], Default = "any" }],
            Talents = [rules.Talents[0] with { Modifiers = [rules.Talents[0].Modifiers[0] with { RequiresContext = ImmutableDictionary<string, string>.Empty.Add("range", "any") }] }],
            Simulation = new()
            {
                Triggers = [new() { Talent = "test_buff", Key = "range", Kind = "distance",
                    Phases = [new() { Until = 15, Value = "close" }, new() { Until = 1000, Value = "far" }], AssumeWithoutDistance = true }]
            }
        });
        double FirstShot(double? distance) => Run(pack, build, Context, DrivenRotation(2) with { Rotation = null, Distance = distance })
            .FirstFightEvents.First(entry => entry.Kind == DummyEventKind.Shot).Damage;
        Assert.Equal(20, FirstShot(null));
        Assert.Equal(10, FirstShot(10));
    }

    [Fact]
    public void WithoutRotationAGateOnDifferingWeaponTypesStepsAside()
    {
        // With one weapon there's no second to compare, so a type-gated rule on a kill steps aside for the user's setting.
        var (pack, build) = RotatingBuff("kill", gear: true, change: rules => rules with
        {
            Items = [.. rules.Items.Select(item => item.ID == "gun" ? item with { Tags = ["weapon_type_rifle"] } : item)],
            Simulation = new() { Triggers = [rules.Simulation.Triggers[0] with { WeaponTypes = ["weapon_type_rifle"], DifferentTypes = true }] }
        });
        var result = Run(pack, build, Context, DrivenRotation(4) with { Rotation = null });
        Assert.Contains("This fight has no weapon rotation, so these read your setting and never react to a swap: Buff", result.Limitations);
        Assert.DoesNotContain("buff", result.TimedTriggerUptime.Keys);
    }

    [Fact]
    public void HolsteredEffectsOfATalentOnBothWeaponsReadItsSharedKey()
    {
        // Back and Forth's swap-from shape on both weapons: +5 damage while holstered, on a shared key gained on a swap. After the swap
        // at 1 s the holstered copy (the rifle's) adds 5 to the sidearm's shot, and after the swap back at 3 s the sidearm's adds 5 to
        // the rifle's. Copied instead, the holstered copy would read the copy in hand's key, so that's still refused.
        GamePack Shape(GamePack rules, bool share) => rules with
        {
            Selectors = [.. rules.Selectors, new() { ID = "from", Name = "From", Values = ["true"] }],
            Talents = [rules.Talents[0] with
            {
                Modifiers = [rules.Talents[0].Modifiers[0] with { Value = new(5), WhileHolstered = true, RequiresContext = ImmutableDictionary<string, string>.Empty.Add("from", "true") }]
            }],
            Simulation = new() { Triggers = [rules.Simulation.Triggers[0] with { Key = "from", On = "swap", ShareCopies = share }] }
        };
        var (pack, build) = OnBothWeapons(rules => Shape(rules, share: true));
        var result = Run(pack, build, Context, DrivenRotation(7));
        Assert.Equal(new double[] { 10, 105, 15 }, result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.Shot).Select(entry => entry.Damage));
        var (copied, copiedBuild) = OnBothWeapons(rules => Shape(rules, share: false));
        Assert.Contains("needs its rules shared", Assert.Throws<SimulationException>(() => Run(copied, copiedBuild, Context, DrivenRotation(7))).Message, StringComparison.Ordinal);
        // One copied rule beside the shared one is enough to refuse.
        var (mixed, mixedBuild) = OnBothWeapons(rules =>
        {
            var shaped = Shape(rules, share: true);
            return shaped with { Simulation = new() { Triggers = [.. shaped.Simulation.Triggers, rules.Simulation.Triggers[0]] } };
        });
        Assert.Contains("needs its rules shared", Assert.Throws<SimulationException>(() => Run(mixed, mixedBuild, Context, DrivenRotation(7))).Message, StringComparison.Ordinal);
        // So is one key gating both its in-hand and holstered effects, which one gain would count twice.
        var (twice, twiceBuild) = OnBothWeapons(rules =>
        {
            var shaped = Shape(rules, share: true);
            var holstered = shaped.Talents[0].Modifiers[0];
            return shaped with { Talents = [shaped.Talents[0] with { Modifiers = [holstered, holstered with { WhileHolstered = false }] }] };
        });
        Assert.Contains("different keys", Assert.Throws<SimulationException>(() => Run(twice, twiceBuild, Context, DrivenRotation(7))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHolsteredEffectReadingAKeyCopiedPerWeaponIsRefused()
    {
        // The talent on both weapons copies its key per weapon; another talent on the rifle reading that key while holstered would read the
        // sidearm's copy, not its own weapon's.
        var (pack, build) = OnBothWeapons(rules => rules with
        {
            Talents = [.. rules.Talents, new()
            {
                ID = "reader",
                Name = "Reader",
                Modifiers = [rules.Talents[0].Modifiers[0] with { WhileHolstered = true }]
            }],
            Items = [.. rules.Items.Select(item => item.Slot == "primary_weapon" ? item with { TalentSlots = [.. item.TalentSlots, new() { ID = "second", Choices = ["reader"] }] } : item)]
        });
        var refused = Assert.Throws<SimulationException>(() => Run(pack, build.WithTalentSelection("primary_weapon", "second", "reader"), Context, DrivenRotation(7)));
        Assert.Contains("reads a key a talent on both swapped weapons copies", refused.Message, StringComparison.Ordinal);
    }

    // ADR 0019, Unwavering's full lockout on the rifle: swaps at 1, 3 and 7 s. Today the draw at 3 s gains until the swap at 7 s (4 s).
    // With the lockout starting on any swap away, the swap at 1 s locks the rifle out to 6 s, so the draw at 3 s gains nothing; the
    // rifle is in hand at 6 s, so the bonus comes then, until the swap at 7 s cancels it (1 s).
    [Theory]
    [InlineData(false, 4)]
    [InlineData(true, 1)]
    public void UnwaveringsLockoutStartsOnAnySwapAwayAndItsBonusComesAtTheEnd(bool full, double uptime)
    {
        var (pack, build) = RotatingBuff(change: rules => rules with
        {
            Simulation = new()
            {
                Triggers = [rules.Simulation.Triggers[0] with { On = "swapped", Duration = 5, CancelOn = "swap", CancelFor = 5, LockoutAlways = full, GainAtLockoutEnd = full }]
            }
        });
        var result = Run(pack, build, Context, DrivenRotation(10));
        Assert.Equal(new[] { 1.0, 3, 7 }, Times(result, DummyEventKind.SwapStarted));
        Assert.Equal(uptime, result.TimedTriggerUptime["buff"].Seconds, 9);
    }

    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    public void ASwapsArrivalReloadReadsTheStateAsItBegins(bool cancelled, double reload)
    {
        // R117: a bonus that speeds reloads (-1 s of 2) gains on each shot, on both weapons. The shot that empties a weapon decides the
        // swap with the bonus up. Cancelled by the swap away, it's gone when the drawn weapon's reload begins, which takes the full 2 s;
        // kept, the reload takes 1 s. A rule on reload_end fires at the completion as retimed, so its uptime runs from the first one.
        var (pack, build) = OnBothWeapons(rules => rules with
        {
            Selectors = [.. rules.Selectors, new() { ID = "done", Name = "Done", Values = ["true"] }],
            Talents = [rules.Talents[0] with { Modifiers = [rules.Talents[0].Modifiers[0] with { Stat = "equipped_weapon_reload_time", Value = new(-1) }] }],
            Simulation = new()
            {
                Triggers = [rules.Simulation.Triggers[0] with { On = "shot", Duration = 100, CancelOn = cancelled ? "swap" : "", ShareCopies = true },
                    new() { Talent = "test_buff", Key = "done", Kind = "buff", On = "reload_end", Duration = 100, ShareCopies = true }]
            }
        });
        var result = Run(pack, build, Context, DrivenRotation(12));
        var starts = Times(result, DummyEventKind.ReloadStarted);
        var ends = Times(result, DummyEventKind.ReloadCompleted);
        Assert.NotEmpty(starts);
        Assert.Equal(starts.Length, ends.Length);
        Assert.All(starts.Zip(ends), pair => Assert.Equal(reload, pair.Second - pair.First, 9));
        Assert.Equal(12 - ends[0], result.TimedTriggerUptime["done"].Seconds, 9);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0.5)]
    public void ASwapsOwnTimeReadsTheBonusAfterItsEvents(bool cancelled, double swap)
    {
        // The owner's reading (2026-11-15): a bonus the swap ends speeds neither its holster nor its draw. A shared bonus doubling swap
        // speed gains on each shot, so it's up when each swap is decided. Cancelled by the swap away, each 1 s swap takes its full time;
        // kept, half.
        var (pack, build) = OnBothWeapons(rules => rules with
        {
            Stats = [.. rules.Stats, new() { ID = "effective_swap_speed", Formula = "0" }],
            Talents = [rules.Talents[0] with { Modifiers = [rules.Talents[0].Modifiers[0] with { Stat = "effective_swap_speed", Value = new(1) }] }],
            Simulation = new() { Triggers = [rules.Simulation.Triggers[0] with { On = "shot", Duration = 100, CancelOn = cancelled ? "swap" : "", ShareCopies = true }] }
        });
        var result = Run(pack, build, Context, DrivenRotation(12) with { Rotation = new("secondary_weapon", SwapTime: 1) });
        var starts = Times(result, DummyEventKind.SwapStarted);
        var arrivals = Times(result, DummyEventKind.SwapCompleted);
        Assert.NotEmpty(starts);
        // A swap starting before the fight's end may arrive after it, beyond the trace.
        Assert.InRange(arrivals.Length, starts.Length - 1, starts.Length);
        Assert.All(starts.Zip(arrivals), pair => Assert.Equal(swap, pair.Second - pair.First, 9));
        // An emptied weapon reloads as it arrives, at the retimed arrival, and the reload count matches the trace.
        var reloads = Times(result, DummyEventKind.ReloadStarted);
        Assert.NotEmpty(reloads);
        Assert.All(reloads, at => Assert.Contains(at, arrivals));
        Assert.Equal(reloads.Length, result.Reloads);
    }

    [Fact]
    public void ABonusASwapsOwnEventsGainSpeedsOnlyTheNextSwap()
    {
        // A bonus doubling swap speed, gained on the draw (swapped) and lasting: it isn't up when the first swap is decided, and the swap
        // it fires on stays at its full 1 s; later swaps are decided with it up and take half.
        var (pack, build) = OnBothWeapons(rules => rules with
        {
            Stats = [.. rules.Stats, new() { ID = "effective_swap_speed", Formula = "0" }],
            Talents = [rules.Talents[0] with { Modifiers = [rules.Talents[0].Modifiers[0] with { Stat = "effective_swap_speed", Value = new(1) }] }],
            Simulation = new() { Triggers = [rules.Simulation.Triggers[0] with { On = "swapped", Duration = 100, ShareCopies = true }] }
        });
        var result = Run(pack, build, Context, DrivenRotation(12) with { Rotation = new("secondary_weapon", SwapTime: 1) });
        var times = Times(result, DummyEventKind.SwapStarted).Zip(Times(result, DummyEventKind.SwapCompleted), (start, end) => end - start).ToArray();
        Assert.True(times.Length >= 2);
        Assert.Equal(1, times[0], 9);
        Assert.All(times.Skip(1), duration => Assert.Equal(0.5, duration, 9));
    }

    [Fact]
    public void ASharedRuleGatedOnACopiedKeyIsRefused()
    {
        // A copied rule's key is per weapon, and a shared rule has no weapon of its own to pick the copy.
        var (pack, build) = OnBothWeapons(rules => rules with
        {
            Selectors = [.. rules.Selectors, new() { ID = "primed", Name = "Primed", Values = ["true"] }],
            Simulation = new()
            {
                Triggers = [new() { Talent = "test_buff", Key = "primed", Kind = "ready", PrimeHolstered = 1 },
                    rules.Simulation.Triggers[0] with { On = "swapped", WhileKey = "primed", ShareCopies = true }]
            }
        });
        var refused = Assert.Throws<SimulationException>(() => Run(pack, build, Context, DrivenRotation(10)));
        Assert.Equal("rotation-shared-talent-unsupported", refused.Code);
        Assert.Contains("A rule shared by a talent's copies", refused.Message, StringComparison.Ordinal);
        // So is one with a magazine-sized cap, each weapon's own.
        var (capped, cappedBuild) = OnBothWeapons(rules => rules with
        {
            NumericContextInputs = [.. rules.NumericContextInputs, new() { ID = "heat", Name = "Heat", Min = 0, Max = 100, Step = 1, Default = 0 }],
            Simulation = new() { Triggers = [rules.Simulation.Triggers[0] with { Key = "heat", Kind = "stacks", Max = 100, MaxMagazine = 0.5, ShareCopies = true }] }
        });
        Assert.Contains("A rule shared by a talent's copies", Assert.Throws<SimulationException>(() => Run(capped, cappedBuild, Context, DrivenRotation(10))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASharedRuleWithoutACancelIsNotedWithoutOne()
    {
        // Back and Forth's swap-to shape: no cancel, so the note doesn't speak of one.
        var (pack, build) = OnBothWeapons(rules => rules with
        {
            Simulation = new() { Triggers = [rules.Simulation.Triggers[0] with { On = "swapped", Duration = 10, ShareCopies = true }] }
        });
        var note = Assert.Single(Run(pack, build, Context, DrivenRotation(10)).Limitations, note => note.StartsWith("Buff is on both weapons", StringComparison.Ordinal));
        Assert.DoesNotContain("cancel", note, StringComparison.Ordinal);
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
    public void WithoutRotationARuleThatGainsOnASwapStepsAsideForTheUsersSetting()
    {
        // Committed Gunslinger gains on swapped. With one weapon nothing swaps (ADR 0019), so its rule steps aside: the talent reads the
        // user's setting, +23% total weapon damage when set, and the result names it. It was refused before.
        var pack = CommittedPack();
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr")
            .WithEquipment("chest", "chest_n_5_11_tactical_chest").WithTalentSelection("chest", "talent", "gear_talent_gunslinger");
        DummyFightResult Fight(ImmutableDictionary<string, string> context) => DummyWeaponSimulator.RunAgainstTargets(pack, build, context,
            new() { Duration = 10, Mode = SimulationMode.Random, Iterations = 1, Seed = 1, DriveSimpleBuffs = true }, cancellationToken: TestContext.Current.CancellationToken);
        var unset = Fight(Context);
        var set = Fight(Context.SetItem("gunslinger_active", "true"));
        Assert.Contains("This fight has no weapon rotation, so these read your setting and never react to a swap: Gunslinger", unset.Limitations);
        Assert.True(set.WeaponDps.Mean > unset.WeaponDps.Mean * 1.1);
    }

    [Fact]
    public void WithoutRotationAPrimedRuleStepsAsideForTheUsersSetting()
    {
        // A primed rule never primes with one weapon, so it steps aside: the talent's +10 damage, gated on the prime, follows the user's
        // setting, and the result names the talent.
        var (pack, build) = RotatingBuff(change: rules => rules with
        {
            Selectors = [.. rules.Selectors, new() { ID = "primed", Name = "Primed", Values = ["true"] }],
            Talents = [rules.Talents[0] with { Modifiers = [rules.Talents[0].Modifiers[0] with { RequiresContext = ImmutableDictionary<string, string>.Empty.Add("primed", "true") }] }],
            Simulation = new() { Triggers = [new() { Talent = "test_buff", Key = "primed", Kind = "ready", PrimeHolstered = 1 }] }
        });
        var unset = Run(pack, build, Context, DrivenRotation(4) with { Rotation = null });
        var set = Run(pack, build, Context.SetItem("primed", "true"), DrivenRotation(4) with { Rotation = null });
        Assert.Contains("This fight has no weapon rotation, so these read your setting and never react to a swap: Buff", unset.Limitations);
        Assert.True(set.WeaponDps.Mean > unset.WeaponDps.Mean);
    }

    [Fact]
    public void WithoutRotationRulesGainingOnlyOnSwapsStepAsideAndOthersKeepTheirOtherGains()
    {
        // Without a rotation: stacks gaining on a swap and a hit run on the hits; stacks gaining on two swap events, and a status rule
        // gaining on a swap, step aside rather than refuse the fight.
        var (pack, build) = RotatingBuff(change: rules => rules with
        {
            Selectors = [.. rules.Selectors, new() { ID = "burn", Name = "Burn", Values = ["true"] }],
            NumericContextInputs = [.. rules.NumericContextInputs, new() { ID = "heat", Name = "Heat", Min = 0, Max = 5, Step = 1, Default = 0 },
                new() { ID = "spin", Name = "Spin", Min = 0, Max = 5, Step = 1, Default = 0 }],
            Simulation = new()
            {
                Triggers = [new() { Talent = "test_buff", Key = "heat", Kind = "stacks", Max = 5, On = "swapped", Also = [new() { On = "hit", Gain = 1 }] },
                    new() { Talent = "test_buff", Key = "spin", Kind = "stacks", Max = 5, On = "swap", Also = [new() { On = "swap_after_kill", Gain = 1 }] },
                    new() { Talent = "test_buff", Key = "burn", Kind = "status", Status = "burning", On = "swapped", Duration = 1 },
                    rules.Simulation.Triggers[0]]
            }
        });
        var result = Run(pack, build, Context, DrivenRotation(4) with { Rotation = null });
        Assert.True(result.TimedTriggerUptime["heat"].Seconds > 0);
        Assert.DoesNotContain("spin", result.TimedTriggerUptime.Keys);
        Assert.Contains("This fight has no weapon rotation, so these read your setting and never react to a swap: Buff", result.Limitations);
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
