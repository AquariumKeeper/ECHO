using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// Configured equipment talents (F10): gear talent hosts, gated and stacked assumptions, amplifiers and their
/// composition, ported from Go's gear_talents_g52_test.go, talents_g63_test.go and talents_g398_test.go.
/// </summary>
public sealed class GearTalentTests
{
    private static readonly Dictionary<string, (string[] Ids, string Slot, string Stat)> Contracts = new(StringComparer.Ordinal)
    {
        ["Opportunistic"] = ([], "backpack", "weapon_damage_multiplier"),
        ["Perfect Opportunistic"] = (["backpack_the_setup"], "backpack", "weapon_damage_multiplier"),
        ["Energize"] = ([], "backpack", "skill_tier"),
        ["Perfect Energize"] = (["backpack_axel"], "backpack", "skill_tier"),
        ["Vicious Cycle"] = (["mask_catharsis"], "mask", "weapon_damage"),
        ["Weakest Link"] = (["gloves_overdogs"], "gloves", "weapon_damage_multiplier"),
        ["Over the top"] = (["gloves_bloody_knuckles"], "gloves", "total_weapon_damage"),
        ["Decoy King"] = (["backpack_sarru"], "backpack", "weapon_damage_multiplier"),
    };

    private static readonly string[] WeaponSlots = ["primary_weapon", "secondary_weapon", "sidearm"];
    private static readonly string[] GearSlots = ["mask", "chest", "holster", "backpack", "gloves", "knees"];
    private static readonly string[] CardStats = ["weapon_rpm", "weapon_magazine_size", "weapon_reload_time", "weapon_optimal_range"];

    /// <summary>
    /// G52: each gear talent is offered only in its contract slot and, where named, on exactly its approved hosts;
    /// generic Opportunistic and Energize are backpack-only on at least two hosts; Throttle Control stays set-linked.
    /// </summary>
    [Fact]
    public void G52GearTalentHostContracts()
    {
        foreach (var (name, contract) in Contracts)
        {
            var talentId = TalentId(name);
            var legal = LegalSlots(talentId);
            Assert.True(legal.SequenceEqual([contract.Slot]), $"{name} legal gear slots = [{string.Join(", ", legal)}], want [{contract.Slot}]");
            foreach (var hostId in contract.Ids)
            {
                Assert.True(Compiled.Pack.Items.Any(item => item.ID == hostId && item.Slot == contract.Slot && Offers(item, talentId)), $"{name} missing approved host {hostId}");
            }
            if (contract.Ids.Length > 0)
            {
                var actual = Compiled.Pack.Items.Where(item => Offers(item, talentId)).Select(item => item.ID).Order(StringComparer.Ordinal).ToArray();
                var want = contract.Ids.Order(StringComparer.Ordinal).ToArray();
                Assert.True(actual.SequenceEqual(want), $"{name} hosts = [{string.Join(", ", actual)}], want exactly [{string.Join(", ", want)}]");
            }
        }
        foreach (var name in new[] { "Opportunistic", "Energize" })
        {
            var talentId = TalentId(name);
            var hosts = Compiled.Pack.Items.Where(item => Offers(item, talentId)).ToArray();
            foreach (var item in hosts)
            {
                Assert.True(item.Slot == "backpack", $"generic {name} is available outside backpack on {item.ID}");
            }
            Assert.True(hosts.Length >= 2, $"generic {name} has only {hosts.Length} backpack hosts");
        }
        var throttle = Compiled.Pack.Talents.FirstOrDefault(talent => talent.Name == "Throttle Control");
        Assert.True(throttle is not null, "missing Throttle Control set-linked talent");
        Assert.True(throttle.Modifiers.IsDefaultOrEmpty, $"Throttle Control has {throttle.Modifiers.Length} direct modifiers; it should remain set-linked");
        Assert.True(throttle.ModeledBySets.Contains("gear_set_tipping_scales"), $"Throttle Control modeledBySets = [{string.Join(", ", throttle.ModeledBySets)}]");
    }

