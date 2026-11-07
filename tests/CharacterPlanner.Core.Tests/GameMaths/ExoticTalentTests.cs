using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// Named and exotic items (F11): exotic weapon and gear talents, their stacked and grouped assumptions, owner scope,
/// provenance and composition, ported from Go's exotic_talents_g47_test.go, exotic_gear_g53_test.go and
/// dragons_breath_g64_test.go.
/// </summary>
public sealed class ExoticTalentTests
{
    private static readonly Dictionary<string, (string Item, string Slot, string Stat)> Contracts = new(StringComparer.Ordinal)
    {
        ["Bond"] = ("chest_beacon", "chest", "critical_hit_damage"),
        ["Chemical Agent"] = ("mask_catalyst", "mask", "weapon_damage"),
        ["Counter"] = ("holster_centurion_scabbard", "holster", "weapon_damage"),
        ["One in the Hand... / ...Two in the Bag"] = ("backpack_acosta_s_go_bag", "backpack", "ammo_capacity"),
    };

    private const string Acosta = "One in the Hand... / ...Two in the Bag";
    private static readonly string[] WeaponSlots = ["primary_weapon", "secondary_weapon", "sidearm"];
    private static readonly string[] GearSlots = ["mask", "chest", "holster", "backpack", "gloves", "knees"];
    private static readonly string[] CardStats = ["weapon_rpm", "weapon_magazine_size", "weapon_reload_time", "weapon_optimal_range"];

    /// <summary>
    /// G47: Chameleon's Adaptive Instincts gives its headshot, bodyshot and legshot buffs only when flagged, on its own
    /// weapon, with provenance, and once when both weapons carry it.
    /// </summary>
    [Fact]
    public void G47ChameleonAdaptiveInstincts()
    {
        const string talentId = "weapon_talent_adaptive_instincts";
        var effects = new (string Flag, string Stat, double Bonus)[]
        {
            ("chameleon_headshot_buff_active", "critical_hit_chance", .20),
            ("chameleon_headshot_buff_active", "critical_hit_damage", .50),
            ("chameleon_bodyshot_buff_active", "weapon_damage", .90),
            ("chameleon_legshot_buff_active", "reload_speed", 1.50),
        };
        foreach (var slot in new[] { "primary_weapon", "secondary_weapon" })
        {
            var b = WeaponBuild(talentId, slot, Build());
            var baseline = Resolve(b, Division.Weapon(slot));
            foreach (var effect in effects)
            {
                foreach (var flag in new[] { "false", "invalid" })
                {
                    CloseTo(Delta(Resolve(b, Context((GamePack.WeaponSlotSelector, slot), (effect.Flag, flag))), baseline, effect.Stat), 0, 1e-12);
                }
                var active = Resolve(b, Context((GamePack.WeaponSlotSelector, slot), (effect.Flag, "true")));
                CloseTo(Delta(active, baseline, effect.Stat), effect.Bonus, 1e-12);
                var (value, found) = Contribution(active, effect.Stat, talentId);
                Assert.True(found, $"missing {talentId} provenance for {effect.Stat}");
                CloseTo(value, effect.Bonus, 1e-12);
            }
            var allActive = Resolve(b, Context((GamePack.WeaponSlotSelector, slot), ("chameleon_headshot_buff_active", "true"),
                ("chameleon_bodyshot_buff_active", "true"), ("chameleon_legshot_buff_active", "true")));
            CloseTo(Delta(allActive, baseline, "weapon_damage"), .90, 1e-12);
            CloseTo(allActive.Calculation.Stats.Of("equipped_weapon_damage"), baseline.Calculation.Stats.Of("equipped_weapon_damage") * 1.90, 1e-8);

            var otherSlot = Other(slot);
            var wrong = Resolve(b, Context((GamePack.WeaponSlotSelector, otherSlot), ("chameleon_headshot_buff_active", "true"),
                ("chameleon_bodyshot_buff_active", "true"), ("chameleon_legshot_buff_active", "true")));
            var wrongBaseline = Resolve(b, Division.Weapon(otherSlot));
            foreach (var effect in effects)
            {
                CloseTo(Delta(wrong, wrongBaseline, effect.Stat), 0, 1e-12);
            }
            var cleared = ClearTalent(b, slot, talentId);
            CloseTo(Stats(cleared, Context((GamePack.WeaponSlotSelector, slot), ("chameleon_bodyshot_buff_active", "true"))).Of("weapon_damage"), baseline.Calculation.Stats.Of("weapon_damage"), 1e-12);
            var reselected = SelectTalent(cleared, slot, talentId);
            CloseTo(Stats(reselected, Context((GamePack.WeaponSlotSelector, slot), ("chameleon_bodyshot_buff_active", "true"))).Of("weapon_damage"), baseline.Calculation.Stats.Of("weapon_damage") + .90, 1e-12);
        }

        var both = WeaponBuild(talentId, "secondary_weapon", WeaponBuild(talentId, "primary_weapon", Build()));
        foreach (var slot in new[] { "primary_weapon", "secondary_weapon" })
        {
            var baseline = Resolve(both, Division.Weapon(slot));
            var active = Resolve(both, Context((GamePack.WeaponSlotSelector, slot), ("chameleon_bodyshot_buff_active", "true")));
            CloseTo(Delta(active, baseline, "weapon_damage"), .90, 1e-12);
            var (value, found) = Contribution(active, "weapon_damage", talentId);
            Assert.True(found, "missing duplicate Chameleon provenance");
            CloseTo(value, .90, 1e-12);
        }
    }

