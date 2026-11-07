using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// Skill damage and repair channels, skill bonus pools, Skill Tier readers and the grenade and hive references (F20,
/// Active skills and skill previews), ported from Go's skill_damage_g103_test.go, skill_bonuses_g115_test.go,
/// skill_healing_g159_test.go, capacitor_tier_g113_test.go and grenade_hive_g402_test.go.
/// </summary>
public sealed class SkillDamageTests
{
    private static readonly string[] Slots = ["skill_slot_1", "skill_slot_2"];

    /// <summary>Go's isDamagePreviewChannel.</summary>
    private static bool IsDamagePreviewChannel(string id) => id is "damage" or "average_damage" or "cloud_damage" or "burn_damage" or "bleed_damage" or "explosion_damage";

    /// <summary>The committed pack with every active skill's modifiers replaced, as Go's edit of pack.ActiveSkills[i].Modifiers and ValidatePack.</summary>
    private static CompiledPackExpressions WithSkillModifiers(params (string Stat, string Op, double Value)[] modifiers)
    {
        ImmutableArray<StatModifier> replaced = [.. modifiers.Select(modifier => new StatModifier { Stat = modifier.Stat, Op = modifier.Op, Value = new(modifier.Value) })];
        var pack = Compiled.Pack with { ActiveSkills = [.. Compiled.Pack.ActiveSkills.Select(skill => skill with { Modifiers = replaced })] };
        return Validated(pack);
    }

    /// <summary>The committed pack with some stat formulas replaced by constants, as Go's edit of c.Pack.Stats and ValidatePack.</summary>
    private static CompiledPackExpressions WithFormulas(IReadOnlyDictionary<string, double> values)
    {
        var pack = Compiled.Pack with
        {
            Stats = [.. Compiled.Pack.Stats.Select(stat => values.TryGetValue(stat.ID, out var value) ? stat with { Formula = value.ToString("R", CultureInfo.InvariantCulture) } : stat)],
        };
        return Validated(pack);
    }

    private static CompiledPackExpressions Validated(GamePack pack)
    {
        var result = PackValidator.Validate(pack);
        Assert.True(result.Compiled is not null, $"invalid fixture: {string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message))}");
        return result.Compiled;
    }

    /// <summary>As Go's assertNoErrorDiagnostics.</summary>
    private static void NoErrors(ResolvedBuild result)
    {
        var errors = result.Diagnostics.Where(diagnostic => diagnostic.Severity == "error").ToArray();
        Assert.True(errors.Length == 0, $"error diagnostics: {string.Join("; ", errors.Select(diagnostic => diagnostic.ToString()))}");
    }

    private static IEnumerable<SkillPreviewOutput> Outputs(ResolvedBuild result, string slot) =>
        result.ActiveSkillPreviews.TryGetValue(slot, out var preview) ? preview.Outputs : [];

    /// <summary>The first pieces of a set, each in its own slot, as Go's g54SetBuild.</summary>
    private static BuildSnapshot SetBuild(string setId, int pieces)
    {
        var set = Compiled.Pack.Sets.FirstOrDefault(set => set.ID == setId) ?? throw new InvalidOperationException($"missing set {setId}");
        var b = BuildSnapshot.Create(Compiled.Pack, "G54 set talent regression: " + setId);
        foreach (var itemId in set.Items.Take(pieces))
        {
            var item = Compiled.Pack.Items.FirstOrDefault(item => item.ID == itemId);
            if (item is not null) { b = b.WithEquipment(item.Slot, item.ID); }
        }
        return b;
    }

    /// <summary>A talent selected on the equipped item's talent slot that offers it, as Go's g54SelectUpgrade.</summary>
    private static BuildSnapshot SelectUpgrade(BuildSnapshot b, string slot, string talentId)
    {
        var itemId = b.Equipment.GetValueOrDefault(slot, "");
        var talentSlot = Compiled.Pack.Items.Where(item => item.ID == itemId).SelectMany(item => item.TalentSlots).FirstOrDefault(talentSlot => talentSlot.Choices.Contains(talentId))
            ?? throw new InvalidOperationException($"{talentId} is not selectable on equipped {slot} item {itemId}");
        return b.WithTalentSelection(slot, talentSlot.ID, talentId);
    }

