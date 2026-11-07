using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// Weapon talents gated on the selected weapon and on transient buff, phase and stack assumptions, with their
/// provenance (F04/F05 modifier aggregation, conditional modifiers and provenance; F10 configured equipment
/// talents), ported from Go's talent_effects_g32_test.go, talent_effects_g33_test.go, talent_effects_g34_test.go,
/// talent_effects_g37_test.go and talent_effects_g38_test.go.
/// </summary>
public sealed class WeaponTalentAssumptionTests
{
    /// <summary>
    /// G32: In Sync's two buffs add skill damage and weapon damage while the host weapon is selected, each
    /// doubling the other's bonus, without changing critical or headshot damage or the non-damage skill previews.
    /// </summary>
    [Fact]
    public void G32InSyncBuffAssumptions()
    {
        foreach (var (talent, host, bonus) in new[] { ("weapon_talent_in_sync", "primary_weapon_acr", .15), ("weapon_talent_perfect_in_sync", "primary_weapon_harmony", .20) })
        {
            var b = TalentBuild(talent, host).WithActiveSkillSelection("skill_slot_1", "skill_assault_turret");
            var baseline = Resolve(b, Weapon("primary_weapon"));
            foreach (var (name, weaponBuff, skillBuff, wantSkillDamage, wantWeaponDamage) in new[]
            {
                ("neither buff", "false", "false", 0, 0),
                ("weapon hit buff", "true", "false", bonus, 0),
                ("skill trigger buff", "false", "true", 0, bonus),
                ("both buffs double each bonus", "true", "true", 2 * bonus, 2 * bonus),
            })
            {
                var what = $"{talent} {name}";
                var result = Resolve(b, Context(("weapon_slot", "primary_weapon"), ("in_sync_weapon_hit_active", weaponBuff), ("in_sync_skill_trigger_active", skillBuff)));
                var s = result.Calculation.Stats;
                NoErrors(result, what);
                Near(s.Of("skill_damage"), baseline.Calculation.Stats.Of("skill_damage") + wantSkillDamage, 1e-12, $"{what} skill_damage");
                Near(s.Of("weapon_damage"), baseline.Calculation.Stats.Of("weapon_damage") + wantWeaponDamage, 1e-12, $"{what} weapon_damage");
                Near(s.Of("critical_hit_damage"), baseline.Calculation.Stats.Of("critical_hit_damage"), 1e-12, $"{what} critical_hit_damage");
                Near(s.Of("headshot_damage"), baseline.Calculation.Stats.Of("headshot_damage"), 1e-12, $"{what} headshot_damage");
                Assert.True(SamePreviews(result.ActiveSkillPreviews, baseline.ActiveSkillPreviews, nonDamageOnly: true), $"{what} changed the active skill preview profile");
                var wantPreview = s.Of("weapon_base_damage") * (1 + s.Of("weapon_damage") + s.Of("assault_rifle_damage")) * s.Of("weapon_damage_multiplier") * (1 + s.Of("total_weapon_damage"));
                Near(s.Of("equipped_weapon_damage"), wantPreview, 1e-8, $"{what} equipped_weapon_damage");
                Assert.True(wantSkillDamage == 0 || HasContribution(result, "skill_damage", "talent", talent), $"{what}: skill damage has no {talent} provenance");
                Assert.True(wantWeaponDamage == 0 || HasContribution(result, "weapon_damage", "talent", talent), $"{what}: weapon damage has no {talent} provenance");
            }

            foreach (var context in new[]
            {
                Context(("in_sync_weapon_hit_active", "true"), ("in_sync_skill_trigger_active", "true")),
                Context(("weapon_slot", "secondary_weapon"), ("in_sync_weapon_hit_active", "true"), ("in_sync_skill_trigger_active", "true")),
            })
            {
                var inactive = Resolve(b, context);
                var what = $"{talent} with weapon_slot={context.GetValueOrDefault("weapon_slot", "")}";
                Near(inactive.Calculation.Stats.Of("skill_damage"), baseline.Calculation.Stats.Of("skill_damage"), 1e-12, $"{what} skill_damage");
                Near(inactive.Calculation.Stats.Of("weapon_damage"), baseline.Calculation.Stats.Of("weapon_damage"), 1e-12, $"{what} weapon_damage");
                Assert.False(HasContribution(inactive, "skill_damage", "talent", talent) || HasContribution(inactive, "weapon_damage", "talent", talent), $"{what}: affected an unselected weapon");
            }
        }
    }

    /// <summary>
    /// G32: Close &amp; Personal adds additive weapon damage only on its selected host weapon, and composes with the
    /// gear core, Watch, weapon Expertise, Glass Cannon and Vigilance in their own pools.
    /// </summary>
    [Fact]
    public void G32ClosePersonalWeaponDamageComposition()
    {
        foreach (var (talent, host, slot, bonus) in new[]
        {
            ("weapon_talent_close_personal", "primary_weapon_acr", "primary_weapon", .30),
            ("weapon_talent_perfect_close_personal", "primary_weapon_savage_wolverine", "primary_weapon", .38),
            ("weapon_talent_perfect_close_personal", "secondary_weapon_old_glory", "secondary_weapon", .38),
        })
        {
            var what = $"{talent} on {host}";
            var b = TalentBuild(talent, host);
            var inactive = Resolve(b, Context(("weapon_slot", slot), ("close_personal_active", "false")));
            var active = Resolve(b, Context(("weapon_slot", slot), ("close_personal_active", "true")));
            NoErrors(active, what);
            Near(active.Calculation.Stats.Of("weapon_damage"), inactive.Calculation.Stats.Of("weapon_damage") + bonus, 1e-12, $"{what} weapon_damage");
            Assert.True(HasContribution(active, "weapon_damage", "talent", talent), $"{what}: weapon damage has no {talent} provenance");
            var wrongSlot = slot == "secondary_weapon" ? "primary_weapon" : "secondary_weapon";
            var wrongWeapon = Resolve(b, Context(("weapon_slot", wrongSlot), ("close_personal_active", "true")));
            Near(wrongWeapon.Calculation.Stats.Of("weapon_damage"), inactive.Calculation.Stats.Of("weapon_damage"), 1e-12, $"{what} on {wrongSlot}");
            Assert.False(HasContribution(wrongWeapon, "weapon_damage", "talent", talent), $"{what} leaked to the unselected weapon");

            if (talent == "weapon_talent_close_personal")
            {
                b = b.WithAccountUpgrade("watch_weapon_damage", 50)
                    .WithEquipmentUpgrade("primary_weapon", host, 20)
                    .WithAttributeRoll("primary_weapon", "core_1", new("assault_rifle_damage_weapon_core", .15))
                    .WithEquipment("chest", "chest_n_5_11_tactical_chest")
                    .WithTalentSelection("chest", "talent", "gear_talent_glass_cannon")
                    .WithEquipment("backpack", "backpack_n_5_11_tactical_backpack")
                    .WithTalentSelection("backpack", "talent", "gear_talent_vigilance");
                var combined = Resolve(b, Context(("weapon_slot", "primary_weapon"), ("close_personal_active", "true"), ("vigilance_active", "true")));
                NoErrors(combined, "combined");
                var s = combined.Calculation.Stats;
                // Gear core .15 + Watch .10 + weapon Expertise .20 + Close & Personal .30.
                Near(s.Of("weapon_damage"), .75, 1e-12, "combined weapon_damage");
                Near(s.Of("total_weapon_damage"), .25, 1e-12, "combined total_weapon_damage");
                Near(s.Of("weapon_damage_multiplier"), 1.25, 1e-12, "combined weapon_damage_multiplier");
                Near(s.Of("equipped_weapon_damage"), s.Of("weapon_base_damage") * (1 + .75 + .15) * 1.25 * (1 + .25), 1e-8, "combined equipped_weapon_damage");
            }
        }
    }

