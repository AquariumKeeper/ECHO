using System.Collections.Immutable;
using System.Text;
using CharacterPlanner.Core.Packs;
using V1 = CharacterPlanner.Legacy.PackV1;

namespace CharacterPlanner.Legacy.Tests;

public sealed class PackV1ConverterTests
{
    private static readonly V1.GamePack DivisionV1 = LegacyPacks.Read(LegacyPacks.Bytes(Path.Combine("packs", "division-2.json")));
    private static readonly V1.PackConversion Division = V1.PackV1Converter.Convert(DivisionV1);

    [Fact]
    public void DivisionConvertsWithoutProblems()
    {
        Assert.True(Division.Problems.IsEmpty, string.Join(Environment.NewLine, Division.Problems));
        var pack = Assert.IsType<GamePack>(Division.Pack);
        Assert.Equal(40, pack.Level);
        Assert.Equal(1022, pack.Items.Length);
        Assert.Equal(522, pack.Items.Count(item => !item.Weapon.IsEmpty));
        Assert.Equal(5197, pack.Items.Sum(item => item.Weapon.Length));
        Assert.Equal(["primary_weapon", "secondary_weapon", "sidearm"], pack.Slots.Where(slot => slot.Weapon).Select(slot => slot.ID));
        Assert.Equal(DivisionV1.Slots.Select(slot => slot.ID), pack.LegacyShareOrder.Slots);
        Assert.Empty(pack.LegacyShareOrder.Stats);
        Assert.Empty(pack.LegacyShareOrder.Scenarios);
        Assert.Empty(pack.LegacyShareOrder.PassiveSkills);
        Assert.Equal(["weapons", "prototype", "skill-mods", "party", "talent-copy", "simulation"], pack.Capabilities);
        Assert.Empty(pack.RenamedIds);
        Assert.Equal(111, pack.Selectors.Length);
        Assert.Equal(pack.Selectors.Select(selector => selector.ID).Order(StringComparer.Ordinal), pack.Selectors.Select(selector => selector.ID));
        Assert.All(pack.Selectors, selector =>
        {
            Assert.Equal(selector.ID, selector.Name);
            Assert.Equal("", selector.Section);
            Assert.NotEmpty(selector.Values);
            Assert.Equal(selector.Values.Order(StringComparer.Ordinal), selector.Values);
        });
        Assert.DoesNotContain(pack.Selectors, selector => selector.ID == GamePack.WeaponSlotSelector);
        Assert.DoesNotContain(AllModifiers(pack), modifier => modifier.RequiresContext?.ContainsKey(GamePack.WeaponSlotSelector) == true);
        // Every gate key is a declared selector, and every selector is read by some gate.
        var used = AllModifiers(pack).SelectMany(modifier => modifier.RequiresContext ?? ImmutableDictionary<string, string>.Empty).ToArray();
        Assert.Equal(pack.Selectors.Select(selector => selector.ID).ToHashSet(), used.Select(pair => pair.Key).ToHashSet());
        Assert.All(used, pair => Assert.Contains(pair.Value, pack.Selectors.Single(selector => selector.ID == pair.Key).Values));
    }

    [Fact]
    public void PinnedConversionReferenceMatchesTheFrozenV1Pack()
    {
        // The producer delivery is independent; this pinned reference keeps converter behavior
        // checked against the frozen v1 source without requiring producer bytes to match.
        var expected = PackWriter.Write(Division.Pack!, new() { Provenance = LegacyPacks.Bytes(Path.Combine("packs", "division-2.provenance.md")) });
        var committed = LegacyPacks.V2Files(Path.Combine("packs", "conversion-reference"));
        Assert.Equal(expected.Keys, committed.Keys.Order(StringComparer.Ordinal));
        Assert.All(expected, pair => Assert.True(pair.Value.Span.SequenceEqual(committed[pair.Key].Span), $"{pair.Key} differs from the converter output"));
    }

