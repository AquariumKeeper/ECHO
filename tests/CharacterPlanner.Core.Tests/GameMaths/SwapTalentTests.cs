using System.Text;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// Swap-triggered talents and swap-time multipliers (F35), ported from Go's swap_talents_g401_test.go and
/// swap_proximity_talents_g46_test.go.
/// </summary>
public sealed class SwapTalentTests
{
    private static ItemDefinition ItemIn(BuildSnapshot build, string slot) =>
        Compiled.Pack.Items.Single(item => item.ID == build.Equipment.GetValueOrDefault(slot, ""));

    private static string TalentSlotOf(BuildSnapshot build, string slot, string talent)
    {
        var talentSlot = ItemIn(build, slot).TalentSlots.FirstOrDefault(candidate => candidate.Choices.Contains(talent));
        Assert.True(talentSlot is not null, $"{talent} is not an allowed selection on {slot}");
        return talentSlot.ID;
    }

    /// <summary>Go's g46SelectTalent.</summary>
    private static BuildSnapshot SelectTalent(BuildSnapshot build, string slot, string talent) => build.WithTalentSelection(slot, TalentSlotOf(build, slot, talent), talent);

    /// <summary>Go's g46ClearTalent.</summary>
    private static BuildSnapshot ClearTalent(BuildSnapshot build, string slot, string talent) => build.WithTalentSelection(slot, TalentSlotOf(build, slot, talent), "");

    /// <summary>Go's g46GearBuild: the first backpack that allows the talent, with it selected, and ACRs in both weapon slots.</summary>
    private static BuildSnapshot GearBuild(string talent)
    {
        foreach (var item in Compiled.Pack.Items.Where(item => item.Slot == "backpack"))
        {
            foreach (var talentSlot in item.TalentSlots.Where(talentSlot => talentSlot.Choices.Contains(talent)))
            {
                return Build().WithEquipment("backpack", item.ID).WithTalentSelection("backpack", talentSlot.ID, talent)
                    .WithEquipment("primary_weapon", "primary_weapon_acr").WithEquipment("secondary_weapon", "secondary_weapon_acr");
            }
        }
        Assert.Fail($"no backpack host allows {talent}");
        return Build();
    }

    /// <summary>Go's g46WeaponTalentBuild: the host (or the first weapon in the slot) that allows the talent, with it selected.</summary>
    private static BuildSnapshot WeaponTalentBuild(string talent, string slot, string host)
    {
        foreach (var item in Compiled.Pack.Items.Where(item => (host.Length == 0 || item.ID == host) && item.Slot == slot))
        {
            foreach (var talentSlot in item.TalentSlots.Where(talentSlot => talentSlot.Choices.Contains(talent)))
            {
                return Build().WithEquipment(slot, item.ID).WithTalentSelection(slot, talentSlot.ID, talent);
            }
        }
        Assert.Fail($"no {slot} host allows {talent} (host \"{host}\")");
        return Build();
    }

    /// <summary>Go's g46WeaponWithTag.</summary>
    private static ItemDefinition WeaponWithTag(string tag, string slot)
    {
        var item = Compiled.Pack.Items.FirstOrDefault(item => item.Slot == slot && item.Tags.Contains(tag));
        Assert.True(item is not null, $"no {slot} weapon has tag {tag}");
        return item;
    }

    /// <summary>Go's g46ContributionValue: the talent's summed contributions to a stat, and whether it has any.</summary>
    private static (double Value, bool Found) ContributionValue(ResolvedBuild result, string stat, string talent)
    {
        if (!result.Calculation.Breakdowns.TryGetValue(stat, out var aggregated)) { return (0, false); }
        var contributions = aggregated.Contributions.Where(contribution => contribution.Source.Kind == "talent" && contribution.Source.Id == talent).ToArray();
        return (contributions.Sum(contribution => contribution.Value), contributions.Length > 0);
    }

    private static void HasContribution(ResolvedBuild result, string stat, string talent, double want)
    {
        var (value, found) = ContributionValue(result, stat, talent);
        Assert.True(found, $"missing {talent} {stat} provenance");
        CloseTo(value, want, 1e-12);
    }