    /// <summary>G52: gear talent assumptions apply with or without an active weapon, with talent provenance.</summary>
    [Fact]
    public void G52GearAssumptionsDoNotRequireWeaponOwnerSelection()
    {
        var cases = new (string Name, string Key, string Value, string Stat, double Want)[]
        {
            ("Opportunistic", "opportunistic_target_active", "true", "weapon_damage_multiplier", .10),
            ("Perfect Opportunistic", "opportunistic_target_active", "true", "weapon_damage_multiplier", .15),
            ("Energize", "energize_skill_tier_active", "true", "skill_overcharge", 1),
            ("Perfect Energize", "energize_skill_tier_active", "true", "skill_overcharge", 1),
            ("Weakest Link", "weakest_link_target_active", "true", "weapon_damage_multiplier", .30),
            ("Over the top", "seeing_red_active", "true", "total_weapon_damage", .25),
            ("Decoy King", "decoy_king_amplification_percent", "25", "weapon_damage_multiplier", .25),
        };
        foreach (var tc in cases)
        {
            var b = G52Build(tc.Name);
            var baselineBuild = WithoutTalent(b, tc.Name);
            var baseline = Resolve(baselineBuild, Division.Weapon("primary_weapon"));
            var active = Resolve(b, Context((GamePack.WeaponSlotSelector, "primary_weapon"), (tc.Key, tc.Value)));
            CloseTo(Delta(active, baseline, tc.Stat), tc.Want, 1e-12);
            ExpectContribution(active, tc.Stat, TalentId(tc.Name), tc.Want);
            var withoutWeapon = Resolve(b, Context((tc.Key, tc.Value)));
            var withoutWeaponBaseline = Resolve(baselineBuild, Context((tc.Key, tc.Value)));
            CloseTo(Delta(withoutWeapon, withoutWeaponBaseline, tc.Stat), tc.Want, 1e-12);
            CloseTo(withoutWeapon.Calculation.Stats.Of("equipped_weapon_damage"), 0, 1e-12);
        }
    }

    /// <summary>G52: Opportunistic amplifies the selected assault rifle and pistol alike.</summary>
    [Fact]
    public void G52OpportunisticAppliesToSelectedARAndPistol()
    {
        var b = G52Build("Opportunistic");
        var baselineBuild = WithoutTalent(b, "Opportunistic");
        foreach (var slot in new[] { "primary_weapon", "sidearm" })
        {
            var baseline = Stats(baselineBuild, Division.Weapon(slot));
            var active = Stats(b, Context((GamePack.WeaponSlotSelector, slot), ("opportunistic_target_active", "true")));
            CloseTo(active.Of("weapon_damage_multiplier"), baseline.Of("weapon_damage_multiplier") + .10, 1e-12);
            CloseTo(active.Of("equipped_weapon_damage"), baseline.Of("equipped_weapon_damage") * 1.10, 1e-8);
        }
    }

    /// <summary>
    /// G52: absent, false and invalid flags change nothing; Vicious Cycle stacks and Decoy King amplification scale
    /// linearly, reject invalid inputs with a diagnostic, and are defined as bounded numeric inputs.
    /// </summary>
    [Fact]
    public void G52InactiveFlagsAndNumericInputContracts()
    {
        foreach (var (name, key, stat) in new[]
        {
            ("Opportunistic", "opportunistic_target_active", "weapon_damage_multiplier"),
            ("Perfect Opportunistic", "opportunistic_target_active", "weapon_damage_multiplier"),
            ("Energize", "energize_skill_tier_active", "skill_tier"),
            ("Perfect Energize", "energize_skill_tier_active", "skill_tier"),
            ("Weakest Link", "weakest_link_target_active", "weapon_damage_multiplier"),
            ("Over the top", "seeing_red_active", "total_weapon_damage"),
        })
        {
            var b = G52Build(name);
            var baseline = Resolve(WithoutTalent(b, name), Division.Weapon("primary_weapon"));
            foreach (var value in new[] { "", "false", "invalid" })
            {
                var context = Division.Weapon("primary_weapon");
                if (value.Length > 0) { context = context.SetItem(key, value); }
                Assert.True(Math.Abs(Delta(Resolve(b, context), baseline, stat)) <= 1e-12, $"{name} with {key}=\"{value}\" changed {stat}");
            }
        }
        var numeric = new (string Name, string Key, string Stat, int[] Values, double Factor, string[] Invalid, double Min, double Max, double Step)[]
        {
            ("Vicious Cycle", "vicious_cycle_stacks", "weapon_damage", [0, 1, 15, 30], .015, ["-1", "1.5", "31", "NaN"], 0, 30, 1),
            ("Decoy King", "decoy_king_amplification_percent", "weapon_damage_multiplier", [0, 5, 10, 25], .01, ["-5", "7", "30", "NaN"], 0, 25, 5),
        };
        foreach (var tc in numeric)
        {
            var b = G52Build(tc.Name);
            var baseline = Resolve(WithoutTalent(b, tc.Name), Division.Weapon("primary_weapon"));
            foreach (var value in tc.Values)
            {
                var active = Resolve(b, Context((GamePack.WeaponSlotSelector, "primary_weapon"), (tc.Key, value.ToString(CultureInfo.InvariantCulture))));
                var want = value * tc.Factor;
                CloseTo(Delta(active, baseline, tc.Stat), want, 1e-12);
                ExpectContribution(active, tc.Stat, TalentId(tc.Name), want);
            }
            foreach (var value in tc.Invalid)
            {
                var invalid = Resolve(b, Context((GamePack.WeaponSlotSelector, "primary_weapon"), (tc.Key, value)));
                ExpectInvalidContext(invalid, tc.Key);
                CloseTo(Delta(invalid, baseline, tc.Stat), 0, 1e-12);
            }
            var input = Compiled.Pack.NumericContextInputs.Where(input => input.ID == tc.Key).ToArray();
            Assert.True(input.Length > 0, $"missing numeric input {tc.Key}");
            foreach (var definition in input)
            {
                Assert.True(definition.Min == tc.Min && definition.Max == tc.Max && definition.Step == tc.Step && definition.Default == 0, $"{tc.Key} definition = {definition}");
            }
        }
    }