    /// <summary>
    /// A weapon and a mask with a Weapon Damage core, and an item offering the talent with it selected, as Go's
    /// division2TalentBuild.
    /// </summary>
    private static BuildSnapshot TalentBuild(string talentId, string expectedItemId)
    {
        var item = Compiled.Pack.Items.FirstOrDefault(item => item.ID == expectedItemId && item.TalentSlots.Any(slot => slot.Choices.Contains(talentId)))
            ?? throw new InvalidOperationException($"no item offers talent {talentId}");
        var talentSlot = item.TalentSlots.Last(slot => slot.Choices.Contains(talentId));
        return BuildSnapshot.Create(Compiled.Pack, "Talent regression: " + talentId)
            .WithEquipment("primary_weapon", "primary_weapon_acr")
            .WithEquipment("mask", "mask_n_5_11_tactical_mask")
            .WithAttributeRoll("mask", "core_1", new("weapon_damage_gear_core", 0.15))
            .WithEquipment(item.Slot, item.ID)
            .WithTalentSelection(item.Slot, talentSlot.ID, talentId);
    }

    private static readonly (string Name, string Channel, double Base, double Step, bool Explosive)[] DamageChannels =
    [
        ("Burn Sticky Bomb", "damage", 995434, 0, false),
        ("Burn Sticky Bomb", "burn_damage", 79635, 0.1, false),
        ("Explosive Sticky Bomb", "damage", 1327245, 0.1, true),
        ("Shrapnel Trap", "damage", 663623, 0.1, true),
        ("Assault Turret", "damage", 26545, 0.2, false),
        ("Incinerator Turret", "damage", 13272, 0.1, false),
        ("Incinerator Turret", "burn_damage", 53090, 0.1, false),
        ("Sniper Turret", "damage", 729985, 0.15, false),
        ("Artillery Turret", "damage", 729985, 0.15, true),
        ("Artillery Turret", "bleed_damage", 29863, 0, false),
        ("Stinger Hive", "damage", 265449, 0.2, false),
        ("Stinger Hive", "bleed_damage", 29863, 0, false),
        ("Firestarter Chem Launcher", "damage", 265449, 0, false),
        ("Firestarter Chem Launcher", "burn_damage", 79635, 0.1, false),
        ("Oxidizer Chem Launcher", "cloud_damage", 53090, 0.1, false),
        ("Burster Firefly", "damage", 663623, 0.15, true),
        ("Demolisher Firefly", "damage", 1659056, 0.35, false),
        ("Explosive Seeker Mine", "damage", 1061796, 0.1, true),
        ("Explosive Seeker Mine", "bleed_damage", 29863, 0, false),
        ("Airburst Seeker Mine", "damage", 331811, 0.1, false),
        ("Airburst Seeker Mine", "burn_damage", 79635, 0.1, false),
        ("Cluster Seeker Mine", "damage", 729985, 0.1, true),
        ("Striker Drone", "damage", 16591, 0.2, false),
        ("Bombardier Drone", "explosion_damage", 564079, 0.1, true),
        ("Deflector Shield", "average_damage", 80465, 0.1, false),
    ];

    /// <summary>
    /// G103: every skill damage channel scales by its tier step (Skill Tier capped at 6), by its damage pool (status
    /// effects for bleed, status effects and burn for burn), by total skill damage and explosive damage, and keeps
    /// its provenance.
    /// </summary>
    [Fact]
    public void G103AllSkillDamageChannels()
    {
        foreach (var tier in new double[] { 0, 1, 2, 3, 4, 5, 6, 8 })
        {
            var compiled = WithSkillModifiers(
                ("skill_tier", ModifierOps.Flat, tier), ("skill_damage", ModifierOps.Flat, .3), ("skill_efficiency", ModifierOps.Flat, .1),
                ("status_effects", ModifierOps.Flat, .2), ("total_skill_damage", ModifierOps.Flat, .25), ("explosive_damage", ModifierOps.Flat, .15));
            foreach (var (name, channel, @base, step, explosive) in DamageChannels)
            {
                var id = compiled.Pack.ActiveSkills.LastOrDefault(skill => skill.Name == name)?.ID ?? "";
                foreach (var slot in Slots)
                {
                    var b = BuildSnapshot.Create(compiled.Pack).WithActiveSkillSelection(slot, id);
                    var result = BuildResolver.Resolve(compiled, b);
                    NoErrors(result);
                    var found = false;
                    foreach (var output in Outputs(result, slot).Where(output => output.Id == channel))
                    {
                        found = true;
                        // R54: bleeds and burns add Skill Damage and Status Effects, Burn Sticky Bomb's burn multiplies them,
                        // and three skills' hits take Status Effects too.
                        var pool = (name, channel) switch
                        {
                            ("Burn Sticky Bomb", "burn_damage") => 1.4 * 1.3,
                            (_, "bleed_damage" or "burn_damage") => 1.7,
                            ("Firestarter Chem Launcher" or "Airburst Seeker Mine" or "Incinerator Turret", "damage") => 1.7,
                            _ => 1.4,
                        };
                        var factor = explosive ? 1.15 : 1;
                        CloseTo(output.Value, @base * (1 + (Math.Min(tier, 6) * step)) * pool * 1.25 * factor, 1e-6);
                        Assert.True(output.Inputs.GetValueOrDefault("total_skill_damage") == .25 && output.Description.Contains("Skill List!", StringComparison.Ordinal),
                            $"missing skill provenance {name}/{channel}: {output}");
                    }
                    Assert.True(found, $"missing {name}/{channel}/{slot} at tier {tier}");
                }
            }
        }
    }

