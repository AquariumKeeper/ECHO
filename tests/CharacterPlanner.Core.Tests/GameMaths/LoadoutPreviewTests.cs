using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// Shield health from Skill Tier and armor cores (F25, Shield health and regeneration), armor cores counted for Skill
/// Tier (F20, Active skills and skill previews), and the Frenzy magazine and critical headshot loadout previews that
/// Go kept in the same file (F30, weapon DPS), ported from Go's shield_health_g161_test.go and
/// loadout_previews_g107_test.go.
/// </summary>
public sealed class LoadoutPreviewTests
{
    /// <summary>As Go's assertNoErrorDiagnostics.</summary>
    private static void NoErrors(ResolvedBuild result)
    {
        var errors = result.Diagnostics.Where(diagnostic => diagnostic.Severity == "error").ToArray();
        Assert.True(errors.Length == 0, $"error diagnostics: {string.Join("; ", errors.Select(diagnostic => diagnostic.ToString()))}");
    }

    private static IEnumerable<SkillPreviewOutput> Outputs(ResolvedBuild result, string slot) =>
        result.ActiveSkillPreviews.TryGetValue(slot, out var preview) ? preview.Outputs : [];

    private static readonly (string Id, double Base, double[] Tiers)[] Shields =
    [
        ("skill_bulwark_shield", 2654490, [0, .67, 1.33, 2, 2.66, 3.33, 4]),
        ("skill_crusader_shield", 1327245, [0, .4, .66, 1, 1.5, 2, 2.5]),
        ("skill_deflector_shield", 2123592, [0, .4, .66, 1, 1.5, 2, 2.5]),
        ("skill_striker_shield", 1327245, [0, .4, .66, 1, 1.5, 2, 2.5]),
    ];

    /// <summary>
    /// G161: each shield's health adds its tier bonus (whole Skill Tier, capped at 6) and Shield Health to its base,
    /// scaled by Skill Health with Skill Efficiency, ignoring repair and damage bonuses; six armor cores give a
    /// Bulwark Tier 6.
    /// </summary>
    [Fact]
    public void G161ShieldHealth()
    {
        foreach (var tier in new[] { 0, 1, 2, 3, 4, 5, 6, 6.9, 8 })
        {
            ImmutableArray<StatModifier> modifiers =
            [
                new() { Stat = "skill_tier", Op = ModifierOps.Flat, Value = new(tier) }, new() { Stat = "shield_health", Op = ModifierOps.Flat, Value = new(.5) },
                new() { Stat = "skill_health", Op = ModifierOps.Flat, Value = new(.2) }, new() { Stat = "skill_efficiency", Op = ModifierOps.Flat, Value = new(.1) },
                new() { Stat = "repair_skills", Op = ModifierOps.Flat, Value = new(3) }, new() { Stat = "skill_damage", Op = ModifierOps.Flat, Value = new(3) },
            ];
            var validated = PackValidator.Validate(Compiled.Pack with { ActiveSkills = [.. Compiled.Pack.ActiveSkills.Select(skill => skill with { Modifiers = modifiers })] });
            Assert.True(validated.Compiled is not null, "invalid fixture");
            var c = validated.Compiled;
            foreach (var (id, @base, tiers) in Shields)
            {
                foreach (var slot in new[] { "skill_slot_1", "skill_slot_2" })
                {
                    var r = BuildResolver.Resolve(c, BuildSnapshot.Create(c.Pack).WithActiveSkillSelection(slot, id));
                    NoErrors(r);
                    var found = false;
                    foreach (var o in Outputs(r, slot).Where(o => o.Id == "skill_health"))
                    {
                        found = true;
                        CloseTo(o.Value, @base * (1 + tiers[(int)Math.Min(6, Math.Floor(tier))] + .5) * 1.3, 1e-6);
                    }
                    Assert.True(found, $"missing skill_health for {id} at tier {tier}");
                }
            }
        }
        var b = Build();
        foreach (var slot in new[] { "mask", "chest", "backpack", "gloves", "holster", "knees" })
        {
            var name = slot == "knees" ? "kneepads" : slot;
            b = b.WithEquipment(slot, slot + "_belstone_armory_" + name).WithAttributeRoll(slot, "core_1", new("armor_gear_core", 170000));
        }
        b = b.WithActiveSkillSelection("skill_slot_1", "skill_bulwark_shield");
        var resolved = Resolve(b);
        CloseTo(resolved.Calculation.Stats.Of("armor_core_count"), 6, 1e-12);
        foreach (var o in Outputs(resolved, "skill_slot_1").Where(o => o.Id == "skill_health")) { CloseTo(o.Value, 13272450, 1e-6); }
    }

