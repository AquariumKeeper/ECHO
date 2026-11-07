using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// Bonus armor from talents and sets, and the armor capacities and budgets sets derive from maximum armor (F33
/// armor), ported from Go's bonus_armor_g55_test.go, bonus_armor_g56_test.go, armor_capacity_g147_test.go and
/// available_armor_g148_test.go.
/// </summary>
public sealed class BonusArmorTests
{
    private const string BonusArmor = "bonus_armor_percent";

    /// <summary>
    /// Go's g55TalentBuild and g56TalentBuild: the named host, or the first ordinary gear item (in the slot, when
    /// given) that offers the talent, equipped with the talent selected.
    /// </summary>
    private static (BuildSnapshot Build, string Slot, ItemDefinition Item, string TalentSlot) TalentBuild(string talentId, string host = "", string slot = "")
    {
        var item = host.Length > 0
            ? Compiled.Pack.Items.FirstOrDefault(item => item.Name == host)
            : Compiled.Pack.Items.FirstOrDefault(item => item.Tags.Contains("gear") && !item.Tags.Contains("named") && !item.Tags.Contains("exotic")
                && (slot.Length == 0 || item.Slot == slot) && item.TalentSlots.Any(talentSlot => talentSlot.Choices.Contains(talentId)));
        Assert.True(item is not null && item.Slot.Length > 0, $"no host item for {talentId} (preferred host \"{host}\")");
        var talentSlot = item.TalentSlots.FirstOrDefault(talentSlot => talentSlot.Choices.Contains(talentId));
        Assert.True(talentSlot is not null, $"{item.Name} does not offer {talentId}");
        var b = BuildSnapshot.Create(Compiled.Pack, "G55").WithEquipment(item.Slot, item.ID).WithTalentSelection(item.Slot, talentSlot.ID, talentId);
        return (b, item.Slot, item, talentSlot.ID);
    }

    private static double Bonus(BuildSnapshot b, params (string Key, string Value)[] context) => Stats(b, Context(context)).Of(BonusArmor);

    /// <summary>Go's expectG55TalentContribution, g56ExpectTalent and g56ExpectSet: an exact flat contribution to bonus armor.</summary>
    private static void ExpectContribution(ResolvedBuild result, string kind, string id, double value)
    {
        var contribution = result.Calculation.Breakdowns.TryGetValue(BonusArmor, out var breakdown)
            ? breakdown.Contributions.FirstOrDefault(contribution => contribution.Source.Kind == kind && contribution.Source.Id == id)
            : null;
        Assert.True(contribution is not null, $"no {kind} provenance for {id} in {BonusArmor}");
        Assert.True(contribution.Op == "flat" && contribution.Value == value, $"{id} contribution = ({contribution.Op}, {contribution.Value}), want flat {value}");
    }

    private static bool HasCode(ResolvedBuild result, string code) => result.Diagnostics.Any(diagnostic => diagnostic.Code == code);

    private static void Near(double got, double want, double tolerance, string what)
    {
        if (Math.Abs(got - want) > tolerance) { Assert.Fail($"{what}: got {got}, want {want} (within {tolerance})"); }
    }