    /// <summary>
    /// G32: Breadbasket adds headshot damage per retained stack up to its cap on the selected host weapon; invalid
    /// stack values are diagnosed and ignored, and the stacks don't survive the share code.
    /// </summary>
    [Fact]
    public void G32BreadbasketHeadshotDamageAssumption()
    {
        foreach (var (talent, bonus, cap) in new[] { ("weapon_talent_breadbasket", .55, 3.0), ("weapon_talent_perfect_breadbasket", .70, 2.0) })
        {
            var (host, slot) = talent == "weapon_talent_perfect_breadbasket" ? ("sidearm_mozambique_special", "sidearm") : ("primary_weapon_acr", "primary_weapon");
            var b = TalentBuild(talent, host);
            var baseline = Resolve(b, Weapon(slot));
            var baseHeadshot = baseline.Calculation.Stats.Of("headshot_damage");
            foreach (var stacks in new[] { 0.0, 1, 2, 3 })
            {
                var what = $"{talent} at {stacks} stacks";
                var result = Resolve(b, Context(("weapon_slot", slot), ("breadbasket_stacks", Format(stacks))));
                NoErrors(result, what);
                var applied = Math.Min(stacks, cap);
                Near(result.Calculation.Stats.Of("headshot_damage"), baseHeadshot + bonus * applied, 1e-12, $"{what} headshot_damage");
                Near(result.Calculation.Stats.Of("weapon_damage"), baseline.Calculation.Stats.Of("weapon_damage"), 1e-12, $"{what} weapon_damage");
                Near(result.Calculation.Stats.Of("equipped_weapon_damage"), baseline.Calculation.Stats.Of("equipped_weapon_damage"), 1e-8, $"{what} equipped_weapon_damage");
                Assert.True(applied == 0 || HasContribution(result, "headshot_damage", "talent", talent), $"{what}: headshot damage has no provenance");
            }

            foreach (var raw in new[] { "-1", "1.5", "4", "NaN" })
            {
                var invalid = Resolve(b, Context(("weapon_slot", slot), ("breadbasket_stacks", raw)));
                Assert.True(HasDiagnostic(invalid, "context-value-invalid", "breadbasket_stacks"), $"{talent}: invalid stack value \"{raw}\" lacks a diagnostic");
                Near(invalid.Calculation.Stats.Of("headshot_damage"), baseHeadshot, 1e-12, $"{talent} with invalid \"{raw}\"");
            }
            var wrongSlot = slot != "sidearm" ? "sidearm" : "secondary_weapon";
            Near(Stats(b, Context(("weapon_slot", wrongSlot), ("breadbasket_stacks", "3"))).Of("headshot_damage"), baseHeadshot, 1e-12, $"{talent} on {wrongSlot}");

            var decoded = ShareCode.Decode(ShareCode.Encode(b));
            Near(Stats(decoded, Weapon(slot)).Of("headshot_damage"), baseHeadshot, 1e-12, $"{talent} after the share-code round trip");
        }
    }

    /// <summary>
    /// G33: Spike adds skill damage and Reformation adds repair skills while active on their selected host weapon,
    /// beside the Watch, without changing the other skill or weapon stats or the non-damage skill previews.
    /// </summary>
    [Fact]
    public void G33SpikeAndReformationContextAssumptions()
    {
        foreach (var (talent, host, slot, flag, stat, bonus) in new[]
        {
            ("weapon_talent_spike", "primary_weapon_acr", "primary_weapon", "spike_active", "skill_damage", .20),
            ("weapon_talent_spike", "secondary_weapon_acr", "secondary_weapon", "spike_active", "skill_damage", .20),
            ("weapon_talent_perfect_spike", "primary_weapon_surge", "primary_weapon", "spike_active", "skill_damage", .25),
            ("weapon_talent_perfect_spike", "secondary_weapon_surge", "secondary_weapon", "spike_active", "skill_damage", .25),
            ("weapon_talent_perfect_spike", "sidearm_maxim_9", "sidearm", "spike_active", "skill_damage", .25),
            ("weapon_talent_reformation", "primary_weapon_acr", "primary_weapon", "reformation_active", "repair_skills", .60),
            ("weapon_talent_reformation", "secondary_weapon_acr", "secondary_weapon", "reformation_active", "repair_skills", .60),
            ("weapon_talent_perfect_reformation", "primary_weapon_designated_hitter", "primary_weapon", "reformation_active", "repair_skills", .80),
        })
        {
            var what = $"{talent} on {host}";
            var b = TalentBuild(talent, host).WithActiveSkillSelection("skill_slot_1", "skill_assault_turret")
                .WithAccountUpgrade(stat == "repair_skills" ? "watch_repair_skills" : "watch_skill_damage", 50);
            var baseline = Resolve(b, Weapon(slot));
            var bs = baseline.Calculation.Stats;
            Near(bs.Of(stat), .10, 1e-12, $"{what} baseline {stat}");
            var wrongSlot = slot == "primary_weapon" ? "secondary_weapon" : "primary_weapon";
            foreach (var context in new[] { Context(("weapon_slot", slot), (flag, "false")), Context(("weapon_slot", wrongSlot), (flag, "true")) })
            {
                var inactive = Resolve(b, context);
                Near(inactive.Calculation.Stats.Of(stat), bs.Of(stat), 1e-12, $"{what} inactive on {context["weapon_slot"]}");
                Assert.False(HasContribution(inactive, stat, "talent", talent), $"inactive {what} contributed to {stat}");
            }

            var active = Resolve(b, Context(("weapon_slot", slot), (flag, "true")));
            NoErrors(active, what);
            Near(active.Calculation.Stats.Of(stat), bs.Of(stat) + bonus, 1e-12, $"{what} {stat}");
            Near(active.Calculation.Stats.Of("equipped_weapon_damage"), bs.Of("equipped_weapon_damage"), 1e-8, $"{what} equipped_weapon_damage");
            foreach (var other in new[] { "skill_damage", "repair_skills", "skill_haste", "weapon_damage" })
            {
                if (other != stat) { Near(active.Calculation.Stats.Of(other), bs.Of(other), 1e-12, $"{what} {other}"); }
            }
            Assert.True(SamePreviews(active.ActiveSkillPreviews, baseline.ActiveSkillPreviews, nonDamageOnly: true), $"{what} changed active skill preview outputs");
            Assert.True(HasContribution(active, stat, "talent", talent), $"{what} contribution is missing from {stat} provenance");
        }
    }