    /// <summary>
    /// G401: Finisher and Busy Little Bee buff the agent while their pistol is holstered, once their assumption is
    /// set, and not while the pistol is out.
    /// </summary>
    [Fact]
    public void G401SwapTalentsApplyWhileHolstered()
    {
        var b = Build().WithEquipment("primary_weapon", "primary_weapon_acr");

        var finisher = b.WithEquipment("sidearm", "sidearm_m1911").WithTalentSelection("sidearm", "talent", "weapon_talent_finisher");
        var plain = Stats(finisher, Division.Weapon("primary_weapon"));
        var swapped = Resolve(finisher, Context(("weapon_slot", "primary_weapon"), ("finisher_swap_buff", "true")));
        CloseTo(swapped.Calculation.Stats.Of("critical_hit_chance") - plain.Of("critical_hit_chance"), 0.3, 1e-12);
        CloseTo(swapped.Calculation.Stats.Of("critical_hit_damage") - plain.Of("critical_hit_damage"), 0.3, 1e-12);
        var drawn = Stats(finisher, Context(("weapon_slot", "sidearm"), ("finisher_swap_buff", "true")));
        CloseTo(drawn.Of("critical_hit_chance"), Stats(finisher, Division.Weapon("sidearm")).Of("critical_hit_chance"), 1e-12);
        Assert.True(ContributionValue(swapped, "critical_hit_chance", "weapon_talent_finisher").Found, "the breakdown names Finisher");

        var bee = b.WithEquipment("sidearm", "sidearm_busy_little_bee").WithTalentSelection("sidearm", "talent", "weapon_talent_busy_little_bee");
        var rifle = Stats(bee, Division.Weapon("primary_weapon")).Of("weapon_damage_multiplier");
        var stacked = Stats(bee, Context(("weapon_slot", "primary_weapon"), ("busy_little_bee_stacks", "5"))).Of("weapon_damage_multiplier");
        CloseTo(stacked / rifle, 2, 1e-12);
        var own = Stats(bee, Context(("weapon_slot", "sidearm"), ("busy_little_bee_stacks", "5"))).Of("weapon_damage_multiplier");
        Assert.True(own == Stats(bee, Division.Weapon("sidearm")).Of("weapon_damage_multiplier"), "Busy Little Bee's stacks don't apply to itself");
    }

    /// <summary>G46: Overclock and its perfect version add reload speed only while active, with provenance, and follow the talent's selection.</summary>
    [Fact]
    public void G46OverclockReloadSpeed()
    {
        foreach (var (id, reload) in new[] { ("gear_talent_overclock", .25), ("gear_talent_perfect_overclock", .30) })
        {
            var b = GearBuild(id);
            foreach (var context in new[] { Context(), Context(("overclock_active", "false")), Context(("overclock_active", "invalid")) })
            {
                var inactive = Resolve(b, context);
                CloseTo(inactive.Calculation.Stats.Of("reload_speed"), 0, 1e-12);
                Assert.False(ContributionValue(inactive, "reload_speed", id).Found, $"inactive {id} still has reload-speed provenance");
            }
            foreach (var slot in new[] { "primary_weapon", "secondary_weapon" })
            {
                var active = Resolve(b, Context(("weapon_slot", slot), ("overclock_active", "true")));
                CloseTo(active.Calculation.Stats.Of("reload_speed"), reload, 1e-12);
                HasContribution(active, "reload_speed", id, reload);
                Assert.True(active.Calculation.Stats.Of("equipped_weapon_damage") > 0, $"{slot} lost weapon preview");
            }
            var cleared = ClearTalent(b, "backpack", id);
            CloseTo(Stats(cleared, Context(("overclock_active", "true"))).Of("reload_speed"), 0, 1e-12);
            var reselected = SelectTalent(cleared, "backpack", id);
            CloseTo(Stats(reselected, Context(("overclock_active", "true"))).Of("reload_speed"), reload, 1e-12);
        }
    }