    /// <summary>
    /// G55: Protected Reload, Bloodsucker, Adrenaline Rush and Protector (and their perfect versions) add bonus armor
    /// only under their assumptions, independent of the weapon, with talent provenance, on fixed named and generic
    /// hosts, withheld when removed, cleared or unsupported, and summed across chest and backpack.
    /// </summary>
    [Fact]
    public void G55BonusArmorTalents()
    {
        // raw stat exists at zero and is independent of weapon selection
        var empty = Resolve(BuildSnapshot.Create(Compiled.Pack));
        Assert.True(empty.Calculation.Stats.Of(BonusArmor) == 0, $"{BonusArmor} = {empty.Calculation.Stats.Of(BonusArmor)}, want 0");
        Assert.True(empty.Calculation.Breakdowns.ContainsKey(BonusArmor), $"{BonusArmor} breakdown missing");
        var sleight = TalentBuild("gear_talent_perfect_protected_reload", "Sleight").Build;
        foreach (var weaponSlot in new[] { "primary_weapon", "secondary_weapon", "sidearm" })
        {
            var weapon = Compiled.Pack.Items.FirstOrDefault(item => item.Slot == weaponSlot && item.Tags.Contains("weapon"));
            Assert.True(weapon is not null, $"no weapon item for slot {weaponSlot}");
            var got = Resolve(sleight.WithEquipment(weaponSlot, weapon.ID), Context(("weapon_slot", weaponSlot), ("protected_reload_active", "true")));
            Near(got.Calculation.Stats.Of(BonusArmor), 0.4, 1e-12, $"Sleight with {weaponSlot} active");
            ExpectContribution(got, "talent", "gear_talent_perfect_protected_reload", 0.4);
        }
        var unmatched = Bonus(sleight, ("weapon_slot", "missing"), ("protected_reload_active", "true"));
        Assert.True(unmatched == 0.4, $"unmatched weapon selection bonus = {unmatched}, want 0.4");

        foreach (var (id, host, value) in new[] { ("gear_talent_protected_reload", "", 0.2), ("gear_talent_perfect_protected_reload", "Sleight", 0.4) })
        {
            var b = TalentBuild(id, host).Build;
            foreach (var context in new[] { Context(), Context(("protected_reload_active", "false")), Context(("protected_reload_active", "unknown")) })
            {
                var inactive = Stats(b, context).Of(BonusArmor);
                Assert.True(inactive == 0, $"{id} inactive bonus = {inactive}, want 0");
            }
            var active = Resolve(b, Context(("protected_reload_active", "true")));
            Near(active.Calculation.Stats.Of(BonusArmor), value, 1e-12, $"{id} active");
            ExpectContribution(active, "talent", id, value);
        }

        foreach (var (id, perStack) in new[] { ("gear_talent_bloodsucker", 0.1), ("gear_talent_perfect_bloodsucker", 0.12) })
        {
            var b = TalentBuild(id, id.Contains("perfect", StringComparison.Ordinal) ? "Liquid Engineer" : "").Build;
            Assert.True(Bonus(b) == 0, $"{id} missing stacks bonus = {Bonus(b)}, want 0");
            var max = Resolve(b, Context(("bloodsucker_stacks", "10")));
            Near(max.Calculation.Stats.Of(BonusArmor), perStack * 10, 1e-12, $"{id} 10 stacks");
            ExpectContribution(max, "talent", id, perStack * 10);
            foreach (var invalid in new[] { "-1", "11", "1.5", "NaN", "Infinity", "1e999", "unknown" })
            {
                var result = Resolve(b, Context(("bloodsucker_stacks", invalid)));
                Assert.True(result.Calculation.Stats.Of(BonusArmor) == 0, $"{id} invalid stack \"{invalid}\" gives {result.Calculation.Stats.Of(BonusArmor)}, want 0");
                Assert.True(HasCode(result, "context-value-invalid"), $"{id} invalid stack \"{invalid}\" has no context-value-invalid diagnostic");
            }
        }

        foreach (var (id, host, perStack) in new[] { ("gear_talent_adrenaline_rush", "", 0.2), ("gear_talent_perfect_adrenaline_rush", "Matador", 0.23) })
        {
            var b = TalentBuild(id, host).Build;
            foreach (var (what, context) in new[]
            {
                ("missing active flag", new[] { ("adrenaline_rush_stacks", "3") }),
                ("false active flag", new[] { ("adrenaline_rush_active", "false"), ("adrenaline_rush_stacks", "3") }),
                ("missing stacks", new[] { ("adrenaline_rush_active", "true") }),
                ("zero stacks", new[] { ("adrenaline_rush_active", "true"), ("adrenaline_rush_stacks", "0") }),
            })
            {
                var got = Bonus(b, context);
                Assert.True(got == 0, $"{id} {what} bonus = {got}, want 0");
            }
            var active = Resolve(b, Context(("adrenaline_rush_active", "true"), ("adrenaline_rush_stacks", "3")));
            Near(active.Calculation.Stats.Of(BonusArmor), perStack * 3, 1e-12, $"{id} 3 stacks");
            ExpectContribution(active, "talent", id, perStack * 3);
            foreach (var invalid in new[] { "-1", "4", "1.5", "NaN", "Infinity", "1e999", "unknown" })
            {
                var result = Resolve(b, Context(("adrenaline_rush_active", "true"), ("adrenaline_rush_stacks", invalid)));
                Assert.True(result.Calculation.Stats.Of(BonusArmor) == 0 && HasCode(result, "context-value-invalid"), $"{id} invalid adrenaline stacks \"{invalid}\" were not rejected");
            }
        }

        foreach (var (id, host, value) in new[] { ("gear_talent_protector", "", 0.05), ("gear_talent_perfect_protector", "Keeper", 0.25) })
        {
            var b = TalentBuild(id, host).Build;
            Assert.True(Bonus(b) == 0, $"{id} missing active flag bonus = {Bonus(b)}, want 0");
            Assert.True(Bonus(b, ("protector_active", "false")) == 0, $"{id} false active flag bonus = {Bonus(b, ("protector_active", "false"))}, want 0");
            var active = Resolve(b, Context(("protector_active", "true")));
            Near(active.Calculation.Stats.Of(BonusArmor), value, 1e-12, $"{id} active");
            ExpectContribution(active, "talent", id, value);
        }

        // fixed perfected hosts and generic ordinary hosts
        foreach (var (talentId, hostName) in new[] { ("gear_talent_perfect_protected_reload", "Sleight"), ("gear_talent_perfect_bloodsucker", "Liquid Engineer"), ("gear_talent_perfect_adrenaline_rush", "Matador"), ("gear_talent_perfect_protector", "Keeper") })
        {
            var item = TalentBuild(talentId, hostName).Item;
            var wantSlot = talentId.Contains("protected_reload", StringComparison.Ordinal) ? "chest" : "backpack";
            Assert.True(item.Slot == wantSlot, $"{item.Name} occupies {item.Slot}, want {wantSlot}");
        }
        foreach (var talentId in new[] { "gear_talent_protected_reload", "gear_talent_bloodsucker", "gear_talent_adrenaline_rush", "gear_talent_protector" })
        {
            var item = TalentBuild(talentId).Item;
            Assert.False(item.Tags.Contains("named"), $"ordinary {talentId} selected named host {item.Name}");
        }

        // equipment removal and unsupported selection
        {
            var (b, slot, _, talentSlot) = TalentBuild("gear_talent_protected_reload");
            b = b.WithEquipment("primary_weapon", "primary_weapon_acr");
            var baseline = Stats(b, Context(("weapon_slot", "primary_weapon")));
            var active = Stats(b, Context(("weapon_slot", "primary_weapon"), ("protected_reload_active", "true")));
            Near(active.Of(BonusArmor), 0.2, 1e-12, "Protected Reload with a weapon");
            Assert.True(baseline.Of("equipped_weapon_damage") > 0, "weapon preview fixture must have positive damage");
            foreach (var stat in new[] { "armor", "health", "character_armor", "character_health", "weapon_base_damage", "equipped_weapon_damage", "weapon_damage_multiplier", "total_weapon_damage" })
            {
                Assert.True(active.Of(stat) == baseline.Of(stat), $"bonus armor talent changed unrelated {stat}: {baseline.Of(stat)} -> {active.Of(stat)}");
            }
            var removed = b.WithEquipment(slot, "");
            Assert.True(removed.TalentSelections.GetValueOrDefault(slot) is not { Count: > 0 }, "talent selection survived equipment removal");
            Assert.True(Bonus(removed, ("protected_reload_active", "true")) == 0, $"removed talent still contributes {Bonus(removed, ("protected_reload_active", "true"))}");
            var cleared = b.WithTalentSelection(slot, talentSlot, "");
            Assert.True(cleared.TalentSelections.GetValueOrDefault(slot) is not { Count: > 0 }, "cleared talent selection remains");
            Assert.True(Bonus(cleared, ("protected_reload_active", "true")) == 0, $"cleared talent still contributes {Bonus(cleared, ("protected_reload_active", "true"))}");
            var invalid = Resolve(b.WithTalentSelection(slot, talentSlot, "gear_talent_perfect_protected_reload"), Context(("protected_reload_active", "true")));
            Assert.True(invalid.Calculation.Stats.Of(BonusArmor) == 0 && HasCode(invalid, "talent-not-allowed"),
                $"unsupported talent was not withheld: stat={invalid.Calculation.Stats.Of(BonusArmor)} diagnostics={string.Join("; ", invalid.Diagnostics.Select(diagnostic => diagnostic.Code))}");
            var intimidate = TalentBuild("gear_talent_intimidate").Build;
            Assert.True(Stats(intimidate).Of("weapon_damage_multiplier") == 1, $"Intimidate activated without its context: {Stats(intimidate).Of("weapon_damage_multiplier")}");
            Near(Stats(intimidate, Context(("intimidate_active", "true"), ("intimidate_stacks", "1"))).Of("weapon_damage_multiplier"), 1.04, 1e-12, "Intimidate active");
            Assert.True(active.Of("weapon_damage_multiplier") == 1, $"bonus armor buff implicitly activated Intimidate: {active.Of("weapon_damage_multiplier")}");
        }

        // combines chest and backpack talent contributions without simulating triggers
        {
            static BuildSnapshot WithBackpack(BuildSnapshot b, string talentId)
            {
                var (_, slot, item, talentSlot) = TalentBuild(talentId, slot: "backpack");
                return b.WithEquipment(slot, item.ID).WithTalentSelection(slot, talentSlot, talentId);
            }
            var combined = WithBackpack(TalentBuild("gear_talent_intimidate", slot: "chest").Build, "gear_talent_protector");
            var active = Stats(combined, Context(("protector_active", "true")));
            Near(active.Of(BonusArmor), 0.05, 1e-12, "Protector with Intimidate");
            Assert.True(active.Of("weapon_damage_multiplier") == 1, $"Protector implicitly activated Intimidate: {active.Of("weapon_damage_multiplier")}");
            Near(Stats(combined, Context(("protector_active", "true"), ("intimidate_active", "true"), ("intimidate_stacks", "1"))).Of("weapon_damage_multiplier"), 1.04, 1e-12, "Intimidate active with Protector");

            combined = WithBackpack(TalentBuild("gear_talent_protected_reload", slot: "chest").Build, "gear_talent_bloodsucker");
            var summed = Resolve(combined, Context(("protected_reload_active", "true"), ("bloodsucker_stacks", "10")));
            Near(summed.Calculation.Stats.Of(BonusArmor), 1.2, 1e-12, "Protected Reload with Bloodsucker");
            var talents = summed.Calculation.Breakdowns[BonusArmor].Contributions.Count(contribution => contribution.Source.Kind == "talent");
            Assert.True(talents == 2, $"talent contribution count = {talents}, want 2");
        }
    }