    /// <summary>
    /// G33: Future Perfect's retained stacks raise skill tier up to its cap on the selected host weapon, which the
    /// Reviver Hive preview reads (capped at tier six) while skill haste stays put; invalid stacks are diagnosed,
    /// the raw tier isn't capped, and the stacks don't survive the share code.
    /// </summary>
    [Fact]
    public void G33FuturePerfectSkillTierAssumptionAndPreviews()
    {
        foreach (var (talent, host, slot, cap) in new[]
        {
            ("weapon_talent_future_perfect", "primary_weapon_acr", "primary_weapon", 3.0),
            ("weapon_talent_future_perfect", "secondary_weapon_acr", "secondary_weapon", 3.0),
            ("weapon_talent_future_perfection", "primary_weapon_mechanical_animal", "primary_weapon", 4.0),
            ("weapon_talent_future_perfection", "secondary_weapon_mechanical_animal", "secondary_weapon", 4.0),
            ("weapon_talent_future_perfection", "primary_weapon_scalpel", "primary_weapon", 4.0),
            ("weapon_talent_future_perfection", "secondary_weapon_scalpel", "secondary_weapon", 4.0),
        })
        {
            var what = $"{talent}/{host}";
            var b = TalentBuild(talent, host)
                .WithSpecialization("technician")
                .WithSpecializationNode("technician_amped_tier_1", true)
                .WithActiveSkillSelection("skill_slot_1", "skill_reviver_hive")
                .WithActiveSkillSelection("skill_slot_2", "skill_assault_turret");
            var baseline = Resolve(b, Weapon(slot));
            var bs = baseline.Calculation.Stats;
            Assert.True(bs.Of("skill_tier") == 1, $"{what}: Technician baseline skill tier = {bs.Of("skill_tier")}, want 1");
            Assert.True(HasContribution(baseline, "skill_tier", "specialization-node", "technician_amped_tier_1"), $"{what}: Technician Amped contribution is missing from the skill tier baseline");
            var basePreview = Outputs(baseline, "skill_slot_1");
            Assert.True(basePreview.Length >= 2 && basePreview[0].Value == 1 && basePreview[1].Value == 2, $"{what}: baseline Reviver preview = {Describe(basePreview)}, want effective tier 1 and two charges");
            var baseCooldown = Outputs(baseline, "skill_slot_2");
            Assert.True(baseCooldown.Length >= 3 && baseCooldown[1].Id == "cooldown_factor", $"{what}: baseline Assault Turret preview = {Describe(baseCooldown)}, want a cooldown factor");

            foreach (var stacks in new[] { 0.0, 1, 2, 3, 4 })
            {
                var at = $"{what} at {stacks} stacks";
                var result = Resolve(b, Context(("weapon_slot", slot), ("future_perfect_stacks", Format(stacks))));
                NoErrors(result, at);
                var applied = Math.Min(stacks, cap);
                var s = result.Calculation.Stats;
                Near(s.Of("skill_tier"), bs.Of("skill_tier") + applied, 1e-12, $"{at} skill_tier");
                Near(s.Of("skill_damage"), bs.Of("skill_damage"), 1e-12, $"{at} skill_damage");
                Near(s.Of("repair_skills"), bs.Of("repair_skills"), 1e-12, $"{at} repair_skills");
                Near(s.Of("skill_haste"), bs.Of("skill_haste"), 1e-12, $"{at} skill_haste");
                var preview = Outputs(result, "skill_slot_1");
                var wantEffective = Math.Min(bs.Of("skill_tier") + applied, 6);
                Assert.True(preview.Length >= 2 && preview[0].Value == wantEffective && preview[1].Value == 1 + wantEffective,
                    $"{at}: Reviver preview = {Describe(preview)}, want effective tier {wantEffective} and {1 + wantEffective} charges");
                var cooldown = Outputs(result, "skill_slot_2");
                Assert.True(cooldown.Length >= 3 && cooldown[1].Value == baseCooldown[1].Value, $"{at} changed skill haste cooldown factor: {Describe(cooldown)}, baseline {Describe(baseCooldown)}");
                Assert.True(applied == 0 || HasContribution(result, "skill_tier", "talent", talent), $"{at}: skill tier has no provenance");
            }

            foreach (var raw in new[] { "-1", "1.5", "5", "NaN" })
            {
                var invalid = Resolve(b, Context(("weapon_slot", slot), ("future_perfect_stacks", raw)));
                Assert.True(HasDiagnostic(invalid, "context-value-invalid", "future_perfect_stacks"), $"{what}: invalid stack value \"{raw}\" lacks a diagnostic");
                Near(invalid.Calculation.Stats.Of("skill_tier"), bs.Of("skill_tier"), 1e-12, $"{what} with invalid \"{raw}\"");
            }

            var wrongSlot = slot == "primary_weapon" ? "secondary_weapon" : "primary_weapon";
            var wrongWeapon = Resolve(b, Context(("weapon_slot", wrongSlot), ("future_perfect_stacks", "4")));
            Near(wrongWeapon.Calculation.Stats.Of("skill_tier"), bs.Of("skill_tier"), 1e-12, $"{what} on {wrongSlot}");
            Assert.False(HasContribution(wrongWeapon, "skill_tier", "talent", talent), $"{what} contributed while its host weapon was unselected");

            b = AddSixGearSkillCores(b);
            var overTier = Resolve(b, Context(("weapon_slot", slot), ("future_perfect_stacks", Format(cap))));
            Assert.True(overTier.Calculation.Stats.Of("skill_tier") > 6, $"{what}: raw skill tier = {overTier.Calculation.Stats.Of("skill_tier")}, want uncapped bonus above six");
            var overPreview = Outputs(overTier, "skill_slot_1");
            Assert.True(overPreview.Length >= 2 && overPreview[0].Value == 6 && overPreview[1].Value == 7, $"{what}: over-tier Reviver preview = {Describe(overPreview)}, want tier cap 6, seven charges, no overcharge output");

            var decoded = ShareCode.Decode(ShareCode.Encode(b));
            Assert.True(Stats(decoded, Weapon(slot)).Of("skill_tier") == Stats(decoded, Context(("weapon_slot", slot), ("future_perfect_stacks", "0"))).Of("skill_tier"),
                $"{what}: transient Future Perfect stacks unexpectedly survived the share-code round trip");
        }
    }