    /// <summary>
    /// G52: Energize raises Skill Tier alone, leaving the weapon card untouched, and Over the top adds total weapon
    /// damage and melee damage with provenance.
    /// </summary>
    [Fact]
    public void G52RawEffectsAndOverTheTopOutputs()
    {
        foreach (var (name, key, value, stat, want) in new[]
        {
            ("Energize", "energize_skill_tier_active", "true", "skill_overcharge", 1.0),
            ("Perfect Energize", "energize_skill_tier_active", "true", "skill_overcharge", 1.0),
        })
        {
            var b = G52Build(name);
            var baseline = Resolve(WithoutTalent(b, name), Division.Weapon("primary_weapon"));
            var active = Resolve(b, Context((GamePack.WeaponSlotSelector, "primary_weapon"), (key, value)));
            CloseTo(Delta(active, baseline, stat), want, 1e-12);
            CloseTo(active.Calculation.Stats.Of("equipped_weapon_damage"), baseline.Calculation.Stats.Of("equipped_weapon_damage"), 1e-12);
            foreach (var card in CardStats)
            {
                CloseTo(active.Calculation.Stats.Of(card), baseline.Calculation.Stats.Of(card), 1e-12);
            }
        }
        var top = G52Build("Over the top");
        var topBaseline = Resolve(WithoutTalent(top, "Over the top"), Division.Weapon("primary_weapon"));
        var topActive = Resolve(top, Context((GamePack.WeaponSlotSelector, "primary_weapon"), ("seeing_red_active", "true")));
        CloseTo(Delta(topActive, topBaseline, "total_weapon_damage"), .25, 1e-12);
        CloseTo(Delta(topActive, topBaseline, "melee_damage"), 1, 1e-12);
        ExpectContribution(topActive, "total_weapon_damage", TalentId("Over the top"), .25);
        ExpectContribution(topActive, "melee_damage", TalentId("Over the top"), 1);
    }

    /// <summary>G52: Energize's Overcharge lifts a five-tier build's skill preview to tier six, as a tier bonus would.</summary>
    [Fact]
    public void G52EnergizeLiftsTierFiveToSix()
    {
        var b = FiveCoreEnergizeBuild();
        var baseline = Resolve(WithoutTalent(b, "Energize"), Context(("energize_skill_tier_active", "true")));
        var active = Resolve(b, Context(("energize_skill_tier_active", "true")));
        CloseTo(active.Calculation.Stats.Of("skill_overcharge"), 1, 1e-12);
        CloseTo(active.Calculation.Stats.Of("skill_tier"), baseline.Calculation.Stats.Of("skill_tier"), 1e-12);
        CloseTo(baseline.Calculation.Stats.Of("skill_tier"), 5, 1e-12);
        var got = PreviewValue(active, "skill_slot_1", "effective_skill_tier");
        Assert.True(got == 6, $"effective skill tier = {got}, want 6");
    }

    /// <summary>R64: the game's Min(Min(tier bonuses, 6) + Overcharge bonus, 7): tier 7 needs a full six, and more tiers don't reach it.</summary>
    [Fact]
    public void G52EnergizeAtTierSixGivesTierSeven()
    {
        var b = FiveCoreEnergizeBuild();
        var (coreId, coreMax) = Attribute("skill_tier_gear_core");
        var (item, attributeSlot) = CoreHost("knees", coreId);
        b = b.WithEquipment("knees", item.ID).WithAttributeRoll("knees", attributeSlot, new(coreId, coreMax));
        var off = Resolve(b);
        var on = Resolve(b, Context(("energize_skill_tier_active", "true")));
        CloseTo(off.Calculation.Stats.Of("skill_tier"), 6, 1e-12);
        Assert.Equal(6, PreviewValue(off, "skill_slot_1", "effective_skill_tier"));
        Assert.Equal(7, PreviewValue(on, "skill_slot_1", "effective_skill_tier"));
    }