    /// <summary>
    /// G103: Glass Cannon, Spotter and Opportunistic (and their perfect versions) amplify skill damage, the gated
    /// ones only while their assumption is on; the electronic skills have no invented damage channel.
    /// </summary>
    [Fact]
    public void G103SkillAmplificationAndElectronicGaps()
    {
        foreach (var (id, flag, bonus) in new[]
        {
            ("gear_talent_glass_cannon", "", .25), ("gear_talent_perfect_glass_cannon", "", .30),
            ("gear_talent_spotter", "target_pulsed", .15), ("gear_talent_perfect_spotter", "target_pulsed", .20),
            ("gear_talent_opportunistic", "opportunistic_target_active", .10), ("gear_talent_perfect_opportunistic", "opportunistic_target_active", .15),
        })
        {
            var host = Compiled.Pack.Items.FirstOrDefault(item => item.TalentSlots.Any(slot => slot.Choices.Contains(id)))?.ID ?? "";
            var b = TalentBuild(id, host).WithActiveSkillSelection("skill_slot_1", "skill_assault_turret").WithActiveSkillSelection("skill_slot_2", "skill_striker_drone");
            var context = flag.Length > 0 ? Context((flag, "true")) : Context();
            var result = Resolve(b, context);
            CloseTo(result.Calculation.Stats.Of("skill_damage_multiplier"), 1 + bonus, 1e-12);
            foreach (var (slot, @base) in new[] { ("skill_slot_1", 26545.0), ("skill_slot_2", 16591.0) })
            {
                foreach (var output in Outputs(result, slot).Where(output => output.Id == "damage")) { CloseTo(output.Value, @base * (1 + bonus), 1e-8); }
            }
            if (flag.Length > 0) { CloseTo(Stats(b, Context((flag, "false"))).Of("skill_damage_multiplier"), 1, 1e-12); }
        }
        foreach (var id in new[] { "skill_emp_sticky_bomb", "skill_jammer_pulse" })
        {
            var b = Build().WithActiveSkillSelection("skill_slot_1", id);
            Assert.False(Outputs(Resolve(b), "skill_slot_1").Any(output => IsDamagePreviewChannel(output.Id)), $"invented electronics damage for {id}");
        }
    }

