using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

/// <summary>
/// ADR 0031 stages 2b and 2c: the Artificer Hive in fights, on the committed rows (R151, r151.candidate.1): charges to the build's other
/// skill while its use runs, 5 m from the hive (0.75 s flights), each turning on a 10 s bonus that refreshes and adding the refresh seconds
/// to the use, and pulses while holstered.
/// </summary>
public sealed partial class DummyWeaponSimulatorTests
{
    private const string ArtificerHive = "skill_artificer_hive";
    private const string ArtificerKey = "artificer_bonus_active";

    // The committed pack with the Assault Turret's window cut so a lengthened use shows inside the fight, and the holstered pulses taken
    // out unless asked for.
    private static CompiledPackExpressions ArtificerPack(string turretSeconds = "10", bool holstered = false)
    {
        var committed = CommittedPack();
        return Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(committed.Pack with
        {
            ActiveSkills = [.. committed.Pack.ActiveSkills.Select(skill => skill.ID != "skill_assault_turret" ? skill : skill with
            {
                PreviewOutputs = [.. skill.PreviewOutputs.Select(output => output.ID == "duration_seconds" ? output with { Formula = turretSeconds } : output)]
            })],
            Simulation = committed.Pack.Simulation with
            {
                Skills = [.. committed.Pack.Simulation.Skills.Select(rule => rule.Skill == ArtificerHive && !holstered ? rule with { HolsteredEvery = 0, HolsteredLag = 0 } : rule)]
            }
        }).Compiled);
    }

    private static DummyFightResult ArtificerFight(CompiledPackExpressions pack, string? other = "skill_assault_turret", double duration = 70, double hiveAfter = 0)
    {
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr").WithActiveSkillSelection("skill_slot_1", ArtificerHive);
        if (other is not null) { build = build.WithActiveSkillSelection("skill_slot_2", other); }
        // No enemy hits, so the player isn't downed. The turret's ticks wait while a killed enemy's replacement arrives.
        return DummyWeaponSimulator.RunAgainstTargets(pack, build, Context.Add("enemy_target", "6").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "0"), new()
        {
            Duration = duration,
            IncludeIncoming = true,
            Mode = SimulationMode.Random,
            Iterations = 1,
            Seed = 1,
            IncludeSkills = true,
            DriveSimpleBuffs = true,
            CaptureFirstFightEvents = true,
            SkillUse = hiveAfter > 0
                ? ImmutableDictionary<string, SkillUseCondition>.Empty.Add("skill_slot_1", new() { When = SkillUseCondition.AfterSeconds, Seconds = hiveAfter })
                : ImmutableDictionary<string, SkillUseCondition>.Empty
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static double[] TurretTicks(DummyFightResult result) =>
        [.. result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.SkillTick && entry.WeaponSlot == "skill_slot_2").Select(entry => entry.Time)];

    [Fact]
    public void TheArtificersChargesLengthenTheTurretsUse()
    {
        var result = ArtificerFight(ArtificerPack());
        // Eight charges at tier 0, each 3 s more: launched at 0, then at the first 0.2 s check 0.75 s after each lands (1.6 s apart), so all
        // land while the turret runs. Its 10 s window ends at 34 s, and its 25 s cooldown from the end brings it back at 59 s.
        var ticks = TurretTicks(result);
        Assert.Contains(ticks, time => time > 33 && time <= 34 + 1e-9);
        // Each moment once: the added batches neither repeat nor overlap the use's own.
        Assert.Equal(ticks.Length, ticks.Distinct().Count());
        Assert.DoesNotContain(ticks, time => time > 34 + 1e-9 && time <= 59);
        Assert.Contains(ticks, time => time > 59);
        // The bonus from the first landing (0.75 s) until 10 s after the last (11.95 s), each charge restarting it.
        var uptime = Assert.Contains(ArtificerKey, result.TimedTriggerUptime);
        Assert.Equal(21.2, uptime.Seconds, 6);
        Assert.Contains(result.Limitations, note => note.StartsWith("Hives charging skills reach the build's other skills", StringComparison.Ordinal) && note.EndsWith(": Artificer Hive", StringComparison.Ordinal));
    }

    [Fact]
    public void TheHolsteredArtificerPulsesOnceItsUseEnds()
    {
        var result = ArtificerFight(ArtificerPack(holstered: true));
        // As above until the hive's use ends at 14.7 s (2 s after its last charge's hold), the turret running to 34 s. Holstered, its
        // pulses at 15 s and 25 s (each target held 5 s after a 5 s flight) land at 20 s and 30 s, each 3 s more: the turret runs to 40 s.
        // The pulse at 35 s lands at 40 s, as the turret ends, so nothing; its cooldown from 40 s brings it back at 65 s.
        var ticks = TurretTicks(result);
        Assert.Contains(ticks, time => time > 39 && time <= 40 + 1e-9);
        Assert.DoesNotContain(ticks, time => time > 40 + 1e-9 && time <= 65);
        Assert.Contains(ticks, time => time > 65);
        Assert.Equal(ticks.Length, ticks.Distinct().Count());
        // The bonus from 0.75 s, unbroken, to 10 s after the last landing (30 s).
        Assert.Equal(39.25, Assert.Contains(ArtificerKey, result.TimedTriggerUptime).Seconds, 6);
        Assert.Contains("Pulse while holstered, between their uses, as their rules give them: Artificer Hive", result.Limitations);
    }

    [Fact]
    public void AHolsteredPulsesMarkHoldsTheDeployedHivesCharges()
    {
        // The hive first used at 3 s: holstered before then, its pulse at 0 s lands at 5 s (3 s more, to 13 s) and marks the turret to
        // 10 s, so its deployed charges wait for that: launched from 10 s, 1.6 s apart, landing from 10.75 s to 21.95 s (to 37 s). Its use
        // ends at 24.7 s; pulses at 25 s and 35 s land at 30 s (to 40 s) and 40 s, as the turret ends. The bonus runs from 5 s to 40 s.
        var result = ArtificerFight(ArtificerPack(holstered: true), hiveAfter: 3);
        var ticks = TurretTicks(result);
        Assert.Contains(ticks, time => time > 39 && time <= 40 + 1e-9);
        Assert.DoesNotContain(ticks, time => time > 40 + 1e-9 && time <= 65);
        Assert.Equal(35, Assert.Contains(ArtificerKey, result.TimedTriggerUptime).Seconds, 6);
    }

    [Fact]
    public void TheCommittedTurretReadsTheArtificersBonusLive()
    {
        // R152: the committed turret reads its rate live, so a turret out before the first charge lands deals more once the bonus is on
        // than the same turret read as it deployed.
        var live = ArtificerPack();
        var held = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(live.Pack with
        {
            Simulation = live.Pack.Simulation with
            {
                Skills = [.. live.Pack.Simulation.Skills.Select(rule => rule.Skill == "skill_assault_turret" ? rule with { RateRead = "" } : rule)]
            }
        }).Compiled);
        Assert.Equal("live", live.Pack.Simulation.Skills.Single(rule => rule.Skill == "skill_assault_turret").RateRead);
        Assert.True(ArtificerFight(live).SkillDps!.Total.Mean > ArtificerFight(held).SkillDps!.Total.Mean);
    }

    [Fact]
    public void TheArtificersKeyRaisesTheSkillBonusStat()
    {
        // The key a charge turns on raises artificer_skill_bonus by the tier's amount (0.1 at tier 0), which the other skills' damage
        // and repair formulas read; off, as by default, it's 0. The Assault Turret, Oxidizer and Fixer read it live (R152); other
        // skills as they deploy, so in fights it reaches only their uses deployed while the key is on.
        var pack = CommittedPack();
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr").WithActiveSkillSelection("skill_slot_1", ArtificerHive);
        Assert.Equal(0.1, BuildResolver.Resolve(pack, build, Context.Add(ArtificerKey, "true")).Calculation.Stats["artificer_skill_bonus"], 12);
        Assert.Equal(0, BuildResolver.Resolve(pack, build, Context).Calculation.Stats["artificer_skill_bonus"]);
    }

    // An Artificer beside a repair skill under fire, its repairs' amounts in the first fight, the skill's rule read live or not and
    // changed as asked.
    private static double[] LiveRepairs(string skill, bool live, Func<SimSkillDefinition, SimSkillDefinition>? change = null)
    {
        var committed = CommittedPack();
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(committed.Pack with
        {
            Simulation = committed.Pack.Simulation with
            {
                Skills = [.. committed.Pack.Simulation.Skills.Select(rule => rule.Skill != skill ? rule : (change ?? (row => row))(rule with { RateRead = live ? "live" : "" }))]
            }
        }).Compiled);
        var build = Equip(pack.Pack, "primary_weapon_acr", "mask_badger_tuff_mask", "chest_badger_tuff_chest", "backpack_badger_tuff_backpack",
            "gloves_badger_tuff_gloves", "holster_badger_tuff_holster", "knees_badger_tuff_kneepads")
            .WithActiveSkillSelection("skill_slot_1", ArtificerHive).WithActiveSkillSelection("skill_slot_2", skill);
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, Context.Add("practical_hit_rate", "90").Add("enemy_target", "6").Add("enemy_difficulty", "1"), new()
        {
            Duration = 20,
            Mode = SimulationMode.Random,
            Iterations = 1,
            Seed = 1,
            IncludeIncoming = true,
            IncludeSkills = true,
            DriveSimpleBuffs = true,
            CaptureFirstFightEvents = true
        }, cancellationToken: TestContext.Current.CancellationToken);
        return [.. result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.SkillRepair && entry.WeaponSlot == "skill_slot_2").Select(entry => entry.Damage)];
    }

    [Fact]
    public void ALiveRepairTakesTheArtificersBonusOnAFixerAlreadyOut()
    {
        // The Fixer Drone deploys once armor falls, before the Artificer's first charge lands on it; read live, its repairs after that
        // take the bonus (+10% at tier 0), and read as it deployed they don't.
        var held = LiveRepairs("skill_fixer_drone", live: false);
        var live = LiveRepairs("skill_fixer_drone", live: true);
        // Each repair of 17,918 armor (uncapped here) as deployed; live, the first is read before the charge lands, and every one after
        // it, the bonus held by the charges and the holstered pulses, 10% more.
        Assert.All(held, amount => Assert.Equal(held[0], amount, 6));
        Assert.Equal(held[0], live[0], 6);
        Assert.All(live.Skip(1), amount => Assert.Equal(held[0] * 1.1, amount, 6));
        Assert.Equal(held.Length, live.Length);
        Assert.True(live.Length >= 10);
    }

    [Fact]
    public void ALiveRepairIsReadAtTheCheckForADelayedRepairAndACloud()
    {
        var heldLag = LiveRepairs("skill_fixer_drone", live: false, rule => rule with { RepairLag = 0.3 });
        var liveLag = LiveRepairs("skill_fixer_drone", live: true, rule => rule with { RepairLag = 0.3 });
        var heldCloud = LiveRepairs("skill_mender_seeker_mine", live: false);
        var liveCloud = LiveRepairs("skill_mender_seeker_mine", live: true);
        // A delayed repair lands what its check read: the first before the first charge lands, the rest 10% more.
        Assert.True(liveLag.Length >= 10);
        Assert.Equal(heldLag[0], liveLag[0], 6);
        Assert.All(liveLag.Skip(1), amount => Assert.Equal(heldLag[0] * 1.1, amount, 6));
        // The Mender's first cloud starts once armor is below 90%, after the first charge lands: every tick, half the rate each 0.5 s,
        // read at the check that made its cloud, is 10% more than as deployed.
        Assert.True(liveCloud.Length >= 10);
        Assert.Equal(heldCloud.Length, liveCloud.Length);
        Assert.All(heldCloud, amount => Assert.Equal(heldCloud[0], amount, 6));
        Assert.All(liveCloud, amount => Assert.Equal(heldCloud[0] * 1.1, amount, 6));
    }

    [Fact]
    public void AChargeLandingAfterTheUseEndsGivesNothing()
    {
        // A 0.5 s turret is over before the first charge lands (0.75 s): it lands nothing, so no bonus and no longer use.
        var result = ArtificerFight(ArtificerPack(turretSeconds: "0.5"));
        Assert.Equal(0, Assert.Contains(ArtificerKey, result.TimedTriggerUptime).Seconds);
        Assert.Equal(0.5, TurretTicks(result).Where(time => time < 20).Max(), 9);
    }

    [Fact]
    public void AnArtificerWithNothingToReachStepsAside()
    {
        var result = ArtificerFight(ArtificerPack(), other: null);
        Assert.Contains("Run without hives charging skills, since no other skill in the fight runs a use for them to reach: Artificer Hive", result.Limitations);
        // So does one beside a skill with no window of its own.
        var beside = ArtificerFight(ArtificerPack(), other: "skill_explosive_sticky_bomb");
        Assert.Contains("Run without hives charging skills, since no other skill in the fight runs a use for them to reach: Artificer Hive", beside.Limitations);
    }
}