    private static BuildSnapshot FiveCoreEnergizeBuild()
    {
        var b = G52Build("Energize").WithActiveSkillSelection("skill_slot_1", "skill_assault_turret");
        var (coreId, coreMax) = Attribute("skill_tier_gear_core");
        Assert.True(coreId.Length > 0 && coreMax >= 1, "missing skill tier gear core");
        foreach (var gearSlot in new[] { "mask", "chest", "holster", "gloves" })
        {
            var (item, attributeSlot) = CoreHost(gearSlot, coreId);
            b = b.WithEquipment(gearSlot, item.ID).WithAttributeRoll(gearSlot, attributeSlot, new(coreId, coreMax));
        }
        var backpack = Compiled.Pack.Items.First(item => item.ID == b.Equipment["backpack"]);
        var backpackCore = backpack.AttributeSlots.LastOrDefault(slot => slot.Choices.Any(choice => choice.AttributeID == coreId))?.ID ?? "";
        Assert.True(backpackCore.Length > 0, "Energize backpack has no skill tier core");
        return b.WithAttributeRoll("backpack", backpackCore, new(coreId, coreMax));
    }

    /// <summary>
    /// G52: Vicious Cycle's stacks add to a configured Weapon Damage core in the weapon's damage category, leave the
    /// weapon card alone, and the talent survives sharing while its assumptions don't.
    /// </summary>
    [Fact]
    public void G52ViciousCycleCompositionAndTalentSharing()
    {
        var b = G52Build("Vicious Cycle");
        var chest = Compiled.Pack.Items.FirstOrDefault(item => item.Slot == "chest" && item.AttributeSlots.Any(slot => slot.Choices.Any(choice => choice.AttributeID == "weapon_damage_gear_core")));
        var attributeSlot = chest?.AttributeSlots.First(slot => slot.Choices.Any(choice => choice.AttributeID == "weapon_damage_gear_core")).ID ?? "";
        var (attributeId, attributeMax) = Attribute("weapon_damage_gear_core");
        Assert.True(chest is not null && attributeSlot.Length > 0 && attributeMax > 0, "missing weapon damage core roll for composition");
        b = b.WithEquipment("chest", chest.ID).WithAttributeRoll("chest", attributeSlot, new(attributeId, attributeMax));
        var baseline = Stats(WithoutTalent(b, "Vicious Cycle"), Division.Weapon("primary_weapon"));
        var active = Stats(b, Context((GamePack.WeaponSlotSelector, "primary_weapon"), ("vicious_cycle_stacks", "2")));
        Assert.True(baseline.Of("weapon_damage") > 0, $"composition baseline weapon damage = {baseline.Of("weapon_damage")}, want positive");
        CloseTo(active.Of("weapon_damage") - baseline.Of("weapon_damage"), .03, 1e-12);
        var baseCategory = 1 + baseline.Of("weapon_damage") + baseline.Of("assault_rifle_damage");
        CloseTo(active.Of("equipped_weapon_damage"), baseline.Of("equipped_weapon_damage") * (baseCategory + .03) / baseCategory, 1e-8);
        foreach (var card in CardStats)
        {
            CloseTo(active.Of(card), baseline.Of(card), 1e-12);
        }

        var encoded = ShareCode.Encode(b);
        var decoded = ShareCode.Decode(encoded);
        // Go compared TalentSelections with reflect.DeepEqual; this compares the same slots and selections.
        Assert.True(decoded.TalentSelections.Count == b.TalentSelections.Count
            && b.TalentSelections.All(pair => decoded.TalentSelections.TryGetValue(pair.Key, out var slots) && slots.Count == pair.Value.Count
                && pair.Value.All(selection => slots.TryGetValue(selection.Key, out var talent) && talent == selection.Value)),
            "talents did not persist through share");
        foreach (var key in new[] { "weapon_slot", "opportunistic_target_active", "energize_skill_tier_active", "vicious_cycle_stacks", "weakest_link_target_active", "seeing_red_active", "decoy_king_amplification_percent" })
        {
            Assert.False(encoded.Contains(key, StringComparison.Ordinal), $"share unexpectedly contains transient context \"{key}\"");
        }
    }

    /// <summary>G52: the amplifiers scale the selected weapon's damage preview by their factor and leave its card alone.</summary>
    [Fact]
    public void G52AmplificationDamagePreviewAndRawCardOutputs()
    {
        foreach (var (name, key, value, factor) in new[]
        {
            ("Opportunistic", "opportunistic_target_active", "true", 1.10),
            ("Perfect Opportunistic", "opportunistic_target_active", "true", 1.15),
            ("Weakest Link", "weakest_link_target_active", "true", 1.30),
            ("Decoy King", "decoy_king_amplification_percent", "25", 1.25),
            ("Over the top", "seeing_red_active", "true", 1.25),
        })
        {
            var b = G52Build(name);
            var baseline = Stats(WithoutTalent(b, name), Division.Weapon("primary_weapon"));
            var active = Stats(b, Context((GamePack.WeaponSlotSelector, "primary_weapon"), (key, value)));
            CloseTo(active.Of("equipped_weapon_damage"), baseline.Of("equipped_weapon_damage") * factor, 1e-8);
            foreach (var card in CardStats)
            {
                CloseTo(active.Of(card), baseline.Of(card), 1e-12);
            }
        }
    }