    /// <summary>
    /// G34: Measured's top phase trades weapon damage for rate of fire and its bottom phase trades rate of fire for
    /// total weapon damage, only for an exact phase on the selected host weapon, and the phase doesn't survive the
    /// share code.
    /// </summary>
    [Fact]
    public void G34MeasuredTalentPhaseAssumptions()
    {
        foreach (var (talent, slot, item, topDamage, bottomTwd) in new[]
        {
            ("weapon_talent_measured", "primary_weapon", "primary_weapon_acr", -.25, .30),
            ("weapon_talent_measured", "secondary_weapon", "secondary_weapon_acr", -.25, .30),
            ("weapon_talent_perfect_measured", "primary_weapon", "primary_weapon_gear_shift", -.30, .38),
            ("weapon_talent_perfect_measured", "secondary_weapon", "secondary_weapon_gear_shift", -.30, .38),
            ("weapon_talent_perfect_measured", "primary_weapon", "primary_weapon_the_apartment", -.30, .38),
            ("weapon_talent_perfect_measured", "secondary_weapon", "secondary_weapon_the_apartment", -.30, .38),
        })
        {
            var what = $"{talent}/{item}";
            var b = TalentBuild(talent, item);
            var baseline = Resolve(b, Weapon(slot));
            var bs = baseline.Calculation.Stats;

            foreach (var phase in new[] { "", "other", "TOP", "Bottom " })
            {
                var inactive = Resolve(b, Context(("weapon_slot", slot), ("measured_phase", phase)));
                var s = inactive.Calculation.Stats;
                Near(s.Of("weapon_damage"), bs.Of("weapon_damage"), 1e-12, $"{what} phase \"{phase}\" weapon_damage");
                Near(s.Of("total_weapon_damage"), bs.Of("total_weapon_damage"), 1e-12, $"{what} phase \"{phase}\" total_weapon_damage");
                Near(s.Of("rate_of_fire"), bs.Of("rate_of_fire"), 1e-12, $"{what} phase \"{phase}\" rate_of_fire");
                Assert.False(HasNonZeroTalentContribution(inactive, "weapon_damage", talent) || HasNonZeroTalentContribution(inactive, "total_weapon_damage", talent), $"invalid phase \"{phase}\" applied {what}");
            }

            var top = Resolve(b, Context(("weapon_slot", slot), ("measured_phase", "top")));
            NoErrors(top, what);
            Near(top.Calculation.Stats.Of("weapon_damage"), bs.Of("weapon_damage") + topDamage, 1e-12, $"{what} top weapon_damage");
            Near(top.Calculation.Stats.Of("total_weapon_damage"), bs.Of("total_weapon_damage"), 1e-12, $"{what} top total_weapon_damage");
            Near(top.Calculation.Stats.Of("rate_of_fire"), bs.Of("rate_of_fire") + .25, 1e-12, $"{what} top rate_of_fire");
            Near(top.Calculation.Stats.Of("weapon_rpm"), bs.Of("weapon_rpm"), 1e-9, $"{what} top weapon_rpm");
            Assert.True(HasContribution(top, "weapon_damage", "talent", talent) && HasContribution(top, "rate_of_fire", "talent", talent), $"top phase lacks {what} provenance");

            var bottom = Resolve(b, Context(("weapon_slot", slot), ("measured_phase", "bottom")));
            NoErrors(bottom, what);
            Near(bottom.Calculation.Stats.Of("weapon_damage"), bs.Of("weapon_damage"), 1e-12, $"{what} bottom weapon_damage");
            Near(bottom.Calculation.Stats.Of("total_weapon_damage"), bs.Of("total_weapon_damage") + bottomTwd, 1e-12, $"{what} bottom total_weapon_damage");
            Near(bottom.Calculation.Stats.Of("rate_of_fire"), bs.Of("rate_of_fire") - .18, 1e-12, $"{what} bottom rate_of_fire");
            Near(bottom.Calculation.Stats.Of("weapon_rpm"), bs.Of("weapon_rpm"), 1e-9, $"{what} bottom weapon_rpm");
            Assert.True(bottom.Calculation.Stats.Of("equipped_weapon_damage") > bs.Of("equipped_weapon_damage"),
                $"{what}: bottom phase equipped damage {bottom.Calculation.Stats.Of("equipped_weapon_damage")} did not rise above baseline {bs.Of("equipped_weapon_damage")}");
            Assert.True(HasContribution(bottom, "total_weapon_damage", "talent", talent) && HasContribution(bottom, "rate_of_fire", "talent", talent), $"bottom phase lacks {what} provenance");

            // Selecting a different weapon must leave the talent inactive even
            // when the user supplies a valid phase for the host slot.
            var otherSlot = slot == "primary_weapon" ? "secondary_weapon" : "primary_weapon";
            var other = Resolve(b, Context(("weapon_slot", otherSlot), ("measured_phase", "top")));
            Near(other.Calculation.Stats.Of("weapon_damage"), bs.Of("weapon_damage"), 1e-12, $"{what} on {otherSlot}");
            Assert.False(HasContribution(other, "weapon_damage", "talent", talent), $"{what} affected unselected slot {otherSlot}");

            var decoded = ShareCode.Decode(ShareCode.Encode(b));
            Assert.True(Stats(decoded, Weapon(slot)).Of("weapon_damage") == bs.Of("weapon_damage"), $"{what}: transient Measured phase unexpectedly survived the share-code round trip");
        }
    }