    /// <summary>G115: each skill bonus adds Skill Efficiency into its effective pool, and the damage channels read their pools.</summary>
    [Fact]
    public void G115CombinedSkillPoolsAndDamageChannels()
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["skill_damage"] = .3,
            ["skill_haste"] = .1,
            ["repair_skills"] = .4,
            ["skill_duration"] = .5,
            ["skill_health"] = .6,
            ["status_effects"] = .7,
            ["skill_efficiency"] = .2,
        };
        var compiled = WithFormulas(values);
        var r = BuildResolver.Resolve(compiled, BuildSnapshot.Create(compiled.Pack, "Bonuses"));
        var s = r.Calculation.Stats;
        foreach (var id in new[] { "skill_damage", "skill_haste", "repair_skills", "skill_duration", "skill_health", "status_effects" })
        {
            CloseTo(s.Of(id), values[id], 1e-12);
            CloseTo(s.Of("effective_" + id), values[id] + .2, 1e-12);
        }
        CloseTo(s.Of("total_skill_damage"), 0, 1e-12);
        CloseTo(s.Of("total_skill_repair"), 0, 1e-12);
        foreach (var (skill, channel, expected) in new[]
        {
            ("skill_assault_turret", "damage", 26545 * 1.5), ("skill_stinger_hive", "bleed_damage", 29863 * 2.4), ("skill_firestarter_chem_launcher", "burn_damage", 79635 * 2.4),
        })
        {
            var b = BuildSnapshot.Create(compiled.Pack, "Bonuses").WithActiveSkillSelection("skill_slot_1", skill);
            var found = false;
            foreach (var output in Outputs(BuildResolver.Resolve(compiled, b), "skill_slot_1").Where(output => output.Id == channel))
            {
                found = true;
                CloseTo(output.Value, expected, 1e-8);
            }
            Assert.True(found, $"missing {channel} channel");
        }
    }

    private static readonly (string Id, string Channel, double Base)[] HealingChannels =
    [
        ("skill_repair_trap", "repair", 165906), ("skill_restorer_hive", "repair", 59726),
        ("skill_reinforcer_chem_launcher", "ally_repair", 199087), ("skill_reinforcer_chem_launcher", "repair_over_time", 39817),
        ("skill_mender_seeker_mine", "repair_rate", 17918), ("skill_fixer_drone", "repair", 17918),
    ];

    /// <summary>
    /// G159: every skill repair channel scales by 20% per whole Skill Tier (0 to 6), by Repair Skills with Skill
    /// Efficiency and by total skill repair, and ignores damage bonuses and incoming repairs.
    /// </summary>
    [Fact]
    public void G159AllHealingChannels()
    {
        foreach (var tier in new[] { -1, 0, 1, 2, 3, 4, 5, 6, 6.9, 8 })
        {
            var compiled = Validated(Compiled.Pack with
            {
                ActiveSkills = [.. Compiled.Pack.ActiveSkills.Select(skill => skill with
                {
                    Modifiers =
                    [
                        Modifier("skill_tier", ModifierOps.Flat, tier), Modifier("repair_skills", ModifierOps.Flat, .3), Modifier("skill_efficiency", ModifierOps.Flat, .1),
                        Modifier("total_skill_repair", ModifierOps.Flat, .25), Modifier("skill_damage", ModifierOps.Flat, 5), Modifier("status_effects", ModifierOps.Flat, 5),
                        Modifier("total_skill_damage", ModifierOps.Flat, 5), Modifier("skill_damage_multiplier", ModifierOps.Mult, 5), Modifier("incoming_repairs", ModifierOps.Flat, 5),
                    ],
                })],
            });
            foreach (var (id, channel, @base) in HealingChannels)
            {
                foreach (var slot in Slots)
                {
                    var r = BuildResolver.Resolve(compiled, BuildSnapshot.Create(compiled.Pack).WithActiveSkillSelection(slot, id));
                    NoErrors(r);
                    var found = false;
                    foreach (var o in Outputs(r, slot).Where(o => o.Id == channel))
                    {
                        found = true;
                        CloseTo(o.Value, @base * (1 + (.2 * Math.Min(6, Math.Max(0, Math.Floor(tier))))) * 1.4 * 1.25, 1e-6);
                        CloseTo(o.Inputs.GetValueOrDefault("effective_repair_skills"), .4, 1e-12);
                        if (id == "skill_fixer_drone" || channel == "repair_over_time")
                        {
                            Assert.True(o.Name.Contains("unit unspecified", StringComparison.Ordinal), o.Name);
                        }
                    }
                    Assert.True(found, $"missing {id}/{channel} at tier {tier}");
                }
            }
        }
    }

    private static StatModifier Modifier(string stat, string op, double value) => new() { Stat = stat, Op = op, Value = new(value) };

    /// <summary>
    /// G159: Hard Wired's Feedback Loop raises skill repair by 10%, 25% with Positive Reinforcement, only while it's
    /// active and only with four pieces.
    /// </summary>
    [Fact]
    public void G159LegalFeedbackRepair()
    {
        var b = SetBuild("gear_set_hard_wired", 4).WithActiveSkillSelection("skill_slot_1", "skill_restorer_hive");
        var @base = Resolve(b).ActiveSkillPreviews["skill_slot_1"].Outputs[1].Value;
        foreach (var upgraded in new[] { false, true })
        {
            var current = upgraded ? SelectUpgrade(b, "chest", "gear_talent_positive_reinforcement") : b;
            var factor = upgraded ? 1.25 : 1.1;
            CloseTo(Resolve(current, Context(("feedback_loop_active", "true"))).ActiveSkillPreviews["skill_slot_1"].Outputs[1].Value, @base * factor, 1e-6);
            CloseTo(Resolve(current, Context(("feedback_loop_active", "false"))).ActiveSkillPreviews["skill_slot_1"].Outputs[1].Value, @base, 1e-6);
            current = current.WithEquipment("gloves", "");
            CloseTo(Stats(current, Context(("feedback_loop_active", "true"))).Of("total_skill_repair"), 0, 1e-12);
        }
    }

    /// <summary>G113: Capacitance reads the build's own Skill Tier (from armor cores' absence), not an old context value.</summary>
    [Fact]
    public void G113CapacitorReadsConfiguredCoresAndIgnoresOldContext()
    {
        var b = BuildSnapshot.Create(Compiled.Pack, "Capacitor").WithEquipment("primary_weapon", "primary_weapon_capacitor")
            .WithTalentSelection("primary_weapon", "talent", "weapon_talent_capacitance")
            .WithEquipment("mask", "mask_n_5_11_tactical_mask");
        var @base = Stats(b, Division.Weapon("primary_weapon"));
        b = b.WithAttributeRoll("mask", "core_1", new("skill_tier_gear_core", 1));
        var r = Stats(b, Context((GamePack.WeaponSlotSelector, "primary_weapon"), ("capacitor_skill_tier", "6")));
        CloseTo(r.Of("weapon_damage") - @base.Of("weapon_damage"), .075, 1e-12);
        b = b.WithAttributeRoll("mask", "core_1", new("armor_gear_core", 170000));
        CloseTo(Stats(b, Division.Weapon("primary_weapon")).Of("weapon_damage"), @base.Of("weapon_damage"), 1e-12);
    }

    /// <summary>G113: Capacitance gives 7.5% Weapon Damage per whole Skill Tier, 0 to 6, only on its own active weapon.</summary>
    [Fact]
    public void G113CapacitorTierBoundsAndOwnerScope()
    {
        foreach (var (tier, bonus) in new[] { (-1.0, 0.0), (0, 0), (1.9, .075), (6, .45), (8, .45) })
        {
            var compiled = WithFormulas(new Dictionary<string, double>(StringComparer.Ordinal) { ["skill_tier"] = tier });
            var b = BuildSnapshot.Create(compiled.Pack, "Capacitor").WithEquipment("primary_weapon", "primary_weapon_capacitor")
                .WithTalentSelection("primary_weapon", "talent", "weapon_talent_capacitance");
            var r = BuildResolver.Resolve(compiled, b, Division.Weapon("primary_weapon")).Calculation.Stats;
            var clear = BuildResolver.Resolve(compiled, b.WithTalentSelection("primary_weapon", "talent", ""), Division.Weapon("primary_weapon")).Calculation.Stats;
            CloseTo(r.Of("weapon_damage") - clear.Of("weapon_damage"), bonus, 1e-12);
            CloseTo(BuildResolver.Resolve(compiled, b, Division.Weapon("secondary_weapon")).Calculation.Stats.Of("weapon_damage"), 0, 1e-12);
        }
    }

    /// <summary>
    /// G402: Hoarder fills the grenade reference stats, with its per-enemy damage from an input, and Transference
    /// Overclock gives Hive Skill Haste per tier.
    /// </summary>
    [Fact]
    public void G402GrenadeAndHiveReferenceStats()
    {
        var b = BuildSnapshot.Create(Compiled.Pack, "G402")
            .WithEquipment("chest", "chest_collector")
            .WithTalentSelection("chest", "talent", "gear_talent_hoarder")
            .WithEquipment("gloves", "gloves_btsu_datagloves")
            .WithTalentSelection("gloves", "talent", "gear_talent_transference_overclock");
        var s = Stats(b, Context(("hoarder_extra_enemies", "2")));
        CloseTo(s.Of("grenade_capacity"), 3, 1e-12);
        CloseTo(s.Of("grenade_radius"), 0.5, 1e-12);
        CloseTo(s.Of("grenade_damage"), 0.35 + 0.5, 1e-12);
        CloseTo(s.Of("hive_skill_haste"), 0.15 * Math.Min(Math.Max(s.Of("skill_tier"), 0), 6), 1e-12);
    }
}
