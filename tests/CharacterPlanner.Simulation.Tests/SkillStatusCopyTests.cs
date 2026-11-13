using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

public sealed partial class IncomingFightTests
{
    // ADR 0015: a Burn Sticky Bomb's shape (an impact of 5 and a burn of 4 a second for 3 s), or with `window` a skill that keeps the burn
    // on for that long, beside a spread or a pass-on on the gun's talent. The player's own burn would tick 100 a second; a copy carries the
    // skill's 4 (R105).
    private static (CompiledPackExpressions Pack, BuildSnapshot Build) SkillCopyFixture(bool spread, bool passOn, bool skillStatuses = true, string health = "1000000",
        string window = "0")
    {
        var (original, build) = IncomingSkillFixture(impact: "5", window: window, cooldown: "20", changes: [("character_health", "100000"), ("enemy_target_health", health)]);
        var withStatus = WithSkillStatus(original, rule => rule with { Rate = "", Window = window == "0" ? "" : "window", Status = "burning", StatusRate = "status_rate", StatusWindow = "status_window" });
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(withStatus.Pack with
        {
            Simulation = withStatus.Pack.Simulation with
            {
                Spreads = spread ? [new() { Talent = "test", Within = 5, SkillStatuses = skillStatuses }] : [],
                PassOns = passOn ? [new() { Talent = "test", Within = 5, Status = "any", Share = 0.5, SkillStatuses = skillStatuses }] : []
            }
        }).Compiled);
        return (pack, build.WithTalentSelection("primary_weapon", "talent", "test"));
    }

    private static DummyFightResult CopyFight((CompiledPackExpressions Pack, BuildSnapshot Build) fixture, double? spacing = 4) =>
        Run(fixture, Group(10, 3) with { EnemySpacing = spacing, IncludeSkills = true }, delay: 5);

    [Fact]
    public void ASkillsOwnBurnSpreadsAsThePlayersWhereTheRowSaysSo()
    {
        // The bomb's burn first goes on at 0.1 s, until 3 s. The neighbour 4 m away gets what's left, 2.9 s, carrying the bomb's own 4 a
        // second, not the player's 100 (R105): 4 at 1.1 and 2.1 s. The one 8 m away is out of reach.
        var copied = CopyFight(SkillCopyFixture(spread: true, passOn: false));
        Assert.Equal([1.1, 2.1], Times(copied, DummyEventKind.BurnTick).Select(at => Math.Round(at, 6)));
        Assert.All(copied.FirstFightEvents.Where(e => e.Kind == DummyEventKind.BurnTick), e => Assert.Equal(4, e.Damage, 6));
        Assert.Contains(copied.Limitations, note => note.Contains("a skill's own burn or bleed", StringComparison.Ordinal));
        // Without the field the burn doesn't spread, and the fight is the one without a spread at all.
        var plain = CopyFight(SkillCopyFixture(spread: true, passOn: false, skillStatuses: false));
        Assert.Empty(Times(plain, DummyEventKind.BurnTick));
        Assert.Equal(CopyFight(SkillCopyFixture(spread: false, passOn: false)).TotalDps!.Mean, plain.TotalDps!.Mean, 9);
        // Without a spacing nobody is placed, so the field changes nothing.
        var unspaced = CopyFight(SkillCopyFixture(spread: true, passOn: false), spacing: null);
        Assert.Empty(Times(unspaced, DummyEventKind.BurnTick));
        Assert.Equal(CopyFight(SkillCopyFixture(spread: true, passOn: false, skillStatuses: false), spacing: null).TotalDps!.Mean, unspaced.TotalDps!.Mean, 9);
    }

    [Fact]
    public void ACopyOfAStatusTheSkillsRateOnlyCarriesTicksAtThePlayersRate()
    {
        // A Stinger Hive's shape: 2 s of stings at 10 that carry a burn with no rate of its own. Its copy has no skill damage to carry,
        // so it ticks at the player's 100.
        var (original, build) = IncomingSkillFixture(impact: "0", rate: "10", window: "2", cooldown: "20", changes: [("character_health", "100000"), ("enemy_target_health", "1000000")]);
        var withStatus = WithSkillStatus(original, rule => rule with { Status = "burning" });
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(withStatus.Pack with
        {
            Simulation = withStatus.Pack.Simulation with { Spreads = [new() { Talent = "test", Within = 5, SkillStatuses = true }] }
        }).Compiled);
        var ticks = CopyFight((pack, build.WithTalentSelection("primary_weapon", "talent", "test"))).FirstFightEvents.Where(e => e.Kind == DummyEventKind.BurnTick).ToArray();
        Assert.NotEmpty(ticks);
        Assert.All(ticks, e => Assert.Equal(100, e.Damage, 6));
    }

    [Fact]
    public void ACopiedSkillBurnLastsOneApplication()
    {
        // With a 2 s window the bomb keeps its burn on until 5 s, but nothing renews the copy: it lasts one application, 3 s, from 0.1 s,
        // so the neighbour burns at 1.1 and 2.1 s, not on to 4.1 s.
        var copied = CopyFight(SkillCopyFixture(spread: true, passOn: false, window: "2"));
        Assert.Equal([1.1, 2.1], Times(copied, DummyEventKind.BurnTick).Select(at => Math.Round(at, 6)));
    }

    [Fact]
    public void ASkillsOwnBurnPassesOnAtADeathWhereTheRowSaysSo()
    {
        // The first enemy dies at 2.8 s, with 0.2 s of the bomb's burn left. Half of one full application, 1.5 s, still goes on its
        // neighbour (R105: no cap at what was left), carrying the bomb's 4: a tick at 3.8 s, which a cap at 0.2 s would never reach.
        var copied = CopyFight(SkillCopyFixture(spread: false, passOn: true, health: "46"));
        Assert.Equal(2.8, Times(copied, DummyEventKind.StatusPassedOn)[0], 6);
        var ticks = copied.FirstFightEvents.Where(e => e.Kind == DummyEventKind.BurnTick).ToArray();
        Assert.Equal([3.8], ticks.Select(e => Math.Round(e.Time, 6)));
        Assert.Equal(4, ticks[0].Damage, 6);
        // Without the field the burn dies with the enemy.
        var plain = CopyFight(SkillCopyFixture(spread: false, passOn: true, skillStatuses: false, health: "46"));
        Assert.Empty(Times(plain, DummyEventKind.StatusPassedOn));
        Assert.Empty(Times(plain, DummyEventKind.BurnTick));
    }
}
