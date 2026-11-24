using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

/// <summary>ADR 0028: the Leech Hive in fights, its repair per impact and its end delay (R144; owner, 2026-11-20).</summary>
public sealed partial class DummyWeaponSimulatorTests
{
    private const string LeechHive = "skill_leech_hive";

    // From r145.candidate.1 (R145) the pack carries the Leech Hive's 0.5 s interval and its rule with a repair per impact and a 2 s end
    // delay, and from r149.candidate.1 (R149) the repair 1.2 s after each hit. The committed pack as it is, or with its repair or delays
    // changed for comparison.
    private static CompiledPackExpressions LeechPack(bool repair = true, double endDelay = 2, double repairDelay = 1.2, double useArmorBelow = 0, string? regeneration = null)
    {
        var committed = CommittedPack();
        var rule = committed.Pack.Simulation.Skills.Single(rule => rule.Skill == LeechHive);
        Assert.Equal(("damage", "charge_capacity", "leech_interval_seconds", "cooldown_seconds", true, 2d, "repair", 1.2),
            (rule.Impact, rule.Impacts, rule.ImpactInterval, rule.Cooldown, rule.CooldownFromEnd, rule.EndDelay, rule.ImpactRepair, rule.RepairDelay));
        if (repair && endDelay == 2 && repairDelay == 1.2 && useArmorBelow == 0 && regeneration is null) { return committed; }
        return Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(committed.Pack with
        {
            Stats = regeneration is null ? committed.Pack.Stats
                : [.. committed.Pack.Stats.Select(stat => stat.ID == "estimated_armor_regeneration" ? stat with { Formula = regeneration } : stat)],
            Simulation = committed.Pack.Simulation with
            {
                Skills = [.. committed.Pack.Simulation.Skills.Select(other => other != rule ? other : rule with
                {
                    EndDelay = endDelay,
                    ImpactRepair = repair ? "repair" : "",
                    RepairDelay = repairDelay,
                    UseArmorBelow = useArmorBelow
                })]
            }
        }).Compiled);
    }

    // An enemy whose fire wears the player's armor down without downing them in a short fight; with no hits, for a long one.
    private static DummyFightResult LeechFight(CompiledPackExpressions pack, bool incoming = true, double duration = 10, bool hits = true, double? after = null)
    {
        // Armor to repair: the source's leech restores armor only, and a build with no gear has none.
        var build = Equip(pack.Pack, "primary_weapon_acr", "mask_badger_tuff_mask", "chest_badger_tuff_chest", "backpack_badger_tuff_backpack",
            "gloves_badger_tuff_gloves", "holster_badger_tuff_holster", "knees_badger_tuff_kneepads").WithActiveSkillSelection("skill_slot_1", LeechHive);
        var context = Context.Add("practical_hit_rate", "90").Add("enemy_target", "6").Add("enemy_difficulty", "1");
        return DummyWeaponSimulator.RunAgainstTargets(pack, build, hits ? context : context.Add("enemy_hit_rate", "0"), new()
        {
            Duration = duration,
            Mode = SimulationMode.Random,
            Iterations = 1,
            Seed = 1,
            IncludeIncoming = incoming,
            IncludeSkills = true,
            CaptureFirstFightEvents = true,
            SkillUse = after is { } seconds ? ImmutableDictionary<string, SkillUseCondition>.Empty.Add("skill_slot_1", new() { When = SkillUseCondition.AfterSeconds, Seconds = seconds })
                : ImmutableDictionary<string, SkillUseCondition>.Empty
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public void EachLeechHitsHalfASecondApartAndRepairsTheArmor()
    {
        // Deployed after 2 s, once enemy fire has taken some armor, and before the player goes down.
        var pack = LeechPack();
        var result = LeechFight(pack, after: 2);
        var hits = result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.SkillImpact).ToArray();
        // Five charges at tier 0, 0.5 s apart from the first.
        Assert.Equal(5, hits.Length);
        Assert.All(hits.Select((hit, index) => (hit, index)), pair => Assert.Equal(hits[0].Time + 0.5 * pair.index, pair.hit.Time, 9));
        Assert.Contains(result.Limitations, note => note.StartsWith("A skill's repair per impact (Leech Hive)", StringComparison.Ordinal));
        // Against the same hive without its repair: more armor repairs land 1.2 s after the leeches (regeneration flushed there too
        // counts in both), and the extra armor is at most five charges' repair (318,539 each at tier 0).
        var bare = LeechFight(LeechPack(repair: false), after: 2);
        int AfterHits(DummyFightResult fight) => fight.FirstFightEvents.Count(entry => entry.Kind == DummyEventKind.ArmorRepaired && hits.Any(hit => Math.Abs(hit.Time + 1.2 - entry.Time) < 1e-9));
        Assert.True(AfterHits(result) > AfterHits(bare));
        // Without the delay (ADR 0028's engine), the repairs land with the hits instead.
        var atHit = LeechFight(LeechPack(repairDelay: 0), after: 2);
        int AtHits(DummyFightResult fight) => fight.FirstFightEvents.Count(entry => entry.Kind == DummyEventKind.ArmorRepaired && hits.Any(hit => hit.Time == entry.Time));
        Assert.True(AtHits(atHit) > AtHits(bare));
        Assert.InRange(result.Incoming!.ArmorRepaired - bare.Incoming!.ArmorRepaired, 1, 5 * 318539 + 1e-6);
    }

    [Fact]
    public void TheHiveEndsTwoSecondsAfterItsLastLeechBeforeItsCooldown()
    {
        // The cooldown (60 s) runs from the hive's end, 2 s after its last charge: the next use comes 2 + 2 + 60 s after the first hit.
        Impact[] Uses(double endDelay) => [.. LeechFight(LeechPack(endDelay: endDelay), duration: 70, hits: false).FirstFightEvents
            .Where(entry => entry.Kind == DummyEventKind.SkillImpact).Select(entry => new Impact(entry.Time))];
        var delayed = Uses(2);
        Assert.Equal(10, delayed.Length);
        Assert.Equal(delayed[0].Time + 64, delayed[5].Time, 9);
        Assert.Equal(delayed[5].Time - 2, Uses(0)[5].Time, 9);
    }

    [Fact]
    public void WithoutEnemyFireTheLeechRepairsNothing()
    {
        var result = LeechFight(LeechPack(), incoming: false);
        Assert.Equal(5, result.FirstFightEvents.Count(entry => entry.Kind == DummyEventKind.SkillImpact));
        Assert.DoesNotContain(result.FirstFightEvents, entry => entry.Kind == DummyEventKind.ArmorRepaired);
        Assert.Contains("This fight has no enemy fire, so a skill's repair per impact (Leech Hive) has nothing to restore", result.Limitations);
    }

    [Fact]
    public void ADelayedRepairLandsThatLongAfterEachLeechWhileTheFightRuns()
    {
        // ADR 0030: the source lands each repair a travel time plus 0.8 s after the hit, 1.2 s at the assumed 5 m, as the committed rule
        // has it. Hits at 2 to 4 s, as with the repair at the hit; in a 5 s fight the last repair, due at 5.2 s, lands nothing.
        var hits = LeechFight(LeechPack(repairDelay: 0), duration: 5, after: 2).FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.SkillImpact).Select(entry => entry.Time).ToArray();
        Assert.Equal(5, hits.Length);
        var delayed = LeechFight(LeechPack(), duration: 5, after: 2);
        var bare = LeechFight(LeechPack(repair: false), duration: 5, after: 2);
        // The hits don't move, and a repair lands 1.2 s after each of the first four, where none does without the repair.
        Assert.Equal(hits, delayed.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.SkillImpact).Select(entry => entry.Time));
        double[] RepairsAt(DummyFightResult fight) => [.. fight.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.ArmorRepaired).Select(entry => entry.Time)];
        foreach (var due in hits.Take(4).Select(hit => hit + 1.2))
        {
            Assert.Contains(RepairsAt(delayed), at => Math.Abs(at - due) < 1e-9);
            Assert.DoesNotContain(RepairsAt(bare), at => Math.Abs(at - due) < 1e-9);
        }
        Assert.All(RepairsAt(delayed), at => Assert.True(at < 5));
        Assert.InRange(delayed.Incoming!.ArmorRepaired - bare.Incoming!.ArmorRepaired, 1, 4 * 318539 + 1e-6);
    }

    [Fact]
    public void ASkillWaitingForArmorIsUsedWhenTheFirstHitLands()
    {
        // ADR 0030: used only once armor is below full; the hive is ready at 0 s and waits for the enemy's first damaging hit.
        var result = LeechFight(LeechPack(useArmorBelow: 1), duration: 10);
        var firstHit = result.FirstFightEvents.First(entry => entry.Kind == DummyEventKind.EnemyHit && entry.Damage > 0).Time;
        Assert.True(firstHit > 0);
        var hits = result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.SkillImpact).ToArray();
        Assert.Equal(5, hits.Length);
        Assert.Equal(firstHit, hits[0].Time, 9);
        Assert.Contains("Used only once the player's armor is below a share: Leech Hive below full", result.Limitations);
        Assert.DoesNotContain(result.Limitations, note => note.StartsWith("Never used, since the player's armor", StringComparison.Ordinal));
        // With no hits, armor stays full and the hive is never used, which the result says.
        var unhit = LeechFight(LeechPack(useArmorBelow: 1), duration: 10, hits: false);
        Assert.DoesNotContain(unhit.FirstFightEvents, entry => entry.Kind == DummyEventKind.SkillImpact);
        Assert.Contains("Never used, since the player's armor never fell below its share: Leech Hive", unhit.Limitations);
    }

    [Fact]
    public void ASkillWaitingForArmorWaitsAgainOnItsNextCycle()
    {
        // Regeneration fast enough to refill the armor between hits: when the hive is ready again (64 s after its first use), the armor
        // is full, so it waits for the next damaging hit. (The fight reads the armor as regeneration leaves it at the deployment; the
        // player's own shots also bring it up to date, so this doesn't single that read out.)
        var result = LeechFight(LeechPack(useArmorBelow: 1, regeneration: "1000000000"), duration: 90);
        var damaging = result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.EnemyHit && entry.Damage > 0).Select(entry => entry.Time).ToArray();
        var hits = result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.SkillImpact).Select(entry => entry.Time).ToArray();
        Assert.Equal(10, hits.Length);
        Assert.Contains(damaging, at => Math.Abs(at - hits[5]) < 1e-9);
        Assert.True(hits[5] > hits[0] + 64);
    }

    [Fact]
    public void WithoutEnemyFireASkillWaitingForArmorStepsAside()
    {
        var result = LeechFight(LeechPack(useArmorBelow: 1), incoming: false);
        Assert.DoesNotContain(result.FirstFightEvents, entry => entry.Kind == DummyEventKind.SkillImpact);
        Assert.Contains("Run without skills used only once armor falls, since enemy fire is off: Leech Hive (used once armor is below full)", result.Limitations);
        Assert.Contains("Run without skills used only once armor falls, since enemy fire is off: Leech Hive (used once armor is below 90% of its maximum)",
            LeechFight(LeechPack(useArmorBelow: 0.9), incoming: false).Limitations);
    }

    private sealed record Impact(double Time);
}