    /// <summary>G47: Sawyer's Stand Your Ground stacks total weapon damage only while active, and rejects invalid stacks.</summary>
    [Fact]
    public void G47SawyerStandYourGround()
    {
        const string talentId = "gear_talent_stand_your_ground";
        var b = GearBuild(talentId, "knees");
        var baseline = Resolve(b, Division.Weapon("primary_weapon"));
        foreach (var active in new[] { "", "false", "invalid" })
        {
            var context = Context((GamePack.WeaponSlotSelector, "primary_weapon"), ("sawyer_stacks", "10"));
            if (active.Length > 0) { context = context.SetItem("sawyer_active", active); }
            CloseTo(Delta(Resolve(b, context), baseline, "total_weapon_damage"), 0, 1e-12);
        }
        foreach (var stacks in new[] { 0, 1, 5, 10 })
        {
            var active = Resolve(b, Context((GamePack.WeaponSlotSelector, "primary_weapon"), ("sawyer_stacks", Format(stacks)), ("sawyer_active", "true")));
            CloseTo(Delta(active, baseline, "total_weapon_damage"), .03 * stacks, 1e-12);
            var (value, found) = Contribution(active, "total_weapon_damage", talentId);
            Assert.True(found, "missing Sawyer provenance");
            CloseTo(value, .03 * stacks, 1e-12);
        }
        foreach (var invalid in new[] { "-1", "1.5", "11", "NaN" })
        {
            var result = Resolve(b, Context((GamePack.WeaponSlotSelector, "primary_weapon"), ("sawyer_stacks", invalid), ("sawyer_active", "true")));
            ExpectInvalidContext(result, "sawyer_stacks");
            CloseTo(Delta(result, baseline, "total_weapon_damage"), 0, 1e-12);
        }
        var full = Context((GamePack.WeaponSlotSelector, "primary_weapon"), ("sawyer_stacks", "10"), ("sawyer_active", "true"));
        var cleared = ClearTalent(b, "knees", talentId);
        CloseTo(Stats(cleared, full).Of("total_weapon_damage"), baseline.Calculation.Stats.Of("total_weapon_damage"), 1e-12);
        CloseTo(Stats(SelectTalent(cleared, "knees", talentId), full).Of("total_weapon_damage"), baseline.Calculation.Stats.Of("total_weapon_damage") + .30, 1e-12);
    }

    /// <summary>G47: Capacitance's full stacks give skill damage, not weapon damage, on its own weapon, once when duplicated.</summary>
    [Fact]
    public void G47Capacitance()
    {
        const string talentId = "weapon_talent_capacitance";
        foreach (var slot in new[] { "primary_weapon", "secondary_weapon" })
        {
            var b = WeaponBuild(talentId, slot, Build());
            var baseline = Resolve(b, Division.Weapon(slot));
            var full = Resolve(b, Context((GamePack.WeaponSlotSelector, slot), ("capacitance_stacks", "40"), ("capacitor_skill_tier", "6")));
            CloseTo(Delta(full, baseline, "skill_damage"), .60, 1e-12);
            CloseTo(Delta(full, baseline, "weapon_damage"), 0, 1e-12);
            foreach (var (stat, want) in new[] { ("skill_damage", .60), ("weapon_damage", 0) })
            {
                var (value, found) = Contribution(full, stat, talentId);
                Assert.True(found, $"missing Capacitance provenance for {stat}");
                CloseTo(value, want, 1e-12);
            }
            foreach (var value in new[] { "-1", "0.5", "41", "NaN" })
            {
                var result = Resolve(b, Context((GamePack.WeaponSlotSelector, slot), ("capacitance_stacks", value)));
                ExpectInvalidContext(result, "capacitance_stacks");
                CloseTo(Delta(result, baseline, "skill_damage"), 0, 1e-12);
            }
            var wrong = Stats(b, Context((GamePack.WeaponSlotSelector, Other(slot)), ("capacitance_stacks", "40"), ("capacitor_skill_tier", "6")));
            CloseTo(wrong.Of("skill_damage"), 0, 1e-12);
            CloseTo(wrong.Of("weapon_damage"), 0, 1e-12);
        }
        var both = WeaponBuild(talentId, "secondary_weapon", WeaponBuild(talentId, "primary_weapon", Build()));
        var active = Stats(both, Context((GamePack.WeaponSlotSelector, "primary_weapon"), ("capacitance_stacks", "40"), ("capacitor_skill_tier", "6")));
        CloseTo(active.Of("skill_damage"), .60, 1e-12);
        CloseTo(active.Of("weapon_damage"), 0, 1e-12);
    }