    /// <summary>G46: Versatile and its perfect version multiply weapon damage by the class of the weapon in hand, close or mid range, only while active.</summary>
    [Fact]
    public void G46VersatileSelectedWeaponClassAndDistance()
    {
        var classes = new[]
        {
            ("weapon_type_shotgun", false), ("weapon_type_smg", false), ("weapon_type_rifle", false),
            ("weapon_type_marksman_rifle", false), ("weapon_type_assault_rifle", true), ("weapon_type_lmg", true),
        };
        foreach (var (id, close, mid) in new[] { ("gear_talent_versatile", .35, .10), ("gear_talent_perfect_versatile", .45, .20) })
        {
            var b = GearBuild(id);
            foreach (var (tag, isMid) in classes)
            {
                var classBuild = b.WithEquipment("primary_weapon", WeaponWithTag(tag, "primary_weapon").ID);
                var want = isMid ? mid : close;
                CloseTo(Stats(classBuild, Division.Weapon("primary_weapon")).Of("weapon_damage_multiplier"), 1, 1e-12);
                foreach (var flag in new[] { "false", "invalid" })
                {
                    CloseTo(Stats(classBuild, Context(("weapon_slot", "primary_weapon"), ("versatile_active", flag))).Of("weapon_damage_multiplier"), 1, 1e-12);
                }
                var active = Resolve(classBuild, Context(("weapon_slot", "primary_weapon"), ("versatile_active", "true")));
                var inactive = Stats(classBuild, Division.Weapon("primary_weapon"));
                CloseTo(active.Calculation.Stats.Of("weapon_damage_multiplier"), 1 + want, 1e-12);
                HasContribution(active, "weapon_damage_multiplier", id, want);
                Assert.True(Math.Abs(active.Calculation.Stats.Of("equipped_weapon_damage") - (inactive.Of("equipped_weapon_damage") * (1 + want))) <= 1e-8,
                    $"{id} preview did not include selected class bonus for {tag}: got {active.Calculation.Stats.Of("equipped_weapon_damage")} want {inactive.Of("equipped_weapon_damage") * (1 + want)}");
                var cleared = ClearTalent(classBuild, "backpack", id);
                CloseTo(Stats(cleared, Context(("weapon_slot", "primary_weapon"), ("versatile_active", "true"))).Of("weapon_damage_multiplier"), 1, 1e-12);
                var reselected = SelectTalent(cleared, "backpack", id);
                CloseTo(Stats(reselected, Context(("weapon_slot", "primary_weapon"), ("versatile_active", "true"))).Of("weapon_damage_multiplier"), 1 + want, 1e-12);
            }

            var pistolBuild = b.WithEquipment("sidearm", WeaponWithTag("weapon_type_pistol", "sidearm").ID);
            CloseTo(Stats(pistolBuild, Context(("weapon_slot", "sidearm"), ("versatile_active", "true"))).Of("weapon_damage_multiplier"), 1, 1e-12);
            CloseTo(Stats(b, Context(("versatile_active", "true"))).Of("weapon_damage_multiplier"), 1, 1e-12);

            var mixed = b.WithEquipment("primary_weapon", WeaponWithTag("weapon_type_assault_rifle", "primary_weapon").ID)
                .WithEquipment("secondary_weapon", WeaponWithTag("weapon_type_shotgun", "secondary_weapon").ID);
            CloseTo(Stats(mixed, Context(("weapon_slot", "primary_weapon"), ("versatile_active", "true"))).Of("weapon_damage_multiplier"), 1 + mid, 1e-12);
            CloseTo(Stats(mixed, Context(("weapon_slot", "secondary_weapon"), ("versatile_active", "true"))).Of("weapon_damage_multiplier"), 1 + close, 1e-12);
        }
    }

