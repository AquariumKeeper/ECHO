using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

/// <summary>
/// ADR 0033: the Precision Smart Cover, on the committed rows (R157, r157.candidate.1; the owner's call, 2026-11-22): while its
/// skill_out buff is on, each swap refills the weapon put away, with no reload event.
/// </summary>
public sealed partial class DummyWeaponSimulatorTests
{
    private const string PrecisionKey = "precision_cover_active";

    // The committed pack, its refill on swaps taken off if asked.
    private static CompiledPackExpressions PrecisionPack(bool refill)
    {
        var committed = CommittedPack();
        if (refill) { return committed; }
        return Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(committed.Pack with
        {
            Simulation = committed.Pack.Simulation with
            {
                Triggers = [.. committed.Pack.Simulation.Triggers.Select(rule => rule.Key == PrecisionKey ? rule with { RefillOnSwap = false } : rule)]
            }
        }).Compiled);
    }

    private static DummyFightResult PrecisionFight(CompiledPackExpressions pack, bool cover = true, double duration = 20)
    {
        var build = Equip(pack.Pack, "primary_weapon_acr").WithEquipment("sidearm", "sidearm_d50").WithActiveSkillSelection("skill_slot_1", "skill_precision_smart_cover");
        var context = Context.Add("practical_hit_rate", "100").Add("enemy_target", "6").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "0");
        if (cover) { context = context.Add(DummyFightOptions.CoverInput, "true"); }
        return DummyWeaponSimulator.RunAgainstTargets(pack, build, context, new()
        {
            Duration = duration,
            Mode = SimulationMode.Random,
            Iterations = 1,
            Seed = 1,
            IncludeIncoming = true,
            IncludeSkills = true,
            DriveSimpleBuffs = true,
            CaptureFirstFightEvents = true,
            Rotation = new RotationOptions("sidearm")
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static int Reloads(DummyFightResult result) => result.FirstFightEvents.Count(entry => entry.Kind == DummyEventKind.ReloadStarted);

    [Fact]
    public void ThePrecisionRefillsTheWeaponPutAwayOnEachSwap()
    {
        // Swapping when a magazine empties: with the refill the rifle comes back full, so it's never reloaded while the cover is out;
        // without it, each return to the empty rifle reloads it.
        var refilled = PrecisionFight(PrecisionPack(refill: true));
        var held = PrecisionFight(PrecisionPack(refill: false));
        Assert.Equal(20, Assert.Contains(PrecisionKey, refilled.TimedTriggerUptime).Seconds, 9);
        Assert.Equal(0, Reloads(refilled));
        Assert.True(Reloads(held) > 0);
        Assert.True(refilled.ShotsFired > held.ShotsFired);
    }

    [Fact]
    public void ThePrecisionsRefillEndsWithTheCover()
    {
        // The cover's 20 s ends with its 45 s cooldown past the fight: no reload while it's out, and reloads again after.
        var result = PrecisionFight(PrecisionPack(refill: true), duration: 40);
        var reloads = result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.ReloadStarted).Select(entry => entry.Time).ToArray();
        Assert.DoesNotContain(reloads, time => time < 20);
        Assert.Contains(reloads, time => time >= 20);
    }

    [Fact]
    public void ThePrecisionsBonusesNeedItsKeyAndCover()
    {
        // At tier 0: weapon handling +0.15 and damage to targets out of cover +0.1, only with its key and the player in cover.
        var pack = CommittedPack();
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr").WithActiveSkillSelection("skill_slot_1", "skill_precision_smart_cover");
        IReadOnlyDictionary<string, double> Stats(ImmutableDictionary<string, string> context) => BuildResolver.Resolve(pack, build, context).Calculation.Stats;
        var off = Stats(Context);
        var on = Stats(Context.Add(PrecisionKey, "true").Add(DummyFightOptions.CoverInput, "true"));
        var keyOnly = Stats(Context.Add(PrecisionKey, "true"));
        Assert.Equal(off["weapon_handling"] + 0.15, on["weapon_handling"], 9);
        Assert.Equal(off["dtoc"] + 0.1, on["dtoc"], 9);
        Assert.Equal(off["weapon_handling"], keyOnly["weapon_handling"], 9);
        Assert.Equal(off["dtoc"], keyOnly["dtoc"], 9);
        // Nor in cover without its key: every cover fight before the cover deploys.
        var coverOnly = Stats(Context.Add(DummyFightOptions.CoverInput, "true"));
        Assert.Equal(off["weapon_handling"], coverOnly["weapon_handling"], 9);
        Assert.Equal(off["dtoc"], coverOnly["dtoc"], 9);
    }

    [Fact]
    public void ThePrecisionsOutOfCoverDamageReachesTheFight()
    {
        // With "Target out of cover" on, the Precision's 10% reaches the ACR's and D50's hits while it's out; with it off, it doesn't.
        CompiledPackExpressions pack = CommittedPack();
        var build = Equip(pack.Pack, "primary_weapon_acr").WithEquipment("sidearm", "sidearm_d50");
        DummyFightResult Fight(string? skill, bool outOfCover)
        {
            var built = skill is null ? build : build.WithActiveSkillSelection("skill_slot_1", skill);
            var context = Context.Add("practical_hit_rate", "100").Add("enemy_target", "6").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "0")
                .Add(DummyFightOptions.CoverInput, "true");
            if (outOfCover) { context = context.Add("enemy_out_of_cover", "1"); }
            return DummyWeaponSimulator.RunAgainstTargets(pack, built, context, new()
            {
                Duration = 20,
                Mode = SimulationMode.Random,
                Iterations = 1,
                Seed = 1,
                IncludeIncoming = true,
                IncludeSkills = true,
                DriveSimpleBuffs = true,
                Rotation = new RotationOptions("sidearm")
            }, cancellationToken: TestContext.Current.CancellationToken);
        }
        var gain = Fight("skill_precision_smart_cover", true).WeaponDps.Mean / Fight("skill_precision_smart_cover", false).WeaponDps.Mean;
        var baseline = Fight(null, true).WeaponDps.Mean / Fight(null, false).WeaponDps.Mean;
        Assert.True(gain > baseline * 1.05, $"{gain} against {baseline}");
    }

    [Fact]
    public void OutOfCoverThePrecisionRefillsNothing()
    {
        // Its rule needs the player in cover, so out of cover it takes no part: the same reloads as without the refill.
        Assert.Equal(Reloads(PrecisionFight(PrecisionPack(refill: false), cover: false)), Reloads(PrecisionFight(PrecisionPack(refill: true), cover: false)));
        Assert.DoesNotContain(PrecisionKey, PrecisionFight(PrecisionPack(refill: true), cover: false).TimedTriggerUptime.Keys);
    }
}