    [Fact]
    public void DivisionPresentationKeepsEveryArtworkReference()
    {
        var presentation = Division.Pack!.Presentation;
        Assert.Equal(9, presentation.Icons["slots"].Count);
        Assert.Equal(275, presentation.Icons["talents"].Count);
        Assert.Equal(43, presentation.Icons["skills"].Count);
        Assert.Equal(6, presentation.Icons["specializations"].Count);
        Assert.Equal(236, presentation.Icons["nodes"].Count);
        Assert.Equal(3, presentation.SpecializationImages.Count);
        Assert.Equal(DivisionV1.Items.Count(item => item.Artwork is { } art && (art.Image.Length > 0 || art.Icon.Length > 0)), presentation.ItemArt.Count);
        var assets = presentation.ArtworkAssets.Select(asset => asset.ID).ToHashSet(StringComparer.Ordinal);
        Assert.All(presentation.Icons.Values.SelectMany(group => group.Values), asset => Assert.Contains(asset, assets));
        Assert.All(presentation.ItemArt.Values.SelectMany(art => new[] { art.Image, art.Icon }).Where(asset => asset.Length > 0), asset => Assert.Contains(asset, assets));
    }

    /// <summary>
    /// Round trip per item: the same ids, slot, tags and choices, and the same effective modifiers once
    /// the active-weapon gate is reapplied. For a weapon, v1's own-slot gates hold whenever its effects
    /// apply at all, so its effective modifiers are v1's with that gate removed, in authored order.
    /// </summary>
    [Fact]
    public void EveryDivisionItemRoundTrips()
    {
        var pack = Division.Pack!;
        Assert.Equal(DivisionV1.Items.Select(item => item.ID), pack.Items.Select(item => item.ID));
        var weaponSlots = pack.Slots.Where(slot => slot.Weapon).Select(slot => slot.ID).ToHashSet(StringComparer.Ordinal);
        foreach (var (source, item) in DivisionV1.Items.Zip(pack.Items))
        {
            Assert.Equal(source.Name, item.Name);
            Assert.Equal(source.Description, item.Description);
            Assert.Equal(source.Tags, item.Tags);
            Assert.Equal(Assert.Single(source.Occupies), item.Slot);
            var weapon = weaponSlots.Contains(item.Slot);
            Assert.Equal(weapon, source.EffectsRequireContext is { Count: 1 } gate && gate[GamePack.WeaponSlotSelector] == item.Slot);
            Assert.Equal(weapon, !item.Weapon.IsEmpty);
            Assert.Equal(source.AttributeSlots.Select(slot => (slot.ID, slot.Name, string.Join(";", slot.Choices.Select(choice => (choice.AttributeID, choice.Min, choice.Max, choice.Step))))),
                item.AttributeSlots.Select(slot => (slot.ID, slot.Name, string.Join(";", slot.Choices.Select(choice => (choice.AttributeID, choice.Min, choice.Max, choice.Step))))));
            Assert.Equal(source.ModSlots.Select(slot => (slot.ID, slot.Name, string.Join(";", slot.Choices))), item.ModSlots.Select(slot => (slot.ID, slot.Name, string.Join(";", slot.Choices))));
            Assert.Equal(source.TalentSlots.Select(slot => (slot.ID, slot.Name, string.Join(";", slot.Choices))), item.TalentSlots.Select(slot => (slot.ID, slot.Name, string.Join(";", slot.Choices))));
            var ownGate = weapon ? item.Slot : null;
            Assert.Equal(source.Modifiers.Select(modifier => Effective(modifier, ownGate)),
                item.Weapon.Select(stat => Key(stat.Stat, ModifierOps.Override, new ModifierValue(stat.Value), "", null, false)).Concat(item.Modifiers.Select(Key)));
            Assert.Equal(source.Upgrade is null, item.Upgrade is null);
            if (source.Upgrade is { } upgrade)
            {
                Assert.Equal((upgrade.Name, upgrade.Description, upgrade.MaxRank), (item.Upgrade!.Name, item.Upgrade.Description, item.Upgrade.MaxRank));
                Assert.Equal(upgrade.ModifiersPerRank.Select(modifier => Effective(modifier, ownGate)), item.Upgrade.ModifiersPerRank.Select(Key));
            }
        }
    }