    /// <summary>
    /// G34: Steady Handed adds a point of accuracy and stability per retained stack on the selected weapon (Perfect
    /// Steady Handed's bonus drops at 75 stacks) without changing RPM, magazine or damage; invalid stacks are
    /// diagnosed and ignored.
    /// </summary>
    [Fact]
    public void G34SteadyHandedStackAssumptions()
    {
        foreach (var (talent, perfect) in new[] { ("weapon_talent_steady_handed", false), ("weapon_talent_perfect_steady_handed", true) })
        {
            foreach (var slot in new[] { "primary_weapon", "secondary_weapon" })
            {
                var what = $"{talent}/{slot}";
                var b = AnyWeaponTalentBuild(talent, slot);
                var baseline = Resolve(b, Weapon(slot));
                var bs = baseline.Calculation.Stats;

                foreach (var stacks in new[] { 0.0, 1, 74, 75, 99 })
                {
                    var at = $"{what} at {stacks} stacks";
                    var result = Resolve(b, Context(("weapon_slot", slot), ("steady_handed_stacks", Format(stacks))));
                    var want = perfect && stacks >= 75 ? 0 : stacks / 100;
                    var s = result.Calculation.Stats;
                    Near(s.Of("accuracy"), bs.Of("accuracy") + want, 1e-12, $"{at} accuracy");
                    Near(s.Of("stability"), bs.Of("stability") + want, 1e-12, $"{at} stability");
                    Near(s.Of("weapon_rpm"), bs.Of("weapon_rpm"), 1e-9, $"{at} weapon_rpm");
                    Near(s.Of("weapon_magazine_size"), bs.Of("weapon_magazine_size"), 1e-12, $"{at} weapon_magazine_size");
                    Near(s.Of("equipped_weapon_damage"), bs.Of("equipped_weapon_damage"), 1e-8, $"{at} equipped_weapon_damage");
                    foreach (var stat in new[] { "accuracy", "stability" })
                    {
                        var contributed = HasNonZeroTalentContribution(result, stat, talent);
                        Assert.True(contributed == (want > 0), $"{stat} provenance on {at} = {contributed}, want {want > 0}");
                    }
                }

                foreach (var raw in new[] { "-1", "1.5", "100", "NaN" })
                {
                    var invalid = Resolve(b, Context(("weapon_slot", slot), ("steady_handed_stacks", raw)));
                    Assert.True(HasDiagnostic(invalid, "context-value-invalid", "steady_handed_stacks"), $"{what}: invalid Steady Handed stack value \"{raw}\" lacks a diagnostic");
                    Near(invalid.Calculation.Stats.Of("accuracy"), bs.Of("accuracy"), 1e-12, $"{what} with invalid \"{raw}\" accuracy");
                    Near(invalid.Calculation.Stats.Of("stability"), bs.Of("stability"), 1e-12, $"{what} with invalid \"{raw}\" stability");
                }

                var otherSlot = slot == "primary_weapon" ? "secondary_weapon" : "primary_weapon";
                var other = Resolve(b, Context(("weapon_slot", otherSlot), ("steady_handed_stacks", "74")));
                Near(other.Calculation.Stats.Of("accuracy"), bs.Of("accuracy"), 1e-12, $"{what} on {otherSlot} accuracy");
                Near(other.Calculation.Stats.Of("stability"), bs.Of("stability"), 1e-12, $"{what} on {otherSlot} stability");
                Assert.False(HasContribution(other, "accuracy", "talent", talent) || HasContribution(other, "stability", "talent", talent), $"{what} contributed while {otherSlot} was selected");

                var restored = Stats(ShareCode.Decode(ShareCode.Encode(b)), Weapon(slot));
                Near(restored.Of("accuracy"), bs.Of("accuracy"), 1e-12, $"{what} restored accuracy");
                Near(restored.Of("stability"), bs.Of("stability"), 1e-12, $"{what} restored stability");
            }
        }
    }

    /// <summary>
    /// G37: Rifleman's retained headshot stacks add additive weapon damage per stack up to its cap on the selected
    /// host rifle; invalid stacks are diagnosed, and no stacks apply without a selected weapon.
    /// </summary>
    [Fact]
    public void G37RiflemanRetainedHeadshotStacks()
    {
        foreach (var (talent, host, slot, perStack, cap) in new[]
        {
            ("weapon_talent_rifleman", "primary_weapon_acr_ss", "primary_weapon", .10, 5.0),
            ("weapon_talent_rifleman", "secondary_weapon_acr_ss", "secondary_weapon", .10, 5.0),
            ("weapon_talent_perfect_rifleman", "primary_weapon_artist_s_tool", "primary_weapon", .11, 6.0),
            ("weapon_talent_perfect_rifleman", "secondary_weapon_artist_s_tool", "secondary_weapon", .11, 6.0),
        })
        {
            var b = TalentBuild(talent, host);
            var baseline = Stats(b, Weapon(slot));
            for (var stacks = 0.0; stacks <= 6; stacks++)
            {
                var at = $"{host} at {stacks} stacks";
                var result = Resolve(b, Context(("weapon_slot", slot), ("rifleman_stacks", Format(stacks))));
                NoErrors(result, at);
                var applied = Math.Min(stacks, cap);
                var s = result.Calculation.Stats;
                Near(s.Of("weapon_damage"), baseline.Of("weapon_damage") + perStack * applied, 1e-12, $"{at} weapon_damage");
                Near(s.Of("total_weapon_damage"), baseline.Of("total_weapon_damage"), 1e-12, $"{at} total_weapon_damage");
                Near(s.Of("weapon_damage_multiplier"), baseline.Of("weapon_damage_multiplier"), 1e-12, $"{at} weapon_damage_multiplier");
                Near(s.Of("equipped_weapon_damage"), WeaponDamage(s, "rifle_damage"), 1e-8, $"{at} equipped_weapon_damage");
                Assert.True(applied == 0 || HasContribution(result, "weapon_damage", "talent", talent), $"{at}: weapon damage has no {talent} provenance");
            }

            foreach (var raw in new[] { "-1", "1.5", "7", "NaN", "Infinity" })
            {
                var invalid = Resolve(b, Context(("weapon_slot", slot), ("rifleman_stacks", raw)));
                Assert.True(HasDiagnostic(invalid, "context-value-invalid", "rifleman_stacks"), $"{host}: invalid stack value \"{raw}\" lacks a diagnostic");
                Near(invalid.Calculation.Stats.Of("weapon_damage"), baseline.Of("weapon_damage"), 1e-12, $"{host} with invalid \"{raw}\"");
            }

            var wrongSlot = slot == "secondary_weapon" ? "primary_weapon" : "secondary_weapon";
            var inactive = Resolve(b, Context(("weapon_slot", wrongSlot), ("rifleman_stacks", "6")));
            Near(inactive.Calculation.Stats.Of("weapon_damage"), baseline.Of("weapon_damage"), 1e-12, $"{host} on {wrongSlot}");
            Assert.False(HasContribution(inactive, "weapon_damage", "talent", talent), $"{host}: {talent} leaked to the unselected weapon");
            Near(Stats(b, Context(("rifleman_stacks", "6"))).Of("weapon_damage"), baseline.Of("weapon_damage"), 1e-12, $"{host} without a selected weapon");
        }
    }