    /// <summary>
    /// G56: Leadership, Galvanize, Mad Bomber and Parkour (and their perfect versions) add bonus armor only under
    /// their assumption, on hosts in the expected slot, as flat talent contributions.
    /// </summary>
    [Fact]
    public void G56BonusArmorTalentBranches()
    {
        var empty = Resolve(BuildSnapshot.Create(Compiled.Pack));
        Assert.True(empty.Calculation.Stats.Of(BonusArmor) == 0, $"empty bonus armor = {empty.Calculation.Stats.Of(BonusArmor)}, want 0");
        Assert.True(empty.Calculation.Breakdowns.ContainsKey(BonusArmor), "raw bonus armor breakdown missing");
        foreach (var (id, host, slot, key, activeValue, value) in new[]
        {
            ("gear_talent_leadership", "", "backpack", "leadership_buff", "normal", .15),
            ("gear_talent_leadership", "", "backpack", "leadership_buff", "close", .30),
            ("gear_talent_perfect_leadership", "Cap'n", "backpack", "leadership_buff", "normal", .20),
            ("gear_talent_perfect_leadership", "Cap'n", "backpack", "leadership_buff", "close", .60),
            ("gear_talent_galvanize", "", "backpack", "galvanize_active", "true", .40),
            ("gear_talent_perfect_galvanize", "Lavoisier", "backpack", "galvanize_active", "true", .50),
            ("gear_talent_mad_bomber", "", "chest", "mad_bomber_aiming", "true", .15),
            ("gear_talent_perfect_mad_bomber", "Carpenter", "chest", "mad_bomber_aiming", "true", .15),
            ("gear_talent_parkour", "NinjaBike Messenger Kneepads", "knees", "parkour_active", "true", .25),
        })
        {
            var (b, hostSlot, _, _) = TalentBuild(id, host, slot);
            Assert.True(hostSlot == slot, $"{id} host slot {hostSlot}, want {slot}");
            foreach (var inactive in new[] { "", "false", "unknown" })
            {
                var got = inactive.Length > 0 ? Bonus(b, (key, inactive)) : Bonus(b);
                Assert.True(got == 0, $"{id} {key}=\"{inactive}\" armor = {got}, want 0");
            }
            var active = Resolve(b, Context((key, activeValue)));
            Near(active.Calculation.Stats.Of(BonusArmor), value, 1e-12, $"{id} {key}={activeValue}");
            ExpectContribution(active, "talent", id, value);
        }
    }