    private static string Effective(V1.StatModifier modifier, string? ownWeaponSlot)
    {
        var gate = modifier.RequiresContext;
        if (ownWeaponSlot is not null && gate is not null && gate.GetValueOrDefault(GamePack.WeaponSlotSelector) == ownWeaponSlot) { gate = gate.Remove(GamePack.WeaponSlotSelector); }
        Assert.Equal("", modifier.Bucket);
        return Key(modifier.Stat, modifier.Op, modifier.Value, modifier.When, gate, modifier.WhileHolstered);
    }

    private static string Key(StatModifier modifier) => Key(modifier.Stat, modifier.Op, modifier.Value, modifier.When, modifier.RequiresContext, modifier.WhileHolstered);
    private static string Key(string stat, string op, ModifierValue value, string when, ImmutableDictionary<string, string>? gate, bool holstered) =>
        $"{stat}|{op}|{value.Formula ?? value.Number.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}|{when}|" +
        $"{string.Join(",", (gate ?? ImmutableDictionary<string, string>.Empty).OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value))}|{holstered}";

    private static IEnumerable<StatModifier> AllModifiers(GamePack pack) =>
        pack.Items.SelectMany(item => item.Modifiers.Concat(item.Upgrade?.ModifiersPerRank ?? []))
            .Concat(pack.Mods.SelectMany(mod => mod.Modifiers))
            .Concat(pack.Talents.SelectMany(talent => talent.Modifiers.Concat(talent.AllyEffects.SelectMany(effect => effect.Modifiers))))
            .Concat(pack.Sets.SelectMany(set => set.Bonuses.SelectMany(bonus => bonus.Modifiers)))
            .Concat(pack.AccountUpgrades.SelectMany(upgrade => upgrade.ModifiersPerRank))
            .Concat(pack.ActiveSkills.SelectMany(skill => skill.Modifiers))
            .Concat(pack.Specializations.SelectMany(specialization => specialization.Modifiers.Concat(specialization.Nodes.SelectMany(node => node.Modifiers))));

    private const string SmallJson = """
        {"id":"small","schemaVersion":1,"progression":{"startingLevel":7,"maxLevel":7},
         "stats":[{"id":"damage","kind":"derived","formula":"0"},{"id":"armor","kind":"derived","formula":"0"}],
         "attributes":[{"id":"attr","stat":"damage","op":"flat","min":0,"max":1,"step":0.5}],
         "slots":[{"id":"main","name":"Main","accepts":["weapon"],"icon":"main_icon"},{"id":"chest","name":"Chest","accepts":["gear"]}],
         "items":[
          {"id":"gun","name":"Gun","occupies":["main"],"tags":["weapon"],"effectsRequireContext":{"weapon_slot":"main"},
           "modifiers":[{"stat":"damage","op":"override","value":5,"requiresContext":{"weapon_slot":"main"}},
                        {"stat":"armor","op":"override","value":2,"requiresContext":{"weapon_slot":"main"}},
                        {"stat":"damage","op":"flat","value":1,"requiresContext":{"weapon_slot":"main","mode":"on"}}],
           "upgrade":{"name":"Expertise","maxRank":3,"modifiersPerRank":[{"stat":"damage","op":"flat","value":1,"requiresContext":{"weapon_slot":"main"}}]}},
          {"id":"vest","name":"Vest","occupies":["chest"],"tags":["gear"],"modifiers":[{"stat":"armor","op":"flat","value":3,"requiresContext":{"stance":"prone","mode":"off"}}]}],
         "talents":[{"id":"t","name":"T","modifiers":[{"stat":"damage","op":"mult","value":0.1,"whileHolstered":true}]}],
         "artwork":{"assets":[{"id":"main_icon","path":"assets/main.png"}]}}
        """;
    private static readonly V1.GamePack Small = LegacyPacks.Read(Encoding.UTF8.GetBytes(SmallJson));