    /// <summary>G46: Back and Forth and its perfect version add weapon damage and rate of fire to the weapon swapped to, only while active and in hand.</summary>
    [Fact]
    public void G46BackAndForthSwapToOnly()
    {
        var cases = new (string Id, string[] Slots, string[] Hosts, double Damage, double Rof)[]
        {
            ("weapon_talent_back_and_forth", ["primary_weapon", "secondary_weapon", "sidearm"], ["", "", ""], .09, .10),
            ("weapon_talent_perfect_back_and_forth", ["primary_weapon", "secondary_weapon", "primary_weapon", "secondary_weapon"],
                ["primary_weapon_interchange", "secondary_weapon_interchange", "primary_weapon_trader", "secondary_weapon_trader"], .12, .13),
        };
        foreach (var (id, slots, hosts, damage, rof) in cases)
        {
            for (var i = 0; i < slots.Length; i++)
            {
                var slot = slots[i];
                var b = WeaponTalentBuild(id, slot, hosts[i]);
                var inactive = Stats(b, Division.Weapon(slot));
                CloseTo(inactive.Of("weapon_damage"), 0, 1e-12);
                CloseTo(inactive.Of("rate_of_fire"), 0, 1e-12);
                foreach (var flag in new[] { "false", "invalid" })
                {
                    var gated = Stats(b, Context(("weapon_slot", slot), ("back_and_forth_swap_to_active", flag)));
                    CloseTo(gated.Of("weapon_damage"), 0, 1e-12);
                    CloseTo(gated.Of("rate_of_fire"), 0, 1e-12);
                }
                var active = Resolve(b, Context(("weapon_slot", slot), ("back_and_forth_swap_to_active", "true")));
                CloseTo(active.Calculation.Stats.Of("weapon_damage"), damage, 1e-12);
                CloseTo(active.Calculation.Stats.Of("rate_of_fire"), rof, 1e-12);
                HasContribution(active, "weapon_damage", id, damage);
                HasContribution(active, "rate_of_fire", id, rof);
                Assert.True(active.Calculation.Stats.Of("equipped_weapon_damage") > inactive.Of("equipped_weapon_damage"), $"{id} did not increase equipped preview in {slot}");
                if (slot != "sidearm")
                {
                    var other = slot == "primary_weapon" ? "secondary_weapon" : "primary_weapon";
                    var wrong = Stats(b, Context(("weapon_slot", other), ("back_and_forth_swap_to_active", "true")));
                    CloseTo(wrong.Of("weapon_damage"), 0, 1e-12);
                    CloseTo(wrong.Of("rate_of_fire"), 0, 1e-12);
                }
                var cleared = ClearTalent(b, slot, id);
                CloseTo(Stats(cleared, Context(("weapon_slot", slot), ("back_and_forth_swap_to_active", "true"))).Of("weapon_damage"), 0, 1e-12);
            }
        }
    }

    /// <summary>G46: Back and Forth on both weapons doesn't stack.</summary>
    [Fact]
    public void G46BackAndForthDuplicateInstancesDoNotStack()
    {
        const string talent = "weapon_talent_back_and_forth";
        var primary = WeaponTalentBuild(talent, "primary_weapon", "");
        var secondaryHost = Compiled.Pack.Items.FirstOrDefault(item => item.Slot == "secondary_weapon" && item.TalentSlots.Any(slot => slot.Choices.Contains(talent)));
        Assert.True(secondaryHost is not null, "no secondary weapon allows Back and Forth");
        var both = SelectTalent(primary.WithEquipment("secondary_weapon", secondaryHost.ID), "secondary_weapon", talent);
        foreach (var selected in new[] { "primary_weapon", "secondary_weapon" })
        {
            var active = Resolve(both, Context(("weapon_slot", selected), ("back_and_forth_swap_to_active", "true")));
            CloseTo(active.Calculation.Stats.Of("weapon_damage"), .09, 1e-12);
            CloseTo(active.Calculation.Stats.Of("rate_of_fire"), .10, 1e-12);
            HasContribution(active, "weapon_damage", talent, .09);
        }
    }

    /// <summary>G46: combat assumptions aren't part of a build, so they don't survive sharing it.</summary>
    [Fact]
    public void G46AssumptionsAreTransientThroughBuildSharing()
    {
        var b = GearBuild("gear_talent_perfect_versatile");
        var active = Stats(b, Context(("weapon_slot", "primary_weapon"), ("versatile_active", "true")));
        CloseTo(active.Of("weapon_damage_multiplier"), 1.2, 1e-12);
        var decoded = ShareCode.Decode(ShareCode.Encode(b));
        var serialized = Encoding.UTF8.GetString(BuildDocument.Encode(decoded));
        Assert.True(!serialized.Contains("versatile_active", StringComparison.Ordinal) && !serialized.Contains("overclock_active", StringComparison.Ordinal)
            && !serialized.Contains("back_and_forth_swap_to_active", StringComparison.Ordinal), $"combat assumptions leaked into decoded build: {serialized}");
        CloseTo(Stats(decoded, Division.Weapon("primary_weapon")).Of("weapon_damage_multiplier"), 1, 1e-12);
        Assert.True(active.Of("equipped_weapon_damage") > 0, "active build has no weapon preview");
    }
}