    /// <summary>
    /// G52: Weakest Link multiplies with Glass Cannon's amplification, and Over the top's total weapon damage adds to
    /// Vigilance's.
    /// </summary>
    [Fact]
    public void G52AmplificationAndTotalWeaponDamageComposition()
    {
        var weakest = SelectGearTalent(G52Build("Weakest Link"), "Glass Cannon", "chest");
        var weakBase = Stats(WithoutTalent(weakest, "Weakest Link"), Division.Weapon("primary_weapon"));
        var weakActive = Stats(weakest, Context((GamePack.WeaponSlotSelector, "primary_weapon"), ("weakest_link_target_active", "true")));
        CloseTo(weakBase.Of("weapon_damage_multiplier"), 1.25, 1e-12);
        CloseTo(weakActive.Of("weapon_damage_multiplier"), 1.25 * 1.30, 1e-12);

        var seeingRed = SelectGearTalent(G52Build("Over the top"), "Vigilance", "backpack");
        var twdBase = Resolve(WithoutTalent(seeingRed, "Over the top"), Context((GamePack.WeaponSlotSelector, "primary_weapon"), ("vigilance_active", "true")));
        var twdActive = Resolve(seeingRed, Context((GamePack.WeaponSlotSelector, "primary_weapon"), ("vigilance_active", "true"), ("seeing_red_active", "true")));
        CloseTo(twdBase.Calculation.Stats.Of("total_weapon_damage"), .25, 1e-12);
        CloseTo(twdActive.Calculation.Stats.Of("total_weapon_damage"), .50, 1e-12);
        CloseTo(Delta(twdActive, twdBase, "total_weapon_damage"), .25, 1e-12);
        CloseTo(Delta(twdActive, twdBase, "melee_damage"), 1, 1e-12);
    }

    /// <summary>G63: Boiling Point's magazine phases set critical hit chance on its own weapon only, on plain and named hosts.</summary>
    [Fact]
    public void G63BoilingPointMagazinePhases()
    {
        foreach (var (talent, host) in new[] { ("weapon_talent_boiling_point", ""), ("weapon_talent_perfect_boiling_point", "Steamer"), ("weapon_talent_perfect_boiling_point", "Teapot") })
        {
            var (b, owner) = host.Length == 0 ? PlainWeaponBuild(talent, "primary_weapon") : NamedHostBuild(host, talent);
            foreach (var (phase, want) in new[] { ("", 0.0), ("early", -1), ("late", 1) })
            {
                var context = Division.Weapon(owner);
                if (phase.Length > 0) { context = context.SetItem("boiling_point_phase", phase); }
                Close($"{talent} {host} {phase}", Stats(b, context).Of("critical_hit_chance"), want);
            }
            Close($"{talent} unselected owner", Stats(b, Context((GamePack.WeaponSlotSelector, "sidearm"), ("boiling_point_phase", "late"))).Of("critical_hit_chance"), 0);
        }
    }

    /// <summary>G63: Blossom Harvest scales with the target's remaining health in steps, and Shakedown with its shot and stacks.</summary>
    [Fact]
    public void G63WeaponAmplifiers()
    {
        var (blossom, owner) = NamedHostBuild("First Bloom", "weapon_talent_blossom_harvest");
        // Off-step input (55) is rejected as invalid and falls back to the default 0.
        foreach (var (remaining, want) in new[] { ("0", 1.0), ("50", 1 + 0.033 * 5), ("55", 1), ("100", 1.33) })
        {
            var context = Context((GamePack.WeaponSlotSelector, owner), ("blossom_harvest_target_remaining_percent", remaining));
            Close($"Blossom Harvest at {remaining}%", Stats(blossom, context).Of("weapon_damage_multiplier"), want);
        }
        var unselected = Stats(blossom, Context((GamePack.WeaponSlotSelector, "sidearm"), ("blossom_harvest_target_remaining_percent", "100"))).Of("weapon_damage_multiplier");
        Assert.True(unselected == 1, $"Blossom Harvest applied to an unselected weapon: {unselected}");

        var (shakedown, shakedownOwner) = NamedHostBuild("Steel & Sons ACR", "weapon_talent_shakedown");
        // From R55, +4% a stack on every hit and +11% a stack on a headshot, by the kind of hit rather than a selector.
        foreach (var (stacks, weapon, headshot) in new[] { ("0", 1.0, 1.0), ("3", 1.12, 1.33), ("4", 1.16, 1.44) })
        {
            var context = Stats(shakedown, Context((GamePack.WeaponSlotSelector, shakedownOwner), ("shakedown_stacks", stacks)));
            Close($"Shakedown {stacks} on every hit", context.Of("weapon_damage_multiplier"), weapon);
            Close($"Shakedown {stacks} on a headshot", context.Of("headshot_damage_multiplier"), headshot);
        }
    }