    [Fact]
    public void WeaponOverridesBecomeTheWeaponBlockAndOwnSlotGatesAreDropped()
    {
        var pack = LegacyPacks.Convert(Small);
        Assert.Equal(7, pack.Level);
        Assert.Equal([true, false], pack.Slots.Select(slot => slot.Weapon));
        var gun = pack.Items[0];
        Assert.Equal([new WeaponBaseStat("damage", 5), new WeaponBaseStat("armor", 2)], gun.Weapon);
        var modifier = Assert.Single(gun.Modifiers);
        Assert.Equal(1, modifier.Value.Number);
        Assert.Equal("on", Assert.Single(modifier.RequiresContext!).Value);
        Assert.Null(Assert.Single(gun.Upgrade!.ModifiersPerRank).RequiresContext);
        Assert.Empty(pack.Items[1].Weapon);
        Assert.True(pack.Talents[0].Modifiers[0].WhileHolstered);
        Assert.Equal(["mode", "stance"], pack.Selectors.Select(selector => selector.ID));
        Assert.Equal(["off", "on"], pack.Selectors[0].Values);
        Assert.Equal(["weapons"], pack.Capabilities);
        Assert.Equal(["main", "chest"], pack.LegacyShareOrder.Slots);
        Assert.Equal("main_icon", pack.Presentation.Icons["slots"]["main"]);
        LegacyPacks.Compile(pack);
    }

    private static V1.GamePack WithItem(V1.GamePack pack, string id, Func<V1.ItemDefinition, V1.ItemDefinition> change) =>
        pack with { Items = pack.Items.Select(item => item.ID == id ? change(item) : item).ToImmutableArray() };

    private static readonly V1.StatModifier Flat = new() { Stat = "damage", Op = "flat", Value = new(1) };

    // PackConversionProblem has no code, so each rejection is pinned by its path plus a fragment of
    // the problem it states. Two pairs state the same problem by design: gear-gate and empty-gate are
    // both a gate v2 can't express on gear, and gate-on-other-slot and non-weapon-slot both leave the
    // gun outside any weapon slot, so its own-slot gate is one v2 can't express.
    public static readonly TheoryData<string, string, string> Rejections = new()
    {
        { "primary-stat", "stats.power.kind", @"only derived stats exist in v2" },
        { "derived-base", "stats.damage.base", @"base applies only to the removed primary stats" },
        { "level-range", "progression", @"v2 has one fixed level" },
        { "points", "progression", @"point allocation is removed" },
        { "scenarios", "scenarios", @"scenarios are removed" },
        { "passive-skills", "skills", @"passive skill nodes are removed" },
        { "requirements", "items.vest.requirements", @"item requirements are removed" },
        { "multi-slot", "items.vest.occupies", @"occupy exactly one slot, found 2" },
        { "tier", "items.vest.tier", @"item tiers are removed" },
        { "inc", "items.vest.modifiers[1].op", @"the inc operation is removed" },
        { "bucket", "items.vest.modifiers[1].bucket", @"stat buckets are removed" },
        { "attribute-op", "attributes.attr.op", @"always flat, found ""mult""" },
        { "attribute-step", "attributes.attr.step", @"require a step" },
        { "gear-gate", "items.vest.effectsRequireContext", @"the only item gate v2 can express is {weapon_slot: ""chest""}" },
        { "empty-gate", "items.vest.effectsRequireContext", @"the only item gate v2 can express is {weapon_slot: ""chest""}" },
        { "duplicate-item-artwork", "items", @"duplicate id ""vest""" },
        { "gate-on-other-slot", "items.gun.effectsRequireContext", @"the only item gate v2 can express is {weapon_slot: ""main""}" },
        { "ungated-weapon", "items.knife.effectsRequireContext", @"must be gated on being the active weapon" },
        { "non-weapon-slot", "items.gun.effectsRequireContext", @"the only item gate v2 can express is {weapon_slot: ""main""}" },
        { "modifier-gate-other-slot", "items.gun.modifiers[2].requiresContext", @"may require it to be active, found ""chest""" },
        { "gear-weapon-gate", "items.vest.modifiers[0].requiresContext", @"may require it to be active, found ""chest""" },
        { "talent-weapon-gate", "talents.t.modifiers[0].requiresContext", @"may require it to be active, found ""main""" },
        { "override-not-leading", "items.gun.modifiers[2]", @"must precede the item's other modifiers" },
        { "duplicate-base-stat", "items.gun.modifiers[1]", @"weapon base stat ""damage"" is overridden twice" },
        { "unknown-asset", "talents.t.icon", @"unknown artwork asset ""missing""" },
        { "weapon-context-key", "layout.weaponContextKey", @"found ""active_slot""" }
    };