    /// <summary>
    /// G56: Perfect Leadership holds whichever weapon is active, and is withheld when cleared, removed or selected
    /// on a host that doesn't allow it.
    /// </summary>
    [Fact]
    public void G56LeadershipWeaponIndependentAndSelectionLifecycle()
    {
        var (b, slot, item, talentSlot) = TalentBuild("gear_talent_perfect_leadership", "Cap'n", "backpack");
        foreach (var (weaponSlot, weaponId) in new[] { ("primary_weapon", "primary_weapon_acr"), ("secondary_weapon", "secondary_weapon_acr"), ("sidearm", "sidearm_tempest") })
        {
            var result = Resolve(b.WithEquipment(weaponSlot, weaponId), Context(("weapon_slot", weaponSlot), ("leadership_buff", "close")));
            Near(result.Calculation.Stats.Of(BonusArmor), .6, 1e-12, $"with {weaponSlot}");
            ExpectContribution(result, "talent", "gear_talent_perfect_leadership", .6);
        }
        var cleared = b.WithTalentSelection(slot, talentSlot, "");
        Assert.True(Bonus(cleared, ("leadership_buff", "close")) == 0, "cleared leadership still contributes");
        var reselected = cleared.WithTalentSelection(slot, talentSlot, "gear_talent_perfect_leadership");
        Near(Bonus(reselected, ("leadership_buff", "close")), .6, 1e-12, "reselected");
        Assert.True(Bonus(reselected.WithEquipment(slot, ""), ("leadership_buff", "close")) == 0, "removed leadership host still contributes");
        var invalid = Resolve(b.WithTalentSelection(slot, talentSlot, "gear_talent_leadership"), Context(("leadership_buff", "close")));
        Assert.True(invalid.Calculation.Stats.Of(BonusArmor) == 0 && HasCode(invalid, "talent-not-allowed"),
            $"unsupported talent not withheld: armor={invalid.Calculation.Stats.Of(BonusArmor)} diagnostics={string.Join("; ", invalid.Diagnostics)}");
        Assert.True(b.Equipment[slot] == item.ID, "test host changed unexpectedly");
    }