    /// <summary>G63: Pack Instincts, Rebalance, Ostracize, Challenger and Impervious apply their gated gear stats.</summary>
    [Fact]
    public void G63GearTalents()
    {
        var (pack, _) = NamedHostBuild("Coyote's Mask", "gear_talent_pack_instincts");
        foreach (var (range, chc, chd) in new[] { ("", 0.0, 0.0), ("close", 0, 0.25), ("mid", 0.10, 0.10), ("far", 0.25, 0) })
        {
            var context = range.Length > 0 ? Context(("pack_instincts_range", range)) : Context();
            var result = Stats(pack, context);
            Close($"Pack Instincts {range} CHC", result.Of("critical_hit_chance"), chc);
            Close($"Pack Instincts {range} CHD", result.Of("critical_hit_damage"), chd);
        }

        var (rebalance, _) = NamedHostBuild("Harrier Pride", "gear_talent_rebalance");
        var rebalanced = Resolve(rebalance, Context(("rebalance_red_stacks", "10"), ("rebalance_blue_stacks", "20")));
        Close("Rebalance Weapon Damage", rebalanced.Calculation.Stats.Of("weapon_damage"), 0.05);
        Close("Rebalance Damage Resistance", rebalanced.Calculation.Stats.Of("damage_resistance"), 0.10);
        ExpectFlatContribution(rebalanced, "damage_resistance", "gear_talent_rebalance", 0.10);

        var (ostracize, _) = NamedHostBuild("Blacklisters", "gear_talent_ostracize");
        Close("Ostracize inactive", Stats(ostracize).Of("weapon_damage_multiplier"), 1);
        Close("Ostracize unmarked target", Stats(ostracize, Context(("ostracize_target_unmarked", "true"))).Of("weapon_damage_multiplier"), 1.3); // Y8S3.2

        var (challenger, _) = NamedHostBuild("Provocator", "gear_talent_challenger");
        Close("Challenger inactive", Stats(challenger).Of("damage_resistance"), 0);
        Close("Challenger active", Stats(challenger, Context(("challenger_active", "true"))).Of("damage_resistance"), 0.25);

        var (impervious, _) = NamedHostBuild("Nurse's Kneepads", "gear_talent_impervious");
        var resolved = Resolve(impervious);
        Close("Impervious Hazard Protection", resolved.Calculation.Stats.Of("hazard_protection") - Stats(Build()).Of("hazard_protection"), 0.40);
        ExpectFlatContribution(resolved, "hazard_protection", "gear_talent_impervious", 0.40);
    }

    /// <summary>
    /// G398: quick-win talents from the talent audit apply their gated stats, a weapon talent only while its weapon is
    /// active, and Empathic Resolve scales with Skill Tier for the wearer and, as an ally effect, for the party.
    /// </summary>
    [Fact]
    public void G398QuickWinTalents()
    {
        var primary = Division.Weapon("primary_weapon");
        var sledge = Build().WithEquipment("primary_weapon", "primary_weapon_acr").WithTalentSelection("primary_weapon", "talent", "weapon_talent_sledgehammer");
        var off = Stats(sledge, primary);
        var on = Stats(sledge, Context((GamePack.WeaponSlotSelector, "primary_weapon"), ("sledgehammer_target_marked", "true")));
        CloseTo(on.Of("damage_to_armor") - off.Of("damage_to_armor"), 0.15, 1e-12);

        var quickstep = Build().WithEquipment("sidearm", "sidearm_quickstep").WithTalentSelection("sidearm", "talent", "weapon_talent_sport_mode");
        CloseTo(Stats(quickstep, Division.Weapon("sidearm")).Of("movement_speed"), 0.2, 1e-12);
        CloseTo(Stats(quickstep, primary).Of("movement_speed"), 0, 1e-12);

        var waveform = Build().WithEquipment("holster", "holster_waveform").WithTalentSelection("holster", "talent", "gear_talent_alternating_current");
        CloseTo(Stats(waveform, Context(("alternating_current_stacks", "10"))).Of("skill_damage") - Stats(waveform).Of("skill_damage"), 0.25, 1e-12);

        var empathic = Build().WithEquipment("chest", "chest_n_5_11_tactical_chest")
            .WithTalentSelection("chest", "talent", "gear_talent_empathic_resolve")
            .WithAttributeRoll("chest", "core_1", new("skill_tier_gear_core", 1));
        var self = Stats(empathic, Context(("empathic_resolve_self_repair", "true")));
        var tier = self.Of("skill_tier");
        Assert.True(tier >= 1, $"skill tier = {tier}");
        CloseTo(self.Of("total_weapon_damage") - Stats(empathic).Of("total_weapon_damage"), 0.01 + 0.01 * tier, 1e-12);
        var effects = PartyEffectSelection.EffectsOf(Compiled, empathic);
        Assert.True(effects.Length == 1 && effects[0].Id == "empathic_resolve", $"ally effects = [{string.Join(", ", effects.Select(effect => effect.Id))}]");
        CloseTo(effects[0].Modifiers[0].Value, 0.03 + 0.02 * tier, 1e-12);
    }

