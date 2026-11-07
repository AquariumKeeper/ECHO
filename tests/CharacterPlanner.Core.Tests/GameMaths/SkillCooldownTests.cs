using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// Skill Haste, cooldowns, the drone Overcharge cooldown comparison and Cavalier's charging protection (F20, Active
/// skills and skill previews), ported from Go's skill_haste_g112_test.go, skill_cooldowns_g120_test.go,
/// skill_cooldowns_g160_test.go, drone_cooldowns_g223_test.go and charging_protection_g154_test.go. Go's G223 read
/// data/sources/division-2-drone-cooldowns.json, which isn't part of this repository; the profiles' bases and
/// Overcharge bonuses it used are copied here verbatim.
/// </summary>
public sealed class SkillCooldownTests
{
    private static readonly string[] Slots = ["skill_slot_1", "skill_slot_2"];

    /// <summary>The committed pack with some stat formulas replaced by constants, as Go's edit of c.Pack.Stats and ValidatePack.</summary>
    private static CompiledPackExpressions WithFormulas(params (string Stat, double Value)[] values)
    {
        var replaced = values.ToDictionary(value => value.Stat, value => value.Value.ToString("R", CultureInfo.InvariantCulture), StringComparer.Ordinal);
        return Validated(Compiled.Pack with { Stats = [.. Compiled.Pack.Stats.Select(stat => replaced.TryGetValue(stat.ID, out var formula) ? stat with { Formula = formula } : stat)] });
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

    /// <summary>
    /// G112: three Empress International pieces give 8% Skill Efficiency, which feeds effective Skill Haste and both
    /// skills' cooldown factors without a setting; removing a piece removes it.
    /// </summary>
    [Fact]
    public void G112SetEfficiencyAutomaticallyFeedsCooldowns()
    {
        var b = BuildSnapshot.Create(Compiled.Pack, "Haste").WithActiveSkillSelection("skill_slot_1", "skill_assault_turret").WithActiveSkillSelection("skill_slot_2", "skill_striker_drone");
        foreach (var slot in new[] { "mask", "chest", "gloves" }) { b = b.WithEquipment(slot, slot + "_empress_international_" + slot); }
        var r = Resolve(b);
        var s = r.Calculation.Stats;
        CloseTo(s.Of("skill_efficiency"), .08, 1e-12);
        CloseTo(s.Of("effective_skill_haste"), s.Of("skill_haste") + .08, 1e-12);
        foreach (var slot in Slots)
        {
            foreach (var output in Outputs(r, slot).Where(output => output.Id == "cooldown_factor")) { CloseTo(output.Value, 1 / (1 + s.Of("skill_haste") + .08), 1e-12); }
        }
        s = Stats(b.WithEquipment("gloves", ""));
        CloseTo(s.Of("skill_efficiency"), 0, 1e-12);
        CloseTo(s.Of("effective_skill_haste"), s.Of("skill_haste"), 1e-12);
    }

    /// <summary>
    /// G112: Skill Haste and Skill Efficiency combine in the cooldown factor, bounded at 0 when the total haste is -1
    /// or less; Skill Efficiency raises damage once, not twice.
    /// </summary>
    [Fact]
    public void G112CombinedHasteBoundsAndDamageNotDoubled()
    {
        foreach (var (haste, efficiency, factor) in new[] { (.2, .3, 2.0 / 3), (0, 0, 1), (-.25, 0, 4.0 / 3), (-1, 0, 0), (-2, .1, 0) })
        {
            var compiled = WithFormulas(("skill_haste", haste), ("skill_efficiency", efficiency));
            var b = BuildSnapshot.Create(compiled.Pack, "Haste").WithActiveSkillSelection("skill_slot_1", "skill_assault_turret").WithActiveSkillSelection("skill_slot_2", "skill_striker_drone");
            var r = BuildResolver.Resolve(compiled, b);
            CloseTo(r.Calculation.Stats.Of("effective_skill_haste"), haste + efficiency, 1e-12);
            foreach (var slot in Slots)
            {
                var found = false;
                foreach (var output in Outputs(r, slot))
                {
                    if (output.Id == "cooldown_factor")
                    {
                        found = true;
                        CloseTo(output.Value, factor, 1e-12);
                    }
                    if (slot == "skill_slot_1" && output.Id == "damage") { CloseTo(output.Value, 26545 * (1 + efficiency), 1e-8); }
                }
                Assert.True(found, $"missing cooldown factor in {slot} for haste {haste}, efficiency {efficiency}");
            }
            NoErrors(r);
        }
    }

    /// <summary>G120: every cooldown skill variant shows the cooldown factor from combined haste, in either slot.</summary>
    [Fact]
    public void G120CooldownVariantsAndSlots()
    {
        string[] ids = ["assault_turret", "incinerator_turret", "sniper_turret", "artillery_turret", "striker_drone", "bombardier_drone", "burster_firefly", "demolisher_firefly",
            "explosive_seeker_mine", "airburst_seeker_mine", "cluster_seeker_mine", "burn_sticky_bomb", "explosive_sticky_bomb", "shrapnel_trap"];
        foreach (var (haste, efficiency, factor) in new[] { (0.0, 0.0, 1.0), (1, 0, .5), (.2, .3, 2.0 / 3), (-1, 0, 0), (-2, .1, 0) })
        {
            var compiled = WithFormulas(("skill_haste", haste), ("skill_efficiency", efficiency));
            foreach (var id in ids)
            {
                foreach (var slot in Slots)
                {
                    var b = BuildSnapshot.Create(compiled.Pack, "Cooldowns").WithActiveSkillSelection(slot, "skill_" + id);
                    var found = false;
                    foreach (var output in Outputs(BuildResolver.Resolve(compiled, b), slot).Where(output => output.Id == "cooldown_factor"))
                    {
                        found = true;
                        CloseTo(output.Value, factor, 1e-12);
                    }
                    Assert.True(found, $"missing factor for {id} in {slot}");
                }
            }
        }
    }

    /// <summary>G120: charge-based skills and shields have no ordinary cooldown factor.</summary>
    [Fact]
    public void G120ExcludesChargeAndShieldPlatforms()
    {
        foreach (var id in new[] { "stinger_hive", "reviver_hive", "firestarter_chem_launcher", "oxidizer_chem_launcher", "deflector_shield" })
        {
            var b = BuildSnapshot.Create(Compiled.Pack, "Excluded").WithActiveSkillSelection("skill_slot_1", "skill_" + id);
            Assert.False(Outputs(Resolve(b), "skill_slot_1").Any(output => output.Id == "cooldown_factor"), $"unexpected ordinary cooldown for {id}");
        }
    }

    /// <summary>G160: the ordinary cooldowns divide each skill's base by one plus combined haste, 0 when that isn't positive.</summary>
    [Fact]
    public void G160OrdinaryCooldowns()
    {
        (string Id, double Base)[] cases =
        [
            ("skill_assault_turret", 25), ("skill_incinerator_turret", 30), ("skill_artillery_turret", 20), ("skill_burster_firefly", 30), ("skill_explosive_seeker_mine", 35),
            ("skill_airburst_seeker_mine", 30), ("skill_cluster_seeker_mine", 50), ("skill_striker_drone", 25), ("skill_bombardier_drone", 25), ("skill_shrapnel_trap", 60),
        ];
        foreach (var haste in new[] { 0, .9, 1, -1, -2 })
        {
            ImmutableArray<StatModifier> modifiers =
            [
                new() { Stat = "skill_haste", Op = ModifierOps.Flat, Value = new(haste) }, new() { Stat = "skill_efficiency", Op = ModifierOps.Flat, Value = new(.1) },
                new() { Stat = "skill_tier", Op = ModifierOps.Flat, Value = new(8) }, new() { Stat = "skill_damage", Op = ModifierOps.Flat, Value = new(3) },
            ];
            var compiled = Validated(Compiled.Pack with { ActiveSkills = [.. Compiled.Pack.ActiveSkills.Select(skill => skill with { Modifiers = modifiers })] });
            foreach (var (id, @base) in cases)
            {
                foreach (var slot in Slots)
                {
                    var r = BuildResolver.Resolve(compiled, BuildSnapshot.Create(compiled.Pack).WithActiveSkillSelection(slot, id));
                    NoErrors(r);
                    var found = false;
                    foreach (var o in Outputs(r, slot).Where(o => o.Id == "cooldown_seconds"))
                    {
                        found = true;
                        var want = 1 + haste + .1 > 0 ? @base / (1 + haste + .1) : 0;
                        CloseTo(o.Value, want, 1e-10);
                    }
                    Assert.True(found, $"missing cooldown_seconds for {id} in {slot}");
                }
            }
        }
    }

    /// <summary>The drone cooldown profiles of Go's data/sources/division-2-drone-cooldowns.json: skill name, output id, base and Overcharge bonus.</summary>
    private static readonly (string Name, string Id, double Base, double OverchargeBonus)[] DroneCooldowns =
    [
        ("Defender Drone", "cooldown_seconds", 20, 0.0),
        ("Fixer Drone", "cooldown_seconds", 25, 0.0),
        ("Tactician Drone", "cooldown_seconds", 20, 0.0),
    ];

    /// <summary>
    /// G223: the Overcharge recovery comparison adds the drones' cooldown comparisons, in both slots and grades,
    /// without changing stats or ordinary outputs; it leaves unsupported skills alone and an off or invalid control
    /// changes nothing.
    /// </summary>
    [Fact]
    public void G223DroneCooldownsChannelsAndIsolation()
    {
        var checkedChannels = 0;
        foreach (var profile in DroneCooldowns)
        {
            foreach (var skill in Compiled.Pack.ActiveSkills.Where(skill => skill.Name == profile.Name))
            {
                foreach (var slot in Slots)
                {
                    foreach (var rank in new long[] { 0, 20 })
                    {
                        var b = Build().WithActiveSkillSelection(slot, skill.ID).WithSkillUpgrade(slot, skill.ID, rank);
                        var before = Resolve(b);
                        var after = Resolve(b, Context(("overcharge_recovery_preview", "1")));
                        NoErrors(after);
                        Assert.True(SameStats(before.Calculation.Stats, after.Calculation.Stats) && SameBreakdowns(before.Calculation.Breakdowns, after.Calculation.Breakdowns), "stats changed");
                        var outputs = Outputs(after, slot).ToDictionary(output => output.Id, StringComparer.Ordinal);
                        foreach (var output in Outputs(before, slot))
                        {
                            Assert.True(outputs.TryGetValue(output.Id, out var same) && SameOutput(output, same), $"ordinary output changed: {output.Id}");
                        }
                        Assert.True(outputs.TryGetValue("overcharge_" + profile.Id, out var comparison), $"missing comparison overcharge_{profile.Id} for {skill.ID}");
                        CloseTo(comparison!.Value, profile.Base / (1 + profile.OverchargeBonus), 1e-6);
                        checkedChannels++;
                    }
                }
            }
        }
        Assert.True(checkedChannels == 12, $"incomplete coverage {checkedChannels}");

        var unsupported = Build().WithActiveSkillSelection("skill_slot_1", "skill_defender_drone").WithActiveSkillSelection("skill_slot_2", "skill_banshee_pulse");
        var unsupportedBefore = Resolve(unsupported);
        var unsupportedAfter = Resolve(unsupported, Context(("overcharge_recovery_preview", "1")));
        Assert.True(SamePreview(unsupportedBefore.ActiveSkillPreviews.GetValueOrDefault("skill_slot_2"), unsupportedAfter.ActiveSkillPreviews.GetValueOrDefault("skill_slot_2")), "unsupported recovery enabled");
        foreach (var value in new[] { "0", "0.5", "2", "NaN" })
        {
            var r = Resolve(unsupported, Context(("overcharge_recovery_preview", value)));
            Assert.True(SamePreviews(unsupportedBefore.ActiveSkillPreviews, r.ActiveSkillPreviews), $"off or invalid control {value} changed outputs");
        }
    }

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
    /// G154: Cavalier's four-piece Charging gains skill protection per retained second up to its cap, raised by
    /// Overcharging and Safe Charging, with the matching full-charge time; it changes no damage resistance.
    /// </summary>
    [Fact]
    public void G154ChargingSelectedGainCapAndTime()
    {
        foreach (var pieces in new[] { 3, 4, 5 })
        {
            foreach (var chest in new[] { false, true })
            {
                foreach (var backpack in new[] { false, true })
                {
                    var b = SetBuild("gear_set_cavalier", pieces);
                    double gain = .05, cap = .5, seconds = 10;
                    if (chest)
                    {
                        b = SelectUpgrade(b, "chest", "gear_talent_overcharging");
                        cap = .7;
                        seconds = 14;
                    }
                    if (backpack)
                    {
                        b = SelectUpgrade(b, "backpack", "gear_talent_safe_charging");
                        gain = .1;
                        seconds = chest ? 7 : 5;
                    }
                    var partial = 3 * gain;
                    if (pieces < 4) { (partial, cap, seconds) = (0, 0, 0); }
                    var s = Stats(b, Context(("charging_retained_seconds", "3")));
                    CloseTo(s.Of("charging_assumed_skill_protection"), partial, 1e-12);
                    CloseTo(s.Of("charging_full_charge_time"), seconds, 1e-12);
                    s = Stats(b, Context(("charging_retained_seconds", "14")));
                    CloseTo(s.Of("charging_assumed_skill_protection"), cap, 1e-12);
                    CloseTo(s.Of("charging_skill_damage_factor"), 1 - cap, 1e-12);
                    CloseTo(s.Of("damage_resistance"), 0, 1e-12);
                    CloseTo(s.Of("incoming_damage_multiplier"), 1, 1e-12);
                }
            }
        }
    }

    /// <summary>G154: invalid charging progress counts as none, and Resourceful's Charging comes only with its talent.</summary>
    [Fact]
    public void G154ChargingInvalidProgressAndResourceful()
    {
        var b = SetBuild("gear_set_cavalier", 4);
        foreach (var value in new[] { "-1", "15", "0.5", "NaN", "Infinity", "bad" })
        {
            var s = Stats(b, Context(("charging_retained_seconds", value)));
            CloseTo(s.Of("charging_assumed_skill_protection"), 0, 1e-12);
            CloseTo(s.Of("charging_skill_damage_factor"), 1, 1e-12);
            CloseTo(s.Of("charging_full_charge_time"), 10, 1e-12);
        }
        b = b.WithEquipment("backpack", "backpack_ninjabike_messenger_backpack");
        var context = Context(("charging_retained_seconds", "3"));
        CloseTo(Stats(b, context).Of("charging_assumed_skill_protection"), 0, 1e-12);
        b = b.WithTalentSelection("backpack", "talent", "gear_talent_resourceful");
        CloseTo(Stats(b, context).Of("charging_assumed_skill_protection"), .15, 1e-12);
    }

    // Go compared with reflect.DeepEqual; these compare the same values (doubles with ==, so NaN is never equal).
    private static bool SameStats(ImmutableDictionary<string, double> a, ImmutableDictionary<string, double> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static bool SameBreakdowns(ImmutableDictionary<string, AggregatedStat> a, ImmutableDictionary<string, AggregatedStat> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && pair.Value.Base == other.Base && pair.Value.Final == other.Final
            && pair.Value.Clamped == other.Clamped && pair.Value.Contributions.SequenceEqual(other.Contributions));

    private static bool SameOutput(SkillPreviewOutput a, SkillPreviewOutput b) =>
        a.Id == b.Id && a.Name == b.Name && a.Description == b.Description && a.Format == b.Format && a.Formula == b.Formula
            && a.When == b.When && a.Value == b.Value && a.Kind == b.Kind && SameStats(a.Inputs, b.Inputs);

    private static bool SamePreview(SkillPreview? a, SkillPreview? b) =>
        a is null || b is null ? a == b
            : a.SkillId == b.SkillId && a.Outputs.Length == b.Outputs.Length && a.Outputs.Zip(b.Outputs).All(outputs => SameOutput(outputs.First, outputs.Second));

    private static bool SamePreviews(ImmutableDictionary<string, SkillPreview> a, ImmutableDictionary<string, SkillPreview> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && SamePreview(pair.Value, other));
}
