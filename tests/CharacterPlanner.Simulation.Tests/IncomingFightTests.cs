using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Simulation.Tests;

public sealed partial class IncomingFightTests
{
    private static ImmutableDictionary<string, string> Context => ImmutableDictionary<string, string>.Empty.Add(GamePack.WeaponSlotSelector, "primary_weapon");
    private static SimTriggerDefinition Buff(string on) => new() { Talent = "test", Key = "buff", Kind = "buff", On = on, Duration = 10 };
    private static (CompiledPackExpressions Pack, BuildSnapshot Build) Fixture(SimTriggerDefinition? rule = null, string modifier = "buff_damage", double bonus = 10,
        params (string Id, string Formula)[] changes)
    {
        var values = new Dictionary<string, string>
        {
            ["equipped_weapon_rpm"] = "60",
            ["equipped_weapon_magazine_size"] = "10",
            ["equipped_weapon_reload_time"] = "2",
            ["equipped_weapon_pellets"] = "1",
            ["practical_hit_fraction"] = "1",
            ["practical_headshot_fraction"] = "0",
            ["effective_critical_hit_chance"] = "0",
            ["equipped_weapon_damage"] = "10 + buff_damage",
            ["equipped_weapon_critical_damage"] = "10 + buff_damage",
            ["equipped_weapon_headshot_damage"] = "10 + buff_damage",
            ["equipped_weapon_critical_headshot_damage"] = "10 + buff_damage",
            ["equipped_weapon_practical_shot_damage"] = "10 + buff_damage",
            ["buff_damage"] = "0",
            ["enemy_target_armor"] = "0",
            ["enemy_target_health"] = "100000",
            ["out_of_cover_damage_factor"] = "1",
            ["damage_to_armor"] = "0",
            ["health_damage"] = "0",
            ["character_armor"] = "10",
            ["estimated_bonus_armor"] = "0",
            ["character_health"] = "10",
            ["enemy_target_hit_damage"] = "10",
            ["enemy_damage_taken_factor"] = "1",
            ["enemy_target_incoming_dps"] = "enemy_target_hit_damage * context_value('enemy_hit_rate') / 100"
        };
        foreach (var (id, formula) in changes) { values[id] = formula; }
        var rules = new GamePack
        {
            ID = "incoming-fight",
            Name = "Incoming fight",
            Version = "1",
            Slots = [new() { ID = "primary_weapon", Name = "Weapon", Weapon = true }],
            Items = [new() { ID = "gun", Name = "Gun", Slot = "primary_weapon", TalentSlots = [new() { ID = "talent", Choices = ["test"] }] }],
            Selectors = [new() { ID = "buff", Name = "Buff", Values = ["true"] }],
            NumericContextInputs = [new() { ID = "enemy_hit_rate", Name = "Enemy hit rate", Min = 0, Max = 100, Step = 1, Default = 100 }],
            Stats = [.. values.Select(pair => new StatDefinition { ID = pair.Key, Formula = pair.Value })],
            Talents = [new() { ID = "test", Name = "Test", Modifiers = [new() { Stat = modifier, Op = ModifierOps.Flat, Value = new(bonus), RequiresContext = ImmutableDictionary<string, string>.Empty.Add("buff", "true") }] }],
            Simulation = new() { Triggers = rule is null ? [] : [rule] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun");
        if (rule is not null) { build = build.WithTalentSelection("primary_weapon", "talent", "test"); }
        return (pack, build);
    }

    private static DummyFightOptions Options => new() { Duration = 5, Mode = SimulationMode.Random, Iterations = 1, IncludeIncoming = true, CaptureFirstFightEvents = true };
    private static DummyFightResult Run((CompiledPackExpressions Pack, BuildSnapshot Build) fixture, DummyFightOptions? options = null,
        ImmutableDictionary<string, string>? context = null, double delay = 0) =>
        DummyWeaponSimulator.RunAgainstTargets(fixture.Pack, fixture.Build, context ?? Context, options ?? Options, delay, TestContext.Current.CancellationToken);

    private static (CompiledPackExpressions Pack, BuildSnapshot Build) ShockFixture(int attackers = 1, double duration = 1, string on = "hit", double targetHealth = 100000, bool applyOnHit = false)
    {
        var status = new SimTriggerDefinition { Talent = "test", Key = "buff", Kind = "status", Status = "shocked", On = on, Duration = duration, ApplyOnHit = applyOnHit };
        var (original, _) = Fixture(status, changes: [("character_armor", "0"), ("character_health", "1000"), ("enemy_target_hit_damage", "1")]);
        var rules = original.Pack with
        {
            NumericContextInputs = [.. original.Pack.NumericContextInputs, new() { ID = "enemy_attackers", Name = "Attackers", Min = 1, Max = 10, Step = 1, Default = attackers }],
            Stats = [.. original.Pack.Stats.Select(stat => stat.ID == "enemy_target_incoming_dps" ? stat with { Formula = "enemy_target_hit_damage * 2 * context_value('enemy_hit_rate') / 100 * context_value('enemy_attackers')" } :
                stat.ID == "enemy_target_health" ? stat with { Formula = targetHealth.ToString(System.Globalization.CultureInfo.InvariantCulture) } : stat)]
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        return (pack, BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test"));
    }

    [Theory]
    [InlineData(1, 7, 3, 4)]
    [InlineData(2, 15, 4, 11)]
    public void ShockBlocksOnlyOneAttackersShareAndRecoversAtExactExpiry(int attackers, double attempts, double blocked, double hits)
    {
        var result = Run(ShockFixture(attackers), Options with { Duration = 4, DriveSimpleBuffs = true, Iterations = 2 });
        Assert.Equal(attempts, result.Incoming!.Attempts);
        Assert.Equal(blocked, result.Incoming.BlockedShots);
        Assert.Equal(hits, result.Incoming.Hits);
        Assert.Equal(hits, result.Incoming.DamageTaken);
        Assert.Equal((int)blocked, result.FirstFightEvents.Count(e => e.Kind == DummyEventKind.EnemyShotBlocked));
        Assert.All(result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.EnemyShotBlocked), e => Assert.True(e.Time > 1 && e.Time != Math.Floor(e.Time)));
    }

    [Fact]
    public void UnarmedShockAndExpiredOrDeadTargetsDoNotBlock()
    {
        var unarmed = Run(ShockFixture(on: "kill"), Options with { Duration = 4, DriveSimpleBuffs = true });
        Assert.Equal(0, unarmed.Incoming!.BlockedShots);
        var killed = Run(ShockFixture(duration: 5, targetHealth: 35), Options with { Duration = 4, DriveSimpleBuffs = true }, delay: 3);
        Assert.Equal(1, killed.Kills);
        Assert.Equal(new double[] { 1.5, 2 }, killed.FirstFightEvents.Where(e => e.Kind == DummyEventKind.EnemyShotBlocked).Select(e => e.Time));
        Assert.Equal(5, killed.Incoming!.Hits);
    }

    [Fact]
    public void BlockedAttemptsKeepIncomingHitRollsAlignedAndPreserveOutgoingRolls()
    {
        var fixture = ShockFixture();
        var context = Context.Add("enemy_hit_rate", "50");
        var options = Options with { Duration = 4, DriveSimpleBuffs = true, Seed = 23 };
        var result = Run(fixture, options, context);
        var baseline = Run(fixture, options with { IncludeIncoming = false }, context);
        Assert.Equal(baseline.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot), result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot));
        var blocked = result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.EnemyShotBlocked).Select(e => e.Time).ToHashSet();
        Assert.Equal(new double[] { 1.5, 2.5, 3.5 }, blocked.Order());
        // Each attempt draws its own roll, so blocking one leaves later outcomes as without shock.
        var random = new SimulationRandom(options.Seed, 1UL << 63);
        var expected = new double[] { 0.5, 1, 1.5, 2, 2.5, 3, 3.5 }.Where(_ => random.NextDouble() < 0.5).Where(at => !blocked.Contains(at)).ToArray();
        Assert.Equal(expected, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.EnemyHit).Select(e => e.Time));
    }

    [Fact]
    public void ShockCreditDoesNotCarryFromADeadTargetToTheNext()
    {
        // Three attackers fire every 1/6 s and each later hit shocks for 0.4 s. The first target earns two
        // credits from its 1 s hit and dies at 2 s. The next is shocked from 3 s and 4 s, so its own third
        // shocked attempt (4 1/6 s) is blocked, not its first (3 1/6 s).
        var result = Run(ShockFixture(attackers: 3, duration: 0.4, targetHealth: 25), Options with { Duration = 4.5, DriveSimpleBuffs = true });
        Assert.Equal(1, result.Kills);
        var blocked = Assert.Single(result.FirstFightEvents, e => e.Kind == DummyEventKind.EnemyShotBlocked);
        Assert.True(blocked.Time > 4);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void MagazineOccupancyTracksPostShotAmmoAndStopsAtDowning(bool capture, bool noReload)
    {
        var (original, _) = Fixture();
        var rules = original.Pack with
        {
            Selectors = [],
            NumericContextInputs = [.. original.Pack.NumericContextInputs, new() { ID = "missing", Name = "Missing", Min = 0, Max = 100, Step = 1 }],
            Stats = [.. original.Pack.Stats.Select(stat => stat.ID == "equipped_weapon_magazine_size" ? stat with { Formula = "2" } : stat),
                new() { ID = "weapon_no_reload", Formula = noReload ? "1" : "0" }],
            Talents = [new() { ID = "test", Name = "Magazine" }],
            Simulation = new() { Triggers = [new() { Talent = "test", Key = "missing", Kind = "magazine", Percent = true }] }
        };
        var result = Run(Recompile(rules), Options with { Iterations = 3, DriveSimpleBuffs = true, CaptureFirstFightEvents = capture });
        var values = result.MagazinePhaseUptime["missing"];
        Assert.Equal(2, values.Values.Sum(value => value.Seconds));
        Assert.Equal(new PhaseUptime(noReload ? 2 : 0, noReload ? 0.4 : 0), values["0"]);
        Assert.Equal(new PhaseUptime(noReload ? 0 : 1, noReload ? 0 : 0.2), values["50"]);
        Assert.Equal(values["50"], values["100"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CyclePhaseUptimeStopsAtDowningAndRetainsUnvisitedValues(bool capture)
    {
        var (original, _) = Fixture();
        var rule = new SimTriggerDefinition
        {
            Talent = "test",
            Key = "buff",
            Kind = "cycle",
            Phases = [new() { Until = 1, Value = "early" }, new() { Until = 3, Value = "late" }, new() { Until = 4, Value = "unvisited" }]
        };
        var rules = original.Pack with
        {
            Selectors = [new() { ID = "buff", Name = "Phase", Values = ["early", "late", "unvisited"] }],
            Talents = [new() { ID = "test", Name = "Cycle" }],
            Simulation = new() { Triggers = [rule] }
        };
        var result = Run(Recompile(rules), Options with { Iterations = 3, DriveSimpleBuffs = true, CaptureFirstFightEvents = capture });
        Assert.Equal(2, result.Incoming!.TimeToDown!.Mean);
        Assert.Equal(new PhaseUptime(1, 0.2), result.CyclePhaseUptime["buff"]["early"]);
        Assert.Equal(new PhaseUptime(1, 0.2), result.CyclePhaseUptime["buff"]["late"]);
        Assert.Equal(new PhaseUptime(0, 0), result.CyclePhaseUptime["buff"]["unvisited"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TimedTriggerUptimeStopsAtDowningWithTheFullDurationDenominator(bool capture)
    {
        var result = Run(Fixture(Buff("hit")), Options with { Iterations = 3, DriveSimpleBuffs = true, CaptureFirstFightEvents = capture });
        Assert.Equal(2, result.Incoming!.TimeToDown!.Mean);
        Assert.Equal(new TimedTriggerUptime(2, 0.4, null), result.TimedTriggerUptime["buff"]);
    }

    [Fact]
    public void AWholeSecondFollowsEnemyFireAtTheSameTimeAndStopsAtDowning()
    {
        // Enemy hits at 1 s and 2 s; the second at 1 s follows the first hit and precedes the shot, and the
        // player is down at 2 s before that second fires.
        var result = Run(Fixture(Buff("second")), Options with { DriveSimpleBuffs = true });
        Assert.Equal(2, result.Incoming!.TimeToDown!.Mean);
        Assert.Equal(new double[] { 10, 20 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Damage));
        Assert.Equal(1, result.TimedTriggerUptime["buff"].Seconds, 8);
    }

    [Fact]
    public void AReadyRuleGrantsTemporaryArmorEachTimeItFires()
    {
        // Ablative Nano-Plating's self part (R36): an armor break grants 80% of max armor (10) as temporary armor for 10 s,
        // at most once per 45 s cooldown.
        var rule = new SimTriggerDefinition
        { Talent = "test", Key = "ready", On = "armor_break", Kind = "ready", Duration = 45, Grant = "temporary_armor", GrantAmount = 0.8, GrantDuration = 10 };
        var rules = Fixture().Pack.Pack with
        {
            Selectors = [.. Fixture().Pack.Pack.Selectors, new() { ID = "ready", Name = "Ready", Values = ["true"] }],
            Simulation = new() { Triggers = [rule] }
        };
        var baseline = Run(Fixture(), Options with { Duration = 120, DriveSimpleBuffs = true });
        var result = Run(Recompile(rules), Options with { Duration = 120, DriveSimpleBuffs = true });
        // This fixture breaks armor once before going down; the grant lands at that break (the cooldown is pinned in
        // TimedBuffStateTests), and the bonus armor delays going down.
        var grant = Assert.Single(result.FirstFightEvents, e => e.Kind == DummyEventKind.BonusArmorGranted);
        var armorBreak = Assert.Single(result.FirstFightEvents, e => e.Kind == DummyEventKind.ArmorBreak);
        Assert.Equal((armorBreak.Time, 8.0), (grant.Time, grant.Damage));
        Assert.True((result.Incoming!.TimeToDown?.Mean ?? double.PositiveInfinity) > baseline.Incoming!.TimeToDown!.Mean);
    }

    // R41's armor-repair grants on each enemy hit, with 100 armor and enemy hits of 10 at 1 s and 2 s. The rule is a buff short
    // enough to fire again at the next hit.
    private static DummyFightResult RepairOnHit(string grant, double amount, double duration = 0, string instances = "", double fight = 2.5)
    {
        var rule = new SimTriggerDefinition { Talent = "test", Key = "buff", On = "damaged", Kind = "buff", Duration = 0.1, Grant = grant, GrantAmount = amount, GrantDuration = duration, GrantInstances = instances };
        return Run(Fixture(rule, changes: [("character_armor", "100"), ("character_health", "100")]), Options with { Duration = fight, DriveSimpleBuffs = true });
    }

    [Fact]
    public void AMissingArmorRepairTakesAShareOfWhatIsMissingAtTheEvent()
    {
        // Clutch's shape: 50% of missing armor. 100 -> 90, +5 -> 95; -> 85, +7.5 -> 92.5.
        var result = RepairOnHit("missing_armor_repair", 0.5);
        Assert.Equal([5, 7.5], result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.ArmorRepaired).Select(e => e.Damage));
        Assert.Equal(12.5, result.Incoming!.ArmorRepaired, 8);
    }

    [Fact]
    public void AnInstantArmorRepairIsAShareOfMaxArmorCappedAtFull()
    {
        // Doctor Home's kit shape: 50% of max armor (50), capped by the 10 missing after each hit.
        var result = RepairOnHit("armor_repair", 0.5);
        Assert.Equal([10, 10], result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.ArmorRepaired).Select(e => e.Damage));
    }

    [Theory]
    [InlineData("10", new double[] { 5, 5 })]
    [InlineData("100", new double[0])]
    public void AnArmorGatedRepairFiresOnlyWhileArmorIsLow(string armor, double[] repairs)
    {
        // Clutch's low-armor repair (R41): every shot crits; 50% of missing armor while armor is below 15% of max. With 10 armor
        // the enemy hit at 1 s empties it, so the crits at 1 s and 2 s repair 5 each; 100 armor never drops below 15 here.
        var rule = new SimTriggerDefinition
        { Talent = "test", Key = "buff", On = "crit", Kind = "buff", Duration = 0.1, Grant = "missing_armor_repair", GrantAmount = 0.5, WhileArmorBelow = 0.15 };
        var result = Run(Fixture(rule, changes: [("character_armor", armor), ("character_health", "100"), ("effective_critical_hit_chance", "1")]),
            Options with { Duration = 2.5, DriveSimpleBuffs = true });
        Assert.Equal(repairs, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.ArmorRepaired).Select(e => e.Damage));
    }

    [Theory]
    [InlineData("hit", "100000", "1", new double[] { 5 })]
    [InlineData("hit_pellet", "100000", "1", new double[] { 5, 2.5 })]
    [InlineData("hit_pellet", "5", "1", new double[] { 5 })]
    [InlineData("hit_pellet", "100000", "0", new double[0])]
    public void AHitPelletRuleFiresForEachPelletThatLands(string on, string health, string hit, double[] repairs)
    {
        // As ACritPelletRuleFiresForEachCriticalPellet, without crits: a two-pellet weapon, and a repair of half the missing armor.
        // A pellet on a dead target doesn't count, and neither does one that misses.
        var rule = new SimTriggerDefinition
        { Talent = "test", Key = "buff", On = on, Kind = "buff", Duration = 0.1, Grant = "missing_armor_repair", GrantAmount = 0.5, WhileArmorBelow = 0.9 };
        var result = Run(Fixture(rule, changes: [("character_health", "100"), ("practical_hit_fraction", hit), ("equipped_weapon_pellets", "2"),
            ("enemy_target_health", health)]), Options with { Duration = 1.5, DriveSimpleBuffs = true });
        Assert.Equal(repairs, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.ArmorRepaired).Select(e => e.Damage));
    }

    [Fact]
    public void AStatusCanApplyOnAHitPellet()
    {
        // Shock applying on each landed pellet reads as on each hit, for a one-pellet weapon.
        var options = Options with { Duration = 4, DriveSimpleBuffs = true, Iterations = 2 };
        var pellets = Run(ShockFixture(on: "hit_pellet", applyOnHit: true), options).Incoming!.BlockedShots;
        Assert.True(pellets > 0);
        Assert.Equal(Run(ShockFixture(on: "hit", applyOnHit: true), options).Incoming!.BlockedShots, pellets);
    }

    // A two-item set with a talent no item offers, and a rule on it. As for Apex Engine, the bonus sits on the set's top row, needing
    // both pieces, and reads the key the rule drives: a hit adds 10 damage for 10 s.
    private static (CompiledPackExpressions Pack, BuildSnapshot Build) SetTalentFixture(params string[] pieces)
    {
        var (original, _) = Fixture();
        var rules = original.Pack with
        {
            Slots = [.. original.Pack.Slots, new() { ID = "chest", Name = "Chest" }, new() { ID = "mask", Name = "Mask" }],
            Items = [.. original.Pack.Items, new() { ID = "vest", Name = "Vest", Slot = "chest" }, new() { ID = "hat", Name = "Hat", Slot = "mask" }],
            Sets = [new() { ID = "set", Name = "Set", Items = ["vest", "hat"], Bonuses = [new() { Pieces = 1 }, new() { Pieces = 2, Modifiers = original.Pack.Talents[0].Modifiers }] }],
            Talents = [.. original.Pack.Talents.Select(talent => talent with { ID = "set_talent", ModeledBySets = ["set"], Modifiers = [] })],
            Simulation = original.Pack.Simulation with { Triggers = [Buff("hit") with { Talent = "set_talent" }] }
        };
        rules = rules with { Items = [.. rules.Items.Select(item => item with { TalentSlots = [] })] };
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun");
        foreach (var piece in pieces) { build = build.WithEquipment(piece == "vest" ? "chest" : "mask", piece); }
        return (Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled), build);
    }

    [Fact]
    public void ASetsTalentRunsWithSetPieceCredit()
    {
        // Resourceful's shape: a talent crediting a piece, on the gun, brings one worn piece to the set's top bonus, as the resolver counts.
        var (original, _) = SetTalentFixture();
        var rules = original.Pack with
        {
            Talents = [.. original.Pack.Talents, new() { ID = "credit", Name = "Credit", SetPieceCredit = 1 }],
            Items = [.. original.Pack.Items.Select(item => item.ID == "gun" ? item with { TalentSlots = [new() { ID = "talent", Choices = ["credit"] }] } : item)]
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithEquipment("chest", "vest").WithTalentSelection("primary_weapon", "talent", "credit");
        var result = Run((pack, build), Options with { Duration = 1.5, DriveSimpleBuffs = true, IncludeIncoming = false });
        Assert.Equal(new double[] { 10, 20 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Damage));
    }

    [Theory]
    [InlineData(new string[0], 10)]
    [InlineData(new[] { "vest" }, 10)]
    [InlineData(new[] { "vest", "hat" }, 20)]
    public void ASetsTalentRunsOnceItsTopBonusApplies(string[] pieces, double second)
    {
        // The second shot carries the first shot's buff only with the whole set on.
        var result = Run(SetTalentFixture(pieces), Options with { Duration = 1.5, DriveSimpleBuffs = true, IncludeIncoming = false });
        Assert.Equal(new[] { 10, second }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Damage));
    }

    [Theory]
    [InlineData("crit", "100000", new double[] { 5 })]
    [InlineData("crit_pellet", "100000", new double[] { 5, 2.5 })]
    [InlineData("crit_pellet", "5", new double[] { 5 })]
    public void ACritPelletRuleFiresForEachCriticalPellet(string on, string health, double[] repairs)
    {
        // Clutch per critical pellet (R43): a two-pellet weapon where both pellets crit. The enemy hit at 1 s empties the 10 armor;
        // crit repairs 50% of the missing armor once per shot, crit_pellet once per pellet (5, then 50% of the remaining 5). The
        // gate is 90% here so the second pellet still qualifies; at Clutch's 15% the first repair would close it. With 5 health the
        // first pellet kills, and a pellet landing on a dead target doesn't fire (R43; the source's myTriggerOnZeroDamage).
        var rule = new SimTriggerDefinition
        { Talent = "test", Key = "buff", On = on, Kind = "buff", Duration = 0.1, Grant = "missing_armor_repair", GrantAmount = 0.5, WhileArmorBelow = 0.9 };
        var result = Run(Fixture(rule, changes: [("character_health", "100"), ("effective_critical_hit_chance", "1"), ("equipped_weapon_pellets", "2"),
            ("enemy_target_health", health)]), Options with { Duration = 1.5, DriveSimpleBuffs = true });
        Assert.Equal(repairs, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.ArmorRepaired).Select(e => e.Damage));
    }

    [Theory]
    [InlineData(10.0, null, 0.0, "Enemy hit rate follows the fight distance: 0 at 10 m (up to 15 m)")]
    [InlineData(20.0, null, 1.0, "Enemy hit rate follows the fight distance: 100 at 20 m (beyond the pack's bands)")]
    [InlineData(10.0, "100", 1.0, "Enemy hit rate is set to 100; its default at 10 m (0) is not used")]
    [InlineData(null, null, 1.0, null)]
    public void TheEnemyHitRateFollowsTheFightDistanceUnlessSet(double? distance, string? set, double hitShare, string? note)
    {
        // P12: the pack gives the enemy hit rate by distance (here 0% up to 15 m, 100% beyond; the input's default is 100%).
        // An explicit setting wins, and without a fight distance the bands don't apply.
        var (original, build) = Fixture();
        var rules = original.Pack with
        {
            Simulation = original.Pack.Simulation with
            {
                DistanceDefaults = [new() { Input = "enemy_hit_rate", Bands = [new() { UpTo = 15, Value = 0 }, new() { Value = 100 }] }]
            }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var context = set is null ? Context : Context.Add("enemy_hit_rate", set);
        var result = Run((pack, build), Options with { Duration = 3.5, DriveSimpleBuffs = true, Distance = distance }, context);
        // At 0% this fixture's enemy deals no damage, so it makes no attempts at all.
        if (hitShare == 0) { Assert.Equal(0, result.Incoming!.Hits); }
        else { Assert.Equal(hitShare, result.Incoming!.Hits / result.Incoming.Attempts); }
        if (note is null) { Assert.DoesNotContain(result.Limitations, text => text.Contains("Enemy hit rate", StringComparison.Ordinal)); }
        else { Assert.Contains(note, result.Limitations); }
        // Coverage says the value came from the distance rather than from a choice.
        Assert.Equal(distance is not null && set is null, result.Coverage.InputBasis.Contains("Set from the fight distance, not chosen: Enemy hit rate", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(3, 2.0, 1.0)]
    [InlineData(100, null, 0.0)]
    public void TimeToClearIsTheNthKillsTimeInEachFight(int targets, double? clearedAt, double clearedFraction)
    {
        // P10, an encounter by kill count: a shot a second kills each 5-health target, with no retarget gap, so kill 3 lands at
        // 2 s in every fight; a 3.5 s fight never reaches 100 kills.
        var result = Run(Fixture(changes: [("enemy_target_health", "5"), ("character_health", "1000")]),
            Options with { Duration = 3.5, Iterations = 3, ClearKills = targets });
        Assert.Equal(clearedFraction, result.ClearedFraction);
        Assert.Equal(clearedAt, result.TimeToClear?.Mean);
        Assert.Equal(clearedAt, result.TimeToClear?.Max);
        Assert.Contains(result.Limitations, note => note.StartsWith($"Time to clear {targets} targets is when kill number {targets} lands", StringComparison.Ordinal));
        // Without a count there is nothing to report.
        var plain = Run(Fixture(changes: [("enemy_target_health", "5"), ("character_health", "1000")]), Options with { Duration = 3.5 });
        Assert.Null(plain.ClearedFraction);
        Assert.Null(plain.TimeToClear);
        // P15: kills at 0, 1, 2 and 3 s, each target arriving as the last dies: 0, 1, 1 and 1 s to kill, 0.75 s per fight.
        Assert.Equal((0.75, 0.75, 0.75), (plain.TimeToKill!.Mean, plain.TimeToKill.Min, plain.TimeToKill.Max));
        Assert.Equal(1, plain.KilledFraction);
        Assert.Contains(plain.Limitations, note => note.StartsWith("Time to kill is each fight's average", StringComparison.Ordinal));
    }

    // P10, mixed targets: an enemy_target input with profiles 1 to 3, a target's health 10 per profile number, and enemy hits
    // that scale with the profile too, since the target in play sets enemy fire.
    private static (CompiledPackExpressions Pack, BuildSnapshot Build) ProfileFixture(string damage = "10")
    {
        var (original, build) = Fixture(changes: [("character_health", "100000")]);
        // The formulas read the input, so they go in once it exists.
        var formulas = new Dictionary<string, string>
        {
            ["enemy_target_health"] = "10 * context_value('enemy_target')",
            ["enemy_target_hit_damage"] = "10 * context_value('enemy_target')",
            ["equipped_weapon_damage"] = damage,
            ["equipped_weapon_critical_damage"] = damage,
            ["equipped_weapon_headshot_damage"] = damage,
            ["equipped_weapon_critical_headshot_damage"] = damage,
            ["equipped_weapon_practical_shot_damage"] = damage
        };
        var rules = original.Pack with
        {
            NumericContextInputs = [.. original.Pack.NumericContextInputs, new() { ID = "enemy_target", Name = "Enemy target", Min = 0, Max = 3, Step = 1, Default = 1 }],
            Stats = [.. original.Pack.Stats.Select(stat => formulas.TryGetValue(stat.ID, out var formula) ? stat with { Formula = formula } : stat)]
        };
        return (Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled), build);
    }

    [Theory]
    [InlineData("10", new[] { 0d, 2, 3, 5 }, new[] { 0d, 2, 1, 2 }, 90)]
    [InlineData("10 * context_value('enemy_target')", new[] { 0d, 1, 2, 3, 4, 5 }, new[] { 0d, 1, 1, 1, 1, 1 }, 80)]
    public void MixedTargetsCycleTheirProfilesPoolsAndBonuses(string damage, double[] kills, double[] killTimes, double damageTaken)
    {
        // Targets alternate profiles 1 and 2 (10 and 20 health). A shot a second for 10 kills a profile-1 target at once and a
        // profile-2 one with its second shot. When weapon damage also scales with the target's profile, every target dies to
        // one shot: the bonus follows the target in hand.
        var fixture = ProfileFixture(damage);
        var result = Run(fixture, Options with { Duration = 5.5, DriveSimpleBuffs = true, TargetSequence = [1, 2] });
        Assert.Equal(kills, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Kill).Select(e => e.Time));
        Assert.Equal(killTimes, result.KillTimes);
        // The per-fight time to kill averages the same kills (one fight).
        Assert.Equal(killTimes.Average(), result.TimeToKill!.Mean, 12);
        Assert.Contains("Targets cycle through a sequence of 2 (profiles 1, 2), each with its own pools and target-dependent bonuses, skill damage included; the target in play (the arriving one after a kill) sets enemy fire and recovery, and a kill scales the wait for the next attempt to the new cadence", result.Limitations);
        // Enemy fire follows the target: an attempt a second, for 10 from profile 1 and 20 from profile 2. An attempt wins a tie
        // with a shot, so one at a kill's time still comes from the target dying; the next comes from the next target.
        Assert.Equal(damageTaken, result.Incoming!.DamageTaken);
        // One profile throughout keeps the fixed schedule and that profile's damage: 10 from each of five attempts.
        var single = Run(fixture, Options with { Duration = 5.5, DriveSimpleBuffs = true });
        Assert.Equal(50, single.Incoming!.DamageTaken);
    }

    // Profile 1 fires every second, profile 2 every half second, each hit for 10 per profile number.
    private static (CompiledPackExpressions Pack, BuildSnapshot Build) CadenceFixture(string dps = "enemy_target_hit_damage * context_value('enemy_target')",
        params (string Id, string Formula)[] changes)
    {
        var (original, build) = ProfileFixture();
        var formulas = changes.Append(("enemy_target_incoming_dps", dps)).ToDictionary(change => change.Item1, change => change.Item2);
        var rules = original.Pack with
        {
            Stats = [.. original.Pack.Stats.Select(stat => formulas.TryGetValue(stat.ID, out var formula) ? stat with { Formula = formula } : stat),
                .. formulas.Where(entry => original.Pack.Stats.All(stat => stat.ID != entry.Key)).Select(entry => new StatDefinition { ID = entry.Key, Formula = entry.Value })]
        };
        return (Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled), build);
    }

    [Fact]
    public void EachTargetInPlaySetsEnemyCadenceAndDamage()
    {
        // Kills at 0 s (profile 1), 2 s (profile 2), 3 s (profile 1) and 5 s (profile 2). The first attempt is due at 1 s, but the kill
        // at 0 s brings profile 2 in, and the whole interval left becomes a half-second one: 0.5, 1, 1.5 and 2 s hit for 20. The
        // kill at 2 s turns the half second left into a second: 3 s hits for 10, winning its tie with the shot. The kill at 3 s
        // turns a whole second into a half: 3.5, 4, 4.5 and 5 s hit for 20.
        var result = Run(CadenceFixture(), Options with { Duration = 5.5, DriveSimpleBuffs = true, TargetSequence = [1, 2] });
        var hits = result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.EnemyHit).ToArray();
        Assert.Equal(new[] { 0.5, 1, 1.5, 2, 3, 3.5, 4, 4.5, 5 }, hits.Select(e => e.Time));
        Assert.Equal(new double[] { 20, 20, 20, 20, 10, 20, 20, 20, 20 }, hits.Select(e => e.Damage));
        Assert.Equal(170, result.Incoming!.DamageTaken);
        Assert.Equal(hits.Select(e => e.Time), result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.EnemyShot).Select(e => e.Time));
        Assert.StartsWith("d2p-incoming-random-v73/", result.Contract, StringComparison.Ordinal);
    }

    [Fact]
    public void BetweenAKillAndTheNextArrivalTheArrivingTargetFires()
    {
        // The kill at 0 s leaves a 2 s gap before profile 2 arrives; its fire comes through the gap, every half second.
        var result = Run(CadenceFixture(), Options with { Duration = 2.2, DriveSimpleBuffs = true, TargetSequence = [1, 2] }, delay: 2);
        var hits = result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.EnemyHit).ToArray();
        Assert.Equal(new[] { 0.5, 1, 1.5, 2 }, hits.Select(e => e.Time));
        Assert.All(hits, hit => Assert.Equal(20, hit.Damage));
    }

    [Fact]
    public void ACoverCycleBlocksEachTargetsFire()
    {
        // The cycle applies to whichever target fires: behind cover (2 to 3 s) profile 2's attempts at 2 and 2.5 s are blocked.
        var result = Run(CadenceFixture(), Cover(2, 1) with { Duration = 3, TargetSequence = [1, 2] });
        Assert.Equal(new[] { 2, 2.5 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.EnemyShotBlocked).Select(e => e.Time));
        Assert.Equal(new[] { 0.5, 1, 1.5 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.EnemyHit).Select(e => e.Time));
    }

    [Fact]
    public void ASteadyCadenceInASequenceMatchesTheFixedSchedule()
    {
        // Counted from an anchor, a cadence like 0.1 s gets the same times as the fixed schedule; summing it would add an attempt
        // just before the 1 s end.
        var fixture = CadenceFixture("100");
        var steady = Run(fixture, Options with { Duration = 1, DriveSimpleBuffs = true, TargetSequence = [1, 1] });
        var plain = Run(fixture, Options with { Duration = 1, DriveSimpleBuffs = true });
        Assert.Equal(9, steady.Incoming!.Attempts);
        Assert.Equal(plain.FirstFightEvents.Where(e => e.Kind == DummyEventKind.EnemyShot).Select(e => e.Time),
            steady.FirstFightEvents.Where(e => e.Kind == DummyEventKind.EnemyShot).Select(e => e.Time));
    }

    [Fact]
    public void ATargetsFireStaysTheWeaponInHands()
    {
        // Damage taken doubles with the sidearm in hand. Its resolve of each profile comes second at setup, and mustn't replace the
        // rifle's: the first hit is the rifle's 10. The chosen profile is the first target's, so no state change corrects it later.
        var fixture = WithSidearm(ProfileFixture(), stat: "taken", primary: 1, sidearm: 2, reader: "enemy_damage_taken_factor");
        var result = Run(fixture, SwappingUnderFire(1.5) with { TargetSequence = [1] }, Context.Add("enemy_target", "1"));
        Assert.Equal(10, result.FirstFightEvents.First(e => e.Kind == DummyEventKind.EnemyHit).Damage);
    }

    private static DummyFightOptions Group(double duration, int enemies, params int[] sequence) =>
        Options with { Duration = duration, DriveSimpleBuffs = true, EnemiesAtOnce = enemies, TargetSequence = [.. sequence] };
    private static double[] Times(DummyFightResult result, DummyEventKind kind) => [.. result.FirstFightEvents.Where(e => e.Kind == kind).Select(e => e.Time)];

    [Fact]
    public void SeveralEnemiesFireAtOnceStaggeredAcrossAnInterval()
    {
        // ADR 0008: three enemies, each firing once a second, start a third of a second apart, and none dies.
        var result = Run(Fixture(changes: [("character_armor", "0"), ("character_health", "100000")]), Group(3.5, 3));
        Assert.Equal(Enumerable.Range(1, 10).Select(step => step / 3d), Times(result, DummyEventKind.EnemyHit), (a, b) => Math.Abs(a - b) < 1e-9);
        Assert.Equal(100, result.Incoming!.DamageTaken);
        Assert.Contains(result.Limitations, note => note.StartsWith("3 enemies fire at once, staggered across an interval. Treat this as an upper bound", StringComparison.Ordinal));
        Assert.StartsWith("d2p-incoming-random-v73/", result.Contract, StringComparison.Ordinal);
    }

    [Fact]
    public void KillsDontShiftASteadyGroupsFire()
    {
        // Every shot kills, and each replacement takes its place in the stagger, so the fire is the same as with no kills.
        var killing = Run(Fixture(changes: [("character_armor", "0"), ("character_health", "100000"), ("enemy_target_health", "5")]), Group(3.5, 2));
        var surviving = Run(Fixture(changes: [("character_armor", "0"), ("character_health", "100000")]), Group(3.5, 2));
        Assert.Equal(4, killing.Kills);
        Assert.Equal(Times(surviving, DummyEventKind.EnemyHit), Times(killing, DummyEventKind.EnemyHit));
    }

    [Fact]
    public void EachEnemyInAGroupFiresAsItsOwnProfile()
    {
        // Targets alternate profiles 1 (10 health, a hit for 10 each second) and 2 (20 health, a hit for 20 each half second), two at
        // once, staggered across the shorter interval: 0.25 s and 0.5 s first. Kills at 0 s (profile 1), 2 s (profile 2) and 3 s
        // (profile 1) each bring in the same profile, keeping its clock. The enemy not being shot still hits for its own damage:
        // at 0.25 s the waiting profile 1 hits for 10 while profile 2 is being shot.
        var result = Run(CadenceFixture(), Group(3.2, 2, 1, 2));
        var hits = result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.EnemyHit).ToArray();
        Assert.Equal(new[] { 0.25, 0.5, 1, 1.25, 1.5, 2, 2.25, 2.5, 3 }, hits.Select(e => e.Time));
        Assert.Equal(new double[] { 10, 20, 20, 10, 20, 20, 10, 20, 20 }, hits.Select(e => e.Damage));
        Assert.Equal(new double[] { 0, 2, 3 }, Times(result, DummyEventKind.Kill));
        Assert.Contains(result.Limitations, note => note.EndsWith("each enemy fires as its own profile, and the one being shot sets recovery", StringComparison.Ordinal));
    }

    [Fact]
    public void AReplacementKeepsItsShareOfAnIntervalAtItsOwnCadence()
    {
        // Profiles 1, 2, 2: a profile 2 replaces profile 1 at the 0 s kill. The quarter second left of a whole-second interval
        // becomes an eighth of a half-second one, so 0.125 and 0.625 s are its attempts; 0.5 s is the other enemy's.
        var result = Run(CadenceFixture(), Group(1, 2, 1, 2, 2));
        Assert.Equal(new[] { 0.125, 0.5, 0.625 }, Times(result, DummyEventKind.EnemyHit));
    }

    [Fact]
    public void OnlyTheProfilesThatFireCountTowardTheAttemptLimit()
    {
        // The chosen profile 3 would be over the limit with ten enemies, but only profile 1 fires.
        var fixture = CadenceFixture("enemy_target_hit_damage * (context_value('enemy_target') > 2 ? 1000 : 1)");
        Assert.NotNull(Run(fixture, Group(600, 10, 1), Context.Add("enemy_target", "3")).Incoming);
    }

    [Fact]
    public void SkillBatchesSplitAtASteadyGroupsAttempts()
    {
        // Three enemies a third of a second apart: a continuous skill's 100 ms batches end at the attempts too, and the note says so.
        var result = Run(SkillProfileFixture("0", cooldown: "1", rate: "5", window: "1"), Group(1, 3) with { IncludeSkills = true });
        Assert.Contains(Times(result, DummyEventKind.SkillTick), at => Math.Abs(at - 1d / 3) < 1e-9);
        Assert.Contains(result.Limitations, note => note.Contains("continuous-damage batches split at incoming attempts", StringComparison.Ordinal));
    }

    [Fact]
    public void ShockStopsOnlyTheEnemyBeingShot()
    {
        // Two enemies, each attempting every half second: the first on the quarters, the second on the halves. Only the first is
        // being shot, so only quarter-second attempts can be blocked.
        var result = Run(ShockFixture(duration: 1), Group(4, 2));
        var blocked = Times(result, DummyEventKind.EnemyShotBlocked);
        Assert.NotEmpty(blocked);
        Assert.All(blocked, at => Assert.Equal(0.25, at % 0.5));
    }

    [Fact]
    public void ACoverCycleCoversTheWholeGroup()
    {
        // One enemy on the half seconds and one on the whole seconds; behind cover (2 to 3 s) both are blocked.
        var result = Run(Fixture(changes: [("character_armor", "0"), ("character_health", "100000")]), Group(3, 2) with { PeekSeconds = 2, CoverSeconds = 1, PeekHitShare = 1 });
        Assert.Equal(new[] { 2, 2.5 }, Times(result, DummyEventKind.EnemyShotBlocked));
        Assert.Equal(new[] { 0.5, 1, 1.5 }, Times(result, DummyEventKind.EnemyHit));
    }

    private static (CompiledPackExpressions Pack, BuildSnapshot Build) OneShotTargets(params (string Id, string Formula)[] changes) =>
        Fixture(changes: [("character_armor", "0"), ("character_health", "100000"), ("enemy_target_health", "5"), .. changes]);

    [Fact]
    public void AShrinkingGroupsFireFallsWithEachKill()
    {
        // Two enemies, a shot killing each: the kill at 0 s silences the first before its 0.5 s attempt, and the second fires at 1 s,
        // winning its tie with the shot that kills it. Then nobody fires or is shot for the rest of the fight.
        var result = Run(OneShotTargets(), Group(5, 2) with { ClearKills = 2, EnemiesShrink = true });
        Assert.Equal(new double[] { 1 }, Times(result, DummyEventKind.EnemyHit));
        Assert.Equal(new double[] { 0, 1 }, Times(result, DummyEventKind.Kill));
        Assert.Equal(new double[] { 0, 1 }, Times(result, DummyEventKind.Shot));
        Assert.Equal(1, result.ClearedFraction);
        Assert.Equal(1, result.TimeToClear!.Mean);
        Assert.Contains(result.Limitations, note => note.Contains("There are 2 in all: the next joins in a dead enemy's place until none are left, then fire falls with each kill, and after the last nobody fires or is shot", StringComparison.Ordinal));
        Assert.StartsWith("d2p-incoming-random-v73/", result.Contract, StringComparison.Ordinal);
    }

    [Fact]
    public void ReserveEnemiesJoinUntilNoneAreLeft()
    {
        // Four in all, two at once. The kills at 0 and 1 s bring in the reserves, keeping the clocks: one on the half seconds and
        // one on the whole. The kill at 2 s has nobody left to bring in, so the half-second clock stops, and the last enemy
        // fires at 2 and 3 s.
        var result = Run(OneShotTargets(), Group(5, 2) with { ClearKills = 4, EnemiesShrink = true });
        Assert.Equal(new[] { 0.5, 1, 1.5, 2, 3 }, Times(result, DummyEventKind.EnemyHit));
        Assert.Equal(new double[] { 0, 1, 2, 3 }, Times(result, DummyEventKind.Kill));
        // Without the option the same group keeps firing to the end, with new enemies arriving.
        var endless = Run(OneShotTargets(), Group(5, 2) with { ClearKills = 4 });
        Assert.Equal(9, endless.Incoming!.Attempts);
        Assert.Equal(5, endless.Kills);
    }

    [Fact]
    public void AfterTheLastEnemyNothingElseHappens()
    {
        var shrinking = Group(9, 2) with { ClearKills = 2, EnemiesShrink = true };
        // Cover at 2 s finds nobody left, so the part-used magazine stays as it is.
        var covered = Run(OneShotTargets(), shrinking with { PeekSeconds = 2, CoverSeconds = 1, PeekHitShare = 1 });
        Assert.Equal(new double[] { 0, 1 }, Times(covered, DummyEventKind.Shot));
        Assert.Empty(Times(covered, DummyEventKind.ReloadStarted));
        // A last round spent on the last enemy starts no reload.
        var emptied = Run(OneShotTargets(("equipped_weapon_magazine_size", "2")), shrinking);
        Assert.Equal(new double[] { 0, 1 }, Times(emptied, DummyEventKind.Shot));
        Assert.Equal(0, emptied.Reloads);
        // Swapping after kills, the kill at 0 s swaps at 1 s, and the last kill at 2 s swaps no more.
        var swapping = Run(WithSidearm(OneShotTargets()), shrinking with { Rotation = new("secondary_weapon", WeaponSwapMode.AfterKill) });
        Assert.Equal(new double[] { 0, 2 }, Times(swapping, DummyEventKind.Kill));
        Assert.Equal(new double[] { 1 }, Times(swapping, DummyEventKind.SwapStarted));
        // Nor does a last round swap when empty.
        var emptiedSwapping = Run(WithSidearm(OneShotTargets(("equipped_weapon_magazine_size", "1"))), shrinking with { Rotation = new("secondary_weapon") });
        Assert.Equal(new double[] { 0, 2 }, Times(emptiedSwapping, DummyEventKind.Kill));
        Assert.Equal(new double[] { 1 }, Times(emptiedSwapping, DummyEventKind.SwapStarted));
        var skills = Run(SkillProfileFixture("100", cooldown: "0.5"), Group(5, 2) with { ClearKills = 2, EnemiesShrink = true, IncludeSkills = true });
        Assert.Equal(2, skills.Kills);
        var lastKill = Times(skills, DummyEventKind.Kill).Max();
        Assert.All(Times(skills, DummyEventKind.SkillImpact), at => Assert.True(at <= lastKill));
        Assert.DoesNotContain(skills.FirstFightEvents, e => e.Kind == DummyEventKind.EnemyHit && e.Time > lastKill);
        // A skill landing the last kill while the weapon waits for its next shot: the weapon stops there too, with no shot and no
        // cover reload after it.
        var skillKill = Run(SkillProfileFixture("100", cooldown: "0.5"), shrinking with { IncludeSkills = true, PeekSeconds = 3, CoverSeconds = 1, PeekHitShare = 1 });
        var lastSkillKill = Times(skillKill, DummyEventKind.Kill).Max();
        Assert.All(Times(skillKill, DummyEventKind.Shot), at => Assert.True(at <= lastSkillKill));
        Assert.Empty(Times(skillKill, DummyEventKind.ReloadStarted));
    }

    [Fact]
    public void AfterTheLastEnemyReadingsStayOnIt()
    {
        // Only profile 2 regenerates armor. Three enemies in all, with kills at 0 s (profile 1), 2 s (profile 2) and 3 s (profile 1): the
        // sequence's next target would be profile 2, but it never comes, so nothing regenerates after 3 s.
        var fixture = CadenceFixture(changes: [("armor_regeneration", "context_value('enemy_target') > 1 ? 5 : 0"), ("armor_regeneration_percent", "0"),
            ("estimated_armor_regeneration", "armor_regeneration + character_armor * armor_regeneration_percent")]);
        var result = Run(fixture, Group(6, 2, 1, 2) with { ClearKills = 3, EnemiesShrink = true });
        Assert.Equal(new double[] { 0, 2, 3 }, Times(result, DummyEventKind.Kill));
        Assert.DoesNotContain(result.FirstFightEvents, e => e.Kind == DummyEventKind.ArmorRepaired && e.Time > 3);
    }

    [Fact]
    public void AShrinkingGroupNeedsToKnowHowManyEnemiesThereAre()
    {
        var fixture = OneShotTargets();
        Assert.Equal("enemies-at-once-unsupported", Assert.Throws<SimulationException>(() => Run(fixture, Options with { DriveSimpleBuffs = true, ClearKills = 2, EnemiesShrink = true })).Code);
        Assert.Equal("enemies-at-once-unsupported", Assert.Throws<SimulationException>(() => Run(fixture, Group(5, 2) with { EnemiesShrink = true })).Code);
        Assert.Equal("enemies-at-once-unsupported", Assert.Throws<SimulationException>(() => Run(fixture, Group(5, 3) with { ClearKills = 2, EnemiesShrink = true })).Code);
    }

    [Fact]
    public void SeveralEnemiesAreRefusedWhereTheyAreNotModeled()
    {
        var fixture = Fixture();
        Assert.Equal("enemies-at-once-unsupported", Assert.Throws<SimulationException>(() => Run(fixture, Group(5, 2) with { IncludeIncoming = false })).Code);
        Assert.Equal("enemies-at-once-unsupported", Assert.Throws<SimulationException>(() => Run(fixture, Group(5, 2) with { DriveSimpleBuffs = false })).Code);
        // The attackers input would count them twice.
        Assert.Equal("Several enemies at once replace Enemies Attacking; set that to 1", DummyFightOptions.EnemiesAtOnceProblem(true, true, 2));
        Assert.Equal("enemies-at-once-unsupported", Assert.Throws<SimulationException>(() => Run(ShockFixture(attackers: 2), Group(5, 2))).Code);
        Assert.Throws<ArgumentOutOfRangeException>(() => Run(fixture, Group(5, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Run(fixture, Group(5, 11)));
        // A cadence within the limit for one enemy can be over it for ten: 200 attempts a second for 600 s is 120,000 each.
        Assert.Equal("incoming-work-limit", Assert.Throws<SimulationException>(() =>
            Run(Fixture(changes: [("enemy_target_incoming_dps", "2000")]), Group(600, 10))).Code);
    }

    [Fact]
    public void EnemyFireThatCantFollowTheTargetIsRefused()
    {
        var options = Options with { DriveSimpleBuffs = true, TargetSequence = [1, 2] };
        // A profile that never fires leaves no attempt to time the next from.
        Assert.Equal("incoming-state-unsupported", Assert.Throws<SimulationException>(() =>
            Run(CadenceFixture("context_value('enemy_target') > 1 ? 10 : 0"), options)).Code);
        // The player's own pools can't change with the target.
        Assert.Equal("incoming-state-unsupported", Assert.Throws<SimulationException>(() =>
            Run(CadenceFixture(changes: [("character_armor", "10 * context_value('enemy_target')")]), options)).Code);
        // Refused at setup, whichever targets a fight reaches: profile 1 outlives these fights, so profile 2 never comes up.
        Assert.Equal("incoming-repair-unsupported", Assert.Throws<SimulationException>(() =>
            Run(CadenceFixture(changes: [("enemy_target_health", "100000"), ("headache_armor_repair_share", "context_value('enemy_target') > 1 ? 0.1 : 0")]), options)).Code);
        Assert.Equal("incoming-work-limit", Assert.Throws<SimulationException>(() =>
            Run(CadenceFixture("enemy_target_hit_damage * 100000000 * context_value('enemy_target')", ("enemy_target_health", "100000")), options with { Duration = 600 })).Code);
        // Without incoming fire there is nothing to follow, and those profiles run.
        Assert.True(Run(CadenceFixture("context_value('enemy_target') > 1 ? 10 : 0"), options with { IncludeIncoming = false }).Kills > 0);
    }

    [Fact]
    public void ATargetSequenceIsRefusedWhereItIsNotModeled()
    {
        var fixture = ProfileFixture();
        Assert.Equal("target-sequence-unsupported", Assert.Throws<SimulationException>(() => Run(fixture, Options with { TargetSequence = [1, 2] })).Code);
        Assert.Equal("target-sequence-invalid", Assert.Throws<SimulationException>(() => Run(fixture, Options with { DriveSimpleBuffs = true, TargetSequence = [1, 7] })).Code);
        Assert.Equal("target-sequence-invalid", Assert.Throws<SimulationException>(() => Run(fixture, Options with { DriveSimpleBuffs = true, TargetSequence = [0] })).Code);
        Assert.Equal("target-required", Assert.Throws<SimulationException>(() => DummyWeaponSimulator.Run(fixture.Pack, fixture.Build, Context,
            new() { Duration = 5, Mode = SimulationMode.Random, DriveSimpleBuffs = true, TargetSequence = [1] }, TestContext.Current.CancellationToken)).Code);
    }

    [Fact]
    public void TimeToKillNeedsTargetsAndAKill()
    {
        // No targets, or targets that never die within the fight: nothing to report.
        var (pack, build) = Fixture();
        var dummy = DummyWeaponSimulator.Run(pack, build, Context, new() { Duration = 5 }, TestContext.Current.CancellationToken);
        Assert.Null(dummy.KilledFraction);
        Assert.Null(dummy.TimeToKill);
        var unkilled = Run(Fixture(changes: [("character_health", "1000")]), Options with { Duration = 3.5 });
        Assert.Equal((null, 0.0), (unkilled.TimeToKill, unkilled.KilledFraction));
    }

    // A skill fixture whose targets and skill read the enemy_target profile; the weapon deals nothing, isolating the skill.
    private static (CompiledPackExpressions Pack, BuildSnapshot Build) SkillProfileFixture(string impact, string cooldown = "0.5",
        string health = "5 * context_value('enemy_target')", string rate = "0", string window = "0")
    {
        var (original, build) = IncomingSkillFixture(changes: [("character_health", "100000"), ("equipped_weapon_damage", "0"), ("equipped_weapon_critical_damage", "0"),
            ("equipped_weapon_headshot_damage", "0"), ("equipped_weapon_critical_headshot_damage", "0"), ("equipped_weapon_practical_shot_damage", "0")]);
        var rules = original.Pack with
        {
            NumericContextInputs = [.. original.Pack.NumericContextInputs, new() { ID = "enemy_target", Name = "Enemy target", Min = 0, Max = 3, Step = 1, Default = 1 }],
            Stats = [.. original.Pack.Stats.Select(stat => stat.ID == "enemy_target_health" ? stat with { Formula = health } : stat)],
            ActiveSkills = [original.Pack.ActiveSkills[0] with
            {
                PreviewOutputs = [.. original.Pack.ActiveSkills[0].PreviewOutputs.Select(output => output.ID switch
                {
                    "impact" => output with { Formula = impact },
                    "cooldown" => output with { Formula = cooldown },
                    "rate" => output with { Formula = rate },
                    "window" => output with { Formula = window },
                    _ => output
                })]
            }]
        };
        return (Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled), build);
    }

    [Theory]
    [InlineData("5", new[] { 0d, 1, 1.5 })]
    [InlineData("5 * context_value('enemy_target')", new[] { 0d, 0.5, 1, 1.5, 2 })]
    public void SkillDamageFollowsEachTargetsProfile(string impact, double[] kills)
    {
        // Targets alternate profiles 1 and 2 (5 and 10 health) and a skill hits every 0.5 s. When its impact is 5 per profile
        // number, every impact kills the target in front of it. A flat 5 needs two impacts for a profile-2 target.
        var result = Run(SkillProfileFixture(impact), Options with { Duration = 2.2, DriveSimpleBuffs = true, IncludeSkills = true, TargetSequence = [1, 2] });
        Assert.Equal(kills, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Kill).Select(e => e.Time));
        Assert.Contains(result.Limitations, note => note.Contains("skill damage included", StringComparison.Ordinal));
    }

    [Fact]
    public void ATargetsOwnSkillAmountKillsExactly()
    {
        // The chosen profile's impact is 11 and profile 2's is 15, against 15 health. A ratio of the two (15 / 11 x 11) rounds to
        // 14.999...; the profile's own amount kills on the first impact.
        var result = Run(SkillProfileFixture("context_value('enemy_target') == 2 ? 15 : 11", health: "context_value('enemy_target') == 2 ? 15 : 11"),
            Options with { Duration = 0.4, DriveSimpleBuffs = true, IncludeSkills = true, TargetSequence = [2] });
        Assert.Equal([0d], result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Kill).Select(e => e.Time));
    }

    [Theory]
    [InlineData("5", 3.0)]
    [InlineData("5 * context_value('enemy_target')", 2.0)]
    public void ContinuousSkillDamageFollowsEachTarget(string rate, double secondKill)
    {
        // A continuous skill (no impact) deals its rate every 100 ms. The profile-1 target (5 health) dies near 1 s; the next,
        // profile 2 (10 health), near 2 s when the rate follows the target (10 a second) and near 3 s when it doesn't.
        var result = Run(SkillProfileFixture("0", cooldown: "1", rate: rate, window: "1"),
            Options with { Duration = 3.5, DriveSimpleBuffs = true, IncludeSkills = true, TargetSequence = [1, 2] });
        var kills = result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Kill).Select(e => e.Time).ToArray();
        Assert.InRange(kills[0], 0.9, 1.1);
        Assert.InRange(kills[1], secondKill - 0.1, secondKill + 0.1);
    }

    [Fact]
    public void SkillTimingThatFollowsTheTargetIsRefused()
    {
        var error = Assert.Throws<SimulationException>(() => Run(SkillProfileFixture("5", cooldown: "0.5 * context_value('enemy_target')"),
            Options with { Duration = 2, DriveSimpleBuffs = true, IncludeSkills = true, TargetSequence = [1, 2] }));
        Assert.Equal(("target-sequence-unsupported", "Skill timing, or which kinds of damage a skill deals, changes with the target profile, which mixed targets can't model yet"), (error.Code, error.Message));
    }

    // P10, several distances: falloff from 20 m to 40 m down to half damage, 10-health targets and 10-damage shots.
    private static (CompiledPackExpressions Pack, BuildSnapshot Build) FalloffFixture() => Fixture(changes: [("enemy_target_health", "10"),
        ("character_health", "100000"), ("equipped_weapon_optimal_range", "20"), ("equipped_weapon_long_range", "40"), ("weapon_falloff_floor", "0.5")]);

    [Theory]
    [InlineData(new[] { 10d, 40 }, new[] { 0d, 2, 3, 5 })]
    [InlineData(new[] { 10d }, new[] { 0d, 1, 2, 3, 4, 5 })]
    public void EachTargetsDistanceSetsItsFalloff(double[] distances, double[] kills)
    {
        // Targets alternate 10 m (full damage: one shot) and 40 m (half damage: two shots), a shot a second.
        var result = Run(FalloffFixture(), Options with { Duration = 5.5, DriveSimpleBuffs = true, TargetDistances = [.. distances] });
        Assert.Equal(kills, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Kill).Select(e => e.Time));
        Assert.Contains(result.Limitations, note => note.StartsWith($"Targets cycle through {distances.Length} distances", StringComparison.Ordinal)
            && note.EndsWith("each setting its falloff, its distance rules and any enemy fire that follows distance", StringComparison.Ordinal));
        // Falloff is noted for each target's distance.
        Assert.Contains(result.Limitations, note => note.StartsWith("Weapon damage falls off at each target's distance", StringComparison.Ordinal));
    }

    // The enemy hit rate is 100% up to 15 m and 50% beyond; a hit a second at any rate, since the DPS moves with it.
    private static (CompiledPackExpressions Pack, BuildSnapshot Build) BandFixture((CompiledPackExpressions Pack, BuildSnapshot Build) fixture)
    {
        var rules = fixture.Pack.Pack with
        {
            Simulation = fixture.Pack.Pack.Simulation with { DistanceDefaults = [new() { Input = "enemy_hit_rate", Bands = [new() { UpTo = 15, Value = 100 }, new() { Value = 50 }] }] }
        };
        return (Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled), fixture.Build);
    }

    // The attempts of a fight that land, from each attempt's own roll, for the hit chances given in order.
    private static double[] Landed(ulong seed, params (double At, double Chance)[] attempts)
    {
        var random = new SimulationRandom(seed, 1UL << 63);
        return [.. attempts.Select(attempt => (attempt.At, attempt.Chance, Roll: random.NextDouble())).Where(attempt => attempt.Roll < attempt.Chance).Select(attempt => attempt.At)];
    }

    [Fact]
    public void EnemyFireFollowsEachTargetsDistance()
    {
        // A shot a second kills each target, alternating 10 and 30 m. An attempt comes each second just before the shot, from the
        // next target in line: 30 m at 1 s, 10 m at 2 s, and so on, so the chance alternates 50% and 100%.
        var fixture = BandFixture(Fixture(changes: [("character_armor", "0"), ("character_health", "100000"), ("enemy_target_health", "5")]));
        var options = Options with { Duration = 5.5, DriveSimpleBuffs = true, Seed = 23, TargetDistances = [10, 30] };
        var result = Run(fixture, options);
        Assert.Equal(5, result.Incoming!.Attempts);
        Assert.Equal(Landed(23, (1, 0.5), (2, 1), (3, 0.5), (4, 1), (5, 0.5)), Times(result, DummyEventKind.EnemyHit));
        Assert.Contains("Enemy hit rate follows each target's distance: 100 at 10 m, 50 at 30 m", result.Limitations);
        Assert.Contains("Set from each target's distance, not chosen: Enemy hit rate", result.Coverage.InputBasis, StringComparison.Ordinal);
        // A rate the user sets wins, at every distance.
        var set = Run(fixture, options, Context.Add("enemy_hit_rate", "100"));
        Assert.Equal(5, set.Incoming!.Hits);
        Assert.Contains("Enemy hit rate is set to 100; its defaults by distance are not used", set.Limitations);
    }

    [Fact]
    public void EachEnemyInAGroupFiresFromItsOwnDistance()
    {
        // Two enemies a half second apart and nobody dies: the one at 10 m (being shot) fires at 0.5 and 1.5 s, and the waiting one at
        // 30 m at 1 and 2 s, each with its own chance.
        var fixture = BandFixture(Fixture(changes: [("character_armor", "0"), ("character_health", "100000")]));
        var result = Run(fixture, Group(2.5, 2) with { Seed = 23, TargetDistances = [10, 30] });
        Assert.Equal(Landed(23, (0.5, 1), (1, 0.5), (1.5, 1), (2, 0.5)), Times(result, DummyEventKind.EnemyHit));
        Assert.Contains(result.Limitations, note => note.EndsWith("each enemy's fire comes from its own distance", StringComparison.Ordinal));
        // Without target distances they share the fight's.
        var together = Run(fixture, Group(2.5, 2) with { Seed = 23, Distance = 30 });
        Assert.Contains(together.Limitations, note => note.EndsWith("all fire comes from the fight distance", StringComparison.Ordinal));
        // With no distance at all, the note doesn't claim one.
        Assert.DoesNotContain(Run(fixture, Group(2.5, 2)).Limitations, note => note.Contains("fight distance", StringComparison.Ordinal) && note.Contains("enemies fire at once", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(new[] { 10d, 30 }, new double[] { 5, 10, 5, 10 })]
    [InlineData(new[] { 30d, 10 }, new double[] { 10, 5, 10, 5 })]
    public void DistanceRulesReadEachEnemysOwnDistance(double[] distances, double[] hits)
    {
        // A talent halves the damage from enemies within 20 m (Challenger's shape). The enemy being shot and the one waiting fire in
        // turn, and each hit reads its own enemy's distance, whichever is being shot.
        var rule = new SimTriggerDefinition { Talent = "test", Key = "buff", Kind = "distance", Within = 20 };
        var fixture = Fixture(rule, "enemy_damage_taken_factor", -0.5, ("character_armor", "0"), ("character_health", "100000"));
        var result = Run(fixture, Group(2.5, 2) with { TargetDistances = [.. distances] });
        Assert.Equal(hits, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.EnemyHit).Select(e => e.Damage));
    }

    [Fact]
    public void TheFirstTargetsDistanceReplacesTheFightsAtSetup()
    {
        // A set distance gives way to target distances from the start, for the rule's reading as for the band.
        var rule = new SimTriggerDefinition { Talent = "test", Key = "buff", Kind = "distance", Within = 20 };
        var fixture = BandFixture(Fixture(rule, "enemy_damage_taken_factor", -0.5, ("character_armor", "0"), ("character_health", "100000")));
        var result = Run(fixture, Options with { Duration = 1.5, DriveSimpleBuffs = true, Distance = 5, TargetDistances = [30] }, Context.Add("enemy_hit_rate", "100"));
        Assert.Equal(new double[] { 10 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.EnemyHit).Select(e => e.Damage));
        // Coverage reports the setup, where the fight's 5 m would have turned the rule on.
        Assert.False(result.Coverage.Inputs.ContainsKey("buff"));
    }

    [Fact]
    public void ABandThatMovesTheCadenceIsRefusedAtSetup()
    {
        // Here the DPS doesn't follow the hit rate, so the band would change the cadence. The target outlives the fight, so the 30 m one
        // never comes up, and it's still refused.
        var fixture = BandFixture(Fixture(changes: [("character_armor", "0"), ("character_health", "100000"), ("enemy_target_incoming_dps", "10")]));
        Assert.Equal("incoming-state-unsupported", Assert.Throws<SimulationException>(() =>
            Run(fixture, Options with { Duration = 2, DriveSimpleBuffs = true, TargetDistances = [10, 30] })).Code);
    }

    [Fact]
    public void ASetValueNeedsNoBandAtAnyTargetsDistance()
    {
        // No band reaches 20 m, but a rate the user sets replaces them all.
        var (original, build) = Fixture(changes: [("character_armor", "0"), ("character_health", "100000")]);
        var rules = original.Pack with
        {
            Simulation = original.Pack.Simulation with { DistanceDefaults = [new() { Input = "enemy_hit_rate", Bands = [new() { UpTo = 15, Value = 100 }] }] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var result = Run((pack, build), Options with { Duration = 1.5, DriveSimpleBuffs = true, TargetDistances = [10, 20] }, Context.Add("enemy_hit_rate", "100"));
        Assert.Equal(1, result.Incoming!.Attempts);
    }

    [Fact]
    public void AGroupsFireTakesEachEnemysProfileAndDistanceTogether()
    {
        // Profile 1 at 10 m fires on the half seconds, hitting for 10 at any roll. Profile 2 at 30 m fires on the whole seconds, hitting
        // for 20 on half of them. Kills bring in the same profile and distance, keeping the clocks.
        var (pack, build) = BandFixture(ProfileFixture());
        var result = Run((pack, build), Group(2.5, 2, 1, 2) with { Seed = 23, TargetDistances = [10, 30] });
        var hits = result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.EnemyHit).ToArray();
        var expected = Landed(23, (0.5, 1), (1, 0.5), (1.5, 1), (2, 0.5));
        Assert.Equal(expected, hits.Select(e => e.Time));
        Assert.All(hits, hit => Assert.Equal(hit.Time % 1 == 0.5 ? 10 : 20, hit.Damage));
    }

    [Fact]
    public void TargetDistancesNeedTargetsAndDrivenFights()
    {
        var fixture = FalloffFixture();
        Assert.Equal("target-distances-unsupported", Assert.Throws<SimulationException>(() => Run(fixture, Options with { TargetDistances = [10] })).Code);
        Assert.Equal("target-required", Assert.Throws<SimulationException>(() => DummyWeaponSimulator.Run(fixture.Pack, fixture.Build, Context,
            new() { Duration = 5, Mode = SimulationMode.Random, DriveSimpleBuffs = true, TargetDistances = [10] }, TestContext.Current.CancellationToken)).Code);
        Assert.Throws<ArgumentOutOfRangeException>(() => Run(fixture, Options with { DriveSimpleBuffs = true, TargetDistances = [1001] }));
    }

    [Fact]
    public void ClearingTargetsNeedsTargets()
    {
        var (pack, build) = Fixture();
        Assert.Equal("target-required", Assert.Throws<SimulationException>(() => DummyWeaponSimulator.Run(pack, build, Context,
            new() { Duration = 5, ClearKills = 1 }, TestContext.Current.CancellationToken)).Code);
        Assert.Throws<ArgumentOutOfRangeException>(() => DummyWeaponSimulator.Run(pack, build, Context, new() { Duration = 5, ClearKills = 0 }, TestContext.Current.CancellationToken));
    }

    private static DummyFightOptions Cover(double peek, double cover, double share = 1) =>
        Options with { DriveSimpleBuffs = true, PeekSeconds = peek, CoverSeconds = cover, PeekHitShare = share };

    [Theory]
    [InlineData(5, "0%")] // Ends mid-peek: never behind cover.
    [InlineData(10, "0%")] // Ends as cover starts.
    [InlineData(15, "33.3%")] // Ends mid-cover.
    [InlineData(20, "50%")] // Whole cycles.
    [InlineData(25, "40%")] // A whole cycle, then mid-peek.
    public void TheShareBehindCoverIsThisFightsNotTheCycles(double duration, string share)
    {
        var result = Run(Fixture(changes: [("character_health", "1000")]), Cover(10, 10) with { Duration = duration, IncludeIncoming = false });
        Assert.Contains(result.Limitations, note => note.StartsWith($"Assumed cover cycle: 10 s peeking from cover, then 10 s behind it ({share} of this fight)", StringComparison.Ordinal));
    }

    [Fact]
    public void BehindCoverTheWeaponReloadsAndWaitsForTheNextPeek()
    {
        // ADR 0009: a shot a second, 10 rounds, 2 s reloads; 2.5 s peeks and 2 s behind cover. Shots at 0, 1 and 2; the next
        // would be at 3, but cover starts at 2.5, so the partly spent magazine reloads until 4.5, when the next peek starts.
        var fixture = Fixture(changes: [("character_health", "1000")]);
        var result = Run(fixture, Cover(2.5, 2) with { Duration = 10, IncludeIncoming = false });
        Assert.Equal(new[] { 0, 1, 2, 4.5, 5.5, 6.5, 9 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Time));
        Assert.Equal(new[] { 2.5, 7 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.ReloadStarted).Select(e => e.Time));
        Assert.Contains(result.Limitations, note => note.EndsWith("Enemy fire is off, so the hit chance while peeking isn't used", StringComparison.Ordinal));
        // Without enemy fire the hit share isn't needed, and an unset one goes unmentioned.
        var unset = Run(fixture, Cover(2.5, 2) with { Duration = 10, IncludeIncoming = false, PeekHitShare = null });
        Assert.Equal(result.FirstFightEvents, unset.FirstFightEvents);
        Assert.DoesNotContain(unset.Limitations, note => note.Contains("hit chance", StringComparison.Ordinal));
        Assert.Contains(result.Limitations, note => note.StartsWith("Assumed cover cycle: 2.5 s peeking from cover, then 2 s behind it (40% of this fight), starting with a peek", StringComparison.Ordinal));
        Assert.StartsWith("d2p-simple-buffs-v61/", result.Contract, StringComparison.Ordinal);
    }

    [Fact]
    public void WaitingForTheNextTargetDoesNotSleepThroughACoverReload()
    {
        // The shot at 0 s kills, and the next target arrives at 3 s, behind cover (2.5 to 4.5 s). The part-used magazine still
        // reloads at the boundary, not at the arrival, and the new target takes the next peek's shot.
        var fixture = Fixture(changes: [("character_health", "1000"), ("enemy_target_health", "5")]);
        var result = Run(fixture, Cover(2.5, 2) with { Duration = 6, IncludeIncoming = false }, delay: 3);
        Assert.Equal(new[] { 2.5 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.ReloadStarted).Select(e => e.Time));
        Assert.Equal(new[] { 0, 4.5 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Time));
        // Time to kill counts from the arrival: 0 s, then 1.5 s.
        Assert.Equal(0.75, result.TimeToKill!.Mean);
    }

    [Fact]
    public void AShortCoverPhaseDoesNotHastenTheNextShot()
    {
        // 1.5 s peeks and 0.2 s behind cover, with 0.1 s reloads: each cover phase reloads, then the weapon waits out the
        // rest of the 1 s fire interval rather than shooting as the next peek starts.
        var fixture = Fixture(changes: [("character_health", "1000"), ("equipped_weapon_reload_time", "0.1")]);
        var result = Run(fixture, Cover(1.5, 0.2) with { Duration = 4.5, IncludeIncoming = false });
        Assert.Equal(new double[] { 0, 1, 2, 3, 4 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Time));
        Assert.Equal(2, result.Reloads);
    }

    [Fact]
    public void AnEmptyMagazineReloadsAtTheCoverBoundaryAndAFullOneDoesNot()
    {
        // Three rounds: shots at 0, 1 and 2 empty the magazine, and the next would be at 3. Cover starts at 2.5, so the reload
        // starts there and ends at 4.5, behind cover still (2.5 to 5); the full magazine doesn't reload again. Shots at 5, 6 and
        // 7 empty it once more, and the next cover phase reloads it at 7.5.
        var fixture = Fixture(changes: [("character_health", "1000"), ("equipped_weapon_magazine_size", "3")]);
        var result = Run(fixture, Cover(2.5, 2.5) with { Duration = 8, IncludeIncoming = false });
        Assert.Equal(new double[] { 0, 1, 2, 5, 6, 7 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Time));
        Assert.Equal(new[] { 2.5, 7.5 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.ReloadStarted).Select(e => e.Time));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    [InlineData(0.0)]
    public void BehindCoverEnemyFireIsBlockedAndPeekingLandsAShareOfHits(double share)
    {
        // Enemies fire every 0.5 s at a 50% hit rate. With 2 s peeks and 2 s behind cover over 8 s, the attempts at 2 to 3.5 s
        // and 6 to 7.5 s are blocked; while peeking, an attempt lands when its roll is below 50% of the share. Every attempt
        // draws its roll, so the outcomes are the uncovered fight's rolls.
        var fixture = Fixture(changes: [("character_armor", "0"), ("character_health", "1000"), ("enemy_target_hit_damage", "1"),
            ("enemy_target_incoming_dps", "2 * context_value('enemy_hit_rate') / 100")]);
        var options = Cover(2, 2, share) with { Duration = 8, Seed = 23 };
        var result = Run(fixture, options, Context.Add("enemy_hit_rate", "50"));
        Assert.Equal(15, result.Incoming!.Attempts);
        Assert.Equal(8, result.Incoming.BlockedShots);
        Assert.Equal(new[] { 2, 2.5, 3, 3.5, 6, 6.5, 7, 7.5 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.EnemyShotBlocked).Select(e => e.Time));
        var random = new SimulationRandom(options.Seed, 1UL << 63);
        var expected = Enumerable.Range(1, 15).Select(step => step * 0.5).Select(at => (At: at, Roll: random.NextDouble()))
            .Where(attempt => attempt.At % 4 < 2 && attempt.Roll < 0.5 * share).Select(attempt => attempt.At).ToArray();
        Assert.Equal(expected, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.EnemyHit).Select(e => e.Time));
        Assert.Contains(result.Limitations, note => note.Contains(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"while peeking enemies land {share:0.##%} of their normal hit chance"), StringComparison.Ordinal));
        Assert.StartsWith("d2p-incoming-random-v73/", result.Contract, StringComparison.Ordinal);
    }

    [Fact]
    public void InCoverEffectsHoldForTheWholeCycle()
    {
        // The player shoots from cover, so a talent requiring the in_cover selector applies to every shot, peek or not.
        var (original, _) = Fixture(Buff("kill"), changes: [("character_health", "1000")]);
        var rules = original.Pack with
        {
            Selectors = [.. original.Pack.Selectors, new() { ID = DummyFightOptions.CoverInput, Name = "In cover", Values = ["true"] }],
            Talents = [.. original.Pack.Talents.Select(talent => talent with { Modifiers = [.. talent.Modifiers.Select(modifier => modifier with { RequiresContext = ImmutableDictionary<string, string>.Empty.Add(DummyFightOptions.CoverInput, "true") })] })]
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test");
        var covered = Run((pack, build), Cover(2, 1) with { Duration = 4, IncludeIncoming = false });
        Assert.All(covered.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot), shot => Assert.Equal(20, shot.Damage));
        Assert.Contains(covered.Limitations, note => note.EndsWith("In cover throughout, so in-cover effects apply to every shot", StringComparison.Ordinal));
        Assert.EndsWith("Set by the cover cycle, not chosen: In cover.", covered.Coverage.InputBasis, StringComparison.Ordinal);
        var open = Run((pack, build), Options with { Duration = 4, IncludeIncoming = false, DriveSimpleBuffs = true });
        Assert.All(open.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot), shot => Assert.Equal(10, shot.Damage));
    }

    [Fact]
    public void ACoverCycleIsRefusedWhereItIsNotModeled()
    {
        var fixture = Fixture();
        Assert.Equal("cover-cycle-unsupported", Assert.Throws<SimulationException>(() => Run(fixture, Cover(2, 1) with { DriveSimpleBuffs = false })).Code);
        Assert.Throws<ArgumentException>(() => Run(fixture, Options with { DriveSimpleBuffs = true, PeekSeconds = 2, CoverSeconds = 1 }));
        Assert.Throws<ArgumentException>(() => Run(fixture, Options with { DriveSimpleBuffs = true, PeekSeconds = 2, PeekHitShare = 1 }));
        Assert.Throws<ArgumentException>(() => Run(fixture, Options with { DriveSimpleBuffs = true, PeekHitShare = 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Run(fixture, Cover(0.09, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Run(fixture, Cover(2, 0.05)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Run(fixture, Cover(2, 61)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Run(fixture, Cover(2, 1, 1.5)));
    }

    // A normal kit: 100% of max armor after 2.5 s, plus 0.3 s a hit up to 4.9 s.
    private static (CompiledPackExpressions Pack, BuildSnapshot Build) KitFixture(params (string Id, string Formula)[] changes)
    {
        var (original, build) = Fixture(changes: [("character_armor", "30"), ("character_health", "1000"), .. changes]);
        var rules = original.Pack with
        {
            Simulation = original.Pack.Simulation with { ArmorKit = new() { Repair = 1, UseSeconds = 2.5, ExtendSeconds = 0.3, MaxUseSeconds = 4.9 } }
        };
        return (Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled), build);
    }

    private static DummyFightOptions Kits(double duration, int kits = 1, double percent = 50) =>
        Options with { Duration = duration, DriveSimpleBuffs = true, ArmorKits = kits, ArmorKitAtPercent = percent };

    [Fact]
    public void AnArmorKitStopsTheWeaponAndRepairsAsItEnds()
    {
        // A hit each second takes 10 of 30 armor. The hit at 2 s leaves a third, below 50%, and the kit starts before the 2 s shot. Hits at
        // 3, 4 and 5 s each add 0.3 s, so it ends at 5.4 s, repairing all 30, and firing resumes.
        var result = Run(KitFixture(), Kits(7));
        Assert.Equal(new[] { 2d }, Times(result, DummyEventKind.ArmorKitStarted));
        var used = Assert.Single(result.FirstFightEvents, e => e.Kind == DummyEventKind.ArmorKitUsed);
        Assert.Equal((5.4, 30d), (Math.Round(used.Time, 9), used.Damage));
        Assert.Equal(new[] { 0, 1, 5.4, 6.4 }, Times(result, DummyEventKind.Shot).Select(at => Math.Round(at, 9)));
        Assert.Equal((1d, 30d), (result.Incoming!.ArmorKitsUsed, result.Incoming.ArmorKitRepaired));
        Assert.Equal(30, result.Incoming.ArmorRepaired);
        Assert.Contains(result.Limitations, note => note.StartsWith("Assumed armor kits: 1 carried, each used if armor is below 50% at the weapon's next decision", StringComparison.Ordinal));
        Assert.StartsWith("d2p-incoming-random-v73/", result.Contract, StringComparison.Ordinal);
    }

    [Fact]
    public void HitsLengthenAKitUpToItsLongestUse()
    {
        // Ten hits a second: extensions come at most every 0.3 s, and stop at 4.9 s in all, so a kit started at 1 s ends at 5.9 s.
        var result = Run(KitFixture(("enemy_target_hit_damage", "1"), ("character_armor", "3"), ("enemy_target_incoming_dps", "10")), Kits(8));
        Assert.Equal(new[] { 1d }, Times(result, DummyEventKind.ArmorKitStarted));
        Assert.Equal(5.9, Math.Round(Times(result, DummyEventKind.ArmorKitUsed)[0], 9));
    }

    [Fact]
    public void ExtensionsComeAtMostOncePerExtension()
    {
        // Ten hits a second, but cover lets only those from 2 to 2.3 s through. A kit from 0.4 s would end at 2.9 s. The hit at 2 s adds 0.3 s,
        // and the next that counts is one extension later, at 2.3 s, a time that isn't exact in binary, so it ends at 3.5 s, in cover.
        var result = Run(KitFixture(("enemy_target_hit_damage", "1"), ("character_armor", "3"), ("enemy_target_incoming_dps", "10")),
            Kits(5, percent: 100) with { PeekSeconds = 0.4, CoverSeconds = 1.6, PeekHitShare = 1 });
        Assert.Equal(new[] { 0.4 }, Times(result, DummyEventKind.ArmorKitStarted));
        Assert.Equal(3.5, Math.Round(Times(result, DummyEventKind.ArmorKitUsed)[0], 9));
    }

    [Fact]
    public void AKitIsntStartedIfArmorHasRecoveredByTheDecision()
    {
        // A shot every 2 s, a hit at 1.5 s and 3 s, and regeneration that refills armor before each decision: nothing is spent.
        var result = Run(KitFixture(("equipped_weapon_rpm", "30"), ("enemy_target_incoming_dps", "10 / 1.5"), ("armor_regeneration", "100"), ("armor_regeneration_percent", "0"),
            ("estimated_armor_regeneration", "armor_regeneration + character_armor * armor_regeneration_percent")), Kits(3.9, percent: 80));
        Assert.Equal(2, result.Incoming!.Hits);
        Assert.Empty(Times(result, DummyEventKind.ArmorKitStarted));
    }

    [Fact]
    public void AKitWaitsForASwapUnderWay()
    {
        // One round each: the shot at 0 s swaps from 1 to 2 s. The hit at 1 s puts armor below 80%, and the kit starts as the swap ends.
        var fixture = WithSidearm(KitFixture(("equipped_weapon_magazine_size", "1")));
        var result = Run(fixture, Kits(6, percent: 80) with { Rotation = new("secondary_weapon") });
        Assert.Equal(new double[] { 1 }, Times(result, DummyEventKind.SwapStarted).Take(1));
        Assert.Equal(new double[] { 2 }, Times(result, DummyEventKind.ArmorKitStarted));
        Assert.Equal(1, result.Incoming!.ArmorKitsUsed);
    }

    [Fact]
    public void AKitComesAheadOfACoverPhasesReload()
    {
        // A part-used magazine and a kit both fall due at the 2 s boundary. The kit runs to 4.5 s, into the next peek, so that cover
        // phase's reload doesn't happen.
        var result = Run(KitFixture(), Kits(5, percent: 80) with { PeekSeconds = 2, CoverSeconds = 2, PeekHitShare = 1 });
        Assert.Empty(Times(result, DummyEventKind.ReloadStarted));
        Assert.Contains(result.Limitations, note => note.Contains("ahead of that cover phase's reload", StringComparison.Ordinal));
    }

    [Fact]
    public void KitsRunOutAndAreOnlySpentWhenTheyLand()
    {
        // Two kits, then none: armor goes on falling with no third.
        var used = Run(KitFixture(), Kits(30, kits: 2));
        Assert.Equal((2d, 0d), (used.Incoming!.ArmorKitsUsed, used.Incoming.ArmorKitsCancelled));
        Assert.Equal(2, Times(used, DummyEventKind.ArmorKitStarted).Length);
        // A fight ending during use spends nothing, and a downed player doesn't finish one.
        var ended = Run(KitFixture(), Kits(4));
        Assert.Equal((0d, 0d, 0d), (ended.Incoming!.ArmorKitsUsed, ended.Incoming.ArmorRepaired, ended.Incoming.ArmorKitsCancelled));
        var down = Run(KitFixture(("character_health", "20")), Kits(10));
        Assert.Equal(1, down.Incoming!.DownedFraction);
        Assert.Equal((0d, 1d), (down.Incoming.ArmorKitsUsed, down.Incoming.ArmorKitsCancelled));
        // Going down with the kits already used cancels nothing.
        var spent = Run(KitFixture(("character_health", "100")), Kits(60));
        Assert.Equal(1, spent.Incoming!.DownedFraction);
        Assert.Equal((1d, 0d), (spent.Incoming.ArmorKitsUsed, spent.Incoming.ArmorKitsCancelled));
    }

    [Fact]
    public void AKitWaitsForAReloadUnderWay()
    {
        // One round and a 2 s reload: the shot at 0 s reloads from 1 to 3 s. Armor falls below half at 2 s, so the kit starts at 3 s.
        var result = Run(KitFixture(("equipped_weapon_magazine_size", "1")), Kits(7));
        Assert.Equal(new[] { 3d }, Times(result, DummyEventKind.ArmorKitStarted));
        Assert.Equal(new double[] { 3 }, Times(result, DummyEventKind.ReloadCompleted));
    }

    [Fact]
    public void AKitWaitsForCover()
    {
        // Peeks to 2 s, then cover. The hit at 1 s leaves two thirds, below 80%, but the kit waits for the boundary, and the 1 s shot fires.
        var result = Run(KitFixture(), Kits(5, percent: 80) with { PeekSeconds = 2, CoverSeconds = 2, PeekHitShare = 1 });
        Assert.Equal(new[] { 2d }, Times(result, DummyEventKind.ArmorKitStarted));
        Assert.Equal(new double[] { 0, 1 }, Times(result, DummyEventKind.Shot).Where(at => at < 4));
        Assert.Contains(result.Limitations, note => note.Contains("waiting for cover", StringComparison.Ordinal));
    }

    [Fact]
    public void AFightWithoutKitsIsUnchanged()
    {
        var result = Run(KitFixture(), Options with { Duration = 7, DriveSimpleBuffs = true });
        Assert.Empty(Times(result, DummyEventKind.ArmorKitStarted));
        Assert.Equal(0, result.Incoming!.ArmorKitsUsed);
    }

    [Fact]
    public void ArmorKitsAreRefusedWhereTheyAreNotModeled()
    {
        var fixture = KitFixture();
        // The pack must say what a kit does.
        Assert.Equal("armor-kits-unsupported", Assert.Throws<SimulationException>(() => Run(Fixture(), Kits(5))).Code);
        Assert.Equal("armor-kits-unsupported", Assert.Throws<SimulationException>(() => Run(fixture, Kits(5) with { IncludeIncoming = false })).Code);
        Assert.Equal("armor-kits-unsupported", Assert.Throws<SimulationException>(() => Run(fixture, Kits(5) with { DriveSimpleBuffs = false })).Code);
        Assert.Throws<ArgumentException>(() => Run(fixture, Options with { DriveSimpleBuffs = true, ArmorKits = 1 }));
        Assert.Throws<ArgumentException>(() => Run(fixture, Options with { DriveSimpleBuffs = true, ArmorKitAtPercent = 50 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Run(fixture, Kits(5, kits: 11)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Run(fixture, Kits(5, percent: 0)));
    }

    private static (CompiledPackExpressions Pack, BuildSnapshot Build) KitRule(SimTriggerDefinition rule, params (string Id, string Formula)[] changes)
    {
        var (original, _) = Fixture(rule, changes: [("character_armor", "30"), ("character_health", "1000"), .. changes]);
        var rules = original.Pack with { Simulation = original.Pack.Simulation with { ArmorKit = new() { Repair = 1, UseSeconds = 2.5, ExtendSeconds = 0.3, MaxUseSeconds = 4.9 } } };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        return (pack, BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test"));
    }

    [Fact]
    public void AKitLandingFiresArmorKit()
    {
        // The buff comes with the repair: shots before the kit are plain, and those after it carry the buff's 10.
        var result = Run(KitRule(Buff("armor_kit")), Kits(9));
        var used = Times(result, DummyEventKind.ArmorKitUsed).Single();
        var shots = result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).ToArray();
        Assert.All(shots.Where(e => e.Time < used), e => Assert.Equal(10, e.Damage));
        Assert.Contains(shots, e => e.Time >= used);
        Assert.All(shots.Where(e => e.Time >= used), e => Assert.Equal(20, e.Damage));
        Assert.StartsWith("d2p-incoming-random-v73/", result.Contract, StringComparison.Ordinal);
    }

    [Fact]
    public void AKitCutShortFiresNothing()
    {
        // Going down during the use cancels it, so no talent reacts.
        var result = Run(KitRule(Buff("armor_kit"), ("character_health", "20")), Kits(10));
        Assert.Equal(1, result.Incoming!.ArmorKitsCancelled);
        Assert.DoesNotContain(result.TimedTriggerUptime, entry => entry.Key == "buff" && entry.Value.Seconds > 0);
    }

    // The node's modifier reads the rule's key. The fixture's talent is off, so only the node can lift damage.
    private static (CompiledPackExpressions Pack, BuildSnapshot Build) NodeRule(bool allocated, bool prerequisiteActive = true)
    {
        var (original, kitBuild) = KitRule(Buff("armor_kit") with { Talent = "node" });
        var build = kitBuild.WithTalentSelection("primary_weapon", "talent", "");
        var rules = original.Pack with
        {
            Specializations = [new()
            {
                ID = "spec", Name = "Spec",
                Nodes = [new() { ID = "first", Name = "First", BudgetID = "total", Cost = 1 },
                    new() { ID = "node", Name = "Node", BudgetID = "total", Cost = 1, Prerequisites = ["first"],
                        Modifiers = [new() { Stat = "buff_damage", Op = ModifierOps.Flat, Value = new(10), RequiresContext = ImmutableDictionary<string, string>.Empty.Add("buff", "true") }] }]
            }]
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var chosen = build.WithSpecialization("spec");
        if (prerequisiteActive) { chosen = chosen.WithSpecializationNode("first", true); }
        return (pack, allocated ? chosen.WithSpecializationNode("node", true) : chosen);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ASpecializationNodeOwnsARuleWhileActive(bool allocated)
    {
        var result = Run(NodeRule(allocated), Kits(9));
        Assert.Equal(allocated, result.TimedTriggerUptime.GetValueOrDefault("buff")?.Seconds > 0);
        // The effect sits on the node, and reads the rule's key.
        Assert.Equal(allocated ? 20 : 10, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Max(e => e.Damage));
    }

    [Fact]
    public void ANodeWithAnUnmetPrerequisiteIsRefused() =>
        Assert.Contains("unmet-specialization-prerequisite", Assert.Throws<SimulationException>(() => Run(NodeRule(true, prerequisiteActive: false), Kits(9))).Message, StringComparison.Ordinal);

    [Fact]
    public void ANodeWithNoKitsIsNamed() =>
        Assert.Contains("This fight carries no armor kits, so these read your setting and never react to one: Node",
            Run(NodeRule(true), Options with { Duration = 5, DriveSimpleBuffs = true }).Limitations);

    [Fact]
    public void AFightWithNoKitsNeverFiresArmorKit()
    {
        // A talent on gear that reacts to a kit doesn't refuse a fight: it never fires, and the result says so.
        var gaining = Run(KitRule(Buff("armor_kit")), Options with { Duration = 5, DriveSimpleBuffs = true });
        Assert.All(gaining.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot), e => Assert.Equal(10, e.Damage));
        Assert.Contains("This fight carries no armor kits, so these read your setting and never react to one: Test", gaining.Limitations);
        // The rule steps aside, so the user's setting holds.
        var assumed = Run(KitRule(Buff("armor_kit")), Options with { Duration = 5, DriveSimpleBuffs = true }, Context.Add("buff", "true"));
        Assert.All(assumed.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot), e => Assert.Equal(20, e.Damage));
        // With kits the note goes.
        Assert.DoesNotContain(Run(KitRule(Buff("armor_kit")), Kits(9)).Limitations, note => note.StartsWith("This fight carries no armor kits", StringComparison.Ordinal));
        // A rule that only clears on it runs, and it just never fires.
        var clearing = Buff("hit") with { ConsumeOn = "armor_kit" };
        Assert.All(Run(KitRule(clearing), Options with { Duration = 5, DriveSimpleBuffs = true }).FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Skip(1), e => Assert.Equal(20, e.Damage));
    }

    [Fact]
    public void ABuildThatReplacesTheKitIsRefused()
    {
        // The talent on the gun is one the pack says replaces the kit.
        var (original, _) = Fixture(Buff("hit"), changes: [("character_armor", "30"), ("character_health", "1000")]);
        var rules = original.Pack with
        {
            Simulation = original.Pack.Simulation with { ArmorKit = new() { Repair = 1, UseSeconds = 2.5, ExtendSeconds = 0.3, MaxUseSeconds = 4.9, ReplacedByTalents = ["test"] } }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var selected = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test");
        Assert.Equal("armor-kits-unsupported", Assert.Throws<SimulationException>(() => Run((pack, selected), Kits(5))).Code);
        // Without the talent the same pack runs.
        Assert.Equal(1, Run((pack, BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun")), Kits(7)).Incoming!.ArmorKitsUsed);
    }

    [Fact]
    public void ADistanceDefaultWithoutALegalBandAtATargetsDistanceIsASimulationError()
    {
        var (original, build) = Fixture();
        var rules = original.Pack with
        {
            Simulation = original.Pack.Simulation with { DistanceDefaults = [new() { Input = "enemy_hit_rate", Bands = [new() { UpTo = 15, Value = 100 }] }] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        Assert.Equal("distance-default-invalid", Assert.Throws<SimulationException>(() =>
            Run((pack, build), Options with { Duration = 3.5, DriveSimpleBuffs = true, TargetDistances = [10, 20] })).Code);
    }

    [Fact]
    public void ADistanceDefaultWithoutALegalBandIsASimulationError()
    {
        // A pack compiled without validation, whose only band ends at 15 m, has nothing for a 20 m fight.
        var (original, build) = Fixture();
        var rules = original.Pack with
        {
            Simulation = original.Pack.Simulation with { DistanceDefaults = [new() { Input = "enemy_hit_rate", Bands = [new() { UpTo = 15, Value = 0 }] }] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        Assert.Equal("distance-default-invalid", Assert.Throws<SimulationException>(() =>
            Run((pack, build), Options with { Duration = 3.5, DriveSimpleBuffs = true, Distance = 20 })).Code);
    }

    [Fact]
    public void ADurationStatTimesAFightsBuff()
    {
        // Clutch's post-kill window read from a stat (R41): each one-shot kill holds the flag for the stat's 0.5 s.
        var rule = new SimTriggerDefinition { Talent = "test", Key = "buff", On = "kill", Kind = "buff", DurationStat = "window" };
        var result = Run(Fixture(rule, changes: [("window", "0.5"), ("enemy_target_health", "5")]), Options with { Duration = 3.5, DriveSimpleBuffs = true });
        var kills = result.FirstFightEvents.Count(e => e.Kind == DummyEventKind.Kill);
        Assert.True(kills > 1);
        Assert.Equal(kills * 0.5, result.TimedTriggerUptime["buff"].Seconds, 8);
    }

    [Fact]
    public void ArmorRepairsOverTimeRunAsIndependentInstances()
    {
        // Preservation's shape: 10% of max armor (10) over 5 s per firing, 2 a second each. From 1 s to 2 s one repair runs (+2);
        // from 2 s to 2.5 s two do (+2).
        var result = RepairOnHit("armor_repair", 0.1, duration: 5, instances: "independent");
        Assert.Equal(4, result.Incoming!.ArmorRepaired, 8);
    }

    [Fact]
    public void ClearedStacksGrantTemporaryArmorThatAbsorbsLaterHits()
    {
        // Full Stop's shape: shots build stacks; taking damage here clears them into temporary armor.
        var rule = new SimTriggerDefinition
        {
            Talent = "test",
            Key = "stacks",
            On = "shot",
            Kind = "stacks",
            Max = 5,
            ResetOn = "damaged",
            ClearGrant = "temporary_armor",
            ClearAmount = 1,
            ClearDuration = 10
        };
        var baseline = Run(Fixture(), Options with { DriveSimpleBuffs = true });
        var rules = Fixture().Pack.Pack with
        {
            NumericContextInputs = [.. Fixture().Pack.Pack.NumericContextInputs, new() { ID = "stacks", Name = "Stacks", Min = 0, Max = 5, Step = 1 }],
            Simulation = new() { Triggers = [rule] }
        };
        var result = Run(Recompile(rules), Options with { DriveSimpleBuffs = true });
        // Each grant is one max armor (10) per stack cleared, and the bonus armor delays going down.
        var grants = result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.BonusArmorGranted).ToArray();
        Assert.NotEmpty(grants);
        Assert.All(grants, grant => Assert.Equal(0, grant.Damage % 10));
        Assert.True((result.Incoming!.TimeToDown?.Mean ?? double.PositiveInfinity) > baseline.Incoming!.TimeToDown!.Mean);
        // Without incoming fire there is no armor to grant: a reload clears the stacks and the run still succeeds.
        var reloading = rules with { Simulation = new() { Triggers = [rule with { ResetOn = "reload" }] } };
        var dummy = Run(Recompile(reloading), Options with { Duration = 15, IncludeIncoming = false, DriveSimpleBuffs = true });
        Assert.Contains(dummy.FirstFightEvents, e => e.Kind == DummyEventKind.ReloadStarted);
        Assert.DoesNotContain(dummy.FirstFightEvents, e => e.Kind == DummyEventKind.BonusArmorGranted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StatusUptimeStopsAtDowningAndUsesTheRequestedFightDuration(bool capture)
    {
        var status = new SimTriggerDefinition { Talent = "test", Key = "buff", Kind = "status", Status = "burning", On = "hit", Duration = 10 };
        var fixture = Fixture(status);
        var result = Run(fixture, Options with { Iterations = 3, DriveSimpleBuffs = true, CaptureFirstFightEvents = capture });
        Assert.Equal(2, result.Incoming!.TimeToDown!.Mean);
        Assert.Equal(new StatusUptime(1, 0.2), result.TargetStatusUptime["burning"]);
        Assert.Equal(result.TargetStatusUptime["burning"], result.TargetStatusUptime["any"]);
        Assert.Equal(new StatusUptime(0, 0), result.TargetStatusUptime["shocked"]);
    }

    [Fact]
    public void EnemyEventsWinTiesAndDowningStopsOutgoingDamage()
    {
        var result = Run(Fixture(), Options with { Iterations = 2 });
        var incoming = Assert.IsType<IncomingFightResult>(result.Incoming);
        Assert.Equal(2, incoming.Attempts);
        Assert.Equal(2, incoming.Hits);
        Assert.Equal(20, incoming.DamageTaken);
        Assert.Equal(1, incoming.ArmorBreaks);
        Assert.Equal(1, incoming.DownedFraction);
        Assert.Equal(1, incoming.TimeToFirstArmorBreak!.Mean);
        Assert.Equal(2, incoming.TimeToDown!.Mean);
        Assert.Equal(2, result.ShotsFired);
        Assert.Equal(new[] { DummyEventKind.EnemyShot, DummyEventKind.EnemyHit, DummyEventKind.ArmorBreak, DummyEventKind.Shot },
            result.FirstFightEvents.Where(e => e.Time == 1).Select(e => e.Kind));
        Assert.DoesNotContain(result.FirstFightEvents, e => e.Kind == DummyEventKind.Shot && e.Time >= 2);
    }

    [Fact]
    public void IncomingFireDuringReloadUsesChronologicalTriggerEvents()
    {
        var fixture = Fixture(Buff("damaged"), changes: [("equipped_weapon_magazine_size", "1"), ("equipped_weapon_reload_time", "3"),
            ("character_armor", "0"), ("character_health", "100"), ("enemy_target_hit_damage", "1"), ("enemy_target_incoming_dps", "2")]);
        var result = Run(fixture, Options with { DriveSimpleBuffs = true });
        Assert.Equal(new double[] { 0, 4 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Time));
        Assert.Equal(new[] { DummyEventKind.EnemyShot, DummyEventKind.EnemyHit, DummyEventKind.ReloadCompleted, DummyEventKind.Shot },
            result.FirstFightEvents.Where(e => e.Time == 4).Select(e => e.Kind));
        Assert.Equal(9, result.Incoming!.Hits);
        Assert.Equal(9, result.Incoming.DamageTaken);
    }

    [Fact]
    public void DowningMidReloadDoesNotReportItsFutureCompletion()
    {
        var fixture = Fixture(changes: [("equipped_weapon_magazine_size", "1"), ("equipped_weapon_reload_time", "3"),
            ("character_armor", "0"), ("character_health", "100"), ("enemy_target_hit_damage", "40"), ("enemy_target_incoming_dps", "80")]);
        var result = Run(fixture);
        Assert.Equal(1.5, result.Incoming!.TimeToDown!.Mean);
        Assert.Equal(100, result.Incoming.DamageTaken);
        Assert.Equal(1, result.Reloads);
        Assert.DoesNotContain(result.FirstFightEvents, e => e.Kind == DummyEventKind.ReloadCompleted);
        Assert.All(result.FirstFightEvents, e => Assert.True(e.Time <= 1.5));
    }

    [Fact]
    public void DowningRemovesFutureRetargetArrivalTrace()
    {
        var fixture = Fixture(changes: [("enemy_target_health", "5"), ("character_armor", "0"), ("enemy_target_incoming_dps", "20")]);
        var result = Run(fixture, delay: 3);
        Assert.Equal(0.5, result.Incoming!.TimeToDown!.Mean);
        Assert.DoesNotContain(result.FirstFightEvents, e => e.Kind == DummyEventKind.TargetArrived);
    }

    [Fact]
    public void RetargetPoolReadingsRefreshBeforeEnemyFireWhileWeaponWaits()
    {
        var (original, _) = Fixture(changes: [("enemy_target_health", "10")]);
        var rules = original.Pack with
        {
            NumericContextInputs = [.. original.Pack.NumericContextInputs, new() { ID = "remaining", Name = "Remaining", Min = 0, Max = 100, Step = 10 }],
            Stats = [.. original.Pack.Stats.Select(s => s.ID == "enemy_damage_taken_factor" ? s with { Formula = "context_value('remaining') / 100" } : s)],
            Simulation = new() { Triggers = [new() { Talent = "test", Key = "remaining", Kind = "target" }] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test");
        var result = Run((pack, build), Options with { DriveSimpleBuffs = true }, delay: 0.5);
        Assert.Equal(20, result.Incoming!.DamageTaken);
        Assert.Equal(2, result.Incoming.TimeToDown!.Mean);
    }

    [Fact]
    public void ArmorBreakBuffAffectsTheSameTimesLaterWeaponShot()
    {
        var result = Run(Fixture(Buff("armor_break")), Options with { DriveSimpleBuffs = true });
        Assert.Equal(new double[] { 10, 20 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Damage));
    }

    [Fact]
    public void CoreDefenseChangesAffectSubsequentEnemyHits()
    {
        var result = Run(Fixture(Buff("damaged"), "enemy_damage_taken_factor", -0.5), Options with { DriveSimpleBuffs = true });
        Assert.Equal(3, result.Incoming!.Hits);
        Assert.Equal(3, result.Incoming.TimeToDown!.Mean);
        Assert.Equal(new double[] { 10, 5, 5 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.EnemyHit).Select(e => e.Damage));
    }

    [Fact]
    public void RefillsEarnedBeforeReloadStartsCancelTheEmptyReload()
    {
        var fixture = Fixture(Buff("damaged") with { Refill = true }, changes: [("equipped_weapon_magazine_size", "1"),
            ("character_health", "100"), ("enemy_target_hit_damage", "1"), ("enemy_target_incoming_dps", "2")]);
        var result = Run(fixture, Options with { Duration = 2.5, DriveSimpleBuffs = true });
        Assert.Equal(3, result.ShotsFired);
        Assert.Equal(0, result.Reloads);
    }

    [Fact]
    public void ZeroDamageHitsDoNotFireDamagedTriggers()
    {
        var result = Run(Fixture(Buff("damaged"), changes: [("enemy_damage_taken_factor", "0")]), Options with { DriveSimpleBuffs = true });
        Assert.Equal(4, result.Incoming!.Hits);
        Assert.Equal(0, result.Incoming.DamageTaken);
        Assert.Equal(0, result.Incoming.DownedFraction);
        Assert.All(result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot), e => Assert.Equal(10, e.Damage));
    }

    [Fact]
    public void IncomingRollsDoNotChangeWeaponRollsAndReplayDeterministically()
    {
        var fixture = Fixture(changes: [("equipped_weapon_magazine_size", "2"), ("enemy_damage_taken_factor", "0"),
            ("practical_hit_fraction", "0.8"), ("effective_critical_hit_chance", "0.5"), ("equipped_weapon_critical_damage", "20")]);
        var context = Context.Add("enemy_hit_rate", "50");
        var options = Options with { Iterations = 3, Seed = 13 };
        var baseline = Run(fixture, options with { IncludeIncoming = false }, context);
        var incoming = Run(fixture, options, context);
        var repeated = Run(fixture, options, context);
        Assert.Null(baseline.Incoming);
        Assert.Equal(baseline.WeaponDps, incoming.WeaponDps);
        Assert.Equal(baseline.Timeline, incoming.Timeline);
        Assert.Equal(baseline.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot), incoming.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot));
        Assert.Equal(incoming.Incoming, repeated.Incoming);
        Assert.Equal(incoming.FirstFightEvents, repeated.FirstFightEvents);
    }

    [Fact]
    public void IncomingHorizonIsHalfOpenAndMissingBreaksAreNotZeroSamples()
    {
        var result = Run(Fixture(), Options with { Duration = 1 });
        Assert.Equal(0, result.Incoming!.Attempts);
        Assert.Null(result.Incoming.TimeToDown);
        Assert.Null(result.Incoming.TimeToFirstArmorBreak);
    }

    [Theory]
    [InlineData("enemy_target_incoming_dps", "incoming-dps-modifier-unsupported")]
    [InlineData("character_armor", "incoming-state-unsupported")]
    [InlineData("buff_damage", "incoming-state-unsupported")]
    public void UnsupportedDynamicCadenceAndPoolChangesAreRefused(string stat, string code)
    {
        // buff_damage feeds incoming DPS here without touching hit damage, so the derived cadence really changes.
        var fixture = Fixture(Buff("damaged"), stat, changes: [("enemy_target_incoming_dps", "(enemy_target_hit_damage + buff_damage) * context_value('enemy_hit_rate') / 100")]);
        Assert.Equal(code, Assert.Throws<SimulationException>(() => Run(fixture, Options with { DriveSimpleBuffs = true })).Code);
    }

    [Fact]
    public void AStaticIncomingDpsModifierIsRefusedRatherThanSlowingTheCadence()
    {
        var (pack, build) = Fixture(modifier: "enemy_target_incoming_dps", bonus: -5);
        var selected = (pack, build.WithTalentSelection("primary_weapon", "talent", "test"));
        Assert.Equal("incoming-dps-modifier-unsupported", Assert.Throws<SimulationException>(() => Run(selected, context: Context.Add("buff", "true"))).Code);
    }

    [Fact]
    public void RescalingHitDamageKeepsTheCadenceDespiteFloatingPointDrift()
    {
        // Core computes incoming DPS as damage * 37 / 100, so dps / damage / 0.37 is 1 only up to rounding.
        static double Interval(double raw) => 1 / (raw * 37 / 100 / raw / (37 / 100d));
        var bonus = Enumerable.Range(1, 1000).First(value => Interval(10 + value) != Interval(10));
        var fixture = Fixture(Buff("damaged"), "enemy_target_hit_damage", bonus, changes: [("character_health", "100000")]);
        var result = Run(fixture, Options with { DriveSimpleBuffs = true }, Context.Add("enemy_hit_rate", "37"));
        Assert.True(result.Incoming!.Attempts > 1);
    }

    [Fact]
    public void EnemyFireDuringTheRetargetGapReadsAFullTargetNotADeadOne()
    {
        var (original, _) = Fixture(changes: [("enemy_target_health", "10")]);
        var rules = original.Pack with
        {
            NumericContextInputs = [.. original.Pack.NumericContextInputs, new() { ID = "remaining", Name = "Remaining", Min = 0, Max = 100, Step = 10 }],
            Stats = [.. original.Pack.Stats.Select(s => s.ID == "enemy_damage_taken_factor" ? s with { Formula = "context_value('remaining') / 100" } : s)],
            Simulation = new() { Triggers = [new() { Talent = "test", Key = "remaining", Kind = "target" }] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test");
        // The first shot kills; the enemy fires at 1 s while the next target is still 0.5 s away.
        var result = Run((pack, build), Options with { Duration = 1.2, DriveSimpleBuffs = true }, delay: 1.5);
        Assert.Equal(1, result.Incoming!.Hits);
        Assert.Equal(10, result.Incoming.DamageTaken);
    }

    [Fact]
    public void ARefillDuringAReloadDoesNotFillTheMagazineBeforeTheReloadCompletes()
    {
        var (original, _) = Fixture(Buff("damaged") with { Refill = true, Count = 3 }, changes: [("equipped_weapon_magazine_size", "1"),
            ("equipped_weapon_reload_time", "3"), ("character_armor", "0"), ("character_health", "1000"), ("enemy_target_hit_damage", "1"),
            ("enemy_target_incoming_dps", "2")]);
        var rules = original.Pack with
        {
            NumericContextInputs = [.. original.Pack.NumericContextInputs, new() { ID = "missing", Name = "Missing", Min = 0, Max = 100, Step = 1 }],
            Stats = [.. original.Pack.Stats.Select(s => s.ID == "enemy_damage_taken_factor" ? s with { Formula = "context_value('missing') / 100" } : s)],
            Simulation = new() { Triggers = [.. original.Pack.Simulation.Triggers, new() { Talent = "test", Key = "missing", Kind = "magazine", Percent = true }] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test");
        // The only shot empties the magazine at 0 s; the reload runs from 1 s to 4 s. Hits every 0.5 s deal
        // the missing-magazine share; the third hit's refill must not empty that share mid-reload.
        var result = Run((pack, build), Options with { Duration = 3.8, DriveSimpleBuffs = true });
        Assert.Equal(7, result.Incoming!.Hits);
        Assert.Equal(7, result.Incoming.DamageTaken);
    }

    [Fact]
    public void AnyPackRecoveryStatFailsClosedButSkillRepairScalingDoesNot()
    {
        Assert.Equal("incoming-repair-unsupported", Assert.Throws<SimulationException>(() =>
            Run(Fixture(changes: [("headache_armor_repair_share", "0.1")]))).Code);
        Assert.NotNull(Run(Fixture(changes: [("effective_repair_skills", "0.5")])).Incoming);
    }

    [Fact]
    public void UnsupportedRepairMechanicsAreRefusedAtSetupAndActivation()
    {
        // An unknown recovery stat fails closed, at setup or when a trigger activates it.
        Assert.Equal("incoming-repair-unsupported", Assert.Throws<SimulationException>(() => Run(Fixture(changes: [("armor_repair_on_dodge_percent", "0.025")]))).Code);
        var fixture = Fixture(Buff("damaged"), "armor_repair_on_dodge_percent", changes: [("armor_repair_on_dodge_percent", "0")]);
        Assert.Equal("incoming-repair-unsupported", Assert.Throws<SimulationException>(() => Run(fixture, Options with { DriveSimpleBuffs = true })).Code);
        // Previews of repairs that pack rules make (Clutch, Preservation, Doctor Home's kit, R43) are not refused.
        Assert.Equal(0, Run(Fixture(changes: [("missing_armor_repair_on_crit_percent", "0.025"), ("estimated_armor_repair_on_kill_over_time", "5"),
            ("doctor_home_kit_repair_percent", "0.5")])).Incoming!.ArmorRepaired);
    }

    [Theory]
    [InlineData(10, 2, 0)]
    [InlineData(1, 3, 0)]
    [InlineData(10, 2, 5)]
    public void StaticRegenerationAdvancesAcrossShotsReloadsAndRetargetGaps(int magazine, double reload, double delay)
    {
        var fixture = Fixture(changes: [("armor_regeneration", "2"), ("armor_regeneration_percent", "0.2"),
            ("estimated_armor_regeneration", "armor_regeneration + character_armor * armor_regeneration_percent"), ("character_health", "100"),
            ("enemy_target_health", "5"), ("equipped_weapon_magazine_size", magazine.ToString()), ("equipped_weapon_reload_time", reload.ToString())]);
        var result = Run(fixture, Options with { Duration = 3.5, Iterations = 2 }, delay: delay);
        Assert.Equal(3, result.Incoming!.Attempts);
        Assert.Equal(10, result.Incoming.ArmorRepaired);
        Assert.Equal(30, result.Incoming.DamageTaken);
        Assert.Equal(0, result.Incoming.HealthRepaired);
        Assert.Equal(10, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.ArmorRepaired).Sum(e => e.Damage));
        Assert.Contains(result.FirstFightEvents, e => e.Kind == DummyEventKind.ArmorRepaired && e.Time == 3.5 && e.Damage == 2);
        Assert.Equal(0, result.Incoming.DownedFraction);
    }

    [Fact]
    public void ConstantRegenerationTracesOneEntryPerRunNotPerShot()
    {
        // Ten shots a second; a 10-damage hit each second leaves 100 armor far from full.
        var fixture = Fixture(changes: [("estimated_armor_regeneration", "1"), ("character_armor", "100"), ("character_health", "1000"),
            ("equipped_weapon_rpm", "600")]);
        var result = Run(fixture, Options with { Duration = 3.5 });
        Assert.Equal(2.5, result.Incoming!.ArmorRepaired);
        Assert.Equal(new double[] { 2, 3, 3.5 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.ArmorRepaired).Select(e => e.Time));
        Assert.Equal(2.5, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.ArmorRepaired).Sum(e => e.Damage));
    }

    [Fact]
    public void RegenerationDoesNotWalkStateBoundariesWhileArmorIsFull()
    {
        // A 10 us cycle crosses 60 million boundaries in 600 s, beyond the work budget if each were walked.
        // No enemy hit lands, so armor stays full and the fight costs what it does without incoming fire.
        var rule = new SimTriggerDefinition
        {
            Talent = "test",
            Key = "amount",
            Kind = "cycle",
            Phases = [new() { Until = 0.000005, Value = "0" }, new() { Until = 0.00001, Value = "1" }]
        };
        var fixture = ReadingFixture(rule, "context_value('amount')");
        var options = Options with { Duration = 600, DriveSimpleBuffs = true, CaptureFirstFightEvents = false };
        var context = Context.Add("enemy_hit_rate", "0");
        var result = Run(fixture, options, context);
        var outgoing = Run(fixture, options with { IncludeIncoming = false }, context);
        Assert.Equal(0, result.Incoming!.ArmorRepaired);
        Assert.Equal(outgoing.ShotsFired, result.ShotsFired);
        // Absolute cycle boundaries changed outgoing phase readings too.
        Assert.Equal("d2p-simple-buffs-v61/d2p-splitmix64-v1", outgoing.Contract);
    }

    [Fact]
    public void RegenerationCapsAtMissingArmorAndStopsAtDeath()
    {
        var capped = Run(Fixture(changes: [("estimated_armor_regeneration", "100")]), Options with { Duration = 3.5 });
        Assert.Equal(30, capped.Incoming!.ArmorRepaired);
        Assert.Equal(0, capped.Incoming.DownedFraction);
        var downed = Run(Fixture(changes: [("estimated_armor_regeneration", "1")]), Options with { Duration = 5 });
        Assert.Equal(2, downed.Incoming!.ArmorRepaired);
        Assert.Equal(3, downed.Incoming.TimeToDown!.Mean);
        Assert.DoesNotContain(downed.FirstFightEvents, e => e.Time > 3);
        Assert.Equal(0, Run(Fixture(changes: [("estimated_armor_regeneration", "100")]), context: Context.Add("enemy_hit_rate", "0")).Incoming!.ArmorRepaired);
    }

    [Fact]
    public void RegenerationAndKillRepairDoNotRecoverTheSameMissingArmorTwice()
    {
        var result = Run(Fixture(changes: [("estimated_armor_regeneration", "4"), ("estimated_armor_on_kill", "10"),
            ("enemy_target_health", "5")]), Options with { Duration = 3.5 });
        Assert.Equal(30, result.Incoming!.ArmorRepaired);
        Assert.Equal(0, result.Incoming.DownedFraction);
        Assert.DoesNotContain(result.FirstFightEvents, e => e.Kind == DummyEventKind.ArmorRepaired && e.Time == 3.5);
    }

    [Fact]
    public void CommittedMementoRegeneratesWithFixedTrophyAssumptions()
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr").WithEquipment("backpack", "backpack_memento")
            .WithAttributeRoll("backpack", "core_2", new("armor_gear_core", 170000)).WithTalentSelection("backpack", "talent", "gear_talent_kill_confirmed");
        var context = Context.Add("enemy_target", "1").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "100").Add("enemy_firing_share", "100")
            .Add("memento_trophy_stacks", "10");
        var result = Run((pack, build), Options with { Duration = 5 }, context);
        Assert.True(result.Incoming!.ArmorRepaired > 0);
        Assert.Contains(result.FirstFightEvents, e => e.Kind == DummyEventKind.ArmorRepaired);
    }

    [Theory]
    [InlineData("buff", 0.25, 3)]
    [InlineData("buff", 1, 10)]
    [InlineData("ready", 0.25, 7)]
    [InlineData("lockout", 0.25, 7)]
    public void DrivenRegenerationIntegratesActivationExpiryAndCooldowns(string kind, double duration, double expected)
    {
        var fixture = Fixture(Buff("damaged") with { Kind = kind, Duration = duration }, "estimated_armor_regeneration", 4,
            ("estimated_armor_regeneration", "0"), ("character_health", "100"));
        var result = Run(fixture, Options with { Duration = 3.5, DriveSimpleBuffs = true, Iterations = 2 });
        Assert.Equal(expected, result.Incoming!.ArmorRepaired);
        Assert.Equal(expected, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.ArmorRepaired).Sum(e => e.Damage));
        Assert.DoesNotContain(result.FirstFightEvents, e => e.Kind == DummyEventKind.ArmorRepaired && e.Time <= 1);
        Assert.Equal("d2p-incoming-random-v73/d2p-splitmix64-v1", result.Contract);
    }

    [Theory]
    [InlineData("buff", 0.25, 1)]
    [InlineData("stacks", 0.25, 2)]
    public void RegenerationExpiresDuringReloadAndRetargetWaits(string kind, double duration, int gain)
    {
        var rule = Buff("damaged") with
        {
            Kind = kind,
            Key = kind == "stacks" ? "amount" : "buff",
            Duration = duration,
            Gain = kind == "stacks" ? gain : 0,
            Max = kind == "stacks" ? 10 : 0
        };
        var fixture = kind == "stacks" ? ReadingFixture(rule, "4 * context_value('amount')", ("equipped_weapon_magazine_size", "1"), ("enemy_target_health", "5")) :
            Fixture(rule, "estimated_armor_regeneration", 4, ("estimated_armor_regeneration", "0"), ("character_health", "100"),
                ("equipped_weapon_magazine_size", "1"), ("enemy_target_health", "5"));
        var result = Run(fixture, Options with { Duration = 3.5, DriveSimpleBuffs = true }, delay: 5);
        Assert.Equal(3 * gain, result.Incoming!.ArmorRepaired);
        Assert.Equal(1, result.ShotsFired);
    }

    [Fact]
    public void StatusDependentRegenerationStopsAtStatusExpiryBetweenShots()
    {
        var rule = Buff("hit") with { Kind = "status", Status = "burning", Duration = 0.25 };
        var fixture = Fixture(rule, "estimated_armor_regeneration", 4, ("estimated_armor_regeneration", "0"), ("character_health", "100"));
        var result = Run(fixture, Options with { Duration = 3.5, DriveSimpleBuffs = true });
        Assert.Equal(3, result.Incoming!.ArmorRepaired);
        Assert.Equal(new[] { 1.25, 2.25, 3.25 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.ArmorRepaired).Select(e => e.Time));
    }

    private static (CompiledPackExpressions Pack, BuildSnapshot Build) ReadingFixture(SimTriggerDefinition rule, string formula,
        params (string Id, string Formula)[] changes)
    {
        var (original, _) = Fixture(rule, changes: [("estimated_armor_regeneration", "0"), ("character_health", "100"), .. changes]);
        var rules = original.Pack with
        {
            NumericContextInputs = [.. original.Pack.NumericContextInputs, new() { ID = rule.Key, Name = rule.Key, Min = 0, Max = 100, Step = 1 }],
            Stats = [.. original.Pack.Stats.Select(stat => stat.ID == "estimated_armor_regeneration" ? stat with { Formula = formula } : stat)]
        };
        return Recompile(rules);
    }

    private static (CompiledPackExpressions Pack, BuildSnapshot Build) Recompile(GamePack rules) =>
        (Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled),
        BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test"));

    [Theory]
    [InlineData(false, 8)]
    [InlineData(true, 0)]
    public void MagazineRegenerationChangesAtReloadCompletionAndIncomingRefill(bool refill, double expected)
    {
        var rule = new SimTriggerDefinition { Talent = "test", Key = "missing", Kind = "magazine", Percent = true };
        var fixture = ReadingFixture(rule, "context_value('missing') * 0.04", ("equipped_weapon_magazine_size", "1"), ("enemy_target_health", "5"));
        if (refill)
        {
            fixture = Recompile(fixture.Pack.Pack with { Simulation = new() { Triggers = [rule, Buff("damaged") with { Refill = true }] } });
        }
        var result = Run(fixture, Options with { Duration = 4.5, DriveSimpleBuffs = true }, delay: 5);
        Assert.Equal(expected, result.Incoming!.ArmorRepaired);
        var emptySeconds = refill ? 1d : 3d;
        Assert.Equal(new PhaseUptime(emptySeconds, emptySeconds / 4.5), result.MagazinePhaseUptime["missing"]["100"]);
        Assert.Equal(new PhaseUptime(4.5 - emptySeconds, (4.5 - emptySeconds) / 4.5), result.MagazinePhaseUptime["missing"]["0"]);
    }

    [Fact]
    public void CycleRegenerationIntegratesAllPhasesThroughTheFinalInterval()
    {
        var rule = new SimTriggerDefinition
        {
            Talent = "test",
            Key = "amount",
            Kind = "cycle",
            Phases = [new() { Until = 0.25, Value = "4" }, new() { Until = 0.5, Value = "0" }]
        };
        var result = Run(ReadingFixture(rule, "context_value('amount')"), Options with { Duration = 3.5, DriveSimpleBuffs = true });
        Assert.Equal(5, result.Incoming!.ArmorRepaired);
        Assert.DoesNotContain(result.FirstFightEvents, e => e.Kind == DummyEventKind.ArmorRepaired && e.Time > 3.25);
    }

    [Fact]
    public void StackDecayIntegratesEachDeadlineDuringAWeaponWait()
    {
        var rule = Buff("damaged") with { Kind = "stacks", Key = "amount", Duration = 0.25, Gain = 3, Max = 10, Refresh = true, Decay = 4 };
        var fixture = ReadingFixture(rule, "context_value('amount')", ("enemy_target_incoming_dps", "5"), ("equipped_weapon_rpm", "15"));
        var result = Run(fixture, Options with { Duration = 3.5, DriveSimpleBuffs = true });
        // Three stacks at 2 s; each survives 0.5, 0.75 and 1 s, respectively.
        Assert.Equal(2.25, result.Incoming!.ArmorRepaired);
        Assert.Equal(new[] { 2.5, 2.75, 3.0 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.ArmorRepaired).Select(e => e.Time));
    }

    [Fact]
    public void APhaseBeginningAtTheHorizonIsNotUsedForRecovery()
    {
        var rule = new SimTriggerDefinition
        {
            Talent = "test",
            Key = "amount",
            Kind = "cycle",
            Phases = [new() { Until = 1, Value = "0" }, new() { Until = 2, Value = "4" }]
        };
        var fixture = ReadingFixture(rule, "-context_value('amount')");
        Assert.Equal(0, Run(fixture, Options with { Duration = 1, DriveSimpleBuffs = true }).Incoming!.ArmorRepaired);
        Assert.Equal("incoming-repair-invalid", Assert.Throws<SimulationException>(() => Run(fixture, Options with { Duration = 1.1, DriveSimpleBuffs = true })).Code);
    }

    [Fact]
    public void InvalidRegenerationRemainsRefused()
    {
        Assert.Equal("incoming-repair-invalid", Assert.Throws<SimulationException>(() => Run(Fixture(changes: [("estimated_armor_regeneration", "-1")]))).Code);
        var fixture = Fixture(Buff("damaged"), "estimated_armor_regeneration", -1, ("estimated_armor_regeneration", "0"));
        Assert.Equal("incoming-repair-invalid", Assert.Throws<SimulationException>(() => Run(fixture, Options with { DriveSimpleBuffs = true })).Code);
    }

    [Theory]
    [InlineData("1", "1", "0", 7.5)]
    [InlineData("1", "1", "1", 15)]
    [InlineData("0", "1", "0", 0)]
    [InlineData("1", "0", "0", 0)]
    public void CriticalDamageRepairsOnlyLandedCriticalChannels(string hit, string crit, string head, double expected)
    {
        var fixture = Fixture(changes: [("weapon_crit_repair_share", "0.25"), ("effective_critical_hit_chance", crit),
            ("practical_hit_fraction", hit), ("practical_headshot_fraction", head), ("equipped_weapon_critical_headshot_damage", "20"), ("character_health", "100")]);
        var result = Run(fixture, Options with { Duration = 3.5, Iterations = 2 });
        Assert.Equal(expected, result.Incoming!.ArmorRepaired);
        Assert.Equal(0, result.Incoming.HealthRepaired);
        Assert.Equal(expected, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.ArmorRepaired).Sum(e => e.Damage));
        Assert.DoesNotContain(result.FirstFightEvents, e => e.Kind == DummyEventKind.ArmorRepaired && e.Time == 0);
    }

    [Theory]
    [InlineData(true, "5", 2.5)]
    [InlineData(false, "5", 0)]
    [InlineData(true, "100000", 2.5)]
    [InlineData(false, "100000", 2.5)]
    public void CriticalRepairsIgnoreNoncriticalPelletsAndHitsOnADeadTarget(bool firstCritical, string targetHealth, double expected)
    {
        // Each pellet deals five, with opposite critical rolls. A five-health target dies to the first.
        var fixture = Fixture(changes: [("weapon_crit_repair_share", "0.5"), ("effective_critical_hit_chance", "0.5"),
            ("equipped_weapon_pellets", "2"), ("enemy_target_health", targetHealth), ("character_health", "100")]);
        var seed = Enumerable.Range(1, 1000).Select(value => (ulong)value).First(value =>
        {
            var random = new SimulationRandom(value);
            var rolls = Enumerable.Range(0, 12).Select(_ => random.NextDouble()).ToArray();
            return (rolls[7] < 0.5) == firstCritical && (rolls[10] < 0.5) != firstCritical;
        });
        var result = Run(fixture, Options with { Duration = 1.5, Seed = seed });
        Assert.Equal(expected, result.Incoming!.ArmorRepaired);
        Assert.Equal(targetHealth == "5" ? 10 : 20, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Sum(e => e.Damage));
    }

    [Fact]
    public void CriticalRepairsUseActualOverkillLimitedDamageBeforeKillRecovery()
    {
        var fixture = Fixture(changes: [("weapon_crit_repair_share", "0.5"), ("effective_critical_hit_chance", "1"),
            ("enemy_target_health", "3"), ("estimated_armor_on_kill", "100")]);
        var result = Run(fixture, Options with { Duration = 2 });
        Assert.Equal(10, result.Incoming!.ArmorRepaired);
        var events = result.FirstFightEvents.Where(e => e.Time == 1).ToArray();
        Assert.Equal(new[] { 1.5, 8.5 }, events.Where(e => e.Kind == DummyEventKind.ArmorRepaired).Select(e => e.Damage));
        Assert.True(Array.FindIndex(events, e => e.Kind == DummyEventKind.Shot) < Array.FindIndex(events, e => e.Kind == DummyEventKind.ArmorRepaired));
        Assert.True(Array.FindIndex(events, e => e.Kind == DummyEventKind.ArmorRepaired) < Array.FindIndex(events, e => e.Kind == DummyEventKind.Kill));
    }

    [Fact]
    public void CriticalRepairsRespectTargetFactorsImmunityAndMissingArmorCaps()
    {
        var factor = Fixture(changes: [("weapon_crit_repair_share", "0.25"), ("effective_critical_hit_chance", "1"),
            ("health_damage", "1"), ("character_health", "100")]);
        Assert.Equal(15, Run(factor, Options with { Duration = 3.5 }).Incoming!.ArmorRepaired);
        var immune = Fixture(changes: [("weapon_crit_repair_share", "0.5"), ("effective_critical_hit_chance", "1"),
            ("enemy_target_armor", "100"), ("out_of_cover_damage_factor", "0"), ("character_health", "100")]);
        Assert.Equal(0, Run(immune, Options with { Duration = 3.5 }).Incoming!.ArmorRepaired);
        var huge = Fixture(changes: [("weapon_crit_repair_share", "10 ^ 200"), ("effective_critical_hit_chance", "1"),
            ("equipped_weapon_critical_damage", "10 ^ 200"), ("enemy_target_health", "10 ^ 300")]);
        Assert.Equal(10, Run(huge, Options with { Duration = 2 }).Incoming!.ArmorRepaired);
    }

    [Fact]
    public void CriticalRepairCannotRescueAPlayerDownedBeforeTheShot()
    {
        var fixture = Fixture(changes: [("weapon_crit_repair_share", "1"), ("effective_critical_hit_chance", "1"),
            ("character_armor", "0"), ("character_health", "5")]);
        var result = Run(fixture, Options with { Duration = 3 });
        Assert.Equal(1, result.ShotsFired);
        Assert.Equal(1, result.Incoming!.DownedFraction);
        Assert.Equal(0, result.Incoming.ArmorRepaired);
        Assert.Equal(0, result.Incoming.HealthRepaired);
    }

    [Fact]
    public void CriticalRepairBuffDoesNotHealItsActivatingShot()
    {
        var fixture = Fixture(Buff("crit") with { Count = 2 }, "weapon_crit_repair_share", 0.5,
            ("weapon_crit_repair_share", "0"), ("effective_critical_hit_chance", "1"), ("character_health", "100"));
        var result = Run(fixture, Options with { Duration = 3, DriveSimpleBuffs = true });
        Assert.Equal(5, result.Incoming!.ArmorRepaired);
        Assert.Equal(2, Assert.Single(result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.ArmorRepaired)).Time);
    }

    [Fact]
    public void CriticalRepairSummariesCannotInventDiscreteHeals()
    {
        Assert.Equal("incoming-repair-invalid", Assert.Throws<SimulationException>(() => Run(Fixture(changes: [("estimated_crit_repair_per_second", "1")]))).Code);
        Assert.Equal("incoming-repair-invalid", Assert.Throws<SimulationException>(() => Run(Fixture(changes: [("weapon_crit_repair_share", "-1")]))).Code);
        var fixture = Fixture(Buff("damaged"), "estimated_crit_repair_per_second", changes: [("estimated_crit_repair_per_second", "0")]);
        Assert.Equal("incoming-repair-unsupported", Assert.Throws<SimulationException>(() => Run(fixture, Options with { DriveSimpleBuffs = true })).Code);
    }

    [Fact]
    public void CriticalRepairsPreserveOutgoingRandomRollsAndReplay()
    {
        var changes = new (string, string)[] { ("effective_critical_hit_chance", "0.5"), ("equipped_weapon_pellets", "4"), ("character_health", "100") };
        var options = Options with { Duration = 4, Iterations = 3 };
        var baseline = Run(Fixture(changes: changes), options);
        var fixture = Fixture(changes: [.. changes, ("weapon_crit_repair_share", "0.5")]);
        var result = Run(fixture, options);
        var repeated = Run(fixture, options);
        Assert.Equal(baseline.WeaponDps, result.WeaponDps);
        Assert.Equal(baseline.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot), result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot));
        Assert.Equal(result.Incoming, repeated.Incoming);
        Assert.Equal(result.FirstFightEvents, repeated.FirstFightEvents);
    }

    [Fact]
    public void CommittedCaduceusRepairsPlayerArmorFromCriticalHits()
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_caduceus")
            .WithTalentSelection("primary_weapon", "talent", "weapon_talent_caduceus")
            .WithEquipment("gloves", "gloves_n_5_11_tactical_gloves").WithAttributeRoll("gloves", "minor_1", new("critical_hit_chance_gear_minor", 0.06));
        var context = Context.Add("enemy_target", "1").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "100").Add("enemy_firing_share", "10");
        var result = Run((pack, build), Options with { Duration = 10, Iterations = 10, DriveSimpleBuffs = true }, context);
        Assert.True(result.Incoming!.ArmorRepaired > 0);
        Assert.Equal(0, result.Incoming.HealthRepaired);
    }

    private static (CompiledPackExpressions Pack, BuildSnapshot Build) ClutchFixture(bool perfect = false, double fraction = 0.025, string window = "4",
        params (string Id, string Formula)[] changes)
    {
        var talent = perfect ? "gear_talent_perfect_clutch" : "gear_talent_clutch";
        var (original, _) = Fixture(changes: [("character_armor", "100"), ("character_health", "1000"),
            ("effective_critical_hit_chance", "1"), ("enemy_target_hit_damage", "86"), ("missing_armor_repair_on_crit_percent", "0"),
            ("estimated_armor_repair_on_crit", "0"), ("clutch_post_kill_window", window), .. changes]);
        var rules = original.Pack with
        {
            Items = [original.Pack.Items[0] with { TalentSlots = [new() { ID = "talent", Choices = [talent] }] }],
            Selectors = [.. original.Pack.Selectors, new() { ID = "clutch_repair_state", Name = "Clutch", Values = ["low_armor", "post_kill"] }],
            NumericContextInputs = [.. original.Pack.NumericContextInputs, new() { ID = "clutch_missing_armor_percent", Name = "Missing", Min = 0, Max = 100 }],
            Talents = [new() { ID = talent, Name = "Clutch", Modifiers = [.. new[] { "low_armor", "post_kill" }.Select(state =>
                new StatModifier { Stat = "missing_armor_repair_on_crit_percent", Op = ModifierOps.Flat, Value = new(fraction),
                    RequiresContext = ImmutableDictionary<string, string>.Empty.Add("clutch_repair_state", state) })] }]
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        return (pack, BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", talent));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommittedClutchTalentsUseCoreRepairFractions(bool perfect)
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var original = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        // Keep the committed talent and equipment definitions, with controlled encounter timing and guaranteed criticals.
        var formulas = new Dictionary<string, string>
        {
            ["character_health"] = "100000000",
            ["enemy_target_armor"] = "0",
            ["enemy_target_health"] = "100000000",
            ["enemy_target_hit_damage"] = "character_armor * 0.86",
            ["enemy_target_incoming_dps"] = "enemy_target_hit_damage",
            ["effective_critical_hit_chance"] = "1",
            ["equipped_weapon_rpm"] = "60"
        };
        var rules = original.Pack with { Stats = [.. original.Pack.Stats.Select(stat => formulas.TryGetValue(stat.ID, out var formula) ? stat with { Formula = formula } : stat)] };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "primary_weapon_acr")
            .WithEquipment("backpack", perfect ? "backpack_devil_s_due" : "backpack_n_5_11_tactical_backpack")
            .WithTalentSelection("backpack", "talent", perfect ? "gear_talent_perfect_clutch" : "gear_talent_clutch");
        var context = Context.Add("enemy_target", "1").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "100").Add("practical_hit_rate", "100");
        var maximum = BuildResolver.Resolve(pack, build, context).Calculation.Stats["character_armor"];
        var result = Run((pack, build), Options with { Duration = 1.01, DriveSimpleBuffs = true, Iterations = 2 }, context);
        Assert.True(maximum > 0);
        Assert.Equal(maximum * 0.86 * 0.025, result.Incoming!.ArmorRepaired, 5);
        // The repair is Clutch's pack rule (R43); its preview state stays an assumption that only the previews read.
        Assert.True(result.TimedTriggerUptime["clutch_crit_repair"].Seconds > 0);
        var clutch = result.Coverage.Talents.Single(row => row.Talent == (perfect ? "gear_talent_perfect_clutch" : "gear_talent_clutch"));
        Assert.Contains("clutch_repair_state", clutch.Channels.Single(channel => channel.Kind == EffectCoverage.AssumptionConditions).Sources);
    }

    [Theory]
    [InlineData("0", 15)]
    [InlineData("2", 22)]
    public void MakeshiftQueuesOverlapAndIntegrateWithRegeneration(string regeneration, double expected)
    {
        var fixture = Fixture(changes: [("makeshift_repairs_share", "0.5"), ("makeshift_repairs_duration", "2"),
            ("estimated_armor_regeneration", regeneration), ("character_health", "100")]);
        var result = Run(fixture, Options with { Duration = 4.5, Iterations = 2 });
        Assert.Equal(expected, result.Incoming!.ArmorRepaired);
        Assert.Equal(0, result.Incoming.HealthRepaired);
        Assert.Equal(expected, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.ArmorRepaired).Sum(e => e.Damage));
        Assert.DoesNotContain(result.FirstFightEvents, e => e.Kind == DummyEventKind.ArmorRepaired && e.Time <= 1);
    }

    [Theory]
    [InlineData("makeshift_repairs_duration", 2, "0.5", 7.5)]
    [InlineData("makeshift_repairs_share", 0.5, "0", 5)]
    public void MakeshiftBudgetsSnapshotBeforeTheDamagedTrigger(string stat, double bonus, string share, double expected)
    {
        var fixture = Fixture(Buff("damaged"), stat, bonus, ("makeshift_repairs_share", share), ("makeshift_repairs_duration", "2"), ("character_health", "100"));
        Assert.Equal(expected, Run(fixture, Options with { Duration = 3.5, DriveSimpleBuffs = true }).Incoming!.ArmorRepaired);
    }

    [Fact]
    public void MakeshiftExpiresDuringReloadAndRetargetWaits()
    {
        var fixture = Fixture(Buff("damaged"), "enemy_damage_taken_factor", -1, ("makeshift_repairs_share", "0.5"),
            ("makeshift_repairs_duration", "2"), ("equipped_weapon_magazine_size", "1"), ("equipped_weapon_reload_time", "3"),
            ("enemy_target_health", "5"), ("character_health", "100"));
        var result = Run(fixture, Options with { Duration = 5.5, DriveSimpleBuffs = true }, delay: 5);
        Assert.Equal(10, result.Incoming!.DamageTaken);
        Assert.Equal(5, result.Incoming.ArmorRepaired);
        Assert.DoesNotContain(result.FirstFightEvents, e => e.Kind == DummyEventKind.ArmorRepaired && e.Time > 3);
    }

    [Fact]
    public void MakeshiftCapsAndDoesNotBankFullArmorDelivery()
    {
        var fixture = Fixture(changes: [("makeshift_repairs_share", "1"), ("makeshift_repairs_duration", "1"), ("estimated_bonus_armor", "20")]);
        Assert.Equal(5, Run(fixture, Options with { Duration = 3.5 }).Incoming!.ArmorRepaired);
        var capped = Fixture(changes: [("makeshift_repairs_share", "1"), ("makeshift_repairs_duration", "0.25")]);
        Assert.Equal(30, Run(capped, Options with { Duration = 3.5 }).Incoming!.ArmorRepaired);
        Assert.Equal(0, Run(capped, context: Context.Add("enemy_hit_rate", "0")).Incoming!.ArmorRepaired);
    }

    [Fact]
    public void MakeshiftStopsAtDowningWithoutQueuingFatalOverkill()
    {
        var fixture = Fixture(changes: [("makeshift_repairs_share", "0.5"), ("makeshift_repairs_duration", "2")]);
        var result = Run(fixture, Options with { Duration = 5 });
        Assert.Equal(7.5, result.Incoming!.ArmorRepaired);
        Assert.Equal(3, result.Incoming.TimeToDown!.Mean);
        Assert.DoesNotContain(result.FirstFightEvents, e => e.Time > 3);
    }

    [Theory]
    [InlineData("makeshift_repairs_share", "-1")]
    [InlineData("makeshift_repairs_share", "2")]
    [InlineData("makeshift_repairs_duration", "0")]
    [InlineData("makeshift_repairs_duration", "601")]
    public void MakeshiftRequiresValidShareAndDuration(string stat, string value)
    {
        var fixture = Fixture(changes: [("makeshift_repairs_share", "0.5"), ("makeshift_repairs_duration", "2"), (stat, value)]);
        Assert.Equal("incoming-repair-invalid", Assert.Throws<SimulationException>(() => Run(fixture)).Code);
    }

    [Fact]
    public void MakeshiftAssumedBudgetsCannotCreateActualHealing()
    {
        Assert.Equal("incoming-repair-unsupported", Assert.Throws<SimulationException>(() => Run(Fixture(changes: [("makeshift_repair_budget", "100")]))).Code);
        Assert.Equal("incoming-repair-unsupported", Assert.Throws<SimulationException>(() => Run(Fixture(changes: [("makeshift_repair_average_rate", "100")]))).Code);
    }

    [Theory]
    [InlineData(3, false, false, 0)]
    [InlineData(4, false, false, 0.5)]
    [InlineData(4, true, false, 1)]
    [InlineData(4, false, true, 0.7)]
    [InlineData(4, true, true, 1.4)]
    public void CommittedFoundryQueuesUseSetThresholdAndUpgradeProperties(int pieces, bool chest, bool backpack, double expected)
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var original = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var formulas = new Dictionary<string, string>
        {
            ["character_armor"] = "100",
            ["character_health"] = "1000",
            ["enemy_target_hit_damage"] = "10",
            ["enemy_target_incoming_dps"] = "10",
            ["enemy_damage_taken_factor"] = "1",
            ["estimated_armor_regeneration"] = "0",
            ["enemy_target_armor"] = "0",
            ["enemy_target_health"] = "100000000",
            ["equipped_weapon_rpm"] = "60"
        };
        var rules = original.Pack with { Stats = [.. original.Pack.Stats.Select(stat => formulas.TryGetValue(stat.ID, out var formula) ? stat with { Formula = formula } : stat)] };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "primary_weapon_acr");
        foreach (var itemId in rules.Sets.Single(set => set.ID == "gear_set_foundry_bulwark").Items.Take(pieces))
        { build = build.WithEquipment(rules.Items.Single(item => item.ID == itemId).Slot, itemId); }
        if (chest) { build = build.WithTalentSelection("chest", "talent", "gear_talent_improved_materials"); }
        if (backpack) { build = build.WithTalentSelection("backpack", "talent", "gear_talent_process_refinery"); }
        var context = Context.Add("enemy_target", "1").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "100")
            .Add("makeshift_qualifying_damage", "100000");
        var result = Run((pack, build), Options with { Duration = 2.5, DriveSimpleBuffs = true }, context);
        Assert.Equal(expected, result.Incoming!.ArmorRepaired, 8);
    }

    [Fact]
    public void MakeshiftQueuesPreserveOutgoingRollsAndRepeatExactly()
    {
        var changes = new (string, string)[] { ("character_health", "1000"), ("practical_hit_fraction", "0.8"), ("effective_critical_hit_chance", "0.5") };
        var options = Options with { Duration = 4.5, Iterations = 3 };
        var baseline = Run(Fixture(changes: changes), options);
        var fixture = Fixture(changes: [.. changes, ("makeshift_repairs_share", "0.5"), ("makeshift_repairs_duration", "2")]);
        var result = Run(fixture, options);
        Assert.Equal(baseline.WeaponDps, result.WeaponDps);
        Assert.Equal(baseline.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot), result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot));
        Assert.Equal(result.Incoming, Run(fixture, options).Incoming);
    }

    [Fact]
    public void MakeshiftPreviewInputCannotBeOwnedByAnotherTrigger()
    {
        var (original, _) = Fixture(Buff("damaged"));
        var rules = original.Pack with
        {
            NumericContextInputs = [.. original.Pack.NumericContextInputs, new() { ID = "makeshift_qualifying_damage", Name = "Damage", Min = 0, Max = 100, Step = 1 }],
            Simulation = new() { Triggers = [new() { Talent = "test", Key = "makeshift_qualifying_damage", Kind = "magazine", Percent = true }] }
        };
        Assert.Equal("trigger-key-conflict", Assert.Throws<SimulationException>(() => Run(Recompile(rules), Options with { DriveSimpleBuffs = true })).Code);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ShieldRulesAllowOmittedRegenerationAndUnrestrictedWeapons(bool omitRegeneration, bool omitWeapons)
    {
        var (original, build) = ShieldFixture();
        var rule = original.Pack.Simulation.Skills[0];
        var rules = original.Pack with
        {
            Simulation = original.Pack.Simulation with
            {
                Skills = [rule with {
            Regeneration = omitRegeneration ? "" : rule.Regeneration, Weapons = omitWeapons ? [] : rule.Weapons }]
            }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var result = Run((pack, build), Options with { IncludeShield = true, Duration = 2.5 });
        Assert.Equal(20, result.Incoming!.ShieldAbsorbed);
        Assert.Equal(0, result.Incoming.ShieldRepaired);
    }

    [Theory]
    [InlineData(0.3)]
    [InlineData(0.7)]
    [InlineData(1.3)]
    [InlineData(2.6)]
    public void PickupsKeepTimeOrderWithEnemyAttemptsDuringReloadsAndRetargets(double delay)
    {
        // Kills every second shot, reloads every ten and a retarget gap: enemy attempts often fall between a kill's
        // pickup and the next weapon step, and must not run first.
        var fixture = Fixture(Buff("pickup"), changes: [("enemy_target_health", "15"), ("character_health", "1000000"), ("equipped_weapon_magazine_size", "3")]);
        var result = Run(fixture, Options with { Duration = 30, Iterations = 4, DriveSimpleBuffs = true, PickupDelay = delay }, delay: 0.5);
        var events = result.FirstFightEvents;
        Assert.Contains(events, e => e.Kind == DummyEventKind.Pickup);
        Assert.Equal(events.Select(e => e.Time).Order(), events.Select(e => e.Time));
    }

    [Fact]
    public void ConsumedStacksRepairTheRaisedShield()
    {
        // Liberty or Death's shape: hits build stacks; consuming them repairs the shield a share of its health per stack.
        var rule = new SimTriggerDefinition
        {
            Talent = "test",
            Key = "stacks",
            On = "hit",
            Kind = "stacks",
            Max = 30,
            ConsumeOn = "shot",
            ClearGrant = "shield_repair",
            ClearAmount = 0.1
        };
        var (original, build) = ShieldFixture(capacity: "100");
        var rules = original.Pack with
        {
            NumericContextInputs = [.. original.Pack.NumericContextInputs, new() { ID = "stacks", Name = "Stacks", Min = 0, Max = 30, Step = 1 }],
            Simulation = original.Pack.Simulation with { Triggers = [rule] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test").WithActiveSkillSelection("shield_slot", "shield");
        var options = Options with { Duration = 4.5, IncludeShield = true, DriveSimpleBuffs = true };
        var result = Run((pack, build), options);
        var baseline = Run(ShieldFixture(capacity: "100"), options with { DriveSimpleBuffs = false });
        // Each shot after the first consumes one stack: up to 10 shield health, only what the shield is missing.
        var repairs = result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.ShieldRepaired).ToArray();
        Assert.NotEmpty(repairs);
        Assert.All(repairs, repair => Assert.InRange(repair.Damage, 0.000001, 10));
        Assert.True(result.Incoming!.ShieldRepaired > baseline.Incoming!.ShieldRepaired);
    }

    private static (CompiledPackExpressions Pack, BuildSnapshot Build) ShieldFixture(string capacity = "20", string regeneration = "0", string cooldown = "2",
        SimTriggerDefinition? rule = null, string modifier = "buff_damage", double bonus = 10, params (string Id, string Formula)[] changes)
    {
        var (original, _) = Fixture(rule, modifier, bonus, [("character_health", "100"), .. changes]);
        var rules = original.Pack with
        {
            ActiveSkillSlots = [new() { ID = "shield_slot", Name = "Shield", Choices = ["shield"] }],
            ActiveSkills = [new() { ID = "shield", Name = "Shield", PreviewOutputs = [
                new() { ID = "hp", Name = "Health", Formula = capacity }, new() { ID = "regen", Name = "Regen", Formula = regeneration },
                new() { ID = "cooldown", Name = "Cooldown", Formula = cooldown }] }],
            Simulation = original.Pack.Simulation with { Skills = [new() { Skill = "shield", Shield = "hp", Regeneration = "regen", Cooldown = "cooldown", Weapons = ["primary_weapon"] }] }
        };
        var fixture = Recompile(rules);
        return (fixture.Pack, fixture.Build.WithActiveSkillSelection("shield_slot", "shield"));
    }

    [Fact]
    public void ShieldBreakCooldownAndPlayerDamageShareOneChronologicalClock()
    {
        var fixture = ShieldFixture(changes: [("enemy_damage_taken_factor", "0.5")]);
        var result = Run(fixture, Options with { Duration = 4.5, IncludeShield = true, Iterations = 2 });
        Assert.Equal(30, result.Incoming!.ShieldAbsorbed);
        Assert.Equal(1, result.Incoming.ShieldBreaks);
        Assert.Equal(2, result.Incoming.ShieldDeployments);
        Assert.Equal(5, result.Incoming.DamageTaken);
        Assert.Equal(4, result.Incoming.Hits);
        Assert.Equal(new[] { 0d, 4d }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.ShieldRaised).Select(e => e.Time));
        Assert.DoesNotContain(result.FirstFightEvents, e => e.Kind == DummyEventKind.EnemyHit && e.Time != 3);
        var horizon = Run(fixture, Options with { Duration = 4, IncludeShield = true });
        Assert.Equal(1, horizon.Incoming!.ShieldDeployments);
        Assert.DoesNotContain(horizon.FirstFightEvents, e => e.Time >= 4);
    }

    [Fact]
    public void ShieldActiveRegenerationAndMakeshiftBothRepairOnlyActualMissingHealth()
    {
        var options = Options with { Duration = 4.5, IncludeShield = true };
        var regenerated = Run(ShieldFixture(regeneration: "2"), options);
        Assert.Equal(24, regenerated.Incoming!.ShieldAbsorbed);
        Assert.Equal(4, regenerated.Incoming.ShieldRepaired);
        var repaired = Run(ShieldFixture(changes: [("makeshift_repairs_share", "0.5"), ("makeshift_repairs_duration", "2")]), options);
        Assert.Equal(27.5, repaired.Incoming!.ShieldAbsorbed);
        Assert.Equal(7.5, repaired.Incoming.ShieldRepaired);
        Assert.Equal(2.1875, repaired.Incoming.ArmorRepaired);
        Assert.Equal(repaired.Incoming.ShieldRepaired, repaired.FirstFightEvents.Where(e => e.Kind == DummyEventKind.ShieldRepaired).Sum(e => e.Damage));
    }

    [Fact]
    public void ShieldBreakTriggersBuffsWithoutFiringPlayerDamageEvents()
    {
        var fixture = ShieldFixture(capacity: "5", rule: Buff("shield_break"));
        var result = Run(fixture, Options with { Duration = 1.5, IncludeShield = true, DriveSimpleBuffs = true });
        Assert.Equal(5, result.Incoming!.ShieldAbsorbed);
        Assert.Equal(0, result.Incoming.DamageTaken);
        Assert.Equal(new[] { 10d, 20d }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Damage));
        var damaged = Run(ShieldFixture(rule: Buff("damaged")), Options with { Duration = 1.5, IncludeShield = true, DriveSimpleBuffs = true });
        Assert.Equal(new[] { 10d, 10d }, damaged.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Damage));
    }

    [Fact]
    public void ShieldCannotRaiseAfterPlayerDowningAndRequiresIncomingMode()
    {
        var fixture = ShieldFixture(capacity: "5", cooldown: "10", changes: [("character_armor", "0"), ("character_health", "5")]);
        var result = Run(fixture, Options with { Duration = 20, IncludeShield = true });
        Assert.Equal(2, result.Incoming!.TimeToDown!.Mean);
        Assert.Equal(1, result.Incoming.ShieldDeployments);
        Assert.DoesNotContain(result.FirstFightEvents, e => e.Time > 2);
        Assert.Equal("shield-mode-unsupported", Assert.Throws<SimulationException>(() => Run(fixture, Options with { IncludeShield = true, IncludeIncoming = false })).Code);
    }

    [Fact]
    public void ShieldRequiresAllowedWeaponsAndStaticCoreOutputs()
    {
        var (original, build) = ShieldFixture();
        var rules = original.Pack with { Simulation = original.Pack.Simulation with { Skills = [original.Pack.Simulation.Skills[0] with { Weapons = ["sidearm"] }] } };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        Assert.Equal("shield-weapon-unsupported", Assert.Throws<SimulationException>(() => Run((pack, build), Options with { IncludeShield = true })).Code);
        var dynamic = ShieldFixture(capacity: "20 + buff_damage", rule: Buff("shot"));
        Assert.Equal("shield-state-unsupported", Assert.Throws<SimulationException>(() => Run(dynamic, Options with { IncludeShield = true, DriveSimpleBuffs = true })).Code);
        Assert.Equal("shield-required", Assert.Throws<SimulationException>(() => Run(Fixture(), Options with { IncludeShield = true })).Code);
    }

    [Theory]
    [InlineData("skill_bulwark_shield")]
    [InlineData("skill_crusader_shield")]
    [InlineData("skill_deflector_shield")]
    [InlineData("skill_striker_shield")]
    public void CommittedShieldProfilesLoadAndAbsorbWithTheirAllowedWeapons(string skill)
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var rule = pack.Pack.Simulation.Skills.Single(rule => rule.Skill == skill);
        var slot = rule.Weapons[0];
        var weapon = pack.Pack.Items.First(item => item.Slot == slot && !item.Tags.Any(rule.ExcludeTags.Contains));
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment(slot, weapon.ID).WithActiveSkillSelection("skill_slot_1", skill);
        var context = Context.SetItem(GamePack.WeaponSlotSelector, slot).Add("enemy_target", "1").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "100").Add("enemy_firing_share", "100");
        var result = Run((pack, build), Options with { Duration = 3, IncludeShield = true }, context);
        Assert.True(result.Incoming!.ShieldAbsorbed > 0);
        Assert.True(result.Incoming.ShieldRepaired > 0);
    }

    [Fact]
    public void FullShieldsDoNotHideUnsupportedCycleChangesBetweenCombatEvents()
    {
        var (original, build) = ShieldFixture();
        var skill = original.Pack.ActiveSkills[0];
        var rules = original.Pack with
        {
            NumericContextInputs = [.. original.Pack.NumericContextInputs, new() { ID = "amount", Name = "Amount", Min = 0, Max = 1, Step = 1 }],
            ActiveSkills = [skill with { PreviewOutputs = [.. skill.PreviewOutputs.Select(output => output.ID == "hp" ? output with { Formula = "20 + context_value('amount')" } : output)] }],
            Simulation = original.Pack.Simulation with
            {
                Triggers = [new() { Talent = "test", Key = "amount", Kind = "cycle",
                Phases = [new() { Until = 0.25, Value = "0" }, new() { Until = 0.5, Value = "1" }] }]
            }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        Assert.Equal("shield-state-unsupported", Assert.Throws<SimulationException>(() => Run((pack, build), Options with { Duration = 0.75, IncludeShield = true, DriveSimpleBuffs = true })).Code);
    }

    [Fact]
    public void ShieldsPreserveIncomingAndOutgoingRandomRolls()
    {
        var fixture = ShieldFixture(capacity: "1000", changes: [("character_health", "1000"), ("effective_critical_hit_chance", "0.5")]);
        var options = Options with { Duration = 4.5, Iterations = 3 };
        var context = Context.Add("enemy_hit_rate", "37");
        var baseline = Run(fixture, options, context);
        var result = Run(fixture, options with { IncludeShield = true }, context);
        Assert.Equal(baseline.WeaponDps, result.WeaponDps);
        Assert.Equal(baseline.Incoming!.Hits, result.Incoming!.Hits);
        Assert.Equal(result.Incoming, Run(fixture, options with { IncludeShield = true }, context).Incoming);
    }

    [Theory]
    [InlineData("0", "0", "2")]
    [InlineData("20", "-1", "2")]
    [InlineData("20", "0", "0")]
    public void ShieldRejectsInvalidCorePreviewOutputs(string health, string regeneration, string cooldown)
    {
        Assert.Equal("shield-output-invalid", Assert.Throws<SimulationException>(() => Run(ShieldFixture(health, regeneration, cooldown), Options with { IncludeShield = true })).Code);
    }

    [Fact]
    public void ShieldRejectsExcludedWeaponTags()
    {
        var (original, build) = ShieldFixture();
        var rules = original.Pack with
        {
            Items = [original.Pack.Items[0] with { Tags = ["weapon_type_lmg"] }],
            Simulation = original.Pack.Simulation with { Skills = [original.Pack.Simulation.Skills[0] with { ExcludeTags = ["weapon_type_lmg"] }] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        Assert.Equal("shield-weapon-unsupported", Assert.Throws<SimulationException>(() => Run((pack, build), Options with { IncludeShield = true })).Code);
    }

    private static (CompiledPackExpressions Pack, BuildSnapshot Build) IncomingSkillFixture(string impact = "5", string rate = "0", string window = "0", string cooldown = "0.5",
        params (string Id, string Formula)[] changes)
    {
        var (original, _) = Fixture(changes: changes);
        var rules = original.Pack with
        {
            ActiveSkillSlots = [new() { ID = "damage_slot", Name = "Damage", Choices = ["damage"] }],
            ActiveSkills = [new() { ID = "damage", Name = "Damage", PreviewOutputs = [new() { ID = "impact", Name = "Impact", Formula = impact },
                new() { ID = "rate", Name = "Rate", Formula = rate }, new() { ID = "window", Name = "Window", Formula = window },
                new() { ID = "cooldown", Name = "Cooldown", Formula = cooldown }] }],
            Simulation = original.Pack.Simulation with { Skills = [new() { Skill = "damage", Impact = "impact", Rate = "rate", Window = "window", Cooldown = "cooldown" }] }
        };
        var fixture = Recompile(rules);
        return (fixture.Pack, fixture.Build.WithActiveSkillSelection("damage_slot", "damage"));
    }

    [Fact]
    public void IncomingSkillsRunDuringReloadBeforeLaterEnemyShotsAndStopAtDowning()
    {
        var fixture = IncomingSkillFixture(changes: [("equipped_weapon_magazine_size", "1"), ("equipped_weapon_reload_time", "3")]);
        var result = Run(fixture, Options with { Duration = 5, IncludeSkills = true });
        Assert.Equal(new[] { 0d, 0.5, 1d, 1.5 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.SkillImpact).Select(e => e.Time));
        Assert.Equal(4, result.SkillDps!.Total.Mean);
        Assert.Equal(4, result.SkillDps!.Impact.Mean);
        Assert.Equal(0, result.SkillDps.ContinuousWindow.Mean);
        Assert.Equal(1, result.ShotsFired);
        Assert.Equal(2, result.Incoming!.TimeToDown!.Mean);
        Assert.DoesNotContain(result.FirstFightEvents, e => e.Time > 2);
        var tie = result.FirstFightEvents.Where(e => e.Time == 1).ToArray();
        Assert.True(Array.FindIndex(tie, e => e.Kind == DummyEventKind.EnemyHit) < Array.FindIndex(tie, e => e.Kind == DummyEventKind.SkillImpact));
    }

    [Fact]
    public void ATickEndingAtADeploymentStillBeatsAnEnemyAttackThere()
    {
        // The next use deploys at 0.5 s, as the last tick ends and the lethal attack lands.
        var fixture = IncomingSkillFixture(impact: "0", rate: "10", window: "0.5", cooldown: "0.5", changes: [("enemy_target_incoming_dps", "40")]);
        var result = Run(fixture, Options with { Duration = 2, IncludeSkills = true, DriveSimpleBuffs = true });
        var atDown = result.FirstFightEvents.Where(e => e.Time == 0.5).ToArray();
        Assert.True(Array.FindIndex(atDown, e => e.Kind == DummyEventKind.SkillTick) < Array.FindIndex(atDown, e => e.Kind == DummyEventKind.Downed));
        Assert.Equal(5, result.SkillDps!.Total.Mean * result.Duration, 8);
    }

    [Fact]
    public void ContinuousSkillsAreClippedAtTheLethalIncomingAttack()
    {
        var fixture = IncomingSkillFixture(impact: "0", rate: "10", window: "10", cooldown: "20",
            changes: [("enemy_target_incoming_dps", "40")]);
        var result = Run(fixture, Options with { Duration = 2, IncludeSkills = true });
        Assert.Equal(0.5, result.Incoming!.TimeToDown!.Mean);
        Assert.Equal(5, result.SkillDps!.Total.Mean * result.Duration, 8);
        Assert.Equal(0, result.SkillDps!.Impact.Mean);
        Assert.Equal(5, result.SkillDps.ContinuousWindow.Mean * result.Duration, 8);
        var atDown = result.FirstFightEvents.Where(e => e.Time == 0.5).ToArray();
        Assert.True(Array.FindIndex(atDown, e => e.Kind == DummyEventKind.SkillTick) < Array.FindIndex(atDown, e => e.Kind == DummyEventKind.Downed));
        Assert.DoesNotContain(result.FirstFightEvents, e => e.Time > 0.5);
    }

    // A skill's rate of 100 for 3 s, with a stat on its armor factor, against a target with this much armor.
    private static (CompiledPackExpressions Pack, BuildSnapshot Build) RateBonusFixture(string bonus, string armor, IReadOnlyList<SimTriggerDefinition>? rules = null)
    {
        var (pack, build) = IncomingSkillFixture(impact: "5", rate: "100", window: "3", cooldown: "10",
            changes: [("armor_bonus", bonus), ("enemy_target_armor", armor), ("enemy_target_health", "1000000"), ("character_armor", "0"), ("character_health", "100000")]);
        var changed = pack.Pack with { Simulation = pack.Pack.Simulation with { Skills = [pack.Pack.Simulation.Skills[0] with { RateArmorBonus = "armor_bonus" }], Triggers = [.. rules ?? []] } };
        var fixture = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(changed).Compiled);
        return (fixture, rules is null ? build : build.WithTalentSelection("primary_weapon", "talent", "test"));
    }

    private static double[] Amounts(DummyFightResult result, DummyEventKind kind) => [.. result.FirstFightEvents.Where(e => e.Kind == kind).Select(e => e.Damage)];

    [Fact]
    public void ASkillsRateBonusReachesArmorButNotItsImpactOrHealth()
    {
        var onArmor = Run(RateBonusFixture("0.5", "1000000"), Options with { Duration = 1, IncludeSkills = true });
        Assert.All(Amounts(onArmor, DummyEventKind.SkillTick), damage => Assert.Equal(15, damage, 9));
        Assert.Equal(5, Assert.Single(Amounts(onArmor, DummyEventKind.SkillImpact)));
        var onHealth = Run(RateBonusFixture("0.5", "0"), Options with { Duration = 1, IncludeSkills = true });
        Assert.All(Amounts(onHealth, DummyEventKind.SkillTick), damage => Assert.Equal(10, damage, 9));
    }

    [Fact]
    public void AnArmorBonusIsReadWithTheStateAtEachTick()
    {
        // The stat follows a buff that starts on the first whole second, so ticks before it take no bonus.
        var fixture = RateBonusFixture("buff_damage / 20", "1000000", [Buff("second")]);
        var result = Run(fixture, Options with { Duration = 2, IncludeSkills = true, DriveSimpleBuffs = true });
        var ticks = result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.SkillTick).ToArray();
        Assert.All(ticks.Where(e => e.Time < 0.95), e => Assert.Equal(10, e.Damage, 9));
        Assert.All(ticks.Where(e => e.Time > 1.05), e => Assert.Equal(15, e.Damage, 9));
    }

    [Fact]
    public void ANegativeArmorBonusIsRefused()
    {
        Assert.Equal("status-damage-invalid", Assert.Throws<SimulationException>(() => Run(RateBonusFixture("-0.5", "100"), Options with { Duration = 1, IncludeSkills = true })).Code);
    }

    [Fact]
    public void AnArmorBonusFollowsTheWeaponInHandWithoutDrivenState()
    {
        // A talent on the first weapon carries the bonus, and ten rounds a second apart empty it into the swap, so ticks lose it after.
        var (original, _) = RateBonusFixture("0", "1000000");
        var rules = original.Pack with
        {
            Slots = [.. original.Pack.Slots, new() { ID = "secondary_weapon", Name = "Second", Weapon = true }],
            Items = [.. original.Pack.Items, new() { ID = "other", Name = "Other", Slot = "secondary_weapon" }],
            Talents = [original.Pack.Talents[0] with { Modifiers = [new() { Stat = "armor_bonus", Op = ModifierOps.Flat, Value = new(0.5) }] }]
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithEquipment("secondary_weapon", "other")
            .WithTalentSelection("primary_weapon", "talent", "test").WithActiveSkillSelection("damage_slot", "damage");
        var longer = rules with { ActiveSkills = [rules.ActiveSkills[0] with { PreviewOutputs = [.. rules.ActiveSkills[0].PreviewOutputs.Select(output => output.ID == "window" ? output with { Formula = "30" } : output)] }] };
        var result = Run((Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(longer).Compiled), build),
            Options with { Duration = 15, IncludeIncoming = false, IncludeSkills = true, Rotation = new("secondary_weapon") });
        // A batch boundary can leave a sliver of a tick, so whole batches only.
        var ticks = result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.SkillTick && e.Damage > 1).ToArray();
        Assert.All(ticks.Where(e => e.Time < 5), e => Assert.Equal(15, e.Damage, 9));
        Assert.All(ticks.Where(e => e.Time > 13), e => Assert.Equal(10, e.Damage, 9));
    }

    [Fact]
    public void SkillKillsApplyImmediateKillRecoveryBeforeTheNextEnemyAttempt()
    {
        var fixture = IncomingSkillFixture(impact: "10", cooldown: "1", changes: [("practical_hit_fraction", "0"),
            ("enemy_target_health", "10"), ("estimated_armor_on_kill", "10")]);
        var result = Run(fixture, Options with { Duration = 3, IncludeSkills = true });
        Assert.Equal(3, result.Kills);
        Assert.Equal(20, result.Incoming!.ArmorRepaired);
        Assert.Equal(0, result.Incoming.DownedFraction);
        Assert.Equal(10, result.SkillDps!.Total.Mean);
    }

    [Fact]
    public void ContinuousKillAtTheHorizonDoesNotGrantAnInstantRepairOutsideTheFight()
    {
        var fixture = IncomingSkillFixture(impact: "0", rate: "100", window: "0.1", cooldown: "10", changes: [("practical_hit_fraction", "0"),
            ("enemy_target_health", "10"), ("enemy_target_incoming_dps", "200"), ("estimated_armor_on_kill", "10"), ("character_health", "100")]);
        var result = Run(fixture, Options with { Duration = 0.1, IncludeSkills = true });
        Assert.Equal(1, result.Kills);
        Assert.Equal(100, result.SkillDps!.Total.Mean, 8);
        Assert.Equal(0, result.Incoming!.ArmorRepaired);
    }

    [Fact]
    public void DamagingSkillsAndShieldAbsorptionCanRunTogether()
    {
        var (shieldPack, _) = ShieldFixture();
        var (skillPack, _) = IncomingSkillFixture(impact: "1", cooldown: "1");
        var rules = shieldPack.Pack with
        {
            ActiveSkills = [.. shieldPack.Pack.ActiveSkills, .. skillPack.Pack.ActiveSkills],
            ActiveSkillSlots = [.. shieldPack.Pack.ActiveSkillSlots, .. skillPack.Pack.ActiveSkillSlots],
            Simulation = shieldPack.Pack.Simulation with { Skills = [.. shieldPack.Pack.Simulation.Skills, .. skillPack.Pack.Simulation.Skills] }
        };
        var fixture = Recompile(rules);
        fixture = (fixture.Pack, fixture.Build.WithActiveSkillSelection("shield_slot", "shield").WithActiveSkillSelection("damage_slot", "damage"));
        var result = Run(fixture, Options with { Duration = 3.5, IncludeShield = true, IncludeSkills = true });
        Assert.Equal(20, result.Incoming!.ShieldAbsorbed);
        Assert.Equal(10, result.Incoming.DamageTaken);
        Assert.Equal(4, result.SkillDps!.Total.Mean * result.Duration, 8);
    }

    [Fact]
    public void ASkillsBurnCountsAsAnApplicationOnce()
    {
        var (original, build) = IncomingSkillFixture(impact: "0", rate: "10", window: "2", cooldown: "10", changes: [("character_health", "100000"), ("enemy_target_health", "1000000")]);
        var simulation = original.Pack.Simulation;
        var rules = original.Pack with { Simulation = simulation with { Skills = [simulation.Skills[0] with { Status = "burning" }], Triggers = [Buff("status_applied") with { Duration = 1 }] } };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var result = Run((pack, build), Options with { Duration = 5, IncludeSkills = true, DriveSimpleBuffs = true });
        // The first batch lands at 0.1 s, and 19 more find the burn on, the last ending as the window does, so the buff runs 1 s from then.
        Assert.Equal(0, result.Kills);
        Assert.Equal(1, result.TimedTriggerUptime["buff"].Seconds, 9);
        // Its damage is the rate alone: no ticks join it.
        Assert.DoesNotContain(result.FirstFightEvents, e => e.Kind is DummyEventKind.BurnTick or DummyEventKind.BleedTick);
        Assert.Equal(20, result.SkillDps!.Total.Mean * result.Duration, 8);
    }

    [Fact]
    public void ATalentsStatusOnASkillsBurnStartsNoTicks()
    {
        // One status per target (R63): the skill burns from 0.1 s to 5 s, and no second burn runs beside it. Once it has stopped, the next hit starts ticks.
        var (original, build) = IncomingSkillFixture(impact: "0", rate: "10", window: "5", cooldown: "10", changes: [("character_health", "100000"), ("enemy_target_health", "1000000"), ("burn_tick", "100 + buff_damage")]);
        var simulation = original.Pack.Simulation;
        var rules = original.Pack with
        {
            Simulation = simulation with
            {
                // The skill's own burn, for its window and no longer: the impact output is 0.
                Skills = [simulation.Skills[0] with { Rate = "", Status = "burning", StatusRate = "rate", StatusWindow = "impact" }],
                StatusDamage = [new() { Status = "burning", Tick = "burn_tick", Period = 1 }],
                Triggers = [new() { Talent = "test", Key = "burning_key", On = "hit", Kind = "status", Status = "burning", Duration = 20, Count = 2, ApplyOnHit = true }, Buff("hit") with { Count = 2, Duration = 100 }]
            }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules with
        {
            Stats = [.. rules.Stats.Where(stat => stat.ID != "burn_tick"), new() { ID = "burn_tick", Formula = "100 + buff_damage" }],
            Selectors = [.. rules.Selectors, new() { ID = "burning_key", Name = "Burning", Values = ["true"] }]
        }).Compiled);
        var result = Run((pack, build), Options with { Duration = 8, IncludeSkills = true, DriveSimpleBuffs = true });
        var ticks = result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.BurnTick).Select(e => e.Time).ToArray();
        Assert.NotEmpty(ticks);
        Assert.All(ticks, at => Assert.True(at > 5, $"a tick at {at}"));
    }

    private static CompiledPackExpressions WithSkillStatus(CompiledPackExpressions original, Func<SimSkillDefinition, SimSkillDefinition> rule, params SimTriggerDefinition[] triggers)
    {
        var simulation = original.Pack.Simulation;
        var skill = original.Pack.ActiveSkills[0];
        var rules = original.Pack with
        {
            Selectors = [.. original.Pack.Selectors, new() { ID = "burning_key", Name = "Burning", Values = ["true"] }],
            Stats = [.. original.Pack.Stats, new() { ID = "burn_tick", Formula = "100" }],
            ActiveSkills = [skill with { PreviewOutputs = [.. skill.PreviewOutputs, new() { ID = "status_rate", Name = "Status rate", Formula = "4" },
                new() { ID = "status_window", Name = "Status window", Formula = "3" }, new() { ID = "assumed_ticking_dps", Name = "Ticking", Formula = "10" }] }],
            Simulation = simulation with
            {
                Skills = [rule(simulation.Skills[0])],
                StatusDamage = [new() { Status = "burning", Tick = "burn_tick", Period = 1 }],
                Triggers = [.. triggers]
            }
        };
        return Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
    }

    private static readonly (string, string)[] LongFight = [("character_health", "100000"), ("enemy_target_health", "1000000")];

    private static double SkillTicks(CompiledPackExpressions pack, BuildSnapshot build, double duration, bool driven = true) =>
        Run((pack, build), Options with { Duration = duration, IncludeSkills = true, DriveSimpleBuffs = driven }).FirstFightEvents.Where(e => e.Kind == DummyEventKind.SkillTick).Sum(e => e.Damage);

    [Fact]
    public void ASkillsOwnStatusDealsItsRateBesideTheSkillsAndPastItsWindow()
    {
        // A Stinger Hive's shape: 2 s of stings at 10, and a bleed at 4 from the first until 3 s after the last.
        var (original, build) = IncomingSkillFixture(impact: "0", rate: "10", window: "2", cooldown: "20", changes: LongFight);
        var pack = WithSkillStatus(original, rule => rule with { Status = "burning", StatusRate = "status_rate", StatusWindow = "status_window" });
        Assert.Equal(20 + 4 * 5, SkillTicks(pack, build, 10), 6);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 3)]
    public void SpacedImpactsReachTheEnemiesATotalWouldWasteOnOne(bool spaced, int kills)
    {
        // Three shots of 60 at enemies of 50: together they spend all three on the first. A second apart, each gets its own.
        var (original, build) = IncomingSkillFixture(impact: "60", cooldown: "20", changes: [("enemy_target_health", "50"), ("character_health", "100000"), ("equipped_weapon_damage", "0"), ("equipped_weapon_critical_damage", "0")]);
        var skill = original.Pack.ActiveSkills[0];
        var rules = original.Pack with
        {
            ActiveSkills = [skill with { PreviewOutputs = [.. skill.PreviewOutputs, new() { ID = "shots", Name = "Shots", Formula = "3" }, new() { ID = "spacing", Name = "Spacing", Formula = "1" }] }],
            Simulation = original.Pack.Simulation with { Skills = [original.Pack.Simulation.Skills[0] with { Rate = "", Window = "", Impacts = "shots", ImpactInterval = spaced ? "spacing" : "" }] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var result = Run((pack, build), Options with { Duration = 10, IncludeSkills = true }, delay: 0.5);
        Assert.Equal(kills, result.Kills);
    }

    [Fact]
    public void ASkillsOwnStatusNeedsNoWindow()
    {
        // A Burn Sticky Bomb's shape: an impact, and a burn from it. One application, so 4 for 3 s.
        var (original, build) = IncomingSkillFixture(impact: "5", cooldown: "20", changes: LongFight);
        var pack = WithSkillStatus(original, rule => rule with { Rate = "", Window = "", Status = "burning", StatusRate = "status_rate", StatusWindow = "status_window" });
        Assert.Equal(4 * 3, SkillTicks(pack, build, 10), 6);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TwoUsesOfASkillsOwnStatusDontAddInAnyFight(bool driven)
    {
        // A use every second, each a 3 s burn at 4: one burn on the enemy, not three.
        var (original, build) = IncomingSkillFixture(impact: "0", cooldown: "1", changes: LongFight);
        var pack = WithSkillStatus(original, rule => rule with { Rate = "", Window = "", Status = "burning", StatusRate = "status_rate", StatusWindow = "status_window" });
        Assert.Equal(4 * 10, SkillTicks(pack, build, 10, driven), 6);
    }

    [Fact]
    public void ASkillsOwnStatusIsNoSkillHit()
    {
        // A 3 s burn from one use is no hit at all, and an impact is one.
        var (original, build) = IncomingSkillFixture(impact: "0", cooldown: "20", changes: LongFight);
        var rule = Buff("skill_hit") with { Duration = 100 };
        var pack = WithSkillStatus(original, skill => skill with { Rate = "", Window = "", Status = "burning", StatusRate = "status_rate", StatusWindow = "status_window" }, rule);
        var result = Run((pack, build), Options with { Duration = 10, IncludeSkills = true, DriveSimpleBuffs = true });
        Assert.DoesNotContain(result.TimedTriggerUptime, pair => pair.Value.Seconds > 0);
        // The same burn with an impact fires on it, so the rule is live.
        var (hitting, hitBuild) = IncomingSkillFixture(impact: "5", cooldown: "20", changes: LongFight);
        var impacts = WithSkillStatus(hitting, skill => skill with { Rate = "", Window = "", Status = "burning", StatusRate = "status_rate", StatusWindow = "status_window" }, rule);
        Assert.Contains(Run((impacts, hitBuild), Options with { Duration = 10, IncludeSkills = true, DriveSimpleBuffs = true }).TimedTriggerUptime, pair => pair.Value.Seconds > 0);
    }

    [Fact]
    public void ASkillsOwnStatusDoesntLandOnAnEnemyArrivingAfterIt()
    {
        // The bomb's impact kills, and the next enemy is there 0.05 s later, inside the burn's first batch. It wasn't there as the bomb went off.
        var (original, build) = IncomingSkillFixture(impact: "1000", cooldown: "20", changes: [("enemy_target_health", "50"), ("character_health", "100000")]);
        var pack = WithSkillStatus(original, rule => rule with { Rate = "", Window = "", Status = "burning", StatusRate = "status_rate", StatusWindow = "status_window" });
        var fight = DummyWeaponSimulator.RunAgainstTargets(pack, build, Context, Options with { Duration = 2, IncludeSkills = true, DriveSimpleBuffs = true }, 0.05, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(fight.FirstFightEvents, e => e.Kind == DummyEventKind.SkillTick);
    }

    [Fact]
    public void ASkillsOwnStatusTakesItsOwnArmorBonus()
    {
        var (original, build) = IncomingSkillFixture(impact: "0", cooldown: "20",
            changes: [("armor_bonus", "0.5"), ("enemy_target_armor", "1000000"), ("enemy_target_health", "1000000"), ("character_armor", "0"), ("character_health", "100000")]);
        var pack = WithSkillStatus(original, rule => rule with { Rate = "", Window = "", Status = "burning", StatusRate = "status_rate", StatusWindow = "status_window", StatusArmorBonus = "armor_bonus" });
        Assert.Equal(4 * 3 * 1.5, SkillTicks(pack, build, 10), 6);
    }

    [Fact]
    public void ASkillsOwnStatusDoesntFollowItsEnemysDeathToTheNext()
    {
        // The enemy falls inside the first second, so the 3 s burn's tail has nobody to run on: the next enemy wasn't hit by the use.
        var (original, build) = IncomingSkillFixture(impact: "0", cooldown: "20", changes: [("enemy_target_health", "5"), ("character_health", "100000")]);
        var pack = WithSkillStatus(original, rule => rule with { Rate = "", Window = "", Status = "burning", StatusRate = "status_rate", StatusWindow = "status_window" });
        var ticks = SkillTicks(pack, build, 10);
        Assert.InRange(ticks, double.Epsilon, 4);
    }

    [Fact]
    public void ASkillsRateThatIsAStatusWaitsForATalentsToEnd()
    {
        // The first hit starts the talent's burn, for 2 s, so the first use's 1 s of burn deals nothing. It's gone by the second use, at 5 s.
        var (original, build) = IncomingSkillFixture(impact: "0", rate: "10", window: "1", cooldown: "5", changes: LongFight);
        var burn = new SimTriggerDefinition { Talent = "test", Key = "burning_key", On = "hit", Kind = "status", Status = "burning", Duration = 2, Count = 1, Every = 100, ApplyOnHit = true };
        var waits = WithSkillStatus(original, rule => rule with { Rate = "", Status = "burning", StatusRate = "rate", StatusWindow = "impact" }, burn);
        Assert.Equal(10, SkillTicks(waits, build, 8), 6);
        // A rate that only carries the status carries on beside it, as Stinger Hive's stings do.
        var beside = WithSkillStatus(original, rule => rule with { Status = "burning" }, burn);
        Assert.Equal(20, SkillTicks(beside, build, 8), 6);
        // A rate's name no longer makes it the status's damage: only statusRate does (R68), so Oxidizer's rate keeps its name safely.
        var named = WithSkillStatus(original, rule => rule with { Rate = "assumed_ticking_dps", Status = "burning" }, burn);
        Assert.Equal(20, SkillTicks(named, build, 8), 6);
    }

    [Fact]
    public void ATalentsStatusOnACarriedStatusFixesItsAmountAsAnyDoes()
    {
        // A rate that only carries the burn, as Stinger Hive's stings do a bleed. The bullet at 1 s starts the talent's own burn, and earns a bonus it mustn't read.
        var (original, build) = IncomingSkillFixture(impact: "0", rate: "10", window: "5", cooldown: "10", changes: [("character_health", "100000"), ("enemy_target_health", "1000000")]);
        var simulation = original.Pack.Simulation;
        var rules = original.Pack with
        {
            Selectors = [.. original.Pack.Selectors, new() { ID = "burning_key", Name = "Burning", Values = ["true"] }],
            Stats = [.. original.Pack.Stats, new() { ID = "burn_tick", Formula = "100 + buff_damage" }],
            Simulation = simulation with
            {
                Skills = [simulation.Skills[0] with { Status = "burning" }],
                StatusDamage = [new() { Status = "burning", Tick = "burn_tick", Period = 1 }],
                Triggers = [new() { Talent = "test", Key = "burning_key", On = "hit", Kind = "status", Status = "burning", Duration = 20, Count = 2, ApplyOnHit = true }, Buff("hit") with { Count = 2, Duration = 100 }]
            }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var result = Run((pack, build), Options with { Duration = 4, IncludeSkills = true, DriveSimpleBuffs = true });
        var ticks = result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.BurnTick).ToArray();
        Assert.NotEmpty(ticks);
        Assert.All(ticks, e => Assert.Equal(100, e.Damage));
    }

    [Fact]
    public void ASkillDeployedWithABonusUpReadsItAndItsStatusSpendsIt()
    {
        // Perpetuation's shape: a shot earns a bonus, and a status going on spends it. The use at 0 deploys ahead of the first shot, so it reads none, and its burn
        // spends the bonus that shot earned. A later shot earns another, which the use at 2 s reads as it deploys, for its whole window, and its own burn spends.
        var (original, build) = IncomingSkillFixture(impact: "0", rate: "10 + buff_damage", window: "1", cooldown: "2", changes: [("character_health", "100000"), ("enemy_target_health", "1000000")]);
        var simulation = original.Pack.Simulation;
        var rules = original.Pack with { Simulation = simulation with { Skills = [simulation.Skills[0] with { Status = "burning" }], Triggers = [Buff("shot") with { Duration = 0, ConsumeOn = "status_applied" }] } };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var result = Run((pack, build), Options with { Duration = 3.5, IncludeSkills = true, DriveSimpleBuffs = true });
        var ticks = result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.SkillTick).ToArray();
        Assert.Equal(10, ticks.Where(e => e.Time <= 1).Sum(e => e.Damage), 8);
        Assert.Equal(20, ticks.Where(e => e.Time > 2).Sum(e => e.Damage), 8);
    }

    [Fact]
    public void ASkillReadsTheBuildAsItDeploys()
    {
        var (original, build) = IncomingSkillFixture(impact: "5 + buff_damage", cooldown: "1", changes: [("character_health", "100000")]);
        var rules = original.Pack with { Simulation = original.Pack.Simulation with { Triggers = [Buff("shot") with { Duration = 100 }] } };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var result = Run((pack, build), Options with { Duration = 2.5, IncludeSkills = true, DriveSimpleBuffs = true });
        // The first deploys before the first shot, and the buff is up for the rest.
        Assert.Equal([5d, 15, 15], result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.SkillImpact).Select(e => e.Damage));
    }

    private static (CompiledPackExpressions Pack, BuildSnapshot Build) RateFixture(string rateRead)
    {
        var (original, build) = IncomingSkillFixture(impact: "0", rate: "5 + buff_damage", window: "1", cooldown: "10");
        var simulation = original.Pack.Simulation;
        var rules = original.Pack with { Simulation = simulation with { Triggers = [Buff("shot") with { Duration = 100 }], Skills = [simulation.Skills[0] with { RateRead = rateRead }] } };
        return (Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled), build);
    }

    [Theory]
    [InlineData("", 5)]
    [InlineData("live", 15)]
    public void ARunningRateHoldsUnlessItIsLive(string rateRead, double total)
    {
        // The use deploys before the first shot, and every batch ends after it.
        var result = Run(RateFixture(rateRead), Options with { Duration = 2, IncludeSkills = true, DriveSimpleBuffs = true });
        Assert.Equal(total, result.SkillDps!.Total.Mean * result.Duration, 8);
    }

    [Fact]
    public void ACooldownUnderWayHoldsUntilTheNextUse()
    {
        var (original, build) = IncomingSkillFixture(impact: "1", cooldown: "10 - buff_damage / 2", changes: [("character_health", "100000")]);
        var rules = original.Pack with { Simulation = original.Pack.Simulation with { Triggers = [Buff("shot") with { Duration = 100 }] } };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var result = Run((pack, build), Options with { Duration = 16, IncludeSkills = true, DriveSimpleBuffs = true });
        // The use at 0 read 10 s, before the buff. The next read 5 s.
        Assert.Equal([0d, 10, 15], result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.SkillImpact).Select(e => e.Time));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommittedAssaultTurretFightsAlongsideIncomingDamageAndOptionalShield(bool shield)
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr")
            .WithEquipment("chest", "chest_n_5_11_tactical_chest").WithAttributeRoll("chest", "core_1", new("armor_gear_core", 170000))
            .WithActiveSkillSelection("skill_slot_1", "skill_assault_turret");
        if (shield) { build = build.WithActiveSkillSelection("skill_slot_2", "skill_crusader_shield"); }
        var context = Context.Add("enemy_target", "1").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "100").Add("enemy_firing_share", "10");
        var result = Run((pack, build), Options with { Duration = 3, IncludeSkills = true, IncludeShield = shield }, context);
        Assert.True(result.SkillDps!.Total.Mean > 0);
        Assert.True(result.Incoming!.Attempts > 0);
        Assert.Equal(result.WeaponDps.Mean + result.SkillDps.Total.Mean, result.TotalDps!.Mean, 6);
        if (shield) { Assert.True(result.Incoming.ShieldAbsorbed > 0); }
    }

    [Fact]
    public void StaticIncomingSkillsPreserveOutgoingRandomRollsAndReplay()
    {
        var fixture = IncomingSkillFixture(impact: "1", changes: [("character_health", "1000"), ("effective_critical_hit_chance", "0.5")]);
        var options = Options with { Duration = 3.5, Iterations = 3 };
        var baseline = Run(fixture, options);
        var result = Run(fixture, options with { IncludeSkills = true });
        Assert.Equal(baseline.WeaponDps, result.WeaponDps);
        Assert.Equal(baseline.Incoming!.Hits, result.Incoming!.Hits);
        var repeated = Run(fixture, options with { IncludeSkills = true });
        Assert.Equal(result.Incoming, repeated.Incoming);
        Assert.Equal(result.FirstFightEvents, repeated.FirstFightEvents);
    }

    [Fact]
    public void IncomingSkillsRefuseUnmodeledWeaponKillRepairSources()
    {
        var fixture = IncomingSkillFixture(changes: [("estimated_armor_repair_per_kill", "5")]);
        Assert.Equal("skill-kill-repair-unsupported", Assert.Throws<SimulationException>(() => Run(fixture, Options with { IncludeSkills = true })).Code);
    }

    private static (CompiledPackExpressions Pack, BuildSnapshot Build) DoctorHomeFixture(string impact = "5", string cooldown = "1",
        params (string Id, string Formula)[] changes)
    {
        var (original, _) = IncomingSkillFixture(impact: impact, cooldown: cooldown, changes: [("character_armor", "100"), ("character_health", "1000"),
            ("enemy_target_health", "11"), ("equipped_weapon_damage", "1"), ("armor_repair_per_kill_percent", "0"),
            ("estimated_armor_repair_per_kill", "character_armor * armor_repair_per_kill_percent"), .. changes]);
        // Doctor Home as R43's pack rules: each hit marks the target for 5 s, and a marked target's death grants 50% of max armor.
        var talent = new TalentDefinition { ID = "weapon_talent_doctor_home", Name = "Doctor Home" };
        var rules = original.Pack with
        {
            Talents = [talent],
            Items = [original.Pack.Items[0] with { TalentSlots = [new() { ID = "talent", Choices = [talent.ID] }] }],
            Selectors = [.. original.Pack.Selectors, new() { ID = "doctor_home_mark", Name = "Mark", Values = ["true"] }, new() { ID = "doctor_home_kit", Name = "Kit", Values = ["true"] }],
            Simulation = original.Pack.Simulation with
            {
                Triggers =
                [
                    new() { Talent = talent.ID, Key = "doctor_home_mark", On = "hit", Kind = "buff", Duration = 5, ConsumeOn = "target_death" },
                    new() { Talent = talent.ID, Key = "doctor_home_kit", On = "target_death", Kind = "buff", Duration = 0.1, WhileKey = "doctor_home_mark", Grant = "armor_repair", GrantAmount = 0.5 }
                ]
            }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        return (pack, BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun")
            .WithTalentSelection("primary_weapon", "talent", talent.ID).WithActiveSkillSelection("damage_slot", "damage"));
    }

    [Theory]
    [InlineData(true, true, 40)]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 0)]
    public void CommittedDoctorHomeRequiresAnActiveOwnerAndActualMarkForSkillKills(bool landed, bool active, double expected)
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var original = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var formulas = new Dictionary<string, string>
        {
            ["character_armor"] = "100",
            ["character_health"] = "1000",
            ["enemy_target_armor"] = "0",
            ["enemy_target_health"] = landed ? "11" : "10",
            ["enemy_target_hit_damage"] = "10",
            ["enemy_target_incoming_dps"] = "10",
            ["enemy_damage_taken_factor"] = "1",
            ["equipped_weapon_damage"] = "1",
            ["equipped_weapon_rpm"] = "60",
            ["equipped_weapon_magazine_size"] = "1",
            ["equipped_weapon_reload_time"] = "10",
            ["practical_hit_fraction"] = landed ? "1" : "0",
            ["practical_headshot_fraction"] = "0",
            ["effective_critical_hit_chance"] = "0",
            ["out_of_cover_damage_factor"] = "1",
            ["health_damage"] = "0"
        };
        var skill = new ActiveSkillDefinition
        {
            ID = "mark_test_skill",
            Name = "Controlled skill",
            PreviewOutputs = [
            new() { ID = "impact", Name = "Impact", Formula = "5" }, new() { ID = "cooldown", Name = "Cooldown", Formula = "4.9" }]
        };
        var rules = original.Pack with
        {
            Stats = [.. original.Pack.Stats.Select(stat => formulas.TryGetValue(stat.ID, out var formula) ? stat with { Formula = formula } : stat)],
            ActiveSkills = [.. original.Pack.ActiveSkills, skill],
            ActiveSkillSlots = [.. original.Pack.ActiveSkillSlots.Select(slot => slot.ID == "skill_slot_1" ? slot with { Choices = [.. slot.Choices, skill.ID] } : slot)],
            Simulation = original.Pack.Simulation with
            {
                Skills = [.. original.Pack.Simulation.Skills,
                new() { Skill = skill.ID, Impact = "impact", Cooldown = "cooldown" }]
            }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var owner = active ? "primary_weapon" : "secondary_weapon";
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "primary_weapon_acr")
            .WithEquipment(owner, owner + "_doctor_home").WithTalentSelection(owner, "talent", "weapon_talent_doctor_home")
            .WithActiveSkillSelection("skill_slot_1", skill.ID);
        var context = Context.Add("enemy_target", "1").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "100");
        // Doctor Home's mark and kit are pack rules (R43), which need driven triggers.
        var result = Run((pack, build), Options with { Duration = 5.2, IncludeSkills = true, DriveSimpleBuffs = true }, context);
        Assert.Equal(1, result.Kills);
        Assert.Equal(expected, result.Incoming!.ArmorRepaired, 8);
        if (!active || !landed) { Assert.DoesNotContain(result.FirstFightEvents, e => e.Kind == DummyEventKind.TargetMarked); }
    }

    private static (CompiledPackExpressions Pack, BuildSnapshot Build) SkillTriggerFixture(SimTriggerDefinition rule, bool gearOwner = true,
        string impact = "5", string rate = "0", string window = "0", string cooldown = "10", string modifier = "buff_damage",
        params (string Id, string Formula)[] changes)
    {
        var (original, _) = IncomingSkillFixture(impact, rate, window, cooldown, [("character_health", "1000"), .. changes]);
        var rules = original.Pack with
        {
            Talents = [original.Pack.Talents[0] with { Modifiers = [original.Pack.Talents[0].Modifiers[0] with { Stat = modifier }] }],
            Simulation = original.Pack.Simulation with { Triggers = [rule] }
        };
        if (gearOwner)
        {
            rules = rules with
            {
                Slots = [.. rules.Slots, new() { ID = "chest", Name = "Chest" }],
                Items = [.. rules.Items, new() { ID = "gear", Name = "Gear", Slot = "chest", TalentSlots = [new() { ID = "talent", Choices = ["test"] }] }]
            };
        }
        var fixture = Recompile(rules);
        var build = fixture.Build.WithActiveSkillSelection("damage_slot", "damage");
        if (gearOwner) { build = build.WithTalentSelection("primary_weapon", "talent", "").WithEquipment("chest", "gear").WithTalentSelection("chest", "talent", "test"); }
        return (fixture.Pack, build);
    }

    [Fact]
    public void SkillHitsActivateWeaponBuffsAndExpireWithoutChangingSkillDamage()
    {
        var fixture = SkillTriggerFixture(Buff("skill_hit") with { Duration = 0.75 });
        var result = Run(fixture, Options with { Duration = 1.5, IncludeSkills = true, DriveSimpleBuffs = true });
        Assert.Equal(new[] { 20d, 10d }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Damage));
        Assert.Equal(5, result.SkillDps!.Total.Mean * result.Duration);
    }

    [Theory]
    [InlineData("0.1", 10)]
    [InlineData("0.2", 20)]
    public void EnemySplitsDoNotMultiplyCountedSkillHits(string window, double expectedLastShot)
    {
        var fixture = SkillTriggerFixture(Buff("skill_hit") with { Count = 2 }, impact: "0", rate: "10", window: window,
            changes: [("equipped_weapon_rpm", "600"), ("enemy_target_incoming_dps", "400")]);
        var result = Run(fixture, Options with { Duration = 0.25, IncludeSkills = true, DriveSimpleBuffs = true });
        Assert.Equal(expectedLastShot, result.FirstFightEvents.Last(e => e.Kind == DummyEventKind.Shot).Damage);
    }

    [Theory]
    [InlineData(true, 10)]
    [InlineData(false, 0)]
    public void SkillKillsTriggerGearRulesButNotWeaponKillRules(bool gearOwner, double expectedRepair)
    {
        var fixture = SkillTriggerFixture(Buff("kill"), gearOwner, impact: "10", cooldown: "1", modifier: "estimated_armor_on_kill",
            changes: [("practical_hit_fraction", "0"), ("enemy_target_health", "10"), ("estimated_armor_on_kill", "0")]);
        var result = Run(fixture, Options with { Duration = 1.5, IncludeSkills = true, DriveSimpleBuffs = true });
        Assert.Equal(expectedRepair, result.Incoming!.ArmorRepaired);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SkillKillsConsumeBuffsRegardlessOfGainOwnership(bool gearOwner)
    {
        var fixture = SkillTriggerFixture(Buff("hit") with { ConsumeOn = "kill" }, gearOwner: gearOwner, impact: "5", cooldown: "1",
            changes: [("enemy_target_health", "20")]);
        var result = Run(fixture, Options with { Duration = 1.5, IncludeSkills = true, DriveSimpleBuffs = true });
        Assert.Contains(result.FirstFightEvents, entry => entry.Kind == DummyEventKind.Kill && entry.Time == 1 && entry.WeaponSlot == "damage_slot");
        Assert.Equal(new[] { 10d, 10d }, result.FirstFightEvents.Where(entry => entry.Kind == DummyEventKind.Shot).Select(entry => entry.Damage));
    }

    [Fact]
    public void SkillRefillsCannotExposeAmmoBeforeReloadCompletion()
    {
        var fixture = SkillTriggerFixture(Buff("skill_hit") with { Refill = true }, impact: "1", cooldown: "1.5",
            changes: [("equipped_weapon_magazine_size", "1"), ("equipped_weapon_reload_time", "3")]);
        var rules = fixture.Pack.Pack with
        {
            NumericContextInputs = [.. fixture.Pack.Pack.NumericContextInputs, new() { ID = "missing", Name = "Missing", Min = 0, Max = 100, Step = 1 }],
            Simulation = fixture.Pack.Pack.Simulation with
            {
                Triggers = [.. fixture.Pack.Pack.Simulation.Triggers,
                new() { Talent = "test", Key = "missing", Kind = "magazine", Percent = true }]
            }
        };
        fixture = (Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled), fixture.Build);
        var result = Run(fixture, Options with { Duration = 4.1, IncludeSkills = true, DriveSimpleBuffs = true });
        Assert.Equal(new[] { 0d, 4d }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Time));
        Assert.Equal(1, result.Reloads);
        Assert.Equal(new PhaseUptime(4.1, 1), result.MagazinePhaseUptime["missing"]["100"]);
        Assert.Equal(new PhaseUptime(0, 0), result.MagazinePhaseUptime["missing"]["0"]);
    }

    [Fact]
    public void AStateOnlyBetweenDeploymentsReachesNoSkill()
    {
        var (original, build) = IncomingSkillFixture(impact: "0", rate: "0", window: "0.1", cooldown: "10");
        var skill = original.Pack.ActiveSkills[0];
        var rules = original.Pack with
        {
            NumericContextInputs = [.. original.Pack.NumericContextInputs, new() { ID = "amount", Name = "Amount", Min = 0, Max = 1, Step = 1 }],
            ActiveSkills = [skill with { PreviewOutputs = [.. skill.PreviewOutputs.Select(output => output.ID == "rate" ? output with { Formula = "context_value('amount')" } : output)] }],
            Simulation = original.Pack.Simulation with
            {
                Triggers = [new() { Talent = "test", Key = "amount", Kind = "cycle",
                Phases = [new() { Until = 0.03, Value = "0" }, new() { Until = 0.06, Value = "1" }, new() { Until = 0.1, Value = "0" }] }]
            }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        Assert.Equal(0, Run((pack, build), Options with { Duration = 0.075, IncludeSkills = true, DriveSimpleBuffs = true }).SkillDps!.Total.Mean);
    }

    [Theory]
    [InlineData("chest_n_5_11_tactical_chest", "gear_talent_spark")]
    [InlineData("chest_door_kicker_s_knock", "gear_talent_perfect_spark")]
    public void CommittedSparkTalentsActivateFromAssaultTurretDamage(string chest, string talent)
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var original = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var formulas = new Dictionary<string, string>
        {
            ["character_health"] = "1000000",
            ["enemy_target_health"] = "1000000000",
            ["enemy_target_armor"] = "0",
            ["practical_hit_fraction"] = "1",
            ["effective_critical_hit_chance"] = "0",
            ["practical_headshot_fraction"] = "0"
        };
        var rules = original.Pack with { Stats = [.. original.Pack.Stats.Select(stat => formulas.TryGetValue(stat.ID, out var formula) ? stat with { Formula = formula } : stat)] };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "primary_weapon_acr").WithEquipment("chest", chest)
            .WithTalentSelection("chest", "talent", talent).WithActiveSkillSelection("skill_slot_1", "skill_assault_turret");
        var context = Context.Add("enemy_target", "1").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "100").Add("enemy_firing_share", "10");
        var options = Options with { Duration = 3, IncludeSkills = true };
        var baseline = Run((pack, build), options, context);
        var driven = Run((pack, build), options with { DriveSimpleBuffs = true }, context);
        Assert.True(driven.WeaponDps.Mean > baseline.WeaponDps.Mean);
        Assert.Equal(baseline.SkillDps, driven.SkillDps);
    }

    [Fact]
    public void ZeroSkillDamageCannotActivateSkillHitBuffs()
    {
        var fixture = SkillTriggerFixture(Buff("skill_hit"), impact: "0");
        var result = Run(fixture, Options with { Duration = 1.5, IncludeSkills = true, DriveSimpleBuffs = true }, Context.Add("buff", "true"));
        Assert.All(result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot), e => Assert.Equal(10, e.Damage));
    }

    [Fact]
    public void FinalSkillTickCannotActivateABuffOutsideTheHorizon()
    {
        var (original, build) = SkillTriggerFixture(Buff("skill_hit"), impact: "0", rate: "1", window: "0.1");
        var skill = original.Pack.ActiveSkills[0];
        var rules = original.Pack with
        {
            ActiveSkills = [skill with { PreviewOutputs = [.. skill.PreviewOutputs.Select(output =>
            output.ID == "rate" ? output with { Formula = "1 + buff_damage" } : output)] }]
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var result = Run((pack, build), Options with { Duration = 0.1, IncludeSkills = true, DriveSimpleBuffs = true });
        Assert.Equal(0.1, result.SkillDps!.Total.Mean * result.Duration, 8);
    }

    [Fact]
    public void DrivenSkillCallbacksPreserveRandomRollsAndReplay()
    {
        var fixture = SkillTriggerFixture(Buff("skill_hit"), modifier: "unused", changes: [("unused", "0"), ("effective_critical_hit_chance", "0.5")]);
        var options = Options with { Duration = 3.5, IncludeSkills = true, Iterations = 3 };
        var baseline = Run(fixture, options);
        var driven = Run(fixture, options with { DriveSimpleBuffs = true });
        Assert.Equal(baseline.WeaponDps, driven.WeaponDps);
        Assert.Equal(baseline.SkillDps, driven.SkillDps);
        Assert.Equal(baseline.Incoming!.Hits, driven.Incoming!.Hits);
        Assert.Equal(driven.FirstFightEvents, Run(fixture, options with { DriveSimpleBuffs = true }).FirstFightEvents);
    }

    [Fact]
    public void SkillCallbacksCannotActivateUnmodeledKillRepairSources()
    {
        var fixture = SkillTriggerFixture(Buff("skill_hit"), modifier: "estimated_armor_repair_per_kill",
            changes: [("estimated_armor_repair_per_kill", "0")]);
        Assert.Equal("skill-kill-repair-unsupported", Assert.Throws<SimulationException>(() => Run(fixture,
            Options with { IncludeSkills = true, DriveSimpleBuffs = true })).Code);
    }

    private static (CompiledPackExpressions Pack, BuildSnapshot Build) PreservationFixture(bool perfect = false,
        params (string Id, string Formula)[] changes)
    {
        var talent = perfect ? "weapon_talent_perfect_preservation" : "weapon_talent_preservation";
        var (original, _) = Fixture(changes: [("character_armor", "100"), ("character_health", "1000"), ("enemy_target_health", "5"),
            ("equipped_weapon_magazine_size", "1"), ("equipped_weapon_reload_time", "10"), ("armor_repair_on_kill_over_time_percent", "0"),
            ("estimated_armor_repair_on_kill_over_time", "character_armor * armor_repair_on_kill_over_time_percent"), .. changes]);
        // Preservation as R43's pack rules: each kill, and again each headshot kill, repairs a share of max armor over 5 s.
        var share = perfect ? 0.15 : 0.1;
        SimTriggerDefinition Repair(string key, string on) => new()
        { Talent = talent, Key = key, On = on, Kind = "buff", Duration = 5, Grant = "armor_repair", GrantAmount = share, GrantDuration = 5, GrantInstances = "independent" };
        var rules = original.Pack with
        {
            Selectors = [.. original.Pack.Selectors, new() { ID = "preservation_repair", Name = "Repair", Values = ["true"] },
                new() { ID = "preservation_headshot_repair", Name = "Headshot repair", Values = ["true"] }],
            Items = [original.Pack.Items[0] with { TalentSlots = [new() { ID = "talent", Choices = [talent] }] }],
            Talents = [new() { ID = talent, Name = "Preservation" }],
            Simulation = original.Pack.Simulation with { Triggers = [Repair("preservation_repair", "kill"), Repair("preservation_headshot_repair", "headshot_kill")] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        return (pack, BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", talent));
    }

    [Theory]
    [InlineData(false, "0", 8)]
    [InlineData(false, "1", 16)]
    [InlineData(true, "0", 12)]
    [InlineData(true, "1", 24)]
    public void CommittedPreservationVariantsDeliverCoreBodyAndHeadshotBudgets(bool perfect, string head, double expected)
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var original = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var formulas = new Dictionary<string, string>
        {
            ["character_armor"] = "100",
            ["character_health"] = "1000",
            ["enemy_target_armor"] = "0",
            ["enemy_target_health"] = "5",
            ["enemy_target_hit_damage"] = "10",
            ["enemy_target_incoming_dps"] = "10",
            ["enemy_damage_taken_factor"] = "1",
            ["equipped_weapon_damage"] = "10",
            ["equipped_weapon_headshot_damage"] = "10",
            ["equipped_weapon_critical_damage"] = "10",
            ["equipped_weapon_critical_headshot_damage"] = "10",
            ["equipped_weapon_rpm"] = "60",
            ["equipped_weapon_magazine_size"] = "1",
            ["equipped_weapon_reload_time"] = "10",
            ["practical_hit_fraction"] = "1",
            ["practical_headshot_fraction"] = head,
            ["effective_critical_hit_chance"] = "0",
            ["out_of_cover_damage_factor"] = "1",
            ["health_damage"] = "0"
        };
        var rules = original.Pack with { Stats = [.. original.Pack.Stats.Select(stat => formulas.TryGetValue(stat.ID, out var formula) ? stat with { Formula = formula } : stat)] };
        var talent = perfect ? "weapon_talent_perfect_preservation" : "weapon_talent_preservation";
        var weapon = rules.Items.First(item => item.Slot == "primary_weapon" && item.TalentSlots.Any(slot => slot.Choices.Contains(talent)));
        var talentSlot = weapon.TalentSlots.First(slot => slot.Choices.Contains(talent));
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", weapon.ID).WithTalentSelection("primary_weapon", talentSlot.ID, talent);
        var context = Context.Add("enemy_target", "1").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "100");
        // Preservation's repairs are pack rules (R43), which need driven triggers.
        Assert.Equal(expected, Run((pack, build), Options with { Duration = 6, DriveSimpleBuffs = true }, context).Incoming!.ArmorRepaired, 8);
    }

    [Fact]
    public void PreservationBudgetsPreserveRandomRollsAndReplay()
    {
        var fixture = PreservationFixture(changes: [("practical_hit_fraction", "0.7"), ("practical_headshot_fraction", "0.5")]);
        var options = Options with { Duration = 6, Iterations = 3, DriveSimpleBuffs = true };
        var first = Run(fixture, options);
        var replay = Run(fixture, options);
        var unowned = Run((fixture.Pack, fixture.Build.WithTalentSelection("primary_weapon", "talent", "")), options);
        Assert.Equal(first.FirstFightEvents, replay.FirstFightEvents);
        Assert.Equal(first.Incoming, replay.Incoming);
        Assert.Equal(unowned.WeaponDps, first.WeaponDps);
        Assert.Equal(unowned.Incoming!.Hits, first.Incoming!.Hits);
    }

    [Fact]
    public void KillRepairsUseCoreAmountsAndReportOnlyActualRecovery()
    {
        var fixture = Fixture(changes: [("enemy_target_health", "5"), ("estimated_armor_on_kill", "3"),
            ("estimated_armor_repair_per_kill", "4"), ("estimated_health_on_kill", "5")]);
        var result = Run(fixture, Options with { Duration = 4, Iterations = 2 });
        Assert.Equal(4, result.Kills);
        Assert.Equal(21, result.Incoming!.ArmorRepaired);
        Assert.Equal(6, result.Incoming.HealthRepaired);
        Assert.Equal(0, result.Incoming.DownedFraction);
        Assert.Equal(new double[] { 3, 3 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.HealthRepaired).Select(e => e.Damage));
        Assert.DoesNotContain(result.FirstFightEvents, e => e.Time == 0 && e.Kind is DummyEventKind.ArmorRepaired or DummyEventKind.HealthRepaired);
    }

    [Fact]
    public void KillRepairsClampOverhealAndDoNotRetroactivelyUseKillBuffs()
    {
        var fixture = Fixture(changes: [("enemy_target_health", "5"), ("estimated_armor_on_kill", "50")]);
        var clamped = Run(fixture, Options with { Duration = 3 });
        Assert.Equal(20, clamped.Incoming!.ArmorRepaired);
        var triggered = Fixture(Buff("kill"), "estimated_armor_repair_per_kill", changes: [("enemy_target_health", "20"), ("estimated_armor_repair_per_kill", "0")]);
        var result = Run(triggered, Options with { Duration = 3, DriveSimpleBuffs = true });
        Assert.Equal(0, result.Incoming!.ArmorRepaired);
        Assert.Equal(2, result.Incoming.TimeToDown!.Mean);
    }

    [Fact]
    public void DepletionRepairSamplesReadinessBeforeCooldownAndCanRepairAgainLater()
    {
        var fixture = Fixture(Buff("armor_break") with { Kind = "ready", Duration = 3 }, "armor_repair_on_depletion_percent", 0.5,
            ("armor_repair_on_depletion_percent", "0"), ("estimated_armor_repair_on_depletion", "character_armor * armor_repair_on_depletion_percent"),
            ("estimated_armor_on_kill", "10"), ("enemy_target_health", "5"), ("character_health", "100"));
        var result = Run(fixture, Options with { Duration = 5, DriveSimpleBuffs = true });
        Assert.Equal(4, result.Incoming!.ArmorBreaks);
        Assert.Equal(40, result.Incoming.ArmorRepaired);
        Assert.Equal(new double[] { 5, 5, 10, 10, 5, 5 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.ArmorRepaired).Select(e => e.Damage));
        Assert.Equal(new[] { DummyEventKind.EnemyShot, DummyEventKind.EnemyHit, DummyEventKind.ArmorBreak, DummyEventKind.ArmorRepaired,
            DummyEventKind.Shot, DummyEventKind.Kill, DummyEventKind.ArmorRepaired, DummyEventKind.TargetArrived }, result.FirstFightEvents.Where(e => e.Time == 1).Select(e => e.Kind));
    }

    [Fact]
    public void DepletionRepairWithoutDrivenCooldownsIsRefused()
    {
        // A fixed readiness assumption would restore armor on every break with no cooldown.
        var fixture = Fixture(changes: [("estimated_armor_repair_on_depletion", "5"), ("character_health", "100")]);
        Assert.Equal("incoming-repair-unsupported", Assert.Throws<SimulationException>(() => Run(fixture)).Code);
        Assert.NotNull(Run(fixture, Options with { DriveSimpleBuffs = true }).Incoming);
    }

    [Fact]
    public void ADowningHitReportsNoDepletionRepair()
    {
        var fixture = Fixture(Buff("armor_break") with { Kind = "ready", Duration = 60 }, "estimated_armor_repair_on_depletion", 10,
            ("estimated_armor_repair_on_depletion", "0"), ("enemy_target_hit_damage", "100"));
        var result = Run(fixture, Options with { DriveSimpleBuffs = true });
        Assert.Equal(1, result.Incoming!.DownedFraction);
        Assert.Equal(1, result.Incoming.ArmorBreaks);
        Assert.Equal(0, result.Incoming.ArmorRepaired);
        Assert.DoesNotContain(result.FirstFightEvents, e => e.Kind == DummyEventKind.ArmorRepaired);
    }

    [Fact]
    public void RepairSummaryModifiersAndInvalidAmountsRemainRefused()
    {
        var summary = Fixture(Buff("damaged"), "estimated_kill_repair_per_second", changes: [("estimated_kill_repair_per_second", "0")]);
        Assert.Equal("incoming-repair-unsupported", Assert.Throws<SimulationException>(() => Run(summary, Options with { DriveSimpleBuffs = true })).Code);
        Assert.Equal("incoming-repair-invalid", Assert.Throws<SimulationException>(() => Run(Fixture(changes: [("estimated_health_on_kill", "-1")]))).Code);
    }

    [Theory]
    [InlineData("armor_regeneration")]
    [InlineData("armor_regeneration_percent")]
    [InlineData("armor_on_kill")]
    [InlineData("armor_repair_per_kill_percent")]
    [InlineData("health_on_kill")]
    [InlineData("armor_repair_on_depletion_percent")]
    public void RawRecoveryWithoutItsResolvedAmountIsRefused(string source)
    {
        Assert.Equal("incoming-repair-invalid", Assert.Throws<SimulationException>(() => Run(Fixture(changes: [(source, "1")]))).Code);
    }

    [Fact]
    public void CommittedUnbreakableRepairsArmorOnARealDepletion()
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr").WithEquipment("chest", "chest_n_5_11_tactical_chest")
            .WithAttributeRoll("chest", "core_1", new("armor_gear_core", 170000)).WithTalentSelection("chest", "talent", "gear_talent_unbreakable");
        var context = Context.Add("enemy_target", "1").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "100").Add("enemy_firing_share", "100");
        var result = Run((pack, build), Options with { Duration = 5, DriveSimpleBuffs = true }, context);
        Assert.True(result.Incoming!.ArmorRepaired > 0);
        Assert.True(result.Incoming.ArmorBreaks > 0);
        Assert.Contains(result.FirstFightEvents, e => e.Kind == DummyEventKind.ArmorRepaired);
    }

    [Fact]
    public void IncomingRejectsUnsupportedModes()
    {
        var fixture = Fixture();
        Assert.Equal("incoming-mode-unsupported", Assert.Throws<SimulationException>(() => Run(fixture, Options with { Mode = SimulationMode.Average })).Code);
        Assert.Equal("incoming-rotation-unsupported", Assert.Throws<SimulationException>(() => Run(fixture, Options with { Rotation = new("other") })).Code);
        Assert.Equal("incoming-mode-unsupported", Assert.Throws<SimulationException>(() => DummyWeaponSimulator.Run(fixture.Pack, fixture.Build, Context, Options,
            TestContext.Current.CancellationToken)).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommittedVigilanceIsDisabledByActualIncomingHits(bool regeneration)
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "packs", "division-2"))
            .ToDictionary(path => Path.GetFileName(path), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
        var pack = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(files).Compiled);
        var build = BuildSnapshot.Create(pack.Pack).WithEquipment("primary_weapon", "primary_weapon_acr").WithEquipment("backpack", "backpack_n_5_11_tactical_backpack")
            .WithTalentSelection("backpack", "talent", "gear_talent_vigilance");
        if (regeneration)
        {
            build = build.WithEquipment("knees", "knees_emperor_s_guard")
                .WithAttributeRoll("knees", "minor_1", new("armor_regeneration_emperors_guard", 0.01));
        }
        var context = Context.Add("enemy_target", "1").Add("enemy_difficulty", "1").Add("enemy_hit_rate", "100").Add("enemy_firing_share", "100")
            .Add("practical_hit_rate", "100").Add("practical_headshot_share", "0");
        var result = Run((pack, build), Options with { Duration = 3, DriveSimpleBuffs = true }, context);
        Assert.True(result.Incoming!.Hits > 0);
        var shots = result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Take(2).ToArray();
        Assert.Equal(2, shots.Length);
        Assert.True(shots[1].Damage < shots[0].Damage);
        if (regeneration) { Assert.True(result.Incoming.ArmorRepaired > 0); }
    }

    // A talent that adds its key's value to each shot, with a count rule on that key. Shots are 10, a second apart, and a shot's
    // damage stops at what the target has left, so a 20-health enemy shows the count on its first shot and the rest on its second.
    private static (CompiledPackExpressions Pack, BuildSnapshot Build) CountFixture(SimTriggerDefinition rule, params (string Id, string Formula)[] changes)
    {
        var (original, _) = Fixture(changes: [("character_armor", "0"), ("character_health", "100000"), .. changes]);
        var rules = original.Pack with
        {
            NumericContextInputs = [.. original.Pack.NumericContextInputs, new() { ID = "nearby", Name = "Enemies nearby", Min = 0, Max = 5, Step = 1, Default = 2 }],
            Talents = [original.Pack.Talents[0] with { Modifiers = [new() { Stat = "buff_damage", Op = ModifierOps.Flat, Value = new("context_value('nearby')") }] }],
            Simulation = new() { Triggers = [rule] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        return (pack, BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test"));
    }

    private static readonly SimTriggerDefinition Counted = new() { Talent = "test", Key = "nearby", Kind = "count", Max = 5 };
    private static double[] ShotDamage(DummyFightResult result) => [.. result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot).Select(e => e.Damage)];
    private static DummyFightOptions Shrinking(double duration, int enemies, int total) => Group(duration, enemies) with { ClearKills = total, EnemiesShrink = true };

    // Two kinds of enemy, burning 100 a tick every half second for 30 s from the first hit after arming, with their own health.
    private static (CompiledPackExpressions Pack, BuildSnapshot Build) BurningProfiles(string first, string second, params (string Id, string Formula)[] changes)
    {
        var (original, _) = CadenceFixture("0", [("enemy_target_health", $"context_value('enemy_target') == 1 ? {first} : {second}"), ("burn_tick", "100"), ("character_health", "100000"), .. changes]);
        var rules = original.Pack with
        {
            Simulation = original.Pack.Simulation with
            {
                Triggers = [new() { Talent = "test", Key = "buff", Kind = "status", Status = "burning", On = "hit", Duration = 30 }],
                StatusDamage = [new() { Status = "burning", Tick = "burn_tick", Period = .5 }]
            }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        return (pack, BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test"));
    }

    private static DummyFightOptions Mixed(double duration, double focus) =>
        Options with { Duration = duration, DriveSimpleBuffs = true, EnemiesAtOnce = 2, TargetSequence = [1, 2], FocusSeconds = focus };

    [Fact]
    public void AnArrivalThatsDueComesBeforeATick()
    {
        // The player leaves the first at 2 s, and kills the second with the last round at 3 s. The reload runs to 5 s, past the arrival
        // due at 4 s. A tick kills the first at 4.5 s: by then the player has reached it, so the kill brings a new arrival, at 5.5 s.
        var fixture = BurningProfiles("720", "10", ("equipped_weapon_magazine_size", "3"));
        var result = Run(fixture, Mixed(7, 1.5), delay: 1);
        Assert.Equal(new[] { 3, 4.5 }, Times(result, DummyEventKind.Kill));
        Assert.Contains(5.5, Times(result, DummyEventKind.TargetArrived));
    }

    [Fact]
    public void AnArrivalThatWontComeLeavesNoTrace()
    {
        // Both enemies burn out at 5.5 s while the player is moving back to the first, due at 6 s, and they were the last.
        var fixture = BurningProfiles("920", "520");
        var result = Run(fixture, Mixed(8, 1.5) with { ClearKills = 2, EnemiesShrink = true }, delay: 1);
        Assert.Equal(new[] { 5.5, 5.5 }, Times(result, DummyEventKind.Kill));
        Assert.Equal(new double[] { 3 }, Times(result, DummyEventKind.TargetArrived));
    }

    [Fact]
    public void AMoveClearsAHolsteredWeaponsStacks()
    {
        // Stacks earned with the second weapon on one enemy clear on a move made with the first weapon in hand.
        var (original, _) = Fixture(changes: [("character_armor", "0"), ("character_health", "100000"), ("equipped_weapon_magazine_size", "2")]);
        var rules = original.Pack with
        {
            Slots = [.. original.Pack.Slots, new() { ID = "secondary_weapon", Name = "Second", Weapon = true }],
            Items = [original.Pack.Items[0] with { TalentSlots = [] }, new() { ID = "other", Name = "Other", Slot = "secondary_weapon", TalentSlots = original.Pack.Items[0].TalentSlots }],
            NumericContextInputs = [.. original.Pack.NumericContextInputs, new() { ID = "nearby", Name = "Stacks", Min = 0, Max = 5, Step = 1 }],
            Talents = [original.Pack.Talents[0] with { Modifiers = [new() { Stat = "buff_damage", Op = ModifierOps.Flat, Value = new("context_value('nearby')") }] }],
            Simulation = new() { Triggers = [new() { Talent = "test", Key = "nearby", Kind = "stacks", On = "hit", Max = 5, ResetOn = "target_changed" }] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithEquipment("secondary_weapon", "other").WithTalentSelection("secondary_weapon", "talent", "test");
        var result = Run((pack, build), Group(16, 2) with { Rotation = new("secondary_weapon"), FocusSeconds = 8.5 });
        // The second weapon fires at 3 and 4 s and again from 13 s, and the move comes at 9 s, with the first weapon in hand.
        Assert.Equal(new double[] { 10, 11, 10 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot && e.WeaponSlot == "secondary_weapon").Select(e => e.Damage).Take(3));
        // It clears a holstered weapon's stacks, but a holstered weapon doesn't gain on it.
        var gaining = rules with { Simulation = new() { Triggers = [new() { Talent = "test", Key = "nearby", Kind = "stacks", On = "target_changed", Max = 5 }] } };
        var moved = Run((Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(gaining).Compiled), build), Group(16, 2) with { Rotation = new("secondary_weapon"), FocusSeconds = 8.5 });
        Assert.Equal(10, moved.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot && e.WeaponSlot == "secondary_weapon").Select(e => e.Damage).ElementAt(2));
    }

    [Fact]
    public void AnEnemyYouLeftStaysShocked()
    {
        // ADR 0011: shock stops the enemy it's on. The player shocks the first enemy, then moves to the second at 2 s, so the first's
        // attempts stay blocked and the second's land until it's shocked too.
        var (pack, build) = ShockFixture(1, duration: 100);
        var result = Run((pack, build), Options with { Duration = 3, DriveSimpleBuffs = true, EnemiesAtOnce = 2, FocusSeconds = 2 }, delay: 5);
        Assert.Equal(new[] { 2.25, 2.75 }, Times(result, DummyEventKind.EnemyShotBlocked).Where(time => time >= 2));
        Assert.Equal(new[] { 2d, 2.5 }, Times(result, DummyEventKind.EnemyHit).Where(time => time >= 2));
    }

    [Fact]
    public void ACountReadsTheEnemiesInTheGroup()
    {
        var result = Run(CountFixture(Counted), Group(2.5, 3));
        Assert.All(ShotDamage(result), damage => Assert.Equal(13, damage));
        Assert.Contains(result.Limitations, note => note.StartsWith("Talents that count enemies read those alive in the group", StringComparison.Ordinal));
        // The rule's max clamps it.
        Assert.All(ShotDamage(Run(CountFixture(Counted with { Max = 2 }), Group(2.5, 3))), damage => Assert.Equal(12, damage));
    }

    [Fact]
    public void ACountFallsAsAShrinkingGroupDies()
    {
        // Nobody replaces the dead. After the last, the shots stop.
        var result = Run(CountFixture(Counted, ("enemy_target_health", "20")), Shrinking(8.5, 3, 3));
        Assert.Equal(new double[] { 13, 7, 12, 8, 11, 9 }, ShotDamage(result));
        // With a reserve the group stays full until the reserve is gone.
        Assert.Equal(new double[] { 12, 8, 12, 8, 12, 8, 11, 9 }, ShotDamage(Run(CountFixture(Counted, ("enemy_target_health", "20")), Shrinking(10.5, 2, 4))));
        // A replacement keeps the count where it was.
        Assert.Equal(new double[] { 12, 8, 12, 8 }, ShotDamage(Run(CountFixture(Counted, ("enemy_target_health", "20")), Group(3.5, 2))));
    }

    [Fact]
    public void ACountDoesntNeedTheEnemiesToBeFiring()
    {
        // A group with no fire has no attempt clocks, but its enemies are still there.
        var result = Run(CountFixture(Counted, ("enemy_target_health", "20"), ("enemy_target_incoming_dps", "0")), Shrinking(8.5, 3, 3));
        Assert.Equal(new double[] { 13, 7, 12, 8, 11, 9 }, ShotDamage(result));
    }

    [Fact]
    public void AnEventCountHoldsUntilTheEventComesRound()
    {
        // Nothing is read until the first reload ends, here after two shots and one kill, so two are alive. The kill after it leaves
        // the reading alone: the fourth shot still adds 2, though only one is left.
        var fixture = CountFixture(Counted with { On = "reload_end" }, ("enemy_target_health", "12"), ("equipped_weapon_magazine_size", "2"));
        Assert.Equal(new double[] { 10, 2, 12, 12 }, ShotDamage(Run(fixture, Shrinking(8.5, 3, 3))).Take(4));
        // A reload ending while the next enemy is on its way is no killing blow, so the next kill leaves that reading alone too.
        var waiting = Run(fixture, Shrinking(20, 3, 3), delay: 5);
        Assert.Equal(new double[] { 10, 2, 12, 12 }, ShotDamage(waiting).Take(4));
    }

    [Fact]
    public void AnEventCountWaitsForItsOwnWeapon()
    {
        // The talent is on the second weapon, so the first weapon's ten shots read nothing, and the second's first shot adds none.
        var (original, _) = Fixture(changes: [("character_armor", "0"), ("character_health", "100000")]);
        var rules = original.Pack with
        {
            Slots = [.. original.Pack.Slots, new() { ID = "secondary_weapon", Name = "Second", Weapon = true }],
            Items = [original.Pack.Items[0] with { TalentSlots = [] }, new() { ID = "other", Name = "Other", Slot = "secondary_weapon", TalentSlots = original.Pack.Items[0].TalentSlots }],
            NumericContextInputs = [.. original.Pack.NumericContextInputs, new() { ID = "nearby", Name = "Enemies nearby", Min = 0, Max = 5, Step = 1 }],
            Talents = [original.Pack.Talents[0] with { Modifiers = [new() { Stat = "buff_damage", Op = ModifierOps.Flat, Value = new("context_value('nearby')") }] }],
            Simulation = new() { Triggers = [Counted with { On = "shot" }] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithEquipment("secondary_weapon", "other").WithTalentSelection("secondary_weapon", "talent", "test");
        var result = Run((pack, build), Group(16, 2) with { Rotation = new("secondary_weapon") });
        Assert.Equal(new double[] { 10, 12, 12 }, result.FirstFightEvents.Where(e => e.Kind == DummyEventKind.Shot && e.WeaponSlot == "secondary_weapon").Select(e => e.Damage).Take(3));
    }

    [Theory]
    [InlineData("kill")]
    [InlineData("hit")]
    public void AReadingOnAKillSeesTheGroupAfterIt(string on)
    {
        // The second shot kills, and what it reads is the two left, not the three it found.
        var fixture = CountFixture(Counted with { On = on }, ("enemy_target_health", "20"));
        Assert.Equal(new double[] { 10, 10, 12, 8 }, ShotDamage(Run(fixture, Shrinking(8.5, 3, 3))).Take(4));
    }

    [Fact]
    public void AReadingAfterAKillCountsTheEnemyThatJoined()
    {
        // Two at 10 m face the player, with a 40 m enemy behind them. The first dies and the 40 m enemy joins, so one is within 15 m.
        var fixture = CountFixture(Counted with { On = "kill", Within = 15 }, ("enemy_target_health", "20"));
        Assert.Equal(new double[] { 10, 10, 11, 9 }, ShotDamage(Run(fixture, Group(3.5, 2) with { TargetDistances = [10, 10, 40] })).Take(4));
    }

    [Fact]
    public void ACountOnARecoveryInputIsRefused()
    {
        var (original, _) = Fixture();
        var rules = original.Pack with
        {
            NumericContextInputs = [.. original.Pack.NumericContextInputs, new() { ID = "makeshift_qualifying_damage", Name = "Makeshift", Min = 0, Max = 5, Step = 1 }],
            Simulation = new() { Triggers = [Counted with { Key = "makeshift_qualifying_damage" }] }
        };
        var pack = Assert.IsType<CompiledPackExpressions>(PackExpressionCompiler.Compile(rules).Compiled);
        var build = BuildSnapshot.Create(rules).WithEquipment("primary_weapon", "gun").WithTalentSelection("primary_weapon", "talent", "test");
        Assert.Equal("trigger-key-conflict", Assert.Throws<SimulationException>(() => Run((pack, build), Group(2.5, 2))).Code);
    }

    [Fact]
    public void ACountReadsEachEnemysDistance()
    {
        // Enemies stand at 10 m and 40 m in turn, so two at once have one within 15 m, or two within 50 m.
        Assert.All(ShotDamage(Run(CountFixture(Counted with { Within = 15 }), Group(2.5, 2) with { TargetDistances = [10, 40] })), damage => Assert.Equal(11, damage));
        Assert.All(ShotDamage(Run(CountFixture(Counted with { Within = 50 }), Group(2.5, 2) with { TargetDistances = [10, 40] })), damage => Assert.Equal(12, damage));
        // Without target distances they share the fight's.
        Assert.All(ShotDamage(Run(CountFixture(Counted with { Within = 15 }), Group(2.5, 3) with { Distance = 10 })), damage => Assert.Equal(13, damage));
        Assert.All(ShotDamage(Run(CountFixture(Counted with { Within = 15 }), Group(2.5, 3) with { Distance = 20 })), damage => Assert.Equal(10, damage));
        // A kill can bring a different distance: three at 10, 40 and 10 m, then 40, 10 and 40 m.
        var moving = Run(CountFixture(Counted with { Within = 15 }, ("enemy_target_health", "20")), Group(5.5, 3) with { TargetDistances = [10, 40] });
        Assert.Equal(new double[] { 12, 8, 11, 9, 12, 8 }, ShotDamage(moving));
        // With no distance a range can't be read.
        Assert.Equal("distance-required", Assert.Throws<SimulationException>(() => Run(CountFixture(Counted with { Within = 15 }), Group(2.5, 2))).Code);
    }

    [Fact]
    public void AtLeastTurnsACountIntoACondition()
    {
        // Fixture's buff adds 10 to a shot while three or more are around, so only the first enemy takes it.
        var rule = new SimTriggerDefinition { Talent = "test", Key = "buff", Kind = "count", AtLeast = 3 };
        var result = Run(Fixture(rule, changes: [("character_armor", "0"), ("character_health", "100000"), ("enemy_target_health", "25")]), Shrinking(8.5, 3, 3));
        Assert.Equal(new double[] { 20, 5, 10, 10, 5, 10, 10, 5 }, ShotDamage(result));
    }

    [Fact]
    public void OneEnemyAtATimeKeepsTheAssumption()
    {
        // The input's default is 2, and the fight has no group to count.
        var result = Run(CountFixture(Counted), Options with { Duration = 2.5, DriveSimpleBuffs = true });
        Assert.All(ShotDamage(result), damage => Assert.Equal(12, damage));
        Assert.Contains("With one enemy at a time these stay as set: nearby. Fights count enemies with several at once", result.Limitations);
        Assert.DoesNotContain(result.Limitations, note => note.StartsWith("Talents that count enemies", StringComparison.Ordinal));
    }

    [Fact]
    public void ACountRuleNeedsAnInputThatCanHoldIt()
    {
        Assert.Equal("trigger-pack-mismatch", Assert.Throws<SimulationException>(() => Run(CountFixture(Counted with { Max = 6 }), Group(2.5, 2))).Code);
    }

    [Fact]
    public void ACountOnAnEventTheFightDoesntHaveIsRefused()
    {
        Assert.Equal("trigger-event-unsupported", Assert.Throws<SimulationException>(() => Run(CountFixture(Counted with { On = "swap" }), Group(2.5, 2))).Code);
    }
}