    private static bool Offers(ItemDefinition item, string talentId) => item.TalentSlots.Any(slot => slot.Choices.Contains(talentId));

    private static string TalentId(string name) =>
        Compiled.Pack.Talents.FirstOrDefault(talent => talent.Name == name)?.ID ?? throw new InvalidOperationException($"missing G52 talent \"{name}\"");

    /// <summary>The gear slots with an item offering the talent, as Go's g52LegalSlots.</summary>
    private static string[] LegalSlots(string talentId) =>
        [.. GearSlots.Where(slot => Compiled.Pack.Items.Any(item => item.Slot == slot && Offers(item, talentId)))];

    /// <summary>
    /// The first item in each weapon slot, then the ACR and Tempest, with the talent selected on its first contract
    /// host, as Go's g52Build.
    /// </summary>
    private static BuildSnapshot G52Build(string name)
    {
        var contract = Contracts[name];
        var talentId = TalentId(name);
        var b = Build();
        foreach (var slot in WeaponSlots)
        {
            var first = Compiled.Pack.Items.FirstOrDefault(item => item.Slot == slot);
            if (first is not null) { b = b.WithEquipment(slot, first.ID); }
        }
        foreach (var (slot, itemId) in new[] { ("primary_weapon", "primary_weapon_acr"), ("sidearm", "sidearm_tempest") })
        {
            if (Compiled.Pack.Items.Any(item => item.ID == itemId && item.Slot == slot)) { b = b.WithEquipment(slot, itemId); }
        }
        foreach (var item in Compiled.Pack.Items)
        {
            if ((contract.Ids.Length > 0 && !contract.Ids.Contains(item.ID)) || item.Slot != contract.Slot) { continue; }
            var talentSlot = item.TalentSlots.FirstOrDefault(slot => slot.Choices.Contains(talentId));
            if (talentSlot is not null) { return b.WithEquipment(contract.Slot, item.ID).WithTalentSelection(contract.Slot, talentSlot.ID, talentId); }
        }
        throw new InvalidOperationException($"no {contract.Slot} host for {name}");
    }

    /// <summary>The talent selected on the first item in a slot that offers it, as Go's g52SelectGearTalent.</summary>
    private static BuildSnapshot SelectGearTalent(BuildSnapshot b, string name, string slot)
    {
        var talentId = TalentId(name);
        foreach (var item in Compiled.Pack.Items.Where(item => item.Slot == slot))
        {
            var talentSlot = item.TalentSlots.FirstOrDefault(talentSlot => talentSlot.Choices.Contains(talentId));
            if (talentSlot is not null) { return b.WithEquipment(slot, item.ID).WithTalentSelection(slot, talentSlot.ID, talentId); }
        }
        throw new InvalidOperationException($"no {slot} host for {name}");
    }

    /// <summary>The build with the contract talent's selection cleared, as Go's g52WithoutTalent.</summary>
    private static BuildSnapshot WithoutTalent(BuildSnapshot b, string name)
    {
        var contract = Contracts[name];
        var talentId = TalentId(name);
        var itemId = b.Equipment.GetValueOrDefault(contract.Slot, "");
        var talentSlot = Compiled.Pack.Items.Where(item => item.ID == itemId).SelectMany(item => item.TalentSlots).FirstOrDefault(slot => slot.Choices.Contains(talentId))
            ?? throw new InvalidOperationException($"cannot clear {name} from {contract.Slot}");
        return b.WithTalentSelection(contract.Slot, talentSlot.ID, "");
    }

    private static double Delta(ResolvedBuild result, ResolvedBuild baseline, string stat) => result.Calculation.Stats.Of(stat) - baseline.Calculation.Stats.Of(stat);