    /// <summary>The first pieces of a set, each in its own slot, as Go's g56SetBuild.</summary>
    private static BuildSnapshot StrictSetBuild(string setId, int pieces)
    {
        var set = Compiled.Pack.Sets.FirstOrDefault(set => set.ID == setId);
        Assert.True(set is not null, $"missing set {setId}");
        var b = BuildSnapshot.Create(Compiled.Pack, "G56 " + setId);
        foreach (var itemId in set.Items.Take(pieces))
        {
            var item = Compiled.Pack.Items.FirstOrDefault(item => item.ID == itemId);
            Assert.True(item is not null && item.Slot.Length > 0, $"invalid set item {itemId}");
            b = b.WithEquipment(item.Slot, item.ID);
        }
        return b;
    }

    /// <summary>A talent selected on the equipped item's talent slot that offers it, as Go's g56SelectSetTalent and g54SelectUpgrade.</summary>
    private static BuildSnapshot SelectUpgrade(BuildSnapshot b, string slot, string talentId)
    {
        var itemId = b.Equipment.GetValueOrDefault(slot, "");
        var talentSlot = Compiled.Pack.Items.Where(item => item.ID == itemId).SelectMany(item => item.TalentSlots).FirstOrDefault(talentSlot => talentSlot.Choices.Contains(talentId));
        Assert.True(talentSlot is not null, $"missing {talentId} upgrade on {slot} item {itemId}");
        return b.WithTalentSelection(slot, talentSlot.ID, talentId);
    }