    /// <summary>G37: Rifleman's capped bonus adds to the gear core, Watch and weapon Expertise in additive weapon damage.</summary>
    [Fact]
    public void G37RiflemanComposesWithAdditiveWeaponDamageAndExpertise()
    {
        var b = TalentBuild("weapon_talent_rifleman", "primary_weapon_acr_ss")
            .WithAccountUpgrade("watch_weapon_damage", 50)
            .WithEquipmentUpgrade("primary_weapon", "primary_weapon_acr_ss", 20);
        var result = Resolve(b, Context(("weapon_slot", "primary_weapon"), ("rifleman_stacks", "6")));
        NoErrors(result, "Rifleman");
        // The gear core adds 15%, Watch adds 10%, Expertise adds 20%, and
        // ordinary Rifleman caps at 50%.
        CloseTo(result.Calculation.Stats.Of("weapon_damage"), .95, 1e-12);
        Assert.True(HasContribution(result, "weapon_damage", "talent", "weapon_talent_rifleman"), "weapon damage is missing Rifleman provenance");
        CloseTo(result.Calculation.Stats.Of("equipped_weapon_damage"), WeaponDamage(result.Calculation.Stats, "rifle_damage"), 1e-8);
    }

    /// <summary>
    /// G37: Pummel's active buff adds 40% additive weapon damage on its selected host shotgun and nothing else, and
    /// the Pummel and Rifleman talents are offered only on shotguns and rifles.
    /// </summary>
    [Fact]
    public void G37PummelActiveBuff()
    {
        foreach (var (talent, host, slot) in new[]
        {
            ("weapon_talent_pummel", "primary_weapon_acs_12", "primary_weapon"),
            ("weapon_talent_pummel", "secondary_weapon_acs_12", "secondary_weapon"),
            ("weapon_talent_perfect_pummel", "primary_weapon_tsunami", "primary_weapon"),
            ("weapon_talent_perfect_pummel", "secondary_weapon_tsunami", "secondary_weapon"),
            ("weapon_talent_perfect_pummel", "primary_weapon_enforcer", "primary_weapon"),
            ("weapon_talent_perfect_pummel", "secondary_weapon_enforcer", "secondary_weapon"),
        })
        {
            var b = TalentBuild(talent, host).WithActiveSkillSelection("skill_slot_1", "skill_assault_turret");
            var inactive = Resolve(b, Weapon(slot));
            var active = Resolve(b, Context(("weapon_slot", slot), ("pummel_active", "true")));
            NoErrors(active, host);
            var s = active.Calculation.Stats;
            var off = inactive.Calculation.Stats;
            Near(s.Of("weapon_damage"), off.Of("weapon_damage") + .40, 1e-12, $"{host} weapon_damage");
            Near(s.Of("total_weapon_damage"), off.Of("total_weapon_damage"), 1e-12, $"{host} total_weapon_damage");
            Near(s.Of("weapon_damage_multiplier"), off.Of("weapon_damage_multiplier"), 1e-12, $"{host} weapon_damage_multiplier");
            Near(s.Of("weapon_magazine_size"), off.Of("weapon_magazine_size"), 1e-12, $"{host} weapon_magazine_size");
            Assert.True(SamePreviews(active.ActiveSkillPreviews, inactive.ActiveSkillPreviews), $"{host}: Pummel changed active skill previews");
            Assert.True(HasContribution(active, "weapon_damage", "talent", talent), $"{host}: weapon damage has no {talent} provenance");
            Near(s.Of("equipped_weapon_damage"), WeaponDamage(s, "shotgun_damage"), 1e-8, $"{host} equipped_weapon_damage");

            var wrongSlot = slot == "secondary_weapon" ? "primary_weapon" : "secondary_weapon";
            var wrongWeapon = Resolve(b, Context(("weapon_slot", wrongSlot), ("pummel_active", "true")));
            Near(wrongWeapon.Calculation.Stats.Of("weapon_damage"), off.Of("weapon_damage"), 1e-12, $"{host} on {wrongSlot}");
            Assert.False(HasContribution(wrongWeapon, "weapon_damage", "talent", talent), $"{host}: {talent} leaked to the unselected weapon");
        }
        foreach (var id in new[] { "weapon_talent_pummel", "weapon_talent_perfect_pummel" })
        {
            Assert.True(OfferedOnlyOn(id, "weapon_type_shotgun"), $"{id} is offered on a non-shotgun item or missing from a shotgun");
        }
        Assert.True(OfferedOnlyOn("weapon_talent_rifleman", "weapon_type_rifle") && OfferedOnlyOn("weapon_talent_perfect_rifleman", "weapon_type_rifle"), "Rifleman talent pair is not restricted to rifle items");
    }

    /// <summary>
    /// G38: Unhinged adds additive weapon damage and costs a quarter of accuracy and stability on its selected host
    /// LMG, without changing the other damage pools or the skill previews.
    /// </summary>
    [Fact]
    public void G38UnhingedWeaponHandlingAndWeaponDamage()
    {
        foreach (var (talent, host, slot, weaponDamage) in new[]
        {
            ("weapon_talent_unhinged", "primary_weapon_stoner_lamg", "primary_weapon", .18),
            ("weapon_talent_unhinged", "secondary_weapon_stoner_lamg", "secondary_weapon", .18),
            ("weapon_talent_perfect_unhinged", "primary_weapon_black_friday", "primary_weapon", .22),
            ("weapon_talent_perfect_unhinged", "secondary_weapon_black_friday", "secondary_weapon", .22),
        })
        {
            var b = TalentBuild(talent, host).WithActiveSkillSelection("skill_slot_1", "skill_assault_turret");
            var baseline = Resolve(b.WithTalentSelection(slot, "talent", ""), Weapon(slot));
            var bs = baseline.Calculation.Stats;
            var active = Resolve(b, Weapon(slot));
            NoErrors(active, host);
            var s = active.Calculation.Stats;
            Near(s.Of("weapon_damage"), bs.Of("weapon_damage") + weaponDamage, 1e-12, $"{host} weapon_damage");
            Near(s.Of("accuracy"), bs.Of("accuracy") - .25, 1e-12, $"{host} accuracy");
            Near(s.Of("stability"), bs.Of("stability") - .25, 1e-12, $"{host} stability");
            Near(s.Of("total_weapon_damage"), bs.Of("total_weapon_damage"), 1e-12, $"{host} total_weapon_damage");
            Near(s.Of("weapon_damage_multiplier"), bs.Of("weapon_damage_multiplier"), 1e-12, $"{host} weapon_damage_multiplier");
            Near(s.Of("headshot_damage"), bs.Of("headshot_damage"), 1e-12, $"{host} headshot_damage");
            Assert.True(HasContribution(active, "weapon_damage", "talent", talent), $"{host}: weapon damage has no {talent} provenance");
            foreach (var stat in new[] { "accuracy", "stability" })
            {
                Assert.True(HasContribution(active, stat, "talent", talent), $"{host}: {stat} has no {talent} provenance");
            }
            Near(s.Of("equipped_weapon_damage"), WeaponDamage(s, "lmg_damage"), 1e-8, $"{host} equipped_weapon_damage");
            Assert.True(SamePreviews(active.ActiveSkillPreviews, baseline.ActiveSkillPreviews), $"{host}: Unhinged changed active skill previews");

            var wrongSlot = slot == "secondary_weapon" ? "primary_weapon" : "secondary_weapon";
            var inactive = Resolve(b, Weapon(wrongSlot));
            foreach (var stat in new[] { "weapon_damage", "accuracy", "stability" })
            {
                Near(inactive.Calculation.Stats.Of(stat), bs.Of(stat), 1e-12, $"{host} on {wrongSlot} {stat}");
                Assert.False(HasContribution(inactive, stat, "talent", talent), $"{host}: {talent} leaked into unselected {stat} breakdown");
            }
            Near(Stats(b).Of("weapon_damage"), bs.Of("weapon_damage"), 1e-12, $"{host} without a selected weapon");
        }
    }

