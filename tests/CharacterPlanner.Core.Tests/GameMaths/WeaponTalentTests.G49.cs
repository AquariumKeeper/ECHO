using System.Text;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>Stacking and asserted named weapon talents on fixed hosts, ported from Go's weapon_talents_g49_test.go.</summary>
public sealed partial class WeaponTalentTests
{
    /// <summary>Go's g49HostContracts: each talent's exact hosts and legal slots.</summary>
    private static readonly Dictionary<string, (string[] Ids, string[] Slots)> G49HostContracts = new(StringComparer.Ordinal)
    {
        ["Full Stop"] = (["primary_weapon_dread_edict", "secondary_weapon_dread_edict"], ["primary_weapon", "secondary_weapon"]),
        ["Payment in Kind"] = (["primary_weapon_backfire", "secondary_weapon_backfire"], ["primary_weapon", "secondary_weapon"]),
        ["Big Game Hunter"] = (["primary_weapon_the_bighorn", "secondary_weapon_the_bighorn"], ["primary_weapon", "secondary_weapon"]),
        ["Cover Shooter"] = (["primary_weapon_big_alejandro", "secondary_weapon_big_alejandro"], ["primary_weapon", "secondary_weapon"]),
        ["Gangland Hit"] = (["primary_weapon_underboss", "secondary_weapon_underboss"], ["primary_weapon", "secondary_weapon"]),
        ["Faster Than Reloading"] = (["sidearm_whiplash"], ["sidearm"]),
        ["Perturb"] = (["primary_weapon_agitator", "secondary_weapon_agitator"], ["primary_weapon", "secondary_weapon"]),
    };

    /// <summary>Go's g49Build: the talent on its fixed host in the slot.</summary>
    private static BuildSnapshot G49Build(string name, string slot)
    {
        Assert.True(G49HostContracts.ContainsKey(name), $"missing fixed host contract for {name}");
        return WeaponBuild(TalentId(name), slot, G49HostContracts[name].Ids);
    }

    /// <summary>Go's g49WithSelectedTypeCore: the weapon's own type-damage core at its maximum, and that stat.</summary>
    private static (BuildSnapshot Build, string TypeStat) WithSelectedTypeCore(BuildSnapshot b, string slot)
    {
        var itemId = b.Equipment.GetValueOrDefault(slot, "");
        var item = Compiled.Pack.Items.FirstOrDefault(item => item.ID == itemId);
        var typeTag = item?.Tags.FirstOrDefault(tag => tag.StartsWith("weapon_type_", StringComparison.Ordinal));
        Assert.True(item is not null && typeTag is not null, $"no weapon type tag for {slot} item {itemId}");
        var typeStat = typeTag["weapon_type_".Length..] + "_damage";
        var attributeId = typeStat + "_weapon_core";
        var attribute = Compiled.Pack.Attributes.FirstOrDefault(attribute => attribute.ID == attributeId);
        Assert.True(attribute is not null, $"missing selected type core {attributeId}");
        var attributeSlot = item.AttributeSlots.FirstOrDefault(attributeSlot => attributeSlot.Choices.Any(choice => choice.AttributeID == attributeId));
        Assert.True(attributeSlot is not null, $"item {itemId} does not allow selected type core {attributeId}");
        return (b.WithAttributeRoll(slot, attributeSlot.ID, new(attributeId, attribute.Max)), typeStat);
    }

    /// <summary>Go's g49ExpectWeaponDamagePreview: the weapon damage bonus joins the weapon and type damage pool.</summary>
    private static void ExpectWeaponDamagePreview(ResolvedBuild baseline, ResolvedBuild active, double bonus, string typeStat, string what)
    {
        var stats = baseline.Calculation.Stats;
        var pool = stats.Of("weapon_damage") + stats.Of(typeStat);
        Assert.True(stats.Of(typeStat) > 0, $"{what}: selected weapon type damage = {stats.Of(typeStat)}, want configured type core");
        Assert.True(stats.Of("equipped_weapon_damage") > 0, $"{what}: baseline weapon preview = {stats.Of("equipped_weapon_damage")}, want positive");
        Near(active.Calculation.Stats.Of("equipped_weapon_damage"), stats.Of("equipped_weapon_damage") * (1 + pool + bonus) / (1 + pool), 1e-8, $"{what}: weapon damage preview");
    }

    private static void G49ExpectOwnerSlotEffects(string name, string stat, (string Key, string Value)[] context, double expected) =>
        ExpectOwnerSlotEffect(TalentId(name), G49HostContracts[name].Slots, slot => G49Build(name, slot), stat, context, expected, multiplierPreview: false);