    /// <summary>
    /// G56: Virtuoso's Symphony gives bonus armor beyond range from four pieces, Heartbreaker's stacks give bonus
    /// armor (more with Cold and Max BPM) while its weapon damage still needs a pulse, and Chemical Agent's bonus
    /// armor needs Reactant.
    /// </summary>
    [Fact]
    public void G56VirtuosoHeartbreakerAndChemicalAgentArmor()
    {
        for (var pieces = 0; pieces <= 6; pieces++)
        {
            var set = StrictSetBuild("gear_set_virtuoso", pieces);
            Near(Bonus(set, ("symphony_range_buff", "beyond")), pieces >= 4 ? .25 : 0, 1e-12, $"Virtuoso {pieces} pieces beyond");
            foreach (var value in new[] { "", "within", "false" })
            {
                var got = value.Length > 0 ? Bonus(set, ("symphony_range_buff", value)) : Bonus(set);
                Assert.True(got == 0, $"Virtuoso {pieces} pieces symphony_range_buff=\"{value}\" inactive armor = {got}");
            }
        }
        var virtuoso = StrictSetBuild("gear_set_virtuoso", 4);
        var baseline = Stats(virtuoso, Context(("symphony_range_buff", "beyond")));
        Near(baseline.Of(BonusArmor), .25, 1e-12, "Virtuoso beyond");
        var fortissimo = Stats(SelectUpgrade(virtuoso, "chest", "gear_talent_fortissimo"), Context(("symphony_range_buff", "beyond")));
        Near(fortissimo.Of(BonusArmor), .25, 1e-12, "Fortissimo beyond");
        Assert.True(fortissimo.Of("weapon_damage") == baseline.Of("weapon_damage") && fortissimo.Of("headshot_damage") == baseline.Of("headshot_damage"), "Fortissimo altered Symphony branch totals");

        var heartbreaker = StrictSetBuild("gear_set_heartbreaker", 4);
        var cold = SelectUpgrade(heartbreaker, "backpack", "gear_talent_cold");
        var maxBpm = SelectUpgrade(heartbreaker, "chest", "gear_talent_max_bpm");
        var combined = SelectUpgrade(cold, "chest", "gear_talent_max_bpm");
        Near(Bonus(heartbreaker, ("heartbreaker_stacks", "50")), .5, 1e-12, "Heartbreaker 50 stacks");
        Near(Bonus(cold, ("heartbreaker_stacks", "50")), 1, 1e-12, "Cold 50 stacks");
        Near(Bonus(maxBpm, ("heartbreaker_stacks", "100")), 1, 1e-12, "Max BPM 100 stacks");
        Near(Bonus(combined, ("heartbreaker_stacks", "100")), 2, 1e-12, "Cold and Max BPM 100 stacks");
        var noPulse = Stats(combined, Context(("heartbreaker_stacks", "100"))).Of("weapon_damage_multiplier");
        Assert.True(noPulse == 1, $"Heartbreaker weapon damage escaped pulse gate: {noPulse}");
        Near(Stats(combined, Context(("heartbreaker_stacks", "100"), ("target_pulsed", "true"))).Of("weapon_damage_multiplier"), 1 + .011 * 100, 1e-12, "pulsed multiplier");
        Near(Bonus(heartbreaker, ("heartbreaker_stacks", "60")), .5, 1e-12, "Heartbreaker 60 stacks cap at 50");
        Near(Bonus(heartbreaker, ("heartbreaker_stacks", "0")), 0, 1e-12, "Heartbreaker no stacks");

        var catalyst = TalentBuild("gear_talent_chemical_agent", "Catalyst", "mask").Build;
        foreach (var (stacks, count) in new[] { ("0", 0.0), ("12", 12.0) })
        {
            var active = Stats(catalyst, Context(("catalysis_stacks", stacks), ("reactant_active", "true")));
            Near(active.Of(BonusArmor), .25, 1e-12, $"Chemical Agent {stacks} stacks");
            Near(active.Of("weapon_damage"), .02 * count, 1e-12, $"Catalysis {stacks} stacks weapon damage");
        }
        foreach (var context in new[] { new[] { ("catalysis_stacks", "12") }, new[] { ("catalysis_stacks", "0"), ("reactant_active", "false") } })
        {
            var got = Bonus(catalyst, context);
            Assert.True(got == 0, $"Chemical Agent inactive armor = {got} for {string.Join(", ", context)}");
        }
        Near(Bonus(catalyst, ("catalysis_stacks", "0"), ("reactant_active", "true"), ("target_status_affected", "false")), .25, 1e-12, "Chemical Agent without a status-affected target");
    }

