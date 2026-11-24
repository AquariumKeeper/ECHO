using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

/// <summary>
/// ADR 0031 stage 2a: the Booster Hive in fights, on the committed rows (R151, r151.candidate.1): a stim at a time, the hive 5 m away,
/// each charge firing skill_charge for the hive's own buff rule, whose key gates weapon handling.
/// </summary>
public sealed partial class DummyWeaponSimulatorTests
{
    private const string BoosterHive = "skill_booster_hive";
    private const string StimKey = "booster_stim_active";

    // The committed pack, its stim's weapon handling gated on another key, or its buff given a fixed duration, if asked.
    private static CompiledPackExpressions BoosterPack(string gate = StimKey, double? buffSeconds = null)
    {
        var committed = CommittedPack();
        if (gate == StimKey && buffSeconds is null) { return committed; }
        return Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(committed.Pack with
        {
            Selectors = gate == StimKey ? committed.Pack.Selectors
                : [.. committed.Pack.Selectors, new() { ID = gate, Name = "Never on", Values = ["true"], Section = "Talents and gear" }],
            ActiveSkills = [.. committed.Pack.ActiveSkills.Select(skill => skill.ID != BoosterHive ? skill : skill with
            {
                Modifiers = [.. skill.Modifiers.Select(modifier => modifier.RequiresContext?.ContainsKey(StimKey) == true
                    ? modifier with { RequiresContext = ImmutableDictionary<string, string>.Empty.Add(gate, "true") } : modifier)]
            })],
            Simulation = committed.Pack.Simulation with
            {
                Triggers = [.. committed.Pack.Simulation.Triggers.Select(rule => rule.Talent == BoosterHive && buffSeconds is { } seconds
                    ? rule with { Duration = seconds, DurationStat = "" } : rule)]
            }
        }).Compiled);
    }

    // No enemy hits, so the player isn't downed before the hive runs out.
    private static DummyFightResult BoosterFight(CompiledPackExpressions pack, bool triggers = true, double duration = 60)
    {
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr").WithActiveSkillSelection("skill_slot_1", BoosterHive);
        return DummyWeaponSimulator.RunAgainstTargets(pack, build, Context.Add("practical_hit_rate", "90").Add("enemy_target", "6").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "0"), new()
        {
            Duration = duration,
            Mode = SimulationMode.Random,
            Iterations = 1,
            Seed = 1,
            IncludeIncoming = true,
            IncludeSkills = true,
            DriveSimpleBuffs = triggers,
            CaptureFirstFightEvents = true
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public void TheBoostersStimsComeOneAtATimeUntilItsChargesAreSpent()
    {
        var result = BoosterFight(BoosterPack());
        // Eight charges at tier 0, each a 5 s stim: the first lands 0.35 s after the use, the next launches at the first 0.2 s check
        // after a stim ends (5.4 s on) and lands 0.35 s later. 40 s on in all, the last ending at 43.15 s; the 90 s cooldown from the
        // hive's end (2 s later) runs past the fight.
        var uptime = Assert.Contains(StimKey, result.TimedTriggerUptime);
        Assert.Equal(40, uptime.Seconds, 6);
        Assert.Contains("Buff hives' charges turn on their buffs one at a time, needed or not, timed as their rules give them (the player in range): Booster Hive", result.Limitations);
    }

    [Fact]
    public void AtTier6TheBoostersStimsLast10Seconds()
    {
        // The stim's stat and the charges' spacing share one formula, so a tier-6 Booster runs: 10 s stims, the next launched at the
        // first check after one ends (10.4 s apart), each landing 0.35 s later. Five whole stims and the sixth from 52.35 s to the end.
        var committed = CommittedPack();
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(committed.Pack with
        {
            Stats = [.. committed.Pack.Stats.Select(stat => stat.ID == "skill_tier" ? stat with { Formula = "6" } : stat)]
        }).Compiled);
        Assert.Equal(57.65, Assert.Contains(StimKey, BoosterFight(pack).TimedTriggerUptime).Seconds, 6);
    }

    [Fact]
    public void TheBoostersStimSpeedsReloads()
    {
        // The stim's weapon handling shortens the ACR's reloads, so more shots in the same fight than with the same bonus gated on a key
        // nothing turns on.
        Assert.True(BoosterFight(BoosterPack()).ShotsFired > BoosterFight(BoosterPack(gate: "booster_never")).ShotsFired);
    }

    [Fact]
    public void ABuffHivesBuffLastsAsLongAsItsChargesAreSpacedFor()
    {
        // A fixed 4 s buff on charges spaced for 5 s would leave gaps: refused.
        Assert.Equal("trigger-unsupported", Assert.Throws<SimulationException>(() => BoosterFight(BoosterPack(buffSeconds: 4))).Code);
    }

    [Fact]
    public void WithoutSkillsABuffHivesKeyReadsTheUsersSetting()
    {
        // With talent triggers but no skills, the hive's charges never come, so its rule takes no part and the key isn't driven.
        var pack = BoosterPack();
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr").WithActiveSkillSelection("skill_slot_1", BoosterHive);
        var result = DummyWeaponSimulator.RunAgainstTargets(pack, build, Context.Add("enemy_target", "6").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "0"), new()
        {
            Duration = 10,
            Mode = SimulationMode.Random,
            Iterations = 1,
            Seed = 1,
            IncludeIncoming = true,
            DriveSimpleBuffs = true
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(StimKey, result.TimedTriggerUptime.Keys);
    }

    [Fact]
    public void WithoutTalentTriggersABuffHiveStepsAside()
    {
        var result = BoosterFight(BoosterPack(), triggers: false);
        Assert.DoesNotContain(StimKey, result.TimedTriggerUptime.Keys);
        Assert.Contains("Run without skills whose charges turn on buffs, since talent triggers are off: Booster Hive", result.Limitations);
    }
}
