using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

/// <summary>
/// ADR 0037 (the Reviver Hive), on the committed rows (R166, r166.candidate.1) with what a test needs set: a player going down with the hive out is out of the fight until its
/// revive, fire landing on a downed pool, then back at full health with a share of armor, immune for 5 s (the owner's calls, 2026-11-23).
/// </summary>
public sealed partial class DummyWeaponSimulatorTests
{
    private const double ReviveSeconds = 2.9;
    private static readonly SimSkillRevive SoftRevive = new()
    {
        Armor = "revive_armor_share",
        Seconds = "revive_seconds",
        PoolShare = 0.5,
        // Little enough that the fight's fire never spends the pool before the revive.
        DamageTaken = 0.01,
        BleedSeconds = 45,
        ImmuneSeconds = 5,
        EndAfter = 4
    };

    // The committed pack with the Reviver Hive's revive and outputs set, a 180 s cooldown unless told.
    private static CompiledPackExpressions RevivePack(SimSkillRevive? revive = null, string cooldown = "180", string armor = "0.25", double seconds = ReviveSeconds)
    {
        var committed = CommittedPack();
        return Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(committed.Pack with
        {
            ActiveSkills = [.. committed.Pack.ActiveSkills.Select(skill => skill.ID != "skill_reviver_hive" ? skill : skill with
            {
                PreviewOutputs = [.. skill.PreviewOutputs.Select(output => output.ID switch
                {
                    "revive_armor_share" => output with { Formula = armor },
                    "revive_seconds" => output with { Formula = seconds.ToString(CultureInfo.InvariantCulture) },
                    "cooldown_seconds" => output with { Formula = cooldown },
                    _ => output
                })]
            })],
            Simulation = committed.Pack.Simulation with
            {
                Skills = [.. committed.Pack.Simulation.Skills.Select(rule => rule.Skill != "skill_reviver_hive" ? rule : rule with { Revive = revive ?? SoftRevive })]
            }
        }).Compiled);
    }

    private static DummyFightResult ReviveFight(CompiledPackExpressions pack, string? skill = "skill_reviver_hive", double duration = 20, string? second = null, bool driven = true,
        RotationOptions? rotation = null, int? kits = null, bool incoming = true, double secondAfter = 0, string target = "6",
        Func<BuildSnapshot, BuildSnapshot>? change = null, Func<DummyFightOptions, DummyFightOptions>? with = null, string attackers = "1", string enemyHits = "100")
    {
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr").WithEquipment("sidearm", "sidearm_d50")
            .WithEquipment("chest", "chest_badger_tuff_chest");
        if (skill is not null) { build = build.WithActiveSkillSelection("skill_slot_1", skill); }
        if (second is not null) { build = build.WithActiveSkillSelection("skill_slot_2", second); }
        // The player all but never hits, so the enemy lives.
        var options = new DummyFightOptions
        {
            Duration = duration,
            Mode = SimulationMode.Random,
            Iterations = 1,
            Seed = 1,
            IncludeIncoming = incoming,
            IncludeSkills = true,
            DriveSimpleBuffs = driven,
            Rotation = rotation,
            ArmorKits = kits,
            ArmorKitAtPercent = kits is null ? null : 50,
            SkillUse = secondAfter > 0 ? ImmutableDictionary<string, SkillUseCondition>.Empty.Add("skill_slot_2", new() { When = SkillUseCondition.AfterSeconds, Seconds = secondAfter })
                : ImmutableDictionary<string, SkillUseCondition>.Empty,
            CaptureFirstFightEvents = true
        };
        return DummyWeaponSimulator.RunAgainstTargets(pack, change?.Invoke(build) ?? build, Context.Add("practical_hit_rate", "1").Add("enemy_target", target).Add("enemy_difficulty", "1").Add("enemy_hit_rate", enemyHits)
            .Add(DummyFightOptions.AttackersInput, attackers), with?.Invoke(options) ?? options, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static double FirstTime(DummyFightResult result, DummyEventKind kind) => EventTimes(result, kind)[0];

    private static (double Armor, double Health) PlayerPools(CompiledPackExpressions pack)
    {
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr").WithEquipment("sidearm", "sidearm_d50")
            .WithEquipment("chest", "chest_badger_tuff_chest");
        var stats = BuildResolver.Resolve(pack, build, Context.Add("enemy_target", "6").Add("enemy_difficulty", "1")).Calculation.Stats;
        return (stats["character_armor"], stats["character_health"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheHiveRevivesItsUserOnce(bool driven)
    {
        var pack = RevivePack();
        var alone = ReviveFight(pack, skill: null, driven: driven);
        var result = ReviveFight(pack, driven: driven);
        var down = FirstTime(alone, DummyEventKind.Downed);
        Assert.Equal(1, alone.Incoming!.DownedFraction);
        Assert.Null(alone.Incoming.Revives);
        // Down when they would be, out of the fight until the revive, then back with full health and a quarter of their armor.
        var downs = EventTimes(result, DummyEventKind.Downed);
        Assert.Equal(down, downs[0], 9);
        var revived = Assert.Single(EventTimes(result, DummyEventKind.Revived));
        Assert.Equal(down + ReviveSeconds, revived, 9);
        var (armor, health) = PlayerPools(pack);
        Assert.Equal(armor * 0.25, Assert.Single(result.FirstFightEvents, entry => entry.Kind == DummyEventKind.ArmorRepaired).Damage, 3);
        Assert.Equal(health, Assert.Single(result.FirstFightEvents, entry => entry.Kind == DummyEventKind.HealthRepaired).Damage, 3);
        Assert.Equal(1, result.Incoming!.Revives);
        // No shot while down, and shots again after.
        Assert.DoesNotContain(EventTimes(result, DummyEventKind.Shot), time => time > down && time < revived);
        Assert.Contains(EventTimes(result, DummyEventKind.Shot), time => time >= revived);
        // Nothing hurts them for 5 s, each landing hit a blocked shot; then hits again, to a second down, with the hive spent, which ends the fight.
        Assert.DoesNotContain(EventTimes(result, DummyEventKind.EnemyHit), time => time >= revived && time < revived + 5);
        Assert.Contains(EventTimes(result, DummyEventKind.EnemyShotBlocked), time => time >= revived && time < revived + 5);
        Assert.Equal(2, downs.Length);
        Assert.True(downs[1] > revived + 5);
        Assert.Equal(1, result.Incoming.DownedFraction);
        Assert.Equal(downs[1], result.Incoming.TimeToDown!.Mean, 9);
        Assert.DoesNotContain(result.FirstFightEvents, entry => entry.Time > downs[1]);
    }

    [Fact]
    public void FireWhileDownLandsOnTheDownedPool()
    {
        // Half the damage against half the health: this fight's fire spends the pool before the revive, so the player dies there.
        var pack = RevivePack(SoftRevive with { DamageTaken = 0.5 });
        var result = ReviveFight(pack);
        var down = Assert.Single(EventTimes(result, DummyEventKind.Downed));
        var died = Assert.Single(EventTimes(result, DummyEventKind.Died));
        Assert.True(died > down && died < down + ReviveSeconds);
        Assert.Equal(0, result.Incoming!.Revives);
        Assert.Equal(1, result.Incoming.DownedFraction);
        Assert.Equal(died, result.Incoming.TimeToDown!.Mean, 9);
        Assert.DoesNotContain(result.FirstFightEvents, entry => entry.Time > died);
        // What landed while down, and what bled, are the pool.
        var (_, health) = PlayerPools(pack);
        var hits = result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.EnemyHit).ToArray();
        var whole = hits[0].Damage;
        var landed = hits.Where(entry => entry.Time > down).ToArray();
        Assert.All(landed[..^1], entry => Assert.Equal(whole * 0.5, entry.Damage, 6));
        Assert.Equal(health * 0.5, landed.Sum(entry => entry.Damage) + health * 0.5 * (died - down) / 45, 3);
        // Damage taken counts it.
        Assert.Equal(hits.Sum(entry => entry.Damage), result.Incoming.DamageTaken, 3);
        // Bleeding alone, at a rate that outruns the revive.
        var bled = ReviveFight(RevivePack(SoftRevive with { DamageTaken = 1e-9, BleedSeconds = 1.5 }));
        Assert.Equal(FirstTime(bled, DummyEventKind.Downed) + 1.5, FirstTime(bled, DummyEventKind.Died), 3);
    }

    [Fact]
    public void AFightEndingWhileDownIsntDowned()
    {
        // The fight runs out with the revive still coming: the player isn't counted as downed, nor revived.
        var pack = RevivePack();
        var down = FirstTime(ReviveFight(pack), DummyEventKind.Downed);
        var result = ReviveFight(pack, duration: down + 1);
        Assert.Equal(0, result.Incoming!.DownedFraction);
        Assert.Equal(0, result.Incoming.Revives);
        Assert.Null(result.Incoming.TimeToDown);
    }

    [Fact]
    public void ARevivedPlayerWithTheHiveBackIsRevivedAgain()
    {
        // The use ends 4 s after the revive, and its cooldown runs from then: a second hive is out 1 s later, and revives at the next down.
        var pack = RevivePack(cooldown: "1");
        var result = ReviveFight(pack, duration: 60);
        var revived = EventTimes(result, DummyEventKind.Revived);
        var downs = EventTimes(result, DummyEventKind.Downed);
        Assert.True(revived.Length >= 2);
        Assert.True(downs[1] >= revived[0] + 5);
        Assert.Equal(downs[1] + ReviveSeconds, revived[1], 9);
        // One not back by then doesn't: its cooldown from 4 s after the revive outlasts the 5 s of immunity and what follows.
        var late = downs[1] - revived[0] - 4 + 0.5;
        Assert.Single(EventTimes(ReviveFight(RevivePack(cooldown: late.ToString("R", CultureInfo.InvariantCulture)), duration: 60), DummyEventKind.Revived));
        var early = downs[1] - revived[0] - 4 - 0.5;
        Assert.True(EventTimes(ReviveFight(RevivePack(cooldown: early.ToString("R", CultureInfo.InvariantCulture)), duration: 60), DummyEventKind.Revived).Length >= 2);
    }

    [Fact]
    public void TheArmorShareIsCappedAtMaximum()
    {
        var pack = RevivePack(armor: "1.5");
        var (armor, _) = PlayerPools(pack);
        Assert.Equal(armor, Assert.Single(ReviveFight(pack).FirstFightEvents, entry => entry.Kind == DummyEventKind.ArmorRepaired).Damage, 3);
    }

    [Fact]
    public void AReloadUnderWayStartsOverAtTheRevive()
    {
        // The player goes down while the rifle reloads: the reload stops, and starts over as they're back.
        var pack = RevivePack();
        var result = ReviveFight(pack);
        var down = FirstTime(result, DummyEventKind.Downed);
        var revived = FirstTime(result, DummyEventKind.Revived);
        var starts = EventTimes(result, DummyEventKind.ReloadStarted);
        var ends = EventTimes(result, DummyEventKind.ReloadCompleted);
        Assert.True(starts[0] < down && starts[0] + 1.91 > down, $"{starts[0]} {down}");
        Assert.Equal(revived, starts[1], 9);
        Assert.Equal(revived + 1.91, ends[0], 9);
        Assert.Equal(starts.Length, result.Reloads);
    }

    [Theory]
    [InlineData(WeaponSwapMode.AfterKill, 1)]
    [InlineData(WeaponSwapMode.WhenEmpty, 1)]
    [InlineData(WeaponSwapMode.WhenEmpty, 3)]
    public void ARotationWaitsForTheRevive(WeaponSwapMode mode, double swapTime)
    {
        // Down mid-reload (no kill comes, so the rifle reloads), with the sidearm in hand, or while a slow swap runs.
        var pack = RevivePack();
        var result = ReviveFight(pack, rotation: new("sidearm", mode, swapTime), duration: 30);
        var down = FirstTime(result, DummyEventKind.Downed);
        var revived = FirstTime(result, DummyEventKind.Revived);
        Assert.DoesNotContain(result.FirstFightEvents, entry => entry.Kind is DummyEventKind.Shot or DummyEventKind.ReloadStarted or DummyEventKind.ReloadCompleted or DummyEventKind.SwapStarted &&
            entry.Time > down && entry.Time < revived);
        Assert.Contains(EventTimes(result, DummyEventKind.Shot), time => time >= revived);
        if (mode == WeaponSwapMode.AfterKill)
        {
            var starts = EventTimes(result, DummyEventKind.ReloadStarted);
            Assert.True(starts[0] < down);
            Assert.Equal(revived, starts[1], 9);
            Assert.Equal(revived + 1.91, FirstTime(result, DummyEventKind.ReloadCompleted), 9);
            Assert.Equal(EventTimes(result, DummyEventKind.ReloadStarted).Length, result.Reloads);
        }
        Assert.Equal(EventTimes(result, DummyEventKind.SwapStarted).Length, result.Swaps);
    }

    [Fact]
    public void ASwapWaitsForTheReviveWhereverTheDownFindsIt()
    {
        // Swapping at every shot with the swap's time set so the down comes within a swap or within the reload an empty weapon gets on
        // arrival: no shot, start or reload falls in between, and the weapon shoots on after.
        var pack = RevivePack();
        var found = new HashSet<string>();
        foreach (var time in Enumerable.Range(1, 60).Select(step => step * 0.1))
        {
            // Two single-round weapons that swap at each shot, so the down falls at every part of the swap cycle over these times.
            var single = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
            {
                Stats = [.. pack.Pack.Stats.Select(stat => stat.ID == "equipped_weapon_magazine_size" ? stat with { Formula = "1", Min = null } : stat)]
            }).Compiled);
            var result = ReviveFight(single, rotation: new("sidearm", WeaponSwapMode.WhenEmpty, time), duration: 15);
            var down = FirstTime(result, DummyEventKind.Downed);
            var revived = FirstTime(result, DummyEventKind.Revived);
            Assert.DoesNotContain(result.FirstFightEvents, entry => entry.Kind is DummyEventKind.Shot or DummyEventKind.ReloadStarted or DummyEventKind.ReloadCompleted or DummyEventKind.SwapStarted &&
                entry.Time > down && entry.Time < revived);
            // Every swap started completes, and every reload but one cut short by the down, unless the fight ends first.
            double[] Times(DummyEventKind kind) => EventTimes(result, kind);
            var end = result.Incoming!.TimeToDown?.Mean ?? 15;
            // The fight's last action may be counted as decided and cut from the trace by the down that ends the fight.
            Assert.InRange(result.Swaps - Times(DummyEventKind.SwapStarted).Length, 0, 1);
            Assert.InRange(result.Reloads - Times(DummyEventKind.ReloadStarted).Length, 0, 1);
            var lastSwap = Times(DummyEventKind.SwapStarted).LastOrDefault(start => start <= down, -1);
            var arrived = Times(DummyEventKind.SwapCompleted).FirstOrDefault(done => done > lastSwap, double.PositiveInfinity);
            var lastReload = Times(DummyEventKind.ReloadStarted).LastOrDefault(start => start <= down, -1);
            var loaded = Times(DummyEventKind.ReloadCompleted).FirstOrDefault(done => done > lastReload, double.PositiveInfinity);
            if (lastReload > lastSwap && loaded > down)
            {
                found.Add("reload");
                Assert.Contains(Times(DummyEventKind.ReloadStarted), start => Math.Abs(start - revived) < 1e-9);
            }
            else if (lastSwap >= 0 && arrived > down)
            {
                found.Add("swap");
                // The draw itself runs its course; a reload it needs waits for the revive.
                Assert.DoesNotContain(Times(DummyEventKind.ReloadStarted), start => start > down && start < revived);
            }
            Assert.Contains(Times(DummyEventKind.Shot), shot => shot >= revived || revived + 4 > end);
        }
        Assert.Equal(["reload", "swap"], found.Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AReloadAtTheMomentOfTheDownStartsAtTheRevive(bool rotated)
    {
        // The hit that downs the player comes first at its moment, so a reload ending then hasn't ended and one starting then hasn't begun.
        var pack = RevivePack();
        var rotation = rotated ? new RotationOptions("sidearm", WeaponSwapMode.AfterKill) : null;
        DummyFightResult With(params (string Stat, double Value)[] values)
        {
            var changed = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
            {
                Stats = [.. pack.Pack.Stats.Select(stat => values.FirstOrDefault(entry => entry.Stat == stat.ID) is { Stat: not null } found
                    ? stat with { Formula = found.Value.ToString("R", CultureInfo.InvariantCulture), Min = null } : stat)]
            }).Compiled);
            return ReviveFight(changed, rotation: rotation);
        }
        var plain = With();
        var down = FirstTime(plain, DummyEventKind.Downed);
        var start = FirstTime(plain, DummyEventKind.ReloadStarted);
        // Ending then: cut short, and a reload of its own at the revive.
        Assert.Equal(down, start + (down - start));
        var ending = With(("equipped_weapon_reload_time", down - start));
        var revived = FirstTime(ending, DummyEventKind.Revived);
        Assert.Equal([start, revived], EventTimes(ending, DummyEventKind.ReloadStarted).Take(2));
        Assert.Equal(revived + (down - start), FirstTime(ending, DummyEventKind.ReloadCompleted), 9);
        // Starting then, after one round whose interval ends there: only the one at the revive.
        var rate = 60 / down;
        Assert.Equal(down, 60 / rate);
        var starting = With(("equipped_weapon_magazine_size", 1), ("equipped_weapon_rpm", rate));
        Assert.Equal(down, FirstTime(starting, DummyEventKind.Downed));
        Assert.Equal(revived, FirstTime(starting, DummyEventKind.ReloadStarted), 9);
        Assert.Single(EventTimes(starting, DummyEventKind.Shot), time => time < revived);
        // A rotation's last reload may be counted as decided and cut from the trace by the down that ends the fight.
        Assert.InRange(starting.Reloads - EventTimes(starting, DummyEventKind.ReloadStarted).Length, 0, rotated ? 1 : 0);
    }

    [Fact]
    public void ADownInTheOpenStaysInTheOpen()
    {
        // The cover phase starts 0.5 s after the down: the player doesn't move, so fire goes on landing until the revive.
        var pack = RevivePack();
        var down = FirstTime(ReviveFight(pack), DummyEventKind.Downed);
        var result = ReviveFight(pack, with: options => options with { PeekSeconds = down + 0.5, CoverSeconds = 30, PeekHitShare = 1 });
        Assert.Equal(down, FirstTime(result, DummyEventKind.Downed), 9);
        Assert.Contains(EventTimes(result, DummyEventKind.EnemyHit), time => time > down + 0.6 && time < down + ReviveSeconds);
        // Back up, they're behind cover.
        Assert.DoesNotContain(EventTimes(result, DummyEventKind.EnemyHit), time => time > down + ReviveSeconds);
    }

    [Fact]
    public void QueuedRepairsAndCyclesRunOnWhileDown()
    {
        // A Makeshift queue and a cycle talent's phases both end within the down, and the fight goes on after it.
        var pack = RevivePack();
        var queued = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
        {
            Stats = [.. pack.Pack.Stats.Select(stat => stat.ID switch
            {
                "makeshift_repairs_share" => stat with { Formula = "0.2" },
                "makeshift_repairs_duration" => stat with { Formula = "1" },
                // Enough regeneration that armor is integrated between hits.
                "armor_regeneration" => stat with { Formula = "1" },
                _ => stat
            })],
            Simulation = pack.Pack.Simulation with
            {
                Triggers = [.. pack.Pack.Simulation.Triggers.Select(rule => rule.Talent != "gear_talent_decoy_king" ? rule
                    : rule with { Phases = [rule.Phases[0] with { Until = 0.7 }, rule.Phases[1] with { Until = 1.4 }] })]
            }
        }).Compiled);
        var result = ReviveFight(queued, change: build => WithTalent(queued.Pack, build, "gear_talent_decoy_king"), duration: 30);
        var revived = FirstTime(result, DummyEventKind.Revived);
        var (armor, _) = PlayerPools(pack);
        // Nothing queued before the down is restored after it: until the next hit, only the regeneration of 1 a second.
        var hits = result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.EnemyHit && entry.Time >= revived).ToArray();
        Assert.True(hits.Length > 2 && hits[0].Time >= revived + 5);
        Assert.Equal(hits[0].Time - revived, result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.ArmorRepaired && entry.Time > revived && entry.Time <= hits[0].Time).Sum(entry => entry.Damage), 6);
        Assert.True(result.CyclePhaseUptime["decoy_king_amplification_percent"].Count >= 2);
    }

    [Fact]
    public void StatusesOnThePlayerHurtLessWhileDownAndNotAtAllOnceRevived()
    {
        // Target 5's hits bleed (2 s, a tick each 0.5 s). While down, a tick deals the downed share; after the revive, none for 5 s.
        var pack = RevivePack();
        var result = ReviveFight(pack, target: "5", duration: 30);
        var down = FirstTime(result, DummyEventKind.Downed);
        var revived = FirstTime(result, DummyEventKind.Revived);
        var ticks = result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.PlayerStatusTick && entry.Damage > 0).ToArray();
        var whole = ticks.First(entry => entry.Time < down).Damage;
        var during = ticks.Where(entry => entry.Time > down && entry.Time < revived).ToArray();
        Assert.NotEmpty(during);
        Assert.All(during, entry => Assert.Equal(whole * SoftRevive.DamageTaken, entry.Damage, 6));
        Assert.DoesNotContain(ticks, entry => entry.Time >= revived && entry.Time < revived + 5);
        Assert.True(result.Incoming!.StatusDamageTaken > 0);
        // A tick can be what takes the pool.
        var fatal = ReviveFight(RevivePack(SoftRevive with { DamageTaken = 1 }), target: "5", duration: 30);
        Assert.Equal(0, fatal.Incoming!.Revives);
        Assert.Equal(1, fatal.Incoming.DownedFraction);
    }

    [Fact]
    public void AShieldRaisedWhileDownTakesNoHits()
    {
        // A Striker Shield broken before the down, its cooldown ending within it: a downed player holds no shield, so fire goes on landing
        // on the pool. Back up, the shield takes hits again.
        DummyFightResult Fight(double cooldown, double duration = 30, SimSkillRevive? revive = null)
        {
            var pack = RevivePack(revive);
            pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
            {
                ActiveSkills = [.. pack.Pack.ActiveSkills.Select(skill => skill.ID != "skill_striker_shield" ? skill : skill with
                {
                    PreviewOutputs = [.. skill.PreviewOutputs.Select(output => output.ID switch
                    {
                        "cooldown_seconds" => output with { Formula = cooldown.ToString("R", CultureInfo.InvariantCulture) },
                        "skill_health" => output with { Formula = "100000" },
                        _ => output
                    })]
                })]
            }).Compiled);
            return ReviveFight(pack, second: "skill_striker_shield", duration: duration, with: options => options with { IncludeShield = true });
        }
        var first = Fight(100);
        var broken = FirstTime(first, DummyEventKind.ShieldBreak);
        var down = FirstTime(first, DummyEventKind.Downed);
        var raisedAt = down + 1;
        var result = Fight(raisedAt - broken);
        Assert.Contains(EventTimes(result, DummyEventKind.ShieldRaised), time => Math.Abs(time - raisedAt) < 1e-6);
        Assert.DoesNotContain(EventTimes(result, DummyEventKind.ShieldHit), time => time > down && time < down + ReviveSeconds);
        Assert.Contains(EventTimes(result, DummyEventKind.EnemyHit), time => time > raisedAt && time < down + ReviveSeconds);
        Assert.Contains(EventTimes(result, DummyEventKind.ShieldHit), time => time > down + ReviveSeconds);
        // A player who dies while down raises none after it.
        var dead = Fight(raisedAt - broken + 1, revive: SoftRevive with { DamageTaken = 1e-9, BleedSeconds = 1.5 });
        Assert.Equal(1, dead.Incoming!.DownedFraction);
        Assert.Equal(1, dead.Incoming.ShieldDeployments);
    }

    [Fact]
    public void AKitUnderWayIsCancelledUnspent()
    {
        // The last hits land while a kit is used: the kit stops, unspent, and is used after the revive.
        var pack = RevivePack();
        var result = ReviveFight(pack, kits: 1, duration: 60);
        var down = FirstTime(result, DummyEventKind.Downed);
        var revived = FirstTime(result, DummyEventKind.Revived);
        var starts = EventTimes(result, DummyEventKind.ArmorKitStarted);
        var used = EventTimes(result, DummyEventKind.ArmorKitUsed);
        if (starts.Length > 0 && starts[0] < down && !used.Any(time => time < down))
        {
            Assert.Equal(1, result.Incoming!.ArmorKitsCancelled);
            Assert.True(starts.Length > 1 && starts[1] >= revived);
            Assert.True(used[0] > revived);
        }
        else { Assert.Fail($"The kit isn't under way at the down: {string.Join(", ", starts)}; {string.Join(", ", used)}; {down}"); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACancelledKitDoesntHoldTheWeaponPastTheRevive(bool rotated)
    {
        // The revive comes before the kit would have ended: the player acts as they're back.
        var result = ReviveFight(RevivePack(seconds: 1), kits: 1, duration: 60, rotation: rotated ? new("sidearm", WeaponSwapMode.AfterKill) : null);
        Assert.Equal(1, result.Incoming!.ArmorKitsCancelled);
        var revived = FirstTime(result, DummyEventKind.Revived);
        Assert.Equal(revived, result.FirstFightEvents.First(entry => entry.Time >= revived && entry.Kind is DummyEventKind.Shot or DummyEventKind.ReloadStarted or DummyEventKind.ArmorKitStarted &&
            entry.Time >= revived).Time, 9);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATopUpInCoverStartsOverAtTheRevive(bool rotated)
    {
        // Down while a part-used magazine reloads in cover, for a single weapon and a rotation alike.
        var result = ReviveFight(RevivePack(seconds: 1), rotation: rotated ? new("sidearm", WeaponSwapMode.AfterKill) : null,
            with: options => options with { PeekSeconds = 0.8, CoverSeconds = 0.3, PeekHitShare = 1 });
        var down = FirstTime(result, DummyEventKind.Downed);
        var revived = FirstTime(result, DummyEventKind.Revived);
        var start = EventTimes(result, DummyEventKind.ReloadStarted).Last(time => time <= down);
        Assert.DoesNotContain(EventTimes(result, DummyEventKind.ReloadCompleted), time => time > start && time <= down);
        // Fewer than a magazine's shots since the last reload, so rounds were left.
        Assert.InRange(EventTimes(result, DummyEventKind.Shot).Count(time => time < start && time > EventTimes(result, DummyEventKind.ReloadCompleted).LastOrDefault(done => done < start, -1)), 1, 29);
        Assert.Contains(EventTimes(result, DummyEventKind.ReloadStarted), time => Math.Abs(time - revived) < 1e-9);
    }

    [Fact]
    public void ADownedPlayersOtherSkillsCarryOn()
    {
        // An Assault Turret out goes on while its user is down.
        var result = ReviveFight(RevivePack(), second: "skill_assault_turret");
        var down = FirstTime(result, DummyEventKind.Downed);
        Assert.Contains(EventTimes(result, DummyEventKind.SkillTick), time => time > down + 1 && time < down + ReviveSeconds);
    }

    [Fact]
    public void ADownedPlayerUsesNoSkill()
    {
        // A sticky bomb ready 1 s after the down is used at the revive.
        var pack = RevivePack();
        var down = FirstTime(ReviveFight(pack), DummyEventKind.Downed);
        var result = ReviveFight(pack, second: "skill_explosive_sticky_bomb", secondAfter: down + 1);
        Assert.Equal(down + ReviveSeconds, FirstTime(result, DummyEventKind.SkillImpact), 9);
        // Ready before, it's used then.
        Assert.Equal(1, FirstTime(ReviveFight(pack, second: "skill_explosive_sticky_bomb", secondAfter: 1), DummyEventKind.SkillImpact), 9);
    }

    [Fact]
    public void WithoutEnemyFireItStepsAside()
    {
        var pack = RevivePack();
        var result = ReviveFight(pack, incoming: false, driven: false);
        Assert.Contains(result.Limitations, note => note.Contains("Reviver Hive (it revives you once enemy fire downs you)", StringComparison.Ordinal));
    }

    [Fact]
    public void ANoteSaysWhatTheReviveDoes()
    {
        Assert.Contains(ReviveFight(RevivePack()).Limitations, note => note.StartsWith("A skill reviving you", StringComparison.Ordinal));
        Assert.DoesNotContain(ReviveFight(RevivePack(), skill: null).Limitations, note => note.StartsWith("A skill reviving you", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCommittedRuleAndOutputsAreAsR166ReadThem()
    {
        var pack = CommittedPack();
        var rule = pack.Pack.Simulation.Skills.Single(entry => entry.Skill == "skill_reviver_hive");
        Assert.Equal(("duration_seconds", "cooldown_seconds", true), (rule.Window, rule.Cooldown, rule.CooldownFromEnd));
        Assert.Equal(SoftRevive with { DamageTaken = 0.5 }, rule.Revive);
        double Output(string id, string tier = "0", string? talent = null, string haste = "0", string mod = "0", string? preview = null)
        {
            var changed = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
            {
                Stats = [.. pack.Pack.Stats.Select(stat => stat.ID switch
                {
                    "overcharged_skill_tier" => stat with { Formula = tier },
                    "effective_skill_haste" => stat with { Formula = haste },
                    _ => stat
                })]
            }).Compiled);
            var build = BuildSnapshot.Create(changed.Pack).WithActiveSkillSelection("skill_slot_1", "skill_reviver_hive");
            if (talent is not null) { build = WithTalent(changed.Pack, build, talent); }
            if (mod != "0") { build = build.WithSkillMod("skill_slot_1", "skill_reviver_hive", "drones", new("drones_revive_armor", double.Parse(mod, CultureInfo.InvariantCulture))); }
            return BuildResolver.Resolve(changed, build, preview is null ? Context : Context.Add(preview, "1")).ActiveSkillPreviews["skill_slot_1"].Outputs.Single(output => output.Id == id).Value;
        }
        // The armor share: a quarter, 0.1 a tier, the mod's roll, at most all of it.
        Assert.Equal(0.25, Output("revive_armor_share"), 9);
        Assert.Equal(0.85, Output("revive_armor_share", "6"), 9);
        Assert.Equal(1, Output("revive_armor_share", "7"), 9);
        Assert.Equal(0.35, Output("revive_armor_share", mod: "0.1"), 9);
        Assert.Equal(0.95, Output("revive_armor_share", "6", mod: "0.1"), 9);
        // The revive's seconds, 5 m from the hive: the flight shortens by the tier, no lower than 0.35 s.
        Assert.Equal(2.95, Output("revive_seconds"), 9);
        Assert.Equal(2.725, Output("revive_seconds", "6"), 9);
        Assert.Equal(2.55, Output("revive_seconds", "7"), 9);
        // The cooldown: 180 s whatever the charges, by haste and Combat Medic's bonus, 10 more in the bracket overcharged, 3 s at least.
        Assert.Equal(180, Output("cooldown_seconds"), 9);
        Assert.Equal(180, Output("cooldown_seconds", "6"), 9);
        Assert.Equal(180 / 11d, Output("cooldown_seconds", "7"), 9);
        Assert.Equal(120, Output("cooldown_seconds", haste: "0.5"), 9);
        Assert.Equal(180 / 1.45, Output("cooldown_seconds", talent: "gear_talent_combat_medic"), 9);
        Assert.Equal(3, Output("cooldown_seconds", "7", haste: "9"), 9);
        // Combat Medic's bonus is in the duration and health brackets too.
        Assert.Equal(180 * 1.45, Output("duration_seconds", talent: "gear_talent_combat_medic"), 9);
        Assert.Equal(1.45, Output("skill_health", talent: "gear_talent_combat_medic") / Output("skill_health"), 9);
        // And in the Overcharge comparison (R167), where the health's bracket holds 4.2 already.
        Assert.Equal(180 * 1.45, Output("overcharge_duration_seconds", talent: "gear_talent_combat_medic", preview: "overcharge_lifetime_preview"), 9);
        Assert.Equal((5.2 + 0.45) / 5.2, Output("overcharge_skill_health", talent: "gear_talent_combat_medic", preview: "overcharge_health_preview") / Output("overcharge_skill_health", preview: "overcharge_health_preview"), 9);
    }

    [Fact]
    public void TheCommittedHiveRevivesUnderFireThatSparesThePool()
    {
        // Under a quarter of the fire the pool outlasts the revive; under all of it the player dies while down.
        var pack = CommittedPack();
        var result = ReviveFight(pack, enemyHits: "25", duration: 60);
        var down = FirstTime(result, DummyEventKind.Downed);
        Assert.Equal(down + 2.95, FirstTime(result, DummyEventKind.Revived), 9);
        Assert.Equal(1, result.Incoming!.Revives);
        Assert.True(result.Incoming.TimeToDown!.Mean > down + 2.95 + 5);
        var fierce = ReviveFight(pack);
        Assert.Equal(0, fierce.Incoming!.Revives);
        Assert.Single(EventTimes(fierce, DummyEventKind.Died));
    }

    [Fact]
    public void HiveHasteIsInEveryHivesCooldown()
    {
        // R167: Transference Overclock's 15% a Skill Tier beside the tier's own haste, apart from Skill Haste, with a floor of 3 s.
        var pack = CommittedPack();
        double Cooldown(string hive, string hiveHaste = "0", string skillHaste = "0", string tier = "0")
        {
            var changed = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
            {
                Stats = [.. pack.Pack.Stats.Select(stat => stat.ID switch
                {
                    "hive_skill_haste" => stat with { Formula = hiveHaste },
                    "effective_skill_haste" => stat with { Formula = skillHaste },
                    "overcharged_skill_tier" => stat with { Formula = tier },
                    _ => stat
                })]
            }).Compiled);
            var build = BuildSnapshot.Create(changed.Pack).WithActiveSkillSelection("skill_slot_1", hive);
            return BuildResolver.Resolve(changed, build, Context).ActiveSkillPreviews["skill_slot_1"].Outputs.Single(output => output.Id == "cooldown_seconds").Value;
        }
        foreach (var (hive, seconds, overcharged) in new[] { ("skill_restorer_hive", 90d, 1.5), ("skill_booster_hive", 90, 1.5), ("skill_artificer_hive", 90, 0), ("skill_stinger_hive", 80, 3), ("skill_leech_hive", 60, 0), ("skill_reviver_hive", 180, 10) })
        {
            Assert.Equal(seconds, Cooldown(hive), 9);
            Assert.Equal(seconds / 1.9, Cooldown(hive, "0.9"), 9);
            Assert.Equal(seconds / 1.9 / 1.2, Cooldown(hive, "0.9", "0.2"), 9);
            Assert.Equal(seconds / (1 + overcharged), Cooldown(hive, tier: "7"), 9);
            Assert.Equal(seconds / (2.05 + overcharged), Cooldown(hive, "1.05", tier: "7"), 9);
            Assert.Equal(3, Cooldown(hive, "0.9", "100"), 9);
        }
        // The Stinger's average over its cycle holds the same cooldown, and a fight reads a hive's: the Leech's second use comes sooner.
        double Cycle(string hiveHaste)
        {
            var changed = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with { Stats = [.. pack.Pack.Stats.Select(stat => stat.ID == "hive_skill_haste" ? stat with { Formula = hiveHaste } : stat)] }).Compiled);
            var outputs = BuildResolver.Resolve(changed, BuildSnapshot.Create(changed.Pack).WithActiveSkillSelection("skill_slot_1", "skill_stinger_hive"), Context).ActiveSkillPreviews["skill_slot_1"].Outputs;
            double Of(string id) => outputs.Single(output => output.Id == id).Value;
            // Damage dealt a use: the average times the window and the cooldown.
            return Of("assumed_cycle_dps") * (Of("sting_window_seconds") + Of("cooldown_seconds"));
        }
        Assert.Equal(Cycle("0"), Cycle("0.9"), 3);
        double[] Leeches(string hiveHaste)
        {
            var changed = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with { Stats = [.. pack.Pack.Stats.Select(stat => stat.ID == "hive_skill_haste" ? stat with { Formula = hiveHaste } : stat)] }).Compiled);
            return EventTimes(ReviveFight(changed, "skill_leech_hive", duration: 60, with: options => options with { DriveSimpleBuffs = false, IncludeIncoming = false }), DummyEventKind.SkillImpact);
        }
        // Five leeches 0.5 s apart, 2 s to the end, then the cooldown: 64 s without haste, past the fight, and 4 + 60 / 1.9 with 90%.
        Assert.Equal(5, Leeches("0").Length);
        Assert.Equal(4 + 60 / 1.9, Leeches("0.9")[5], 6);
        // The talent: 15% a tier, to 7 overcharged.
        var transference = WithTalent(pack.Pack, BuildSnapshot.Create(pack.Pack), "gear_talent_transference_overclock");
        double Haste(string tier, string overcharge)
        {
            var changed = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(pack.Pack with
            {
                Stats = [.. pack.Pack.Stats.Select(stat => stat.ID switch { "skill_tier" => stat with { Formula = tier }, "skill_overcharge" => stat with { Formula = overcharge }, _ => stat })]
            }).Compiled);
            return BuildResolver.Resolve(changed, transference, Context).Calculation.Stats["hive_skill_haste"];
        }
        Assert.Equal(0.15, Haste("1", "0"), 9);
        Assert.Equal(0.9, Haste("6", "0"), 9);
        Assert.Equal(0.9, Haste("8", "0"), 9);
        Assert.Equal(1.05, Haste("6", "1"), 9);
    }
}