    /// <summary>The first pieces of a set, each in its own slot, skipping missing items, as Go's g54SetBuild.</summary>
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

    /// <summary>A configured armor core, watch upgrade and mask upgrade, as G147 and G148 add to raise maximum armor.</summary>
    private static BuildSnapshot WithConfiguredArmor(BuildSnapshot b, string set) => b
        .WithAttributeRoll("mask", "core_1", new("armor_gear_core", 170000))
        .WithAccountUpgrade("watch_total_armor", 50)
        .WithEquipmentUpgrade("mask", "mask_" + set + "_mask", 10);

    /// <summary>
    /// G147: Refactor's Return to Sender bonus armor capacity (with Over Engineered) and System Corruption's
    /// available Hackstep repair are shares of maximum armor from four pieces, follow configured armor, ignore the
    /// sets' assumptions and grant no bonus armor.
    /// </summary>
    [Fact]
    public void G147PassiveArmorCapacitiesAndConfiguredArmor()
    {
        foreach (var (set, stat, share) in new[] { ("refactor", "return_to_sender_bonus_armor_capacity", .8), ("system_corruption", "hackstep_available_armor_repair", .2) })
        {
            BuildSnapshot Of(int pieces)
            {
                var b = SetBuild("gear_set_" + set, pieces);
                return set == "refactor" ? SelectUpgrade(b, "backpack", "gear_talent_over_engineered") : b;
            }
            foreach (var pieces in new[] { 3, 4, 5 })
            {
                var b = Of(pieces);
                var r = Stats(b);
                var want = pieces >= 4 ? r.Of("character_armor") * share : 0;
                Near(r.Of(stat), want, 1e-8, $"{set} {pieces} pieces {stat}");
                foreach (var full in new[] { "true", "false" })
                {
                    var on = Stats(b, Context(("full_armor", full), ("return_to_sender_skill_damage", "100000"), ("hackstep_qualifying_bonus_armor_percent", "50")));
                    Near(on.Of(stat), want, 1e-8, $"{set} {pieces} pieces {stat} full_armor={full}");
                    Near(on.Of(BonusArmor), 0, 1e-12, $"{set} {pieces} pieces bonus armor full_armor={full}");
                }
            }
            var four = Of(4);
            var baseArmor = Stats(four).Of("character_armor");
            var configured = WithConfiguredArmor(four, set);
            var configuredStats = Stats(configured);
            Assert.True(configuredStats.Of("character_armor") > baseArmor, $"{set} configured armor did not increase");
            Near(configuredStats.Of(stat), configuredStats.Of("character_armor") * share, 1e-8, $"{set} configured {stat}");
            Near(configuredStats.Of(BonusArmor), 0, 1e-12, $"{set} configured bonus armor");
            if (set == "refactor") { Near(Stats(configured.WithTalentSelection("backpack", "talent", "")).Of(stat), 0, 1e-12, "Over Engineered cleared"); }
        }
    }

