using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

/// <summary>ADR 0026: Symbiosis's shield drain and repair from damage in fights under fire (R140; owner, 2026-11-20).</summary>
public sealed partial class DummyWeaponSimulatorTests
{
    private const string Symbiosis = "weapon_talent_symbiosis";

    // The committed pack with Symbiosis's repair share and drain (shield_health_loss_share, from r141.candidate.1) set for a test.
    private static CompiledPackExpressions SymbiosisPack(double drain = 0.1, double share = 0.25)
    {
        var committed = CommittedPack().Pack;
        return Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(committed with
        {
            Talents = [.. committed.Talents.Select(talent => talent.ID != Symbiosis ? talent : talent with
            {
                Modifiers = [.. talent.Modifiers.Select(modifier => modifier with
                {
                    Value = new ModifierValue(modifier.Stat == DummyWeaponSimulator.ShieldDrainStat ? drain : share)
                })]
            })]
        }).Compiled);
    }

    private static BuildSnapshot Oxpecker(GamePack pack, string shield) => Equip(pack, "primary_weapon_oxpecker").WithActiveSkillSelection("skill_slot_1", shield);

    private static readonly ImmutableDictionary<string, string> UnderFire = Context.Add("practical_hit_rate", "90").Add("enemy_target", "1").Add("enemy_difficulty", "1");

    private static DummyFightResult SymbiosisFight(CompiledPackExpressions pack, BuildSnapshot build, bool shield = true, RotationOptions? rotation = null) =>
        DummyWeaponSimulator.RunAgainstTargets(pack, build, UnderFire, new()
        {
            Duration = 10,
            Mode = SimulationMode.Random,
            Iterations = 1,
            Seed = 1,
            DriveSimpleBuffs = true,
            IncludeIncoming = true,
            IncludeShield = shield,
            CaptureFirstFightEvents = true,
            Rotation = rotation
        }, cancellationToken: TestContext.Current.CancellationToken);

    [Fact]
    public void SymbiosisRunsUnderFireWithTheCommittedPacksDrain()
    {
        // From r141.candidate.1 (R141) Symbiosis carries its 10% drain beside its 25% repair share, so the fight runs.
        var pack = CommittedPack();
        var symbiosis = pack.Pack.Talents.Single(talent => talent.ID == Symbiosis);
        // SymbiosisPack sets every modifier that isn't the drain to the share, so it relies on these two alone.
        Assert.Equal(2, symbiosis.Modifiers.Length);
        Assert.Contains(symbiosis.Modifiers, modifier => modifier.Stat == DummyWeaponSimulator.ShieldDrainStat && modifier.Value == new ModifierValue(0.1));
        Assert.Contains(symbiosis.Modifiers, modifier => modifier.Stat == DummyWeaponSimulator.ShieldDamageShareStat && modifier.Value == new ModifierValue(0.25));
        var incoming = SymbiosisFight(pack, Oxpecker(pack.Pack, "skill_striker_shield")).Incoming!;
        Assert.True(incoming.ShieldDrained > 0);
        Assert.True(incoming.ShieldRepaired > 0);
    }

    [Fact]
    public void SymbiosisDrainsTheRaisedShieldEachSecondAndRepairsItFromDamage()
    {
        var pack = SymbiosisPack();
        var result = SymbiosisFight(pack, Oxpecker(pack.Pack, "skill_striker_shield"));
        var incoming = Assert.IsType<IncomingFightResult>(result.Incoming);
        Assert.True(incoming.ShieldDrained > 0);
        Assert.True(incoming.ShieldRepaired > 0);
        var drains = result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.ShieldDrained).ToArray();
        Assert.NotEmpty(drains);
        // On the fight's whole seconds, never more than a tenth of the shield's health.
        Assert.All(drains, entry => Assert.Equal(Math.Round(entry.Time), entry.Time));
        var health = drains.Max(entry => entry.Damage);
        Assert.All(drains, entry => Assert.InRange(entry.Damage, 0, health));
        Assert.Contains(result.Limitations, note => note.Contains("ADR 0026", StringComparison.Ordinal));
        // Regeneration repairs the drained shield too, so the repair from damage shows against a share of 0.
        Assert.True(incoming.ShieldRepaired > SymbiosisFight(SymbiosisPack(share: 0), Oxpecker(pack.Pack, "skill_striker_shield")).Incoming!.ShieldRepaired);
        // A repair without its drain would be all gain, so it's refused.
        Assert.Equal("incoming-repair-unsupported", Assert.Throws<SimulationException>(() =>
            SymbiosisFight(SymbiosisPack(0), Oxpecker(pack.Pack, "skill_striker_shield"))).Code);
    }

    [Fact]
    public void ADrainThatEmptiesTheShieldBreaksIt()
    {
        // A whole shield's health a second: the first second's drain breaks it, as an enemy's hit would.
        var pack = SymbiosisPack(1);
        var result = SymbiosisFight(pack, Oxpecker(pack.Pack, "skill_striker_shield"));
        var events = result.FirstFightEvents;
        var drain = events.First(entry => entry.Kind == DummyEventKind.ShieldDrained);
        Assert.Contains(events, entry => entry.Kind == DummyEventKind.ShieldBreak && entry.Time == drain.Time);
        Assert.True(result.Incoming!.ShieldBreaks >= 1);
    }

    [Fact]
    public void SymbiosisActsOnlyWithTheOxpeckerInHand()
    {
        // A Bulwark takes only the sidearm, and a raised one holds the sidearm in hand, so the Oxpecker is never out while it's up.
        var pack = SymbiosisPack(1);
        var build = Oxpecker(pack.Pack, "skill_bulwark_shield").WithEquipment("sidearm", "sidearm_d50");
        var result = SymbiosisFight(pack, build, rotation: new("sidearm"));
        Assert.Equal(0, result.Incoming!.ShieldDrained);
        Assert.DoesNotContain(result.FirstFightEvents, entry => entry.Kind == DummyEventKind.ShieldDrained);
    }

    [Fact]
    public void SymbiosisHasNothingToActOnWithoutAShield()
    {
        var pack = SymbiosisPack();
        var result = SymbiosisFight(pack, Oxpecker(pack.Pack, "skill_striker_shield"), shield: false);
        Assert.Null(result.Incoming!.ShieldDrained);
        Assert.Equal(0, result.Incoming.ShieldRepaired);
        Assert.Contains(result.Limitations, note => note.Contains("nothing to act on", StringComparison.Ordinal));
    }
}