    [Theory]
    [MemberData(nameof(Rejections))]
    public void UnconvertibleConstructsAreReportedWithoutAPartialPack(string name, string path, string problem)
    {
        var pack = name switch
        {
            "primary-stat" => Small with { Stats = Small.Stats.Add(new() { ID = "power", Kind = "primary", Base = 10 }) },
            "derived-base" => Small with { Stats = Small.Stats.SetItem(0, Small.Stats[0] with { Base = 1 }) },
            "level-range" => Small with { Progression = new() { StartingLevel = 1, MaxLevel = 7 } },
            "points" => Small with { Progression = Small.Progression with { PointsPerLevel = 1 } },
            "scenarios" => Small with { Scenarios = [new() { ID = "buffed" }] },
            "passive-skills" => Small with { Skills = [new() { ID = "node" }] },
            "requirements" => WithItem(Small, "vest", item => item with { Requirements = ImmutableDictionary<string, double>.Empty.Add("damage", 1) }),
            "multi-slot" => WithItem(Small, "vest", item => item with { Occupies = ["chest", "main"] }),
            "tier" => WithItem(Small, "vest", item => item with { Tier = 1 }),
            "inc" => WithItem(Small, "vest", item => item with { Modifiers = item.Modifiers.Add(Flat with { Op = "inc" }) }),
            "bucket" => WithItem(Small, "vest", item => item with { Modifiers = item.Modifiers.Add(Flat with { Bucket = "weapon" }) }),
            "attribute-op" => Small with { Attributes = [Small.Attributes[0] with { Op = "mult" }] },
            "attribute-step" => Small with { Attributes = [Small.Attributes[0] with { Step = null }] },
            "gear-gate" => WithItem(Small, "vest", item => item with { EffectsRequireContext = ImmutableDictionary<string, string>.Empty.Add("mode", "on") }),
            "empty-gate" => WithItem(Small, "vest", item => item with { EffectsRequireContext = ImmutableDictionary<string, string>.Empty }),
            // Two items with one id and different artwork once made the converter throw.
            "duplicate-item-artwork" => Small with { Items = [.. Small.Items, Small.Items.Single(item => item.ID == "vest") with { Artwork = new() { Image = "other" } }] },
            "gate-on-other-slot" => WithItem(Small, "gun", item => item with { EffectsRequireContext = ImmutableDictionary<string, string>.Empty.Add("weapon_slot", "chest") }),
            "ungated-weapon" => Small with { Items = Small.Items.Add(new() { ID = "knife", Occupies = ["main"] }) },
            "non-weapon-slot" => Small with { Slots = Small.Slots.SetItem(0, Small.Slots[0] with { Accepts = ["gear"] }) },
            "modifier-gate-other-slot" => WithItem(Small, "gun", item => item with
            { Modifiers = item.Modifiers.SetItem(2, item.Modifiers[2] with { RequiresContext = ImmutableDictionary<string, string>.Empty.Add("weapon_slot", "chest") }) }),
            "gear-weapon-gate" => WithItem(Small, "vest", item => item with
            { Modifiers = [item.Modifiers[0] with { RequiresContext = ImmutableDictionary<string, string>.Empty.Add("weapon_slot", "chest") }] }),
            "talent-weapon-gate" => Small with { Talents = [Small.Talents[0] with { Modifiers = [Flat with { RequiresContext = ImmutableDictionary<string, string>.Empty.Add("weapon_slot", "main") }] }] },
            "override-not-leading" => WithItem(Small, "gun", item => item with { Modifiers = [item.Modifiers[0], Flat, item.Modifiers[1]] }),
            "duplicate-base-stat" => WithItem(Small, "gun", item => item with { Modifiers = [item.Modifiers[0], item.Modifiers[0]] }),
            "unknown-asset" => Small with { Talents = [Small.Talents[0] with { Icon = "missing" }] },
            "weapon-context-key" => Small with { Layout = new() { WeaponContextKey = "active_slot" } },
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };
        var conversion = V1.PackV1Converter.Convert(pack);
        Assert.Null(conversion.Pack);
        var reported = Assert.Single(conversion.Problems, value => value.Path == path);
        Assert.Contains(problem, reported.Message, StringComparison.Ordinal);
    }
}