    /// <summary>G47: Memento's long-term trophies add weapon damage, skill efficiency and armor regeneration on either weapon.</summary>
    [Fact]
    public void G47MementoKillConfirmedLongTermTrophies()
    {
        const string talentId = "gear_talent_kill_confirmed";
        var b = GearBuild(talentId, "backpack");
        foreach (var stacks in new[] { 0, 1, 15, 30 })
        {
            foreach (var slot in new[] { "primary_weapon", "secondary_weapon" })
            {
                var baseline = Resolve(b, Division.Weapon(slot));
                var active = Resolve(b, Context((GamePack.WeaponSlotSelector, slot), ("memento_trophy_stacks", Format(stacks))));
                CloseTo(Delta(active, baseline, "weapon_damage"), .01 * stacks, 1e-12);
                CloseTo(Delta(active, baseline, "skill_efficiency"), .01 * stacks, 1e-12);
                CloseTo(Delta(active, baseline, "armor_regeneration_percent"), .001 * stacks, 1e-12);
                CloseTo(active.Calculation.Stats.Of("equipped_weapon_damage"), baseline.Calculation.Stats.Of("equipped_weapon_damage") * (1 + .01 * stacks), 1e-8);
                CloseTo(active.Calculation.Stats.Of("skill_tier"), baseline.Calculation.Stats.Of("skill_tier"), 1e-12);
                var (value, found) = Contribution(active, "weapon_damage", talentId);
                Assert.True(found, "missing Memento provenance");
                CloseTo(value, .01 * stacks, 1e-12);
            }
        }
        foreach (var invalid in new[] { "-1", "0.5", "31", "NaN" })
        {
            var result = Resolve(b, Context((GamePack.WeaponSlotSelector, "primary_weapon"), ("memento_trophy_stacks", invalid)));
            ExpectInvalidContext(result, "memento_trophy_stacks");
            CloseTo(result.Calculation.Stats.Of("skill_efficiency"), 0, 1e-12);
        }
    }

    /// <summary>G47: Liberty's stacks add weapon damage on the sidearm only, rejecting invalid stacks.</summary>
    [Fact]
    public void G47LibertyOrDeath()
    {
        const string talentId = "weapon_talent_liberty_or_death";
        var b = WeaponBuild(talentId, "sidearm", Build());
        var baseline = Resolve(b, Division.Weapon("sidearm"));
        foreach (var stacks in new[] { 0, 1, 15, 30 })
        {
            var active = Resolve(b, Context((GamePack.WeaponSlotSelector, "sidearm"), ("liberty_stacks", Format(stacks))));
            CloseTo(Delta(active, baseline, "weapon_damage"), .02 * stacks, 1e-12);
            CloseTo(active.Calculation.Stats.Of("equipped_weapon_damage"), baseline.Calculation.Stats.Of("equipped_weapon_damage") * (1 + .02 * stacks), 1e-8);
            var (value, found) = Contribution(active, "weapon_damage", talentId);
            Assert.True(found, "missing Liberty provenance");
            CloseTo(value, .02 * stacks, 1e-12);
        }
        CloseTo(Stats(b, Context((GamePack.WeaponSlotSelector, "primary_weapon"), ("liberty_stacks", "30"))).Of("weapon_damage"), 0, 1e-12);
        foreach (var invalid in new[] { "-1", "0.5", "31", "NaN" })
        {
            var result = Resolve(b, Context((GamePack.WeaponSlotSelector, "sidearm"), ("liberty_stacks", invalid)));
            ExpectInvalidContext(result, "liberty_stacks");
            CloseTo(Delta(result, baseline, "weapon_damage"), 0, 1e-12);
        }
    }

    /// <summary>G47: Lady Death's Breathe Free amplifies its own weapon's next shot by 75%, once when duplicated.</summary>
    [Fact]
    public void G47LadyDeathBreatheFree()
    {
        const string talentId = "weapon_talent_breathe_free";
        foreach (var slot in new[] { "primary_weapon", "secondary_weapon" })
        {
            var b = WeaponBuild(talentId, slot, Build());
            var baseline = Resolve(b, Division.Weapon(slot));
            foreach (var flag in new[] { "false", "invalid" })
            {
                CloseTo(Stats(b, Context((GamePack.WeaponSlotSelector, slot), ("lady_death_next_shot_active", flag))).Of("weapon_damage_multiplier"), 1, 1e-12);
            }
            var active = Resolve(b, Context((GamePack.WeaponSlotSelector, slot), ("lady_death_next_shot_active", "true")));
            CloseTo(active.Calculation.Stats.Of("weapon_damage_multiplier"), 1.75, 1e-12);
            CloseTo(active.Calculation.Stats.Of("equipped_weapon_damage"), baseline.Calculation.Stats.Of("equipped_weapon_damage") * 1.75, 1e-8);
            var (value, found) = Contribution(active, "weapon_damage_multiplier", talentId);
            Assert.True(found, "missing Lady Death provenance");
            CloseTo(value, .75, 1e-12);
            CloseTo(Stats(b, Context((GamePack.WeaponSlotSelector, Other(slot)), ("lady_death_next_shot_active", "true"))).Of("weapon_damage_multiplier"), 1, 1e-12);
        }
        var both = WeaponBuild(talentId, "secondary_weapon", WeaponBuild(talentId, "primary_weapon", Build()));
        CloseTo(Stats(both, Context((GamePack.WeaponSlotSelector, "primary_weapon"), ("lady_death_next_shot_active", "true"))).Of("weapon_damage_multiplier"), 1.75, 1e-12);
    }