    /// <summary>G38: Unhinged's bonus adds to the gear core, Watch and weapon Expertise in additive weapon damage.</summary>
    [Fact]
    public void G38UnhingedComposesWithAdditiveWeaponDamageAndExpertise()
    {
        var b = TalentBuild("weapon_talent_unhinged", "primary_weapon_stoner_lamg")
            .WithAccountUpgrade("watch_weapon_damage", 50)
            .WithEquipmentUpgrade("primary_weapon", "primary_weapon_stoner_lamg", 20);
        var result = Resolve(b, Weapon("primary_weapon"));
        NoErrors(result, "Unhinged");
        // Gear core adds 15%, Watch adds 10%, Expertise adds 20%, and Unhinged adds 18%.
        CloseTo(result.Calculation.Stats.Of("weapon_damage"), .63, 1e-12);
        Assert.True(HasContribution(result, "weapon_damage", "talent", "weapon_talent_unhinged"), "weapon damage is missing Unhinged provenance");
        CloseTo(result.Calculation.Stats.Of("equipped_weapon_damage"), WeaponDamage(result.Calculation.Stats, "lmg_damage"), 1e-8);
    }

    /// <summary>
    /// G38: Naked's headshot damage applies only through its own active-buff assumption on the selected host
    /// marksman rifle, not from an armorless target, and its description keeps the bonus and its duration.
    /// </summary>
    [Fact]
    public void G38NakedHeadshotDamageUsesOnlyItsActiveBuffContext()
    {
        foreach (var (talent, host, slot, duration) in new[]
        {
            ("weapon_talent_naked", "primary_weapon_g28", "primary_weapon", "5s"),
            ("weapon_talent_naked", "secondary_weapon_g28", "secondary_weapon", "5s"),
            ("weapon_talent_perfect_naked", "primary_weapon_commando", "primary_weapon", "8s"),
            ("weapon_talent_perfect_naked", "secondary_weapon_commando", "secondary_weapon", "8s"),
        })
        {
            var b = TalentBuild(talent, host).WithActiveSkillSelection("skill_slot_1", "skill_assault_turret");
            var baseline = Resolve(b, Weapon(slot));
            var bs = baseline.Calculation.Stats;
            var inactive = Resolve(b, Context(("weapon_slot", slot), ("target_armorless", "true")));
            Near(inactive.Calculation.Stats.Of("headshot_damage"), bs.Of("headshot_damage"), 1e-12, $"{host} with an armorless target");
            Assert.False(HasContribution(inactive, "headshot_damage", "talent", talent), $"{host}: {talent} inferred its buff from target_armorless");

            var active = Resolve(b, Context(("weapon_slot", slot), ("naked_active", "true")));
            NoErrors(active, host);
            Near(active.Calculation.Stats.Of("headshot_damage"), bs.Of("headshot_damage") + .50, 1e-12, $"{host} headshot_damage");
            foreach (var stat in new[] { "weapon_damage", "weapon_damage_multiplier", "equipped_weapon_damage" })
            {
                Near(active.Calculation.Stats.Of(stat), bs.Of(stat), 1e-12, $"{host} {stat}");
            }
            Assert.True(HasContribution(active, "headshot_damage", "talent", talent), $"{host}: headshot damage has no {talent} provenance");
            Assert.True(SamePreviews(active.ActiveSkillPreviews, baseline.ActiveSkillPreviews), $"{host}: Naked changed active skill previews");
            var description = Compiled.Pack.Talents.FirstOrDefault(candidate => candidate.ID == talent)?.Description ?? "";
            Assert.True(description.Contains("+50% headshot damage", StringComparison.Ordinal) && description.Contains(duration, StringComparison.Ordinal),
                $"{talent} description should preserve its +50% headshot buff duration {duration}: {description}");

            var wrongSlot = slot == "secondary_weapon" ? "primary_weapon" : "secondary_weapon";
            var wrongWeapon = Resolve(b, Context(("weapon_slot", wrongSlot), ("naked_active", "true")));
            Near(wrongWeapon.Calculation.Stats.Of("headshot_damage"), bs.Of("headshot_damage"), 1e-12, $"{host} on {wrongSlot}");
            Assert.False(HasContribution(wrongWeapon, "headshot_damage", "talent", talent), $"{host}: {talent} leaked into the unselected weapon");
        }
        foreach (var (talent, expectedItem) in new[] { ("weapon_talent_naked", "primary_weapon_g28"), ("weapon_talent_perfect_naked", "primary_weapon_commando") })
        {
            Assert.True(OfferedOnlyOn(talent, "weapon_type_marksman_rifle"), $"{talent} is offered on a non-MMR item or missing from an MMR");
            Assert.True(Compiled.Pack.Items.Any(item => item.ID == expectedItem && item.TalentSlots.Any(slot => slot.Choices.Contains(talent))), $"{talent} is not offered on {expectedItem}");
        }
    }

    /// <summary>
    /// Go's division2TalentBuild: an ACR and a 15% weapon-damage mask core, with the talent selected on the host
    /// item that offers it.
    /// </summary>
    private static BuildSnapshot TalentBuild(string talent, string host)
    {
        var item = Compiled.Pack.Items.FirstOrDefault(item => item.ID == host && item.TalentSlots.Any(slot => slot.Choices.Contains(talent)));
        Assert.True(item is not null, $"no item offers talent {talent} as {host}");
        var talentSlot = item.TalentSlots.Last(slot => slot.Choices.Contains(talent));
        return BuildSnapshot.Create(Compiled.Pack, "Talent regression: " + talent)
            .WithEquipment("primary_weapon", "primary_weapon_acr")
            .WithEquipment("mask", "mask_n_5_11_tactical_mask")
            .WithAttributeRoll("mask", "core_1", new("weapon_damage_gear_core", 0.15))
            .WithEquipment(item.Slot, item.ID)
            .WithTalentSelection(item.Slot, talentSlot.ID, talent);
    }