    /// <summary>G49: a stacking talent's per-stack bonus and provenance, and its invalid stack counts.</summary>
    private static void G49Stacks(string name, string key, int[] counts, string[] invalid, (string Stat, double PerStack)[] bonuses, bool typeCore, Action<ResolvedBuild, ResolvedBuild, double, string, string> preview)
    {
        var talentId = TalentId(name);
        foreach (var slot in G49HostContracts[name].Slots)
        {
            var (b, typeStat) = typeCore ? WithSelectedTypeCore(G49Build(name, slot), slot) : (G49Build(name, slot), "");
            var baseline = Resolve(b, On(slot));
            foreach (var stacks in counts)
            {
                var what = $"{name} on {slot} with {stacks} stacks";
                var active = Resolve(b, On(slot, (key, Count(stacks))));
                foreach (var (stat, perStack) in bonuses)
                {
                    Near(Delta(active, baseline, stat), perStack * stacks, 1e-12, $"{what}: {stat} delta");
                    HasContribution(active, stat, talentId, perStack * stacks, what);
                }
                preview(baseline, active, bonuses[0].PerStack * stacks, typeStat, what);
            }
            foreach (var value in invalid)
            {
                var what = $"{name} on {slot} with {key}={value}";
                var result = Resolve(b, On(slot, (key, value)));
                ExpectInvalidContext(result, key, what);
                Near(Delta(result, baseline, bonuses[0].Stat), 0, 1e-12, what);
            }
        }
    }

    private static void PreviewUnchanged(ResolvedBuild baseline, ResolvedBuild active, double bonus, string typeStat, string what) =>
        Near(active.Calculation.Stats.Of("equipped_weapon_damage"), baseline.Calculation.Stats.Of("equipped_weapon_damage"), 1e-12, $"{what}: weapon damage preview");

    /// <summary>G49: each talent is legal on exactly its fixed hosts and slots.</summary>
    [Fact]
    public void G49FixedHostContracts()
    {
        foreach (var (name, (ids, slots)) in G49HostContracts)
        {
            var talentId = TalentId(name);
            Assert.True(SameStrings(Hosts(talentId), ids), $"{name} hosts = [{string.Join(", ", Hosts(talentId))}], want exactly [{string.Join(", ", ids)}]");
            Assert.True(SameStrings(LegalSlots(talentId), slots), $"{name} legal slots = [{string.Join(", ", LegalSlots(talentId))}], want exactly [{string.Join(", ", slots)}]");
        }
    }

    /// <summary>G49: Full Stop adds 2% weapon damage and 5% headshot damage per stack, up to 20.</summary>
    [Fact]
    public void G49FullStopStacks()
    {
        G49Stacks("Full Stop", "full_stop_stacks", [0, 1, 10, 20], ["-1", "1.5", "21", "NaN"], [("weapon_damage", .02), ("headshot_damage", .05)], typeCore: true, ExpectWeaponDamagePreview);
        G49ExpectOwnerSlotEffects("Full Stop", "weapon_damage", [("full_stop_stacks", "20")], .40);
        G49ExpectOwnerSlotEffects("Full Stop", "headshot_damage", [("full_stop_stacks", "20")], 1.00);
    }

    /// <summary>G49: Payment in Kind adds 2% critical hit damage per stack, up to 100, without changing the weapon damage preview.</summary>
    [Fact]
    public void G49PaymentInKindStacks()
    {
        G49Stacks("Payment in Kind", "payment_in_kind_stacks", [0, 1, 50, 100], ["-1", "0.5", "101", "NaN"], [("critical_hit_damage", .02)], typeCore: true, PreviewUnchanged);
        G49ExpectOwnerSlotEffects("Payment in Kind", "critical_hit_damage", [("payment_in_kind_stacks", "100")], 2);
    }

    /// <summary>G49: Big Game Hunter adds 6% headshot damage per stack, up to 25, without changing the weapon damage preview.</summary>
    [Fact]
    public void G49BigGameHunterStacks()
    {
        G49Stacks("Big Game Hunter", "big_game_hunter_stacks", [0, 1, 12, 25], ["-1", "1.5", "26", "NaN"], [("headshot_damage", .06)], typeCore: true, PreviewUnchanged);
        G49ExpectOwnerSlotEffects("Big Game Hunter", "headshot_damage", [("big_game_hunter_stacks", "25")], 1.5);
    }