    /// <summary>G47: Chatterbox's stacks add rate of fire (not the card's RPM) on its own weapon, once when duplicated.</summary>
    [Fact]
    public void G47ChatterboxIncessantChatter()
    {
        const string talentId = "weapon_talent_incessant_chatter";
        foreach (var slot in new[] { "primary_weapon", "secondary_weapon" })
        {
            var b = WeaponBuild(talentId, slot, Build());
            var baseline = Resolve(b, Division.Weapon(slot));
            foreach (var stacks in new[] { 0, 1, 3, 5 })
            {
                var active = Resolve(b, Context((GamePack.WeaponSlotSelector, slot), ("chatterbox_stacks", Format(stacks))));
                CloseTo(Delta(active, baseline, "rate_of_fire"), .25 * stacks, 1e-12);
                CloseTo(active.Calculation.Stats.Of("weapon_rpm"), baseline.Calculation.Stats.Of("weapon_rpm"), 1e-12);
                var (value, found) = Contribution(active, "rate_of_fire", talentId);
                Assert.True(found, "missing Chatterbox provenance");
                CloseTo(value, .25 * stacks, 1e-12);
            }
            CloseTo(Stats(b, Context((GamePack.WeaponSlotSelector, Other(slot)), ("chatterbox_stacks", "5"))).Of("rate_of_fire"), 0, 1e-12);
            foreach (var invalid in new[] { "-1", "0.5", "6", "NaN" })
            {
                var result = Resolve(b, Context((GamePack.WeaponSlotSelector, slot), ("chatterbox_stacks", invalid)));
                ExpectInvalidContext(result, "chatterbox_stacks");
                CloseTo(Delta(result, baseline, "rate_of_fire"), 0, 1e-12);
            }
        }
        var both = WeaponBuild(talentId, "secondary_weapon", WeaponBuild(talentId, "primary_weapon", Build()));
        CloseTo(Stats(both, Context((GamePack.WeaponSlotSelector, "primary_weapon"), ("chatterbox_stacks", "5"))).Of("rate_of_fire"), 1.25, 1e-12);
    }

    /// <summary>G47: exotic assumptions stay out of a shared build, so the decoded build resolves without them.</summary>
    [Fact]
    public void G47ContextAssumptionsAreTransientThroughBuildSharing()
    {
        var b = GearBuild("gear_talent_kill_confirmed", "backpack");
        var active = Stats(b, Context((GamePack.WeaponSlotSelector, "primary_weapon"), ("memento_trophy_stacks", "30")));
        var decoded = ShareCode.Decode(ShareCode.Encode(b));
        // Go marshalled the decoded build to JSON; the build document is this repo's JSON form of a build.
        var serialized = Encoding.UTF8.GetString(BuildDocument.Encode(decoded));
        foreach (var key in new[] { "memento_trophy_stacks", "liberty_stacks", "chatterbox_stacks", "capacitance_stacks", "capacitor_skill_tier", "sawyer_stacks", "chameleon_headshot_buff_active", "sawyer_active", "lady_death_next_shot_active" })
        {
            Assert.False(serialized.Contains(key, StringComparison.Ordinal), $"context assumption {key} leaked into decoded build: {serialized}");
        }
        CloseTo(Stats(decoded, Division.Weapon("primary_weapon")).Of("weapon_damage"), 0, 1e-12);
        CloseTo(Stats(decoded, Division.Weapon("primary_weapon")).Of("skill_efficiency"), 0, 1e-12);
        CloseTo(active.Of("weapon_damage"), .30, 1e-12);
    }

    /// <summary>G53: each exotic gear talent is fixed to its one host in its slot, and contributes with talent provenance.</summary>
    [Fact]
    public void G53ExoticGearHostsAndTalentProvenance()
    {
        foreach (var (name, contract) in Contracts)
        {
            var talentId = TalentId(name);
            var hostItems = Compiled.Pack.Items.Where(item => item.TalentSlots.Any(slot => slot.Choices.Contains(talentId))).ToArray();
            // Go appended a host once per talent slot offering it; an item offering it twice would show twice.
            var hosts = Compiled.Pack.Items.SelectMany(item => item.TalentSlots.Where(slot => slot.Choices.Contains(talentId)).Select(_ => item.ID)).Order(StringComparer.Ordinal).ToArray();
            Assert.True(hosts.SequenceEqual([contract.Item]), $"{name} fixed hosts = [{string.Join(", ", hosts)}], want [{contract.Item}]");
            Assert.True(hostItems.Any(item => GearSlots.Contains(item.Slot) && item.Slot == contract.Slot), $"{name} is not available in {contract.Slot}");
        }
        foreach (var (name, contract) in Contracts)
        {
            var b = G53Build(name);
            var context = name switch
            {
                "Bond" => Context(("bond_proximity_buff", "ally")),
                "Chemical Agent" => Context(("catalysis_stacks", "12")),
                "Counter" => Context(("counter_buff_group", "offense")),
                _ => Context(),
            };
            var active = Resolve(b, context);
            Assert.True(Contribution(active, contract.Stat, TalentId(name)).Found, $"missing talent provenance for {name} on {contract.Stat}");
        }
    }

    /// <summary>G53: Chemical Agent's Catalysis stacks are bounded, and Bond's proximity buffs are exclusive.</summary>
    [Fact]
    public void G53ChemicalAgentAndBondAssumptionsAreBoundedAndExclusive()
    {
        var b = G53Build("Chemical Agent");
        var baseline = Resolve(G53WithoutTalent(b, "Chemical Agent"));
        foreach (var (stacks, want) in new[] { ("0", 0.0), ("12", .24) })
        {
            var active = Resolve(b, Context(("catalysis_stacks", stacks)));
            CloseTo(Delta(active, baseline, "weapon_damage"), want, 1e-12);
            CloseTo(Delta(active, baseline, "status_effects"), want, 1e-12);
        }
        var invalid = Resolve(b, Context(("catalysis_stacks", "13")));
        ExpectInvalidContext(invalid, "catalysis_stacks");
        CloseTo(Delta(invalid, baseline, "weapon_damage"), 0, 1e-12);

        var bondBuild = G53Build("Bond");
        var noBond = Resolve(G53WithoutTalent(bondBuild, "Bond"));
        foreach (var (value, chd, efficiency, armorRegen) in new[] { ("ally", .30, .15, .02), ("skill", .15, .075, .01) })
        {
            var active = Resolve(bondBuild, Context(("bond_proximity_buff", value)));
            CloseTo(Delta(active, noBond, "critical_hit_damage"), chd, 1e-12);
            CloseTo(Delta(active, noBond, "skill_efficiency"), efficiency, 1e-12);
            CloseTo(Delta(active, noBond, "armor_regeneration_percent"), armorRegen, 1e-12);
        }
        var unknown = Resolve(bondBuild, Context(("bond_proximity_buff", "unknown")));
        Assert.True(Delta(unknown, noBond, "critical_hit_damage") == 0, "unknown Bond proximity selection activated a contribution");
    }