    /// <summary>
    /// G148: Hackstep's available bonus armor and Umbra's per-stack and full-stack Shadow repair are shares of
    /// maximum armor from four pieces, raised by the backpack upgrade only there, unchanged by assumptions, and the
    /// NinjaBike backpack's Resourceful credit keeps them.
    /// </summary>
    [Fact]
    public void G148AvailableArmorBudgets()
    {
        foreach (var (set, stat, talent, share, upgradedShare) in new[]
        {
            ("system_corruption", "hackstep_available_bonus_armor", "multithreaded_execution", .5, 1.0),
            ("umbra_initiative", "umbra_shadow_available_repair_per_stack", "into_the_light", .008, .008),
            ("umbra_initiative", "umbra_shadow_full_stack_repair_capacity", "into_the_light", .4, .8),
        })
        {
            foreach (var pieces in new[] { 3, 4, 5 })
            {
                var r = Stats(SetBuild("gear_set_" + set, pieces));
                Near(r.Of(stat), pieces >= 4 ? r.Of("character_armor") * share : 0, 1e-8, $"{set} {pieces} pieces {stat}");
            }
            var b = SetBuild("gear_set_" + set, 4);
            var up = SelectUpgrade(b, "backpack", "gear_talent_" + talent);
            var upgraded = Stats(up);
            Near(upgraded.Of(stat), upgraded.Of("character_armor") * upgradedShare, 1e-8, $"{set} {stat} with {talent}");
            var cleared = Stats(up.WithTalentSelection("backpack", "talent", ""));
            Near(cleared.Of(stat), cleared.Of("character_armor") * share, 1e-8, $"{set} {stat} with {talent} cleared");
            var invalid = Stats(b.WithTalentSelection("chest", "talent", "gear_talent_" + talent));
            Near(invalid.Of(stat), invalid.Of("character_armor") * share, 1e-8, $"{set} {stat} with {talent} on the chest");
            b = WithConfiguredArmor(b, set);
            var baseline = Stats(b);
            Near(baseline.Of(stat), baseline.Of("character_armor") * share, 1e-8, $"{set} configured {stat}");
            var on = Stats(b, Context(("full_armor", "true"), ("umbra_light_active", "true"), ("umbra_light_stacks", "100"), ("hackstep_qualifying_bonus_armor_percent", "100")));
            foreach (var id in new[] { stat, "character_armor", BonusArmor, "estimated_bonus_armor", "estimated_armor_regeneration" })
            {
                Near(on.Of(id), baseline.Of(id), 1e-8, $"{set} {id} under assumptions");
            }
            var ninja = Stats(b.WithEquipment("backpack", "backpack_ninjabike_messenger_backpack").WithTalentSelection("backpack", "talent", "gear_talent_resourceful"));
            Near(ninja.Of(stat), ninja.Of("character_armor") * share, 1e-8, $"{set} {stat} with NinjaBike");
        }
    }
}