    /// <summary>Go's division2AnyWeaponTalentBuild: the first weapon in the slot offering the talent, with a mask core.</summary>
    private static BuildSnapshot AnyWeaponTalentBuild(string talent, string slot)
    {
        foreach (var item in Compiled.Pack.Items.Where(item => item.Slot == slot))
        {
            var talentSlot = item.TalentSlots.FirstOrDefault(candidate => candidate.Choices.Contains(talent));
            if (talentSlot is null) { continue; }
            return BuildSnapshot.Create(Compiled.Pack, "Talent regression: " + talent)
                .WithEquipment(slot, item.ID)
                .WithEquipment("mask", "mask_n_5_11_tactical_mask")
                .WithAttributeRoll("mask", "core_1", new("weapon_damage_gear_core", .15))
                .WithTalentSelection(slot, talentSlot.ID, talent);
        }
        Assert.Fail($"no weapon in {slot} offers talent {talent}");
        return Build();
    }

    /// <summary>Go's addSixGearSkillCores: a 5.11 piece with a skill-tier core in each of the six gear slots.</summary>
    private static BuildSnapshot AddSixGearSkillCores(BuildSnapshot b)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in Compiled.Pack.Slots)
        {
            foreach (var item in Compiled.Pack.Items.Where(item => item.Slot == slot.ID && item.ID.Contains("5_11_tactical", StringComparison.Ordinal)))
            {
                var attributeSlot = item.AttributeSlots.FirstOrDefault(candidate => candidate.Choices.Any(choice => choice.AttributeID == "skill_tier_gear_core"));
                if (attributeSlot is null || used.Contains(slot.ID)) { continue; }
                b = b.WithEquipment(slot.ID, item.ID).WithAttributeRoll(slot.ID, attributeSlot.ID, new("skill_tier_gear_core", 1));
                used.Add(slot.ID);
            }
        }
        Assert.True(used.Count == 6, $"catalog has {used.Count} 5.11 gear slots with skill cores, want six: {string.Join(", ", used)}");
        return b;
    }

    /// <summary>Go's talentOfferedOnlyOnShotguns, talentOfferedOnlyOnRifles and talentOfferedOnlyOnMarksmanRifles.</summary>
    private static bool OfferedOnlyOn(string talent, string tag)
    {
        var hosts = Compiled.Pack.Items.Where(item => item.TalentSlots.Any(slot => slot.Choices.Contains(talent))).ToArray();
        return hosts.All(item => item.Tags.Contains(tag)) && hosts.Length > 0;
    }

    /// <summary>The equipped weapon's damage preview: base × (1 + weapon damage + type damage) × multiplier × (1 + total).</summary>
    private static double WeaponDamage(ImmutableDictionary<string, double> s, string typeDamage) =>
        s.Of("weapon_base_damage") * (1 + s.Of("weapon_damage") + s.Of(typeDamage)) * s.Of("weapon_damage_multiplier") * (1 + s.Of("total_weapon_damage"));

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static void Near(double got, double want, double tolerance, string what)
    {
        if (Math.Abs(got - want) > tolerance) { Assert.Fail($"{what}: got {got}, want {want} (within {tolerance})"); }
    }

    private static void NoErrors(ResolvedBuild result, string what) =>
        Assert.True(!result.Diagnostics.Any(diagnostic => diagnostic.Severity == "error"), $"{what}: legal talent loadout produced an error diagnostic: {string.Join("; ", result.Diagnostics)}");

    private static bool HasDiagnostic(ResolvedBuild result, string code, string subject) =>
        result.Diagnostics.Any(diagnostic => diagnostic.Code == code && diagnostic.Subject == subject);

    /// <summary>Go's hasContribution on a stat's breakdown.</summary>
    private static bool HasContribution(ResolvedBuild result, string stat, string kind, string id) =>
        result.Calculation.Breakdowns.TryGetValue(stat, out var breakdown) && breakdown.Contributions.Any(contribution => contribution.Source.Kind == kind && contribution.Source.Id == id);

    /// <summary>Go's hasNonZeroTalentContribution.</summary>
    private static bool HasNonZeroTalentContribution(ResolvedBuild result, string stat, string talent) =>
        result.Calculation.Breakdowns.TryGetValue(stat, out var breakdown)
            && breakdown.Contributions.Any(contribution => contribution.Source.Kind == "talent" && contribution.Source.Id == talent && contribution.Value != 0);

    private static ImmutableArray<SkillPreviewOutput> Outputs(ResolvedBuild result, string slot) =>
        result.ActiveSkillPreviews.TryGetValue(slot, out var preview) ? preview.Outputs : [];

    private static string Describe(ImmutableArray<SkillPreviewOutput> outputs) => "[" + string.Join(", ", outputs.Select(output => $"{output.Id}={output.Value}")) + "]";

    // Go compared skill previews with reflect.DeepEqual, optionally through nonDamagePreviews (which drops the
    // damage channels and the assumed_ rates); this compares the same values.
    private static bool SamePreviews(ImmutableDictionary<string, SkillPreview> a, ImmutableDictionary<string, SkillPreview> b, bool nonDamageOnly = false)
    {
        ImmutableArray<SkillPreviewOutput> Kept(SkillPreview preview) => nonDamageOnly ? [.. preview.Outputs.Where(output => !IsDamageChannel(output.Id) && !output.Id.StartsWith("assumed_", StringComparison.Ordinal))] : preview.Outputs;
        return a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && pair.Value.SkillId == other.SkillId
            && Kept(pair.Value).Length == Kept(other).Length && Kept(pair.Value).Zip(Kept(other)).All(outputs => SameOutput(outputs.First, outputs.Second)));
    }

    /// <summary>Go's isDamagePreviewChannel.</summary>
    private static bool IsDamageChannel(string id) => id is "damage" or "average_damage" or "cloud_damage" or "burn_damage" or "bleed_damage" or "explosion_damage";

    private static bool SameOutput(SkillPreviewOutput a, SkillPreviewOutput b) =>
        a.Id == b.Id && a.Name == b.Name && a.Description == b.Description && a.Format == b.Format && a.Formula == b.Formula && a.When == b.When
            && a.Value == b.Value && a.Kind == b.Kind && a.Inputs.Count == b.Inputs.Count && a.Inputs.All(pair => b.Inputs.TryGetValue(pair.Key, out var value) && value == pair.Value);
}