    /// <summary>
    /// G53: Counter's offense and handling groups are exclusive; Acosta's passives are always on and its Skill Tier buff
    /// lifts a five-tier build's preview to the cap of six.
    /// </summary>
    [Fact]
    public void G53CounterGroupsAreExclusiveAndAcostaPassivesAreAlwaysOn()
    {
        var b = G53Build("Counter");
        var withoutCounter = Resolve(G53WithoutTalent(b, "Counter"));
        foreach (var (group, stat, want) in new[] { ("offense", "weapon_damage", .20), ("offense", "rate_of_fire", .20), ("handling", "magazine_size_percent", .50), ("handling", "reload_speed", .50) })
        {
            CloseTo(Delta(Resolve(b, Context(("counter_buff_group", group))), withoutCounter, stat), want, 1e-12);
        }
        var offense = Resolve(b, Context(("counter_buff_group", "offense")));
        CloseTo(Delta(offense, withoutCounter, "magazine_size_percent"), 0, 1e-12);
        CloseTo(Delta(offense, withoutCounter, "reload_speed"), 0, 1e-12);
        var handling = Resolve(b, Context(("counter_buff_group", "handling")));
        CloseTo(Delta(handling, withoutCounter, "weapon_damage"), 0, 1e-12);
        CloseTo(Delta(handling, withoutCounter, "rate_of_fire"), 0, 1e-12);

        var acostaBuild = G53Build(Acosta);
        var withoutAcosta = Resolve(G53WithoutTalent(acostaBuild, Acosta));
        var passive = Resolve(acostaBuild);
        foreach (var (stat, want) in new[] { ("ammo_capacity", .25), ("repair_skills", .10), ("status_effects", .10) })
        {
            CloseTo(Delta(passive, withoutAcosta, stat), want, 1e-12);
        }
        CloseTo(Delta(passive, withoutAcosta, "skill_overcharge"), 0, 1e-12);
        CloseTo(Delta(Resolve(acostaBuild, Context(("acosta_skill_tier_active", "true"))), withoutAcosta, "skill_overcharge"), 1, 1e-12);

        // Acosta's fixed yellow core plus four configured yellow cores gives a
        // pre-buff Skill Tier of five; the asserted buff reaches the preview cap.
        var withSkill = acostaBuild.WithActiveSkillSelection("skill_slot_1", "skill_assault_turret");
        var (coreId, coreMax) = Attribute("skill_tier_gear_core");
        Assert.True(coreId.Length > 0 && coreMax >= 1, "missing Skill Tier gear core");
        foreach (var gearSlot in new[] { "mask", "chest", "holster", "gloves" })
        {
            var (host, attributeSlot) = CoreHost(gearSlot, coreId);
            withSkill = withSkill.WithEquipment(gearSlot, host.ID).WithAttributeRoll(gearSlot, attributeSlot, new(coreId, coreMax));
        }
        var backpackCore = Compiled.Pack.Items.Where(item => item.ID == Contracts[Acosta].Item).SelectMany(item => item.AttributeSlots)
            .LastOrDefault(slot => slot.Choices.Any(choice => choice.AttributeID == coreId))?.ID ?? "";
        Assert.True(backpackCore.Length > 0, "Acosta backpack has no Skill Tier gear core");
        withSkill = withSkill.WithAttributeRoll("backpack", backpackCore, new(coreId, coreMax));
        var noBuff = Resolve(withSkill);
        var withBuff = Resolve(withSkill, Context(("acosta_skill_tier_active", "true")));
        CloseTo(noBuff.Calculation.Stats.Of("skill_tier"), 5, 1e-12);
        CloseTo(withBuff.Calculation.Stats.Of("skill_overcharge"), 1, 1e-12);
        var got = PreviewValue(withBuff, "skill_slot_1", "effective_skill_tier");
        Assert.True(got == 6, $"Acosta effective Skill Tier = {got}, want cap 6");
    }