    /// <summary>G49: Cover Shooter adds 1% weapon damage per stack, up to 100.</summary>
    [Fact]
    public void G49CoverShooterStacks()
    {
        G49Stacks("Cover Shooter", "cover_shooter_stacks", [0, 1, 50, 100], ["-1", "1.5", "101", "NaN"], [("weapon_damage", .01)], typeCore: true, ExpectWeaponDamagePreview);
        G49ExpectOwnerSlotEffects("Cover Shooter", "weapon_damage", [("cover_shooter_stacks", "100")], 1);
    }

    /// <summary>
    /// G49: Gangland Hit adds 20% weapon damage while active plus 5% per other mark, up to 3; a missing or invalid
    /// mark count keeps the base 20%.
    /// </summary>
    [Fact]
    public void G49GanglandHitMarksAndActiveFlag()
    {
        const string name = "Gangland Hit";
        var talentId = TalentId(name);
        foreach (var slot in G49HostContracts[name].Slots)
        {
            var b = G49Build(name, slot);
            var baseline = Resolve(b, On(slot));
            foreach (var marks in new[] { 0, 1, 2, 3 })
            {
                var what = $"{name} on {slot} with {marks} other marks";
                var bonus = .20 + (.05 * marks);
                var active = Resolve(b, On(slot, ("gangland_hit_active", "true"), ("gangland_hit_other_marks", Count(marks))));
                Near(Delta(active, baseline, "weapon_damage_multiplier"), bonus, 1e-12, what);
                HasContribution(active, "weapon_damage_multiplier", talentId, bonus, what);
                Near(active.Calculation.Stats.Of("equipped_weapon_damage"), baseline.Calculation.Stats.Of("equipped_weapon_damage") * (1 + bonus), 1e-8, $"{what}: weapon damage preview");
            }
            ExpectInactiveFlag(b, slot, "gangland_hit_active", "weapon_damage_multiplier", baseline, $"{name} on {slot}", ("gangland_hit_other_marks", "3"));
            var missingMarks = Resolve(b, On(slot, ("gangland_hit_active", "true")));
            Near(Delta(missingMarks, baseline, "weapon_damage_multiplier"), .20, 1e-12, $"{name} on {slot} without marks");
            foreach (var invalid in new[] { "-1", "1.5", "4", "NaN" })
            {
                var what = $"{name} on {slot} with marks {invalid}";
                var result = Resolve(b, On(slot, ("gangland_hit_active", "true"), ("gangland_hit_other_marks", invalid)));
                ExpectInvalidContext(result, "gangland_hit_other_marks", what);
                Near(Delta(result, baseline, "weapon_damage_multiplier"), .20, 1e-12, what);
            }
        }
        G49ExpectOwnerSlotEffects(name, "weapon_damage_multiplier", [("gangland_hit_active", "true"), ("gangland_hit_other_marks", "3")], .35);
    }

    /// <summary>G49: Gangland Hit's multiplier compounds with Glass Cannon's.</summary>
    [Fact]
    public void G49GanglandHitCompoundsWithGlassCannon()
    {
        var ganglandId = TalentId("Gangland Hit");
        var b = WithGearTalent(G49Build("Gangland Hit", "primary_weapon"), TalentId("Glass Cannon"));
        var baseline = Resolve(b, On("primary_weapon"));
        Near(baseline.Calculation.Stats.Of("weapon_damage_multiplier"), 1.25, 1e-12, "Glass Cannon multiplier");
        var active = Resolve(b, On("primary_weapon", ("gangland_hit_active", "true"), ("gangland_hit_other_marks", "3")));
        Near(active.Calculation.Stats.Of("weapon_damage_multiplier"), 1.25 * 1.35, 1e-12, "composed multiplier");
        HasContribution(active, "weapon_damage_multiplier", ganglandId, .35, "Gangland Hit in multiplier composition");
        Near(active.Calculation.Stats.Of("equipped_weapon_damage"), baseline.Calculation.Stats.Of("equipped_weapon_damage") * 1.35, 1e-8, "weapon damage preview");
    }