    /// <summary>The talent's summed contributions to a stat, as Go's g52ExpectContribution.</summary>
    private static void ExpectContribution(ResolvedBuild result, string stat, string talentId, double expected)
    {
        Assert.True(result.Calculation.Breakdowns.TryGetValue(stat, out var breakdown), $"missing {stat} breakdown");
        var contributions = breakdown.Contributions.Where(contribution => contribution.Source.Kind == "talent" && contribution.Source.Id == talentId).ToArray();
        Assert.True(contributions.Length > 0, $"missing {talentId} contribution for {stat}");
        CloseTo(contributions.Sum(contribution => contribution.Value), expected, 1e-12);
    }

    /// <summary>The talent's first contribution to a stat is flat and exactly the value, as Go's g57ExpectContribution.</summary>
    private static void ExpectFlatContribution(ResolvedBuild result, string stat, string talentId, double value)
    {
        var contribution = result.Calculation.Breakdowns.TryGetValue(stat, out var breakdown)
            ? breakdown.Contributions.FirstOrDefault(contribution => contribution.Source.Kind == "talent" && contribution.Source.Id == talentId)
            : null;
        Assert.True(contribution is not null, $"no {stat} talent contribution for {talentId}");
        Assert.True(contribution.Op == ModifierOps.Flat && contribution.Value == value, $"{stat} {talentId} contribution=({contribution.Op},{contribution.Value}), want flat {value}");
    }

    private static void ExpectInvalidContext(ResolvedBuild result, string key) =>
        Assert.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == "context-value-invalid" && diagnostic.Subject == key),
            $"missing invalid context diagnostic for {key}: {string.Join("; ", result.Diagnostics)}");

    private static (string Id, double Max) Attribute(string id) =>
        Compiled.Pack.Attributes.Where(attribute => attribute.ID == id).Select(attribute => (attribute.ID, attribute.Max)).LastOrDefault(("", 0));

    /// <summary>The first item in a slot with a core choice, and its last attribute slot offering it, as Go's loops.</summary>
    private static (ItemDefinition Item, string AttributeSlot) CoreHost(string slot, string attributeId)
    {
        var item = Compiled.Pack.Items.FirstOrDefault(item => item.Slot == slot && item.AttributeSlots.Any(attributeSlot => attributeSlot.Choices.Any(choice => choice.AttributeID == attributeId)))
            ?? throw new InvalidOperationException($"missing skill tier gear core host for {slot}");
        return (item, item.AttributeSlots.Last(attributeSlot => attributeSlot.Choices.Any(choice => choice.AttributeID == attributeId)).ID);
    }

    private static double PreviewValue(ResolvedBuild result, string slot, string outputId)
    {
        Assert.True(result.ActiveSkillPreviews.TryGetValue(slot, out var preview), $"missing active skill preview {slot}");
        var output = preview.Outputs.FirstOrDefault(output => output.Id == outputId);
        Assert.True(output is not null, $"missing {outputId} output in {slot} preview");
        return output.Value;
    }

    /// <summary>The named host (in its own slot) with the talent selected, as Go's g63Build.</summary>
    private static (BuildSnapshot Build, string Owner) NamedHostBuild(string hostName, string talentId)
    {
        foreach (var item in Compiled.Pack.Items.Where(item => item.Name == hostName))
        {
            var talentSlot = item.TalentSlots.FirstOrDefault(slot => slot.Choices.Contains(talentId));
            if (talentSlot is not null) { return (Build().WithEquipment(item.Slot, item.ID).WithTalentSelection(item.Slot, talentSlot.ID, talentId), item.Slot); }
        }
        throw new InvalidOperationException($"no host \"{hostName}\" offers {talentId}");
    }

    /// <summary>The first non-named, non-exotic weapon in a slot offering the talent, with it selected, as Go's g57TalentBuild.</summary>
    private static (BuildSnapshot Build, string Owner) PlainWeaponBuild(string talentId, string slot)
    {
        var item = Compiled.Pack.Items.FirstOrDefault(item => item.Tags.Contains("weapon") && !item.Tags.Contains("named") && !item.Tags.Contains("exotic") && item.Slot == slot && Offers(item, talentId))
            ?? throw new InvalidOperationException($"no G57 host for {talentId}");
        var talentSlot = item.TalentSlots.First(talentSlot => talentSlot.Choices.Contains(talentId));
        return (Build().WithEquipment(slot, item.ID).WithTalentSelection(slot, talentSlot.ID, talentId), slot);
    }

    /// <summary>Go's g63Close: within float noise.</summary>
    private static void Close(string label, double got, double want) => Assert.True(Math.Abs(got - want) <= 1e-9, $"{label} = {got}, want {want}");
}