    /// <summary>G53: the exotic gear assumptions are gear-wide, whether or not a weapon is active, and keep the weapon preview.</summary>
    [Fact]
    public void G53AssumptionsRemainGearWideAndDoNotChangeCombatOutputs()
    {
        var cases = new (string Name, ImmutableDictionary<string, string> Context, string[] Stats)[]
        {
            ("Bond", Context(("bond_proximity_buff", "ally")), ["critical_hit_damage", "skill_efficiency", "armor_regeneration_percent"]),
            ("Chemical Agent", Context(("catalysis_stacks", "12")), ["weapon_damage", "status_effects"]),
            ("Counter", Context(("counter_buff_group", "offense")), ["weapon_damage", "rate_of_fire"]),
            (Acosta, Context(), ["ammo_capacity", "repair_skills", "status_effects"]),
        };
        foreach (var tc in cases)
        {
            var b = G53Build(tc.Name);
            var withoutWeapon = Stats(b, tc.Context);
            Assert.True(withoutWeapon.Of("equipped_weapon_damage") == 0, $"{tc.Name} weapon damage preview without selected weapon = {withoutWeapon.Of("equipped_weapon_damage")}, want 0");
            foreach (var weaponSlot in new[] { "primary_weapon", "sidearm" })
            {
                var withWeapon = Stats(b, tc.Context.SetItem(GamePack.WeaponSlotSelector, weaponSlot));
                foreach (var stat in tc.Stats)
                {
                    CloseTo(withoutWeapon.Of(stat), withWeapon.Of(stat), 1e-12);
                }
                Assert.True(withWeapon.Of("equipped_weapon_damage") > 0, $"{tc.Name} selected weapon {weaponSlot} should retain a damage preview");
            }
        }
    }

    /// <summary>G53: Chemical Agent's and Counter's weapon damage adds to a configured Weapon Damage core in the weapon's damage category.</summary>
    [Fact]
    public void G53WeaponDamageExoticsAddAlongsideOneConfiguredCore()
    {
        var (coreId, coreMax) = Attribute("weapon_damage_gear_core");
        Assert.True(coreId.Length > 0 && coreMax > 0, "missing Weapon Damage gear core");
        foreach (var (name, context, bonus) in new[] { ("Chemical Agent", Context(("catalysis_stacks", "12")), .24), ("Counter", Context(("counter_buff_group", "offense")), .20) })
        {
            var (gloves, coreSlot) = CoreHost("gloves", coreId);
            var b = G53Build(name).WithEquipment("gloves", gloves.ID).WithAttributeRoll("gloves", coreSlot, new(coreId, coreMax));
            var baseline = Stats(G53WithoutTalent(b, name), context);
            var active = Stats(b, context);
            Assert.True(baseline.Of("weapon_damage") >= coreMax, $"{name} baseline Weapon Damage = {baseline.Of("weapon_damage")}, expected configured core contribution at least {coreMax}");
            CloseTo(active.Of("weapon_damage") - baseline.Of("weapon_damage"), bonus, 1e-12);
            CloseTo(active.Of("weapon_damage"), baseline.Of("weapon_damage") + bonus, 1e-12);

            var baselineWeapon = Stats(G53WithoutTalent(b, name), Division.Weapon("primary_weapon"));
            var activeWeapon = Stats(b, context.SetItem(GamePack.WeaponSlotSelector, "primary_weapon"));
            var wantRatio = (1 + activeWeapon.Of("weapon_damage") + activeWeapon.Of("assault_rifle_damage")) / (1 + baselineWeapon.Of("weapon_damage") + baselineWeapon.Of("assault_rifle_damage"));
            CloseTo(activeWeapon.Of("equipped_weapon_damage") / baselineWeapon.Of("equipped_weapon_damage"), wantRatio, 1e-12);
            foreach (var card in CardStats)
            {
                CloseTo(activeWeapon.Of(card), baselineWeapon.Of(card), 1e-12);
            }
        }
    }

