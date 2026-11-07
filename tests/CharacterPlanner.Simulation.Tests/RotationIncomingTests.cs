using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

public sealed partial class IncomingFightTests
{
    private static DummyFightOptions SwappingUnderFire(double duration) =>
        Options with { Duration = duration, DriveSimpleBuffs = true, Rotation = new("secondary_weapon") };

    /// <summary>A second weapon slot holding "other"; each weapon's own base value of <paramref name="stat"/> applies while it is in hand.</summary>
    private static (CompiledPackExpressions Pack, BuildSnapshot Build) WithSidearm((CompiledPackExpressions Pack, BuildSnapshot Build) fixture,
        string stat = "sidearm_bonus", double primary = 0, double sidearm = 0, string sidearmTalent = "", string reader = "",
        Func<GamePack, GamePack>? change = null, ImmutableArray<string> sidearmTags = default)
    {
        var original = fixture.Pack.Pack;
        var rules = original with
        {
            Slots = [.. original.Slots, new() { ID = "secondary_weapon", Name = "Sidearm", Weapon = true }],
            // The reader (a stat the fixture already has) takes the weapon value once the stat exists.
            Stats = [.. original.Stats.Where(definition => definition.ID != stat && definition.ID != reader), new() { ID = stat, Formula = "0" },
                .. reader.Length == 0 ? [] : new[] { new StatDefinition { ID = reader, Formula = stat } }],
            Talents = sidearmTalent.Length == 0 ? original.Talents : [.. original.Talents, new() { ID = sidearmTalent, Name = sidearmTalent }],
            Items = [.. original.Items.Select(item => item with { Weapon = [new(stat, primary)] }),
                new() { ID = "other", Name = "Other", Slot = "secondary_weapon", Weapon = [new(stat, sidearm)], Tags = sidearmTags.IsDefault ? [] : sidearmTags,
                    TalentSlots = sidearmTalent.Length == 0 ? [] : [new() { ID = "talent", Choices = [sidearmTalent] }] }]
        };
        if (change is not null) { rules = change(rules); }
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = fixture.Build.WithEquipment("secondary_weapon", "other");
        if (sidearmTalent.Length > 0) { build = build.WithTalentSelection("secondary_weapon", "talent", sidearmTalent); }
        return (pack, build);
    }

    [Fact]
    public void ACoverCycleBlocksEnemyFireWhileSwappingWeapons()
    {
        // ADR 0009 with a rotation: enemies fire every 0.5 s over 8 s, and the attempts at 2 to 3.5 s and 6 to 7.5 s are blocked.
        var fixture = WithSidearm(Fixture(Buff("hit"), changes: [("character_armor", "0"), ("character_health", "1000"),
            ("enemy_target_hit_damage", "1"), ("enemy_target_incoming_dps", "2")]));
        var result = Run(fixture, SwappingUnderFire(8) with { PeekSeconds = 2, CoverSeconds = 2, PeekHitShare = 1 });
        Assert.Equal((15, 8, 7), (result.Incoming!.Attempts, result.Incoming.BlockedShots, result.Incoming.Hits));
        Assert.Contains(result.Limitations, note => note.Contains("a magazine is kept for the swap when empty", StringComparison.Ordinal));
        Assert.StartsWith("d2p-incoming-random-v73/", result.Contract, StringComparison.Ordinal);
    }