    /// <summary>G49: an asserted talent adding weapon damage and rate of fire, leaving the weapon's base RPM alone.</summary>
    private static void G49Buff(string name, string key, double damage, double rate)
    {
        var talentId = TalentId(name);
        foreach (var slot in G49HostContracts[name].Slots)
        {
            var (b, typeStat) = WithSelectedTypeCore(G49Build(name, slot), slot);
            var baseline = Resolve(b, On(slot));
            foreach (var flag in new[] { "", "false", "invalid" })
            {
                var inactive = Resolve(b, flag.Length > 0 ? On(slot, (key, flag)) : On(slot));
                Near(Delta(inactive, baseline, "weapon_damage"), 0, 1e-12, $"{name} on {slot} with {key}=\"{flag}\": weapon_damage");
                Near(Delta(inactive, baseline, "rate_of_fire"), 0, 1e-12, $"{name} on {slot} with {key}=\"{flag}\": rate_of_fire");
            }
            var active = Resolve(b, On(slot, (key, "true")));
            var what = $"{name} on {slot}";
            Near(Delta(active, baseline, "weapon_damage"), damage, 1e-12, $"{what}: weapon_damage");
            Near(Delta(active, baseline, "rate_of_fire"), rate, 1e-12, $"{what}: rate_of_fire");
            HasContribution(active, "weapon_damage", talentId, damage, what);
            HasContribution(active, "rate_of_fire", talentId, rate, what);
            ExpectWeaponDamagePreview(baseline, active, damage, typeStat, what);
            Near(active.Calculation.Stats.Of("weapon_rpm"), baseline.Calculation.Stats.Of("weapon_rpm"), 1e-12, $"{what}: weapon_rpm");
        }
        G49ExpectOwnerSlotEffects(name, "weapon_damage", [(key, "true")], damage);
        G49ExpectOwnerSlotEffects(name, "rate_of_fire", [(key, "true")], rate);
    }

    /// <summary>G49: Faster Than Reloading adds 50% weapon damage and 20% rate of fire to the Whiplash sidearm while active.</summary>
    [Fact]
    public void G49FasterThanReloadingBuff() => G49Buff("Faster Than Reloading", "faster_than_reloading_active", .50, .20);

    /// <summary>G49: Perturb adds 30% weapon damage and 25% rate of fire while active.</summary>
    [Fact]
    public void G49PerturbBuff() => G49Buff("Perturb", "perturb_active", .30, .25);

    /// <summary>G49: the stack inputs run from 0 to their caps in steps of 1, defaulting to 0.</summary>
    [Fact]
    public void G49NumericContextDefinitions()
    {
        foreach (var (id, max) in new[] { ("full_stop_stacks", 20.0), ("payment_in_kind_stacks", 100), ("big_game_hunter_stacks", 25), ("cover_shooter_stacks", 100), ("gangland_hit_other_marks", 3) })
        {
            var input = Compiled.Pack.NumericContextInputs.FirstOrDefault(input => input.ID == id);
            Assert.True(input is not null, $"missing numeric context input {id}");
            Assert.True(input.Min == 0 && input.Max == max && input.Step == 1 && input.Default == 0,
                $"{id} definition = {input.Min}..{input.Max} step {input.Step} default {input.Default}, want 0..{max} step 1 default 0");
        }
    }

    /// <summary>G49: combat assumptions aren't part of a build, so they don't survive sharing it.</summary>
    [Fact]
    public void G49ContextAssumptionsStayTransient()
    {
        (string Name, string Slot, string Stat, string Key, string Value, double Bonus)[] cases =
        [
            ("Full Stop", "primary_weapon", "weapon_damage", "full_stop_stacks", "20", .40),
            ("Payment in Kind", "primary_weapon", "critical_hit_damage", "payment_in_kind_stacks", "100", 2),
            ("Big Game Hunter", "primary_weapon", "headshot_damage", "big_game_hunter_stacks", "25", 1.5),
            ("Cover Shooter", "primary_weapon", "weapon_damage", "cover_shooter_stacks", "100", 1),
            ("Gangland Hit", "primary_weapon", "weapon_damage_multiplier", "gangland_hit_other_marks", "3", .35),
            ("Faster Than Reloading", "sidearm", "weapon_damage", "faster_than_reloading_active", "true", .50),
            ("Perturb", "primary_weapon", "weapon_damage", "perturb_active", "true", .30),
        ];
        string[] keys = ["full_stop_stacks", "payment_in_kind_stacks", "big_game_hunter_stacks", "cover_shooter_stacks",
            "gangland_hit_active", "gangland_hit_other_marks", "faster_than_reloading_active", "perturb_active"];
        foreach (var (name, slot, stat, key, value, bonus) in cases)
        {
            var b = G49Build(name, slot);
            var active = Resolve(b, name == "Gangland Hit" ? On(slot, (key, value), ("gangland_hit_active", "true")) : On(slot, (key, value)));
            var decoded = ShareCode.Decode(ShareCode.Encode(b));
            var serialized = Encoding.UTF8.GetString(BuildDocument.Encode(decoded));
            foreach (var leaked in keys)
            {
                Assert.False(serialized.Contains(leaked, StringComparison.Ordinal), $"{name}: context assumption {leaked} leaked into decoded build: {serialized}");
            }
            var restored = Resolve(decoded, On(slot));
            Near(Delta(active, restored, stat), bonus, 1e-12, $"{name}: {stat} over the restored build");
        }
    }
}