    /// <summary>
    /// G53: Bond's, Counter's and Acosta's non-weapon bonuses change only their raw stats and the skill preview outputs
    /// those stats feed; Chemical Agent's weapon damage scales the damage preview in its category.
    /// </summary>
    [Fact]
    public void G53RawNonWeaponBonusesDoNotCreateDerivedOutputs()
    {
        var cases = new (string Name, ImmutableDictionary<string, string> Context, (string Stat, double Want)[] Changed, string[] Unchanged)[]
        {
            ("Bond", Context(("bond_proximity_buff", "ally")), [("skill_efficiency", .15), ("armor_regeneration_percent", .02)],
                ["skill_damage", "skill_haste", "repair_skills", "skill_duration", "total_skill_repair", "character_armor", "character_health"]),
            ("Counter", Context(("counter_buff_group", "handling")), [("magazine_size_percent", .5), ("reload_speed", .5)],
                ["weapon_damage", "rate_of_fire", "character_armor", "character_health"]),
            (Acosta, Context(), [("repair_skills", .1), ("status_effects", .1)],
                ["skill_damage", "skill_haste", "skill_duration", "skill_efficiency", "total_skill_repair", "character_armor", "character_health"]),
        };
        foreach (var tc in cases)
        {
            var b = G53Build(tc.Name).WithActiveSkillSelection("skill_slot_1", "skill_assault_turret");
            var baseline = Resolve(G53WithoutTalent(b, tc.Name), Division.Weapon("primary_weapon"));
            var active = Resolve(b, tc.Context.SetItem(GamePack.WeaponSlotSelector, "primary_weapon"));
            foreach (var (stat, want) in tc.Changed)
            {
                CloseTo(Delta(active, baseline, stat), want, 1e-12);
            }
            foreach (var stat in tc.Unchanged)
            {
                CloseTo(active.Calculation.Stats.Of(stat), baseline.Calculation.Stats.Of(stat), 1e-12);
            }
            CloseTo(active.Calculation.Stats.Of("equipped_weapon_damage"), baseline.Calculation.Stats.Of("equipped_weapon_damage"), 1e-12);
            var basePreview = NonDamageOutputs(baseline, "skill_slot_1");
            var activePreview = NonDamageOutputs(active, "skill_slot_1");
            Assert.True(activePreview.Length == basePreview.Length, $"{tc.Name} changed selected-skill output count from {basePreview.Length} to {activePreview.Length}");
            for (var index = 0; index < basePreview.Length; index++)
            {
                SkillPreviewOutput a = activePreview[index], o = basePreview[index];
                if (tc.Name == "Bond" && a.Id == "duration_seconds")
                {
                    CloseTo(a.Value, o.Value * (1 + active.Calculation.Stats.Of("effective_skill_duration")) / (1 + baseline.Calculation.Stats.Of("effective_skill_duration")), 1e-8);
                    continue;
                }
                if (tc.Name == "Bond" && a.Id == "skill_health")
                {
                    CloseTo(a.Value, o.Value * (1 + active.Calculation.Stats.Of("effective_skill_health")) / (1 + baseline.Calculation.Stats.Of("effective_skill_health")), 1e-6);
                    continue;
                }
                if (tc.Name == "Bond" && a.Id == "cooldown_seconds")
                {
                    CloseTo(a.Value, 25 / (1 + baseline.Calculation.Stats.Of("effective_skill_haste") + .15), 1e-12);
                    continue;
                }
                if (tc.Name == "Bond" && a.Id == "cooldown_factor")
                {
                    CloseTo(a.Value, 1 / (1 + baseline.Calculation.Stats.Of("effective_skill_haste") + .15), 1e-12);
                    continue;
                }
                Assert.True(a.Id == o.Id && a.Value == o.Value, $"{tc.Name} changed skill preview output {o.Id} from {o.Value} to {a.Value}");
            }
        }
        var chemical = G53Build("Chemical Agent");
        var chemicalBaseline = Stats(G53WithoutTalent(chemical, "Chemical Agent"), Division.Weapon("primary_weapon"));
        var chemicalActive = Stats(chemical, Context((GamePack.WeaponSlotSelector, "primary_weapon"), ("catalysis_stacks", "12")));
        var baseFactor = 1 + chemicalBaseline.Of("weapon_damage") + chemicalBaseline.Of("assault_rifle_damage");
        CloseTo(chemicalActive.Of("equipped_weapon_damage"), chemicalBaseline.Of("equipped_weapon_damage") * (baseFactor + .24) / baseFactor, 1e-12);
    }

    /// <summary>G64: Dragon's Breath's amplifier reads the final Status Effects total, on Fafnir's own weapon only.</summary>
    [Fact]
    public void G64DragonsBreathReadsFinalStatusEffects()
    {
        Assert.True(Compiled.DerivedValueReads.Contains("talents.weapon_talent_dragon_s_breath.modifiers[0].value"), "Dragon's Breath value is not recorded as a derived read");
        var (b, owner) = NamedHostBuild("Fafnir", "weapon_talent_dragon_s_breath");
        var selected = Division.Weapon(owner);
        var baseline = Stats(b, selected);
        // Fafnir's own attributes may already roll Status Effects; the amplifier
        // must track whatever the final total is.
        Close("multiplier", baseline.Of("weapon_damage_multiplier"), 1 + 0.5 * baseline.Of("status_effects"));

        b = b.WithEquipment("mask", "mask_n_5_11_tactical_mask").WithAttributeRoll("mask", "minor_1", new("status_effects_gear_minor", 0.10));
        var boosted = Stats(b, selected);
        Close("status effects", boosted.Of("status_effects") - baseline.Of("status_effects"), 0.10);
        Close("boosted multiplier", boosted.Of("weapon_damage_multiplier"), 1 + 0.5 * boosted.Of("status_effects"));
        Close("unselected Fafnir", Stats(b, Division.Weapon("sidearm")).Of("weapon_damage_multiplier"), 1);
    }

    private static string Other(string slot) => slot == "primary_weapon" ? "secondary_weapon" : "primary_weapon";

    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string TalentId(string name) =>
        Compiled.Pack.Talents.FirstOrDefault(talent => talent.Name == name)?.ID ?? throw new InvalidOperationException($"missing G53 talent \"{name}\"");

    /// <summary>The talent selected on the first item in a weapon slot that offers it, as Go's g47WeaponBuild.</summary>
    private static BuildSnapshot WeaponBuild(string talentId, string slot, BuildSnapshot b)
    {
        foreach (var item in Compiled.Pack.Items.Where(item => item.Slot == slot))
        {
            var talentSlot = item.TalentSlots.FirstOrDefault(talentSlot => talentSlot.Choices.Contains(talentId));
            if (talentSlot is not null) { return b.WithEquipment(slot, item.ID).WithTalentSelection(slot, talentSlot.ID, talentId); }
        }
        throw new InvalidOperationException($"no {slot} host allows {talentId}");
    }

    /// <summary>The talent on the first gear host in a slot, with two ACRs, as Go's g47GearBuild.</summary>
    private static BuildSnapshot GearBuild(string talentId, string slot) =>
        WeaponBuild(talentId, slot, Build()).WithEquipment("primary_weapon", "primary_weapon_acr").WithEquipment("secondary_weapon", "secondary_weapon_acr");

    private static BuildSnapshot ClearTalent(BuildSnapshot b, string slot, string talentId) => b.WithTalentSelection(slot, TalentSlot(b, slot, talentId), "");