    /// <summary>G107: Frenzy reads the magazine size with an installed magazine mod, only on its own active weapon.</summary>
    [Fact]
    public void G107AutomaticMagazineWithInstalledMod()
    {
        var b = BuildSnapshot.Create(Compiled.Pack, "Frenzy").WithEquipment("primary_weapon", "primary_weapon_stoner_lamg")
            .WithTalentSelection("primary_weapon", "talent", "weapon_talent_frenzy");
        CloseTo(Stats(b, Context((GamePack.WeaponSlotSelector, "primary_weapon"), ("frenzy_active", "true"))).Of("weapon_damage"), .6, 1e-12);
        b = b.WithModSelection("primary_weapon", "magazine", "weapon_mod_magazine_large_pouch");
        var s = Stats(b, Context((GamePack.WeaponSlotSelector, "primary_weapon"), ("frenzy_active", "true")));
        CloseTo(s.Of("equipped_weapon_magazine_size"), 235, 1e-12);
        CloseTo(s.Of("weapon_damage"), .69, 1e-12);
        CloseTo(s.Of("rate_of_fire"), .69, 1e-12);
        CloseTo(Stats(b, Context((GamePack.WeaponSlotSelector, "secondary_weapon"), ("frenzy_active", "true"))).Of("weapon_damage"), 0, 1e-12);
    }

    /// <summary>G107: an armor core in its legal range counts toward Skill Tier, with its item as provenance; an out-of-range one doesn't.</summary>
    [Fact]
    public void G107CountLegalArmorCoresWithProvenance()
    {
        var b = BuildSnapshot.Create(Compiled.Pack, "Cores").WithEquipment("backpack", "backpack_ninjabike_messenger_backpack")
            .WithActiveSkillSelection("skill_slot_1", "skill_deflector_shield");
        CloseTo(Stats(b).Of("armor_core_count"), 0, 1e-12);
        foreach (var v in new double[] { 16594, 170000 })
        {
            var changed = b.WithAttributeRoll("backpack", "core_2", new("armor_gear_core", v));
            var r = Resolve(changed);
            CloseTo(r.Calculation.Stats.Of("armor_core_count"), 1, 1e-12);
            foreach (var o in Outputs(r, "skill_slot_1").Where(o => o.Id == "effective_skill_tier")) { CloseTo(o.Value, 1, 1e-12); }
            Assert.True(r.Calculation.Breakdowns["armor_core_count"].Contributions[0].Source.Id == "backpack_ninjabike_messenger_backpack", "lost core provenance");
            changed = changed.WithEquipment("backpack", "");
            CloseTo(Stats(changed).Of("armor_core_count"), 0, 1e-12);
        }
        var invalid = b.WithAttributeRoll("backpack", "core_2", new("armor_gear_core", 170001));
        CloseTo(Stats(invalid).Of("armor_core_count"), 0, 1e-12);
    }

    /// <summary>G108: critical, headshot and critical headshot damage of the active weapon, with an optics mod and on weapon switch.</summary>
    [Fact]
    public void G108CriticalHeadshotsAndWeaponSwitch()
    {
        var b = BuildSnapshot.Create(Compiled.Pack, "Shots").WithEquipment("primary_weapon", "primary_weapon_acr").WithEquipment("secondary_weapon", "secondary_weapon_classic_m1a");
        var s = Stats(b, Division.Weapon("primary_weapon"));
        var damage = s.Of("equipped_weapon_damage");
        CloseTo(s.Of("equipped_weapon_critical_damage"), damage * 1.25, 1e-8);
        CloseTo(s.Of("equipped_weapon_headshot_damage"), damage * 1.55, 1e-8);
        CloseTo(s.Of("equipped_weapon_critical_headshot_damage"), damage * 1.8, 1e-8);
        b = b.WithModSelection("primary_weapon", "optics", "weapon_mod_optics_digital_scope");
        s = Stats(b, Division.Weapon("primary_weapon"));
        CloseTo(s.Of("weapon_total_headshot_damage"), 1, 1e-12);
        CloseTo(s.Of("equipped_weapon_critical_headshot_damage"), damage * 2.2, 1e-8);
        s = Stats(b, Division.Weapon("secondary_weapon"));
        // Classic M1A is a rifle: Y8S3.2 raised the rifle base headshot bonus from 60% to 80%.
        CloseTo(s.Of("weapon_total_headshot_damage"), .8, 1e-12);
        CloseTo(s.Of("equipped_weapon_headshot_damage"), 231346 * 1.8, 1e-8);
        CloseTo(Stats(b).Of("equipped_weapon_critical_damage"), 0, 1e-12);
    }
}