    [Fact]
    public void KillRepairUnderFireFollowsTheWeaponInHand()
    {
        // One-round magazines, a one-second swap and a two-second reload on arrival: kills at 0 (rifle), 2 (sidearm),
        // 6 (rifle) and 10 (sidearm). Only the sidearm repairs 5 armor per kill; enemy hits keep armor below full.
        var fixture = WithSidearm(Fixture(Buff("hit"), changes: [("equipped_weapon_magazine_size", "1"), ("enemy_target_health", "5"),
            ("character_armor", "100")]), sidearm: 5, reader: "estimated_armor_on_kill");
        var result = Run(fixture, SwappingUnderFire(11));
        var kills = result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Kill).ToArray();
        Assert.Equal(new double[] { 0, 2, 6, 10 }, kills.Select(e => e.Time));
        Assert.Equal(new[] { "primary_weapon", "secondary_weapon", "primary_weapon", "secondary_weapon" }, kills.Select(e => e.WeaponSlot));
        var repairs = result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.ArmorRepaired).ToArray();
        Assert.Equal(new double[] { 2, 10 }, repairs.Select(e => e.Time));
        Assert.All(repairs, repair => Assert.Equal(5, repair.Damage));
        Assert.Equal(10, result.Incoming!.ArmorRepaired);
        Assert.True(result.Incoming.Hits > 0);
        // Swaps start at 1, 3 and 7 s; the one after the last kill would start at the 11 s horizon.
        Assert.Equal(3, result.Swaps);
        Assert.StartsWith("d2p-incoming-random-v73/", result.Contract, StringComparison.Ordinal);
    }

    [Fact]
    public void EnemyFireWaitsForAnEarlierQueuedSwapToStart()
    {
        // A 1.5 s swap queues a swap start at 3.5 s while the clock is already at 7 s; the enemy attempt at 4 s
        // must follow it, so the trigger state never steps back in time. The rifle's talent hears "swapped" only when the
        // rifle is drawn, at 3.5 s.
        var fixture = WithSidearm(Fixture(Buff("swapped") with { Duration = 0.25 }, changes: [("equipped_weapon_magazine_size", "1"), ("character_armor", "1000")]));
        var result = Run(fixture, SwappingUnderFire(9) with { Rotation = new("secondary_weapon", SwapTime: 1.5) });
        Assert.Equal(new[] { 1, 3.5, 8 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.SwapStarted).Select(e => e.Time));
        Assert.Equal(0.25, result.TimedTriggerUptime["buff"].Seconds, 8);
    }

    private static Func<GamePack, GamePack> ShieldAllows(string[] weapons, string[] excludeTags) => rules => rules with
    {
        Simulation = rules.Simulation with { Skills = [rules.Simulation.Skills[0] with { Weapons = [.. weapons], ExcludeTags = [.. excludeTags] }] }
    };

    [Fact]
    public void AShieldThatAllowsBothWeaponsStaysRaisedThroughSwaps()
    {
        // 20 shield health absorbs the 10-damage hits at 1 and 2 s and breaks; the two-second cooldown raises it at 4 s
        // to take that hit too, while the rotation keeps swapping between the two allowed weapons.
        var fixture = WithSidearm(ShieldFixture(rule: Buff("hit"), changes: [("equipped_weapon_magazine_size", "1")]),
            change: ShieldAllows(["primary_weapon", "secondary_weapon"], []));
        var result = Run(fixture, SwappingUnderFire(4.5) with { IncludeShield = true });
        Assert.Equal(30, result.Incoming!.ShieldAbsorbed);
        Assert.Equal(1, result.Incoming.ShieldBreaks);
        Assert.Equal(new[] { 0d, 4d }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.ShieldRaised).Select(e => e.Time));
        Assert.Equal(new[] { "primary_weapon", "secondary_weapon" }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.WeaponSlot));
        Assert.StartsWith("d2p-incoming-random-v73/", result.Contract, StringComparison.Ordinal);
    }

    [Fact]
    public void AShieldThatAllowsOneWeaponForcesItAtDecisionPointsAndReturnsTheBuildsWeaponAfterABreak()
    {
        // The shield (20 health, 5 s cooldown) allows only the sidearm; the rifle is the build's weapon. Enemy hits every second.
        // 0 s: raised with the rifle in hand, so a forced swap to the sidearm before the first shot.
        // 1 s: the sidearm fires; swapping to the barred rifle isn't allowed, so it reloads (ready at 4 s).
        // 2 s: the shield breaks. 4 s: the next decision point, with the shield down, returns the rifle (forced).
        // 5 s: the rifle fires and swaps normally at 6 s. 7 s: the shield is back; the sidearm in hand fires.
        var fixture = WithSidearm(ShieldFixture(cooldown: "5", rule: Buff("hit"), changes: [("equipped_weapon_magazine_size", "1")]),
            change: ShieldAllows(["secondary_weapon"], []));
        var result = Run(fixture, SwappingUnderFire(8) with { IncludeShield = true });
        var shots = result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).ToArray();
        Assert.Equal(new double[] { 1, 5, 7 }, shots.Select(e => e.Time));
        Assert.Equal(new[] { "secondary_weapon", "primary_weapon", "secondary_weapon" }, shots.Select(e => e.WeaponSlot));
        Assert.Equal(new double[] { 0, 4, 6 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.SwapStarted).Select(e => e.Time));
        Assert.Equal(2, result.Incoming!.ShieldSwitches);
        Assert.Equal(3, result.Swaps);
        Assert.Equal(30, result.Incoming.ShieldAbsorbed);
        Assert.Contains(result.Limitations, note => note.Contains("allows only the Sidearm: a raised shield swaps", StringComparison.Ordinal));
    }

    [Fact]
    public void ARaisedShieldBarsSwapsToItsBarredWeaponAndNeitherWeaponIsRefused()
    {
        // The rifle is allowed and the sidearm's type is excluded. The emptied rifle may not swap to the barred sidearm
        // while the shield is up, so it reloads: shots at 0 and 3 s, no swaps. A shield for neither weapon is refused.
        var fixture = WithSidearm(ShieldFixture(rule: Buff("hit"), changes: [("equipped_weapon_magazine_size", "1")]),
            change: ShieldAllows(["primary_weapon", "secondary_weapon"], ["weapon_type_lmg"]), sidearmTags: ["weapon_type_lmg"]);
        var result = Run(fixture, SwappingUnderFire(4) with { IncludeShield = true });
        Assert.Equal(new[] { ("primary_weapon", 0d), ("primary_weapon", 3d) },
            result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => (e.WeaponSlot, e.Time)));
        Assert.Equal(0, result.Swaps);
        Assert.Equal(0, result.Incoming!.ShieldSwitches);
        var neither = WithSidearm(ShieldFixture(rule: Buff("hit")), change: ShieldAllows(["sidearm"], []));
        Assert.Equal("shield-weapon-unsupported", Assert.Throws<SimulationException>(() => Run(neither, SwappingUnderFire(4) with { IncludeShield = true })).Code);
    }

    [Fact]
    public void BehindCoverAWeaponAShieldHoldsInHandStillReloads()
    {
        // Swapping when empty, but the raised shield bars the sidearm, so no swap can come: there's nothing to keep the rifle's
        // magazine for, and each cover phase tops it up, at 1.5 and 5 s.
        var fixture = WithSidearm(ShieldFixture(capacity: "100000", rule: Buff("hit"), changes: [("equipped_weapon_magazine_size", "3")]),
            change: ShieldAllows(["primary_weapon", "secondary_weapon"], ["weapon_type_lmg"]), sidearmTags: ["weapon_type_lmg"]);
        var result = Run(fixture, SwappingUnderFire(5.2) with { IncludeShield = true, PeekSeconds = 1.5, CoverSeconds = 2, PeekHitShare = 1 });
        Assert.Equal(new[] { 0, 1, 3.5, 4.5 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Time));
        Assert.Equal(new[] { 1.5, 5 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.ReloadStarted).Select(e => e.Time));
        Assert.Equal(0, result.Swaps);
    }

    [Theory]
    [InlineData("skill_striker_shield", "primary_weapon", "secondary_weapon")]
    [InlineData("skill_bulwark_shield", "sidearm", "primary_weapon")]
    public void CommittedShieldsRotateWithTheWeaponsTheyAllow(string skill, string active, string second)
    {
        // Striker allows both rifle slots (here two assault rifles); Bulwark allows only the sidearm, so the rifle is forced out.
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        string Weapon(string slot) => slot switch
        {
            "primary_weapon" => "primary_weapon_acr",
            "secondary_weapon" => "secondary_weapon_police_m4",
            _ => pack.Pack.Items.First(item => item.Slot == slot).ID
        };
        var context = Context.SetItem(GamePack.WeaponSlotSelector, active).Add("enemy_target", "1").Add("enemy_difficulty", "1")
            .Add("enemy_hit_rate", "100").Add("enemy_firing_share", "100");
        var options = Options with { Duration = 20, IncludeShield = true, DriveSimpleBuffs = true, Rotation = new(second) };
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment(active, Weapon(active)).WithEquipment(second, Weapon(second))
            .WithActiveSkillSelection("skill_slot_1", skill);
        var result = Run((pack, build), options, context);
        Assert.True(result.Incoming!.ShieldAbsorbed > 0);
        Assert.Contains(result.FirstFightEvents, e => e.Kind == DummyEventKind.Shot && e.WeaponSlot == active);
        if (skill == "skill_bulwark_shield")
        {
            // Two rifles under a sidearm-only shield: neither weapon can hold it.
            var rifles = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", Weapon("primary_weapon"))
                .WithEquipment("secondary_weapon", Weapon("secondary_weapon")).WithActiveSkillSelection("skill_slot_1", skill);
            Assert.Equal("shield-weapon-unsupported", Assert.Throws<SimulationException>(() => Run((pack, rifles),
                options with { Rotation = new("secondary_weapon") }, Context.Add("enemy_target", "1").Add("enemy_difficulty", "1")
                    .Add("enemy_hit_rate", "100").Add("enemy_firing_share", "100"))).Code);
        }
    }

    /// <summary>Moves the fixture weapon's talent slot (and its selection) to a second weapon, "other", in the secondary slot.</summary>
    private static (CompiledPackExpressions Pack, BuildSnapshot Build) TalentOnSidearm((CompiledPackExpressions Pack, BuildSnapshot Build) fixture, string talent)
    {
        var original = fixture.Pack.Pack;
        var gun = original.Items[0];
        var rules = original with
        {
            Slots = [.. original.Slots, new() { ID = "secondary_weapon", Name = "Sidearm", Weapon = true }],
            Items = [gun with { TalentSlots = [] }, gun with { ID = "other", Name = "Other", Slot = "secondary_weapon" }, .. original.Items.Skip(1)]
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithEquipment("secondary_weapon", "other")
            .WithTalentSelection("secondary_weapon", "talent", talent);
        foreach (var (slot, skill) in fixture.Build.ActiveSkillSelections) { build = build.WithActiveSkillSelection(slot, skill); }
        return (pack, build);
    }

    [Fact]
    public void PreservationOnTheSidearmRepairsOnlyAfterItsOwnKills()
    {
        // Every shot kills. The rifle kills at 0 s; the sidearm's kill at 2 s starts a 10 armor budget over five seconds,
        // which repairs 8 by the 6 s horizon. Enemy hits keep armor below full.
        var fixture = TalentOnSidearm(PreservationFixture(), "weapon_talent_preservation");
        var result = Run(fixture, SwappingUnderFire(6));
        Assert.Equal(new[] { ("primary_weapon", 0d), ("secondary_weapon", 2d) },
            result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Kill).Select(e => (e.WeaponSlot, e.Time)));
        Assert.Equal(8, result.Incoming!.ArmorRepaired, 8);
        Assert.StartsWith("d2p-incoming-random-v73/", result.Contract, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2", 2, "primary_weapon", 20)]
    [InlineData("3", 6, "secondary_weapon", 50)]
    public void DoctorHomeMarksAndKitsFollowTheWeaponInHand(string targetHealth, double killAt, string killer, double repaired)
    {
        // Doctor Home is on the sidearm, the build's weapon; each shot deals 1. Only the sidearm's hits mark (at 0 s and 6 s;
        // the rifle hits at 2 s). A marked target drops the kit whoever kills it (R41, target_death): one the rifle kills at
        // 2 s (health 2) still carries the 0 s mark, so the 50 armor kit lands, capped by the 20 armor enemy fire removed;
        // one the sidearm kills at 6 s (health 3) gets it capped by the 60 removed. Whether a holstered Doctor Home does
        // this in game is an in-game check.
        var fixture = TalentOnSidearm(DoctorHomeFixture(changes: [("enemy_target_health", targetHealth), ("equipped_weapon_magazine_size", "1")]),
            "weapon_talent_doctor_home");
        var context = Context.SetItem(GamePack.WeaponSlotSelector, "secondary_weapon");
        var result = Run(fixture, SwappingUnderFire(7) with { Rotation = new("primary_weapon") }, context);
        Assert.Equal(new[] { (killer, killAt) }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Kill).Select(e => (e.WeaponSlot, e.Time)));
        Assert.Equal(repaired, result.Incoming!.ArmorRepaired);
    }

    [Fact]
    public void MixedTargetsRunUnderWeaponRotation()
    {
        // Each weapon reads the target in front of it: profile 1 dies to one 10-damage shot, profile 2 takes two. Swapping on an
        // empty one-round magazine, both weapons fire on the alternating targets.
        var fixture = WithSidearm(ProfileFixture(), change: rules => rules with
        { Stats = [.. rules.Stats.Select(stat => stat.ID == "equipped_weapon_magazine_size" ? stat with { Formula = "1" } : stat)] });
        var result = Run(fixture, SwappingUnderFire(8) with { TargetSequence = [1, 2] });
        var kills = result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Kill).ToArray();
        Assert.True(kills.Length >= 2);
        Assert.Contains(result.FirstFightEvents, shot => shot.Kind == DummyEventKind.Shot && shot.WeaponSlot == "secondary_weapon");
        Assert.Contains(result.Limitations, note => note.StartsWith("Targets cycle through a sequence of 2 (profiles 1, 2)", StringComparison.Ordinal));
        // The same fight with profile 2 throughout kills fewer targets: the sequence's profile-1 targets die faster.
        Assert.True(Run(fixture, SwappingUnderFire(8) with { TargetSequence = [2] }).Kills < result.Kills);
    }

    [Fact]
    public void MixedTargetsRunUnderRotationWithSkills()
    {
        // All three together: a rotation, a damaging skill whose damage doesn't follow the target, and a target sequence.
        var (original, build) = IncomingSkillFixture(changes: [("character_health", "100000"), ("equipped_weapon_magazine_size", "1")]);
        var rules = original.Pack with
        {
            NumericContextInputs = [.. original.Pack.NumericContextInputs, new() { ID = "enemy_target", Name = "Enemy target", Min = 0, Max = 3, Step = 1, Default = 1 }],
            Stats = [.. original.Pack.Stats.Select(stat => stat.ID == "enemy_target_health" ? stat with { Formula = "10 * context_value('enemy_target')" } : stat)]
        };
        var fixture = WithSidearm((Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled), build));
        var result = Run(fixture, SwappingUnderFire(6) with { IncludeSkills = true, TargetSequence = [1, 2] });
        Assert.NotEmpty(result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.SkillImpact));
        Assert.Contains(result.FirstFightEvents, shot => shot.Kind == DummyEventKind.Shot && shot.WeaponSlot == "secondary_weapon");
        Assert.Contains(result.Limitations, note => note.StartsWith("Targets cycle through a sequence of 2 (profiles 1, 2)", StringComparison.Ordinal));
    }

    [Fact]
    public void ASequenceProfileMustGiveBothWeaponsTheSamePools()
    {
        // The sidearm's own stat raises profile 2's health only with it in hand. The chosen profile (1) is the same for both
        // weapons, so only the per-profile check at setup can catch profile 2.
        var fixture = WithSidearm(ProfileFixture(), stat: "sidearm_bonus", sidearm: 1, change: rules => rules with
        { Stats = [.. rules.Stats.Select(stat => stat.ID == "enemy_target_health" ? stat with { Formula = "10 * context_value('enemy_target') + (context_value('enemy_target') == 2 ? 10 * sidearm_bonus : 0)" } : stat)] });
        Assert.NotNull(Run(fixture, SwappingUnderFire(6) with { TargetSequence = [1] }));
        Assert.Equal("target-pool-mismatch", Assert.Throws<SimulationException>(() => Run(fixture, SwappingUnderFire(6) with { TargetSequence = [2] })).Code);
    }

    [Fact]
    public void SkillDamageAgainstAProfileFollowsTheWeaponInHand()
    {
        // Profile 2's skill impact rises with the sidearm in hand; the chosen profile (1) is the same with both weapons.
        var fixture = WithSidearm(SkillProfileFixture("5"), stat: "sidearm_bonus", sidearm: 1, change: rules => rules with
        {
            ActiveSkills = [rules.ActiveSkills[0] with { PreviewOutputs = [.. rules.ActiveSkills[0].PreviewOutputs.Select(output =>
                output.ID == "impact" ? output with { Formula = "5 + (context_value('enemy_target') == 2 ? sidearm_bonus : 0)" } : output)] }]
        });
        Assert.True(Run(fixture, SwappingUnderFire(4) with { IncludeSkills = true, TargetSequence = [1, 2] }).SkillDps!.Total.Mean > 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ASkillDeploysWithTheWeaponInHand(bool driven)
    {
        var fixture = WithSidearm(SkillTriggerFixture(Buff("hit"), cooldown: "0.5", changes: [("equipped_weapon_magazine_size", "1"), ("character_health", "100000")]), sidearm: 1, change: rules => rules with
        {
            ActiveSkills = [rules.ActiveSkills[0] with { PreviewOutputs = [.. rules.ActiveSkills[0].PreviewOutputs.Select(output => output.ID == "impact" ? output with { Formula = "5 + sidearm_bonus" } : output)] }]
        });
        var impacts = Run(fixture, SwappingUnderFire(6) with { IncludeSkills = true, DriveSimpleBuffs = driven, IncludeIncoming = driven }).FirstFightEvents.Where(e => e.Kind == DummyEventKind.SkillImpact).Select(e => e.Damage).ToArray();
        Assert.Contains(5, impacts);
        Assert.Contains(6, impacts);
    }

    [Fact]
    public void SkillsUnderFireComparePlansBeforeEnemyAttemptsSplitTheirWindows()
    {
        // A continuous window is split at each enemy attempt; both weapons' unsplit plans are equal, so this is no refusal.
        var fixture = WithSidearm(SkillTriggerFixture(Buff("skill_hit") with { Duration = 0.75 }, rate: "10", window: "3", cooldown: "5",
            changes: [("equipped_weapon_magazine_size", "1")]));
        var result = Run(fixture, SwappingUnderFire(6) with { IncludeSkills = true });
        Assert.True(result.SkillDps!.Total.Mean > 0);
        Assert.True(result.Swaps > 0);
    }
}