    private static BuildSnapshot SelectTalent(BuildSnapshot b, string slot, string talentId) => b.WithTalentSelection(slot, TalentSlot(b, slot, talentId), talentId);

    private static string TalentSlot(BuildSnapshot b, string slot, string talentId)
    {
        var itemId = b.Equipment.GetValueOrDefault(slot, "");
        return Compiled.Pack.Items.Where(item => item.ID == itemId).SelectMany(item => item.TalentSlots).FirstOrDefault(talentSlot => talentSlot.Choices.Contains(talentId))?.ID
            ?? throw new InvalidOperationException($"{talentId} is not allowed on {slot}");
    }

    /// <summary>The first item in each weapon slot, then the ACR and Tempest, with the contract host's talent selected, as Go's g53Build.</summary>
    private static BuildSnapshot G53Build(string name)
    {
        var contract = Contracts[name];
        var b = Build();
        foreach (var slot in WeaponSlots)
        {
            var first = Compiled.Pack.Items.FirstOrDefault(item => item.Slot == slot);
            if (first is not null) { b = b.WithEquipment(slot, first.ID); }
        }
        foreach (var (slot, itemId) in new[] { ("primary_weapon", "primary_weapon_acr"), ("sidearm", "sidearm_tempest") })
        {
            if (Compiled.Pack.Items.Any(item => item.ID == itemId)) { b = b.WithEquipment(slot, itemId); }
        }
        var talentId = TalentId(name);
        var talentSlot = Compiled.Pack.Items.Where(item => item.ID == contract.Item).SelectMany(item => item.TalentSlots).FirstOrDefault(slot => slot.Choices.Contains(talentId))
            ?? throw new InvalidOperationException($"no {contract.Slot} host for {name}");
        return b.WithEquipment(contract.Slot, contract.Item).WithTalentSelection(contract.Slot, talentSlot.ID, talentId);
    }

    private static BuildSnapshot G53WithoutTalent(BuildSnapshot b, string name)
    {
        var contract = Contracts[name];
        var talentId = TalentId(name);
        var talentSlot = Compiled.Pack.Items.Where(item => item.ID == contract.Item).SelectMany(item => item.TalentSlots).FirstOrDefault(slot => slot.Choices.Contains(talentId))
            ?? throw new InvalidOperationException($"cannot clear G53 talent \"{name}\"");
        return b.WithTalentSelection(contract.Slot, talentSlot.ID, "");
    }

    private static double Delta(ResolvedBuild result, ResolvedBuild baseline, string stat) => result.Calculation.Stats.Of(stat) - baseline.Calculation.Stats.Of(stat);

    /// <summary>The talent's summed contributions to a stat and whether it had any, as Go's g47Contribution.</summary>
    private static (double Value, bool Found) Contribution(ResolvedBuild result, string stat, string talentId)
    {
        if (!result.Calculation.Breakdowns.TryGetValue(stat, out var breakdown)) { return (0, false); }
        var contributions = breakdown.Contributions.Where(contribution => contribution.Source.Kind == "talent" && contribution.Source.Id == talentId).ToArray();
        return (contributions.Sum(contribution => contribution.Value), contributions.Length > 0);
    }

    private static void ExpectInvalidContext(ResolvedBuild result, string key) =>
        Assert.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == "context-value-invalid" && diagnostic.Subject == key),
            $"missing context-value-invalid diagnostic for {key}: {string.Join("; ", result.Diagnostics)}");

    private static (string Id, double Max) Attribute(string id) =>
        Compiled.Pack.Attributes.Where(attribute => attribute.ID == id).Select(attribute => (attribute.ID, attribute.Max)).LastOrDefault(("", 0));

    /// <summary>The first item in a slot with a core choice, and its last attribute slot offering it, as Go's loops.</summary>
    private static (ItemDefinition Item, string AttributeSlot) CoreHost(string slot, string attributeId)
    {
        var item = Compiled.Pack.Items.FirstOrDefault(item => item.Slot == slot && item.AttributeSlots.Any(attributeSlot => attributeSlot.Choices.Any(choice => choice.AttributeID == attributeId)))
            ?? throw new InvalidOperationException($"missing {attributeId} host for {slot}");
        return (item, item.AttributeSlots.Last(attributeSlot => attributeSlot.Choices.Any(choice => choice.AttributeID == attributeId)).ID);
    }

    private static double PreviewValue(ResolvedBuild result, string slot, string outputId)
    {
        Assert.True(result.ActiveSkillPreviews.TryGetValue(slot, out var preview), $"missing active skill preview {slot}");
        var output = preview.Outputs.FirstOrDefault(output => output.Id == outputId);
        Assert.True(output is not null, $"missing {outputId} output in {slot} preview");
        return output.Value;
    }

    /// <summary>A slot's preview outputs without damage channels or assumed rates, as Go's nonDamagePreviews.</summary>
    private static SkillPreviewOutput[] NonDamageOutputs(ResolvedBuild result, string slot) =>
        result.ActiveSkillPreviews.TryGetValue(slot, out var preview)
            ? [.. preview.Outputs.Where(output => output.Id is not ("damage" or "average_damage" or "cloud_damage" or "burn_damage" or "bleed_damage" or "explosion_damage")
                && !output.Id.StartsWith("assumed_", StringComparison.Ordinal))]
            : [];

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

    /// <summary>Go's g63Close: within float noise.</summary>
    private static void Close(string label, double got, double want) => Assert.True(Math.Abs(got - want) <= 1e-9, $"{label} = {got}, want {want}");
}
