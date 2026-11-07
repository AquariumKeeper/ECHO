using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// Weapon talents on configured equipment (F10) with their conditional, stacking and static modifiers and per-stat
/// provenance (F05): host contracts, owner-weapon scoping, context-input bounds and transient assumptions. Ported
/// from Go's weapon_talents_g48_test.go (this file), weapon_talents_g49_test.go, weapon_talents_g50_test.go and
/// weapon_talents_g51_test.go (WeaponTalentTests.G49.cs, .G50.cs, .G51.cs). Go's per-file helpers (g48TalentID,
/// g49LegalSlots, g50Build, g51Contribution, ...) are the same functions under different prefixes, so they are
/// shared here once.
/// </summary>
public sealed partial class WeaponTalentTests
{
    private static readonly string[] WeaponSlots = ["primary_weapon", "secondary_weapon", "sidearm"];

    /// <summary>Go's g48TalentID: the talent with this display name.</summary>
    private static string TalentId(string name)
    {
        var talent = Compiled.Pack.Talents.FirstOrDefault(talent => talent.Name == name);
        Assert.True(talent is not null, $"missing modeled talent \"{name}\"");
        return talent.ID;
    }

    private static bool Allows(ItemDefinition item, string talentId) => item.TalentSlots.Any(talentSlot => talentSlot.Choices.Contains(talentId));

    /// <summary>Go's g48LegalSlots: the weapon slots, in order, that some item allowing the talent occupies.</summary>
    private static string[] LegalSlots(string talentId) =>
        WeaponSlots.Where(slot => Compiled.Pack.Items.Any(item => item.Slot == slot && Allows(item, talentId))).ToArray();

    /// <summary>The items that allow the talent, in pack order.</summary>
    private static string[] Hosts(string talentId) => Compiled.Pack.Items.Where(item => Allows(item, talentId)).Select(item => item.ID).ToArray();

    private static bool SameStrings(IEnumerable<string> a, IEnumerable<string> b) =>
        a.Order(StringComparer.Ordinal).SequenceEqual(b.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>
    /// Go's g48WeaponBuild, g49Build, g50Build and g51Build: the first item of each weapon slot equipped, then the
    /// slot's host (the first in pack order, or the first of the fixed hosts) with the talent selected.
    /// </summary>
    private static BuildSnapshot WeaponBuild(string talentId, string slot, IEnumerable<string>? hosts = null)
    {
        var b = Build();
        foreach (var weaponSlot in WeaponSlots)
        {
            var first = Compiled.Pack.Items.FirstOrDefault(item => item.Slot == weaponSlot);
            if (first is not null) { b = b.WithEquipment(weaponSlot, first.ID); }
        }
        var candidates = hosts is null
            ? Compiled.Pack.Items.Where(item => item.Slot == slot)
            : hosts.SelectMany(id => Compiled.Pack.Items.Where(item => item.ID == id && item.Slot == slot));
        foreach (var item in candidates)
        {
            var talentSlot = item.TalentSlots.FirstOrDefault(talentSlot => talentSlot.Choices.Contains(talentId));
            if (talentSlot is not null) { return b.WithEquipment(slot, item.ID).WithTalentSelection(slot, talentSlot.ID, talentId); }
        }
        Assert.Fail($"no {slot} host allows {talentId}");
        return b;
    }

    /// <summary>Go's g48SetTalent and g50WithoutTalent: sets the selection of the talent slot offering the talent on the equipped item.</summary>
    private static BuildSnapshot SetTalent(BuildSnapshot b, string slot, string talentId, string selected)
    {
        var itemId = b.Equipment.GetValueOrDefault(slot, "");
        var talentSlot = Compiled.Pack.Items.Where(item => item.ID == itemId).SelectMany(item => item.TalentSlots).FirstOrDefault(talentSlot => talentSlot.Choices.Contains(talentId));
        Assert.True(talentSlot is not null, $"{talentId} has no selection slot on {slot}");
        return b.WithTalentSelection(slot, talentSlot.ID, selected);
    }

    /// <summary>Go's g49WithGearTalent: the first non-weapon item that allows the talent, equipped with it selected.</summary>
    private static BuildSnapshot WithGearTalent(BuildSnapshot b, string talentId)
    {
        foreach (var item in Compiled.Pack.Items.Where(item => !WeaponSlots.Contains(item.Slot)))
        {
            var talentSlot = item.TalentSlots.FirstOrDefault(talentSlot => talentSlot.Choices.Contains(talentId));
            if (talentSlot is not null) { return b.WithEquipment(item.Slot, item.ID).WithTalentSelection(item.Slot, talentSlot.ID, talentId); }
        }
        Assert.Fail($"no gear host allows {talentId}");
        return b;
    }

    /// <summary>The active weapon plus further assumptions.</summary>
    private static ImmutableDictionary<string, string> On(string slot, params (string Key, string Value)[] values) =>
        Context([(GamePack.WeaponSlotSelector, slot), .. values]);

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Go's g48Delta.</summary>
    private static double Delta(ResolvedBuild result, ResolvedBuild baseline, string stat) => result.Calculation.Stats.Of(stat) - baseline.Calculation.Stats.Of(stat);

    /// <summary>Go's g48Contribution: the talent's summed contributions to a stat, and whether it has any.</summary>
    private static (double Value, bool Found) ContributionValue(ResolvedBuild result, string stat, string talentId)
    {
        if (!result.Calculation.Breakdowns.TryGetValue(stat, out var aggregated)) { return (0, false); }
        var contributions = aggregated.Contributions.Where(contribution => contribution.Source.Kind == "talent" && contribution.Source.Id == talentId).ToArray();
        return (contributions.Sum(contribution => contribution.Value), contributions.Length > 0);
    }

    private static void HasContribution(ResolvedBuild result, string stat, string talentId, double want, string what)
    {
        var (value, found) = ContributionValue(result, stat, talentId);
        Assert.True(found, $"{what}: missing {talentId} provenance for {stat}");
        Near(value, want, 1e-12, $"{what}: {talentId} contribution to {stat}");
    }

    /// <summary>Go's closeTo with a description of the case.</summary>
    private static void Near(double got, double want, double tolerance, string what)
    {
        if (Math.Abs(got - want) > tolerance) { Assert.Fail($"{what}: got {got}, want {want} (within {tolerance})"); }
    }

    /// <summary>Go's g48ExpectInvalidContext.</summary>
    private static void ExpectInvalidContext(ResolvedBuild result, string key, string what) =>
        Assert.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == "context-value-invalid" && diagnostic.Subject == key),
            $"{what}: missing context-value-invalid diagnostic for {key}: {string.Join("; ", result.Diagnostics.Select(diagnostic => $"{diagnostic.Code} {diagnostic.Subject}"))}");

    private static string WrongSlot(string slot) => WeaponSlots[0] == slot ? WeaponSlots[1] : WeaponSlots[0];

    /// <summary>
    /// Go's g48ExpectSelectedWeaponEffect and g49ExpectOwnerSlotEffects: on each slot the talent's assumption raises the
    /// stat by the expected amount with talent provenance while that weapon is in hand, and not while another is.
    /// </summary>
    private static void ExpectOwnerSlotEffect(string talentId, IEnumerable<string> slots, Func<string, BuildSnapshot> build, string stat,
        (string Key, string Value)[] context, double expected, bool multiplierPreview)
    {
        var any = false;
        foreach (var slot in slots)
        {
            any = true;
            var what = $"{talentId} on {slot}";
            var b = build(slot);
            var baseline = Resolve(b, On(slot));
            var active = Resolve(b, On(slot, context));
            Near(Delta(active, baseline, stat), expected, 1e-12, $"{what}: {stat} delta");
            HasContribution(active, stat, talentId, expected, what);
            if (multiplierPreview && stat == "weapon_damage_multiplier")
            {
                var baseMultiplier = baseline.Calculation.Stats.Of(stat);
                Near(active.Calculation.Stats.Of("equipped_weapon_damage"), baseline.Calculation.Stats.Of("equipped_weapon_damage") * (baseMultiplier + expected) / baseMultiplier, 1e-8, $"{what}: weapon damage preview");
            }
            var wrongSlot = WrongSlot(slot);
            var wrongBaseline = Resolve(b, On(wrongSlot));
            var wrong = Resolve(b, On(wrongSlot, context));
            Near(Delta(wrong, wrongBaseline, stat), 0, 1e-12, $"{what}: {stat} delta with {wrongSlot} in hand");
        }
        Assert.True(any, $"{talentId} has no legal weapon slot");
    }

    /// <summary>Go's G48 flag check: the flag missing, false or invalid leaves the stat alone.</summary>
    private static void ExpectInactiveFlag(BuildSnapshot b, string slot, string key, string stat, ResolvedBuild baseline, string what, params (string Key, string Value)[] extra)
    {
        foreach (var flag in new[] { "", "false", "invalid" })
        {
            var context = flag.Length > 0 ? On(slot, [.. extra, (key, flag)]) : On(slot, extra);
            Near(Delta(Resolve(b, context), baseline, stat), 0, 1e-12, $"{what}: {stat} delta with {key}=\"{flag}\"");
        }
    }

    private static void G48ExpectSelectedWeaponEffect(string name, string stat, (string Key, string Value)[] context, double expected)
    {
        var talentId = TalentId(name);
        ExpectOwnerSlotEffect(talentId, LegalSlots(talentId), slot => WeaponBuild(talentId, slot), stat, context, expected, multiplierPreview: true);
    }

    /// <summary>
    /// G48: Precision Strike adds 20% weapon damage per stack up to 3 stacks and Perfect Precision Strike 35% up to
    /// 2, only while active, rejecting invalid stack counts.
    /// </summary>
    [Fact]
    public void G48PrecisionStrikeStacksAndPerfectCap()
    {
        foreach (var (name, perStack, cap) in new[] { ("Precision Strike", .20, 3), ("Perfect Precision Strike", .35, 2) })
        {
            var talentId = TalentId(name);
            foreach (var slot in LegalSlots(talentId))
            {
                var b = WeaponBuild(talentId, slot);
                var baseline = Resolve(b, On(slot));
                foreach (var stacks in new[] { 0, 1, 2, 3 })
                {
                    var what = $"{name} on {slot} with {stacks} stacks";
                    var bonus = perStack * Math.Min(stacks, cap);
                    var active = Resolve(b, On(slot, ("precision_strike_active", "true"), ("precision_strike_stacks", Count(stacks))));
                    Near(Delta(active, baseline, "weapon_damage_multiplier"), bonus, 1e-12, what);
                    HasContribution(active, "weapon_damage_multiplier", talentId, bonus, what);
                    Near(active.Calculation.Stats.Of("equipped_weapon_damage"), baseline.Calculation.Stats.Of("equipped_weapon_damage") * (1 + bonus), 1e-8, $"{what}: weapon damage preview");
                }
                ExpectInactiveFlag(b, slot, "precision_strike_active", "weapon_damage_multiplier", baseline, $"{name} on {slot}", ("precision_strike_stacks", "3"));
                foreach (var invalid in new[] { "-1", "1.5", "4", "NaN" })
                {
                    var what = $"{name} on {slot} with stacks {invalid}";
                    var result = Resolve(b, On(slot, ("precision_strike_active", "true"), ("precision_strike_stacks", invalid)));
                    ExpectInvalidContext(result, "precision_strike_stacks", what);
                    Near(Delta(result, baseline, "weapon_damage_multiplier"), 0, 1e-12, what);
                }
            }
        }
    }

    /// <summary>G48: Swift adds 20% weapon damage to the selected weapon only while asserted.</summary>
    [Fact]
    public void G48SwiftSelectedWeaponBuff()
    {
        var talentId = TalentId("Swift");
        foreach (var slot in LegalSlots(talentId))
        {
            var b = WeaponBuild(talentId, slot);
            var baseline = Resolve(b, On(slot));
            ExpectInactiveFlag(b, slot, "swift_active", "weapon_damage", baseline, $"Swift on {slot}");
            var active = Resolve(b, On(slot, ("swift_active", "true")));
            Near(Delta(active, baseline, "weapon_damage"), .20, 1e-12, $"Swift on {slot}");
            HasContribution(active, "weapon_damage", talentId, .20, $"Swift on {slot}");
            var weaponDamage = baseline.Calculation.Stats.Of("weapon_damage");
            Near(active.Calculation.Stats.Of("equipped_weapon_damage"), baseline.Calculation.Stats.Of("equipped_weapon_damage") * (1 + weaponDamage + .20) / (1 + weaponDamage), 1e-8, $"Swift on {slot}: weapon damage preview");
        }
    }

    /// <summary>G48: Foam at the Mouth adds 25% weapon damage against an ensnared target.</summary>
    [Fact]
    public void G48FoamAtTheMouthEnsnaredTarget()
    {
        G48ExpectSelectedWeaponEffect("Foam at the Mouth", "weapon_damage_multiplier", [("target_ensnared", "true")], .25);
        var talentId = TalentId("Foam at the Mouth");
        foreach (var slot in LegalSlots(talentId))
        {
            var b = WeaponBuild(talentId, slot);
            ExpectInactiveFlag(b, slot, "target_ensnared", "weapon_damage_multiplier", Resolve(b, On(slot)), $"Foam at the Mouth on {slot}");
        }
    }

    /// <summary>G48: High Priority Target adds 125% weapon damage while asserted.</summary>
    [Fact]
    public void G48HighPriorityTargetAssertedBuff()
    {
        G48ExpectSelectedWeaponEffect("High Priority Target", "weapon_damage_multiplier", [("high_priority_target_active", "true")], 1.25);
        var talentId = TalentId("High Priority Target");
        foreach (var slot in LegalSlots(talentId))
        {
            var b = WeaponBuild(talentId, slot);
            ExpectInactiveFlag(b, slot, "high_priority_target_active", "weapon_damage_multiplier", Resolve(b, On(slot)), $"High Priority Target on {slot}");
        }
    }

    /// <summary>G48: Prima Donna's "You can look...but you can't touch" adds 11% weapon damage per stack (Y8S3.2), up to 10.</summary>
    [Fact]
    public void G48PrimaDonnaStacks()
    {
        var talentId = TalentId("You can look...but you can't touch");
        var slots = LegalSlots(talentId);
        Assert.True(slots.Length > 0, "Prima Donna talent has no legal weapon host");
        foreach (var slot in slots)
        {
            var b = WeaponBuild(talentId, slot);
            var baseline = Resolve(b, On(slot));
            foreach (var stacks in new[] { 0, 1, 5, 10 })
            {
                var what = $"Prima Donna on {slot} with {stacks} stacks";
                var bonus = .11 * stacks; // Y8S3.2: 11% per stack
                var active = Resolve(b, On(slot, ("prima_donna_stacks", Count(stacks))));
                Near(Delta(active, baseline, "weapon_damage_multiplier"), bonus, 1e-12, what);
                HasContribution(active, "weapon_damage_multiplier", talentId, bonus, what);
                Near(active.Calculation.Stats.Of("equipped_weapon_damage"), baseline.Calculation.Stats.Of("equipped_weapon_damage") * (1 + bonus), 1e-8, $"{what}: weapon damage preview");
            }
            foreach (var invalid in new[] { "-1", "1.5", "11", "NaN" })
            {
                var what = $"Prima Donna on {slot} with stacks {invalid}";
                var result = Resolve(b, On(slot, ("prima_donna_stacks", invalid)));
                ExpectInvalidContext(result, "prima_donna_stacks", what);
                Near(Delta(result, baseline, "weapon_damage_multiplier"), 0, 1e-12, what);
            }
        }
    }

    /// <summary>G48: Agonizing Bite adds 20% weapon damage to the marked shot.</summary>
    [Fact]
    public void G48AgonizingBiteMarkedShot()
    {
        G48ExpectSelectedWeaponEffect("Agonizing Bite", "weapon_damage_multiplier", [("agonizing_bite_marked_shot", "true")], .20);
        var talentId = TalentId("Agonizing Bite");
        foreach (var slot in LegalSlots(talentId))
        {
            var b = WeaponBuild(talentId, slot);
            ExpectInactiveFlag(b, slot, "agonizing_bite_marked_shot", "weapon_damage_multiplier", Resolve(b, On(slot)), $"Agonizing Bite on {slot}");
        }
    }

    /// <summary>G48: Unnerve adds 15% weapon damage per retained mark, up to 5.</summary>
    [Fact]
    public void G48UnnerveRetainedMarks()
    {
        var talentId = TalentId("Unnerve");
        var slots = LegalSlots(talentId);
        Assert.True(slots.Length > 0, "Unnerve has no legal weapon host");
        foreach (var slot in slots)
        {
            var b = WeaponBuild(talentId, slot);
            var baseline = Resolve(b, On(slot));
            foreach (var marks in new[] { 0, 1, 3, 5 })
            {
                var what = $"Unnerve on {slot} with {marks} marks";
                var bonus = .15 * marks;
                var active = Resolve(b, On(slot, ("unnerve_marks", Count(marks))));
                Near(Delta(active, baseline, "weapon_damage_multiplier"), bonus, 1e-12, what);
                HasContribution(active, "weapon_damage_multiplier", talentId, bonus, what);
                Near(active.Calculation.Stats.Of("equipped_weapon_damage"), baseline.Calculation.Stats.Of("equipped_weapon_damage") * (1 + bonus), 1e-8, $"{what}: weapon damage preview");
            }
            foreach (var invalid in new[] { "-1", "1.5", "6", "NaN" })
            {
                var what = $"Unnerve on {slot} with marks {invalid}";
                var result = Resolve(b, On(slot, ("unnerve_marks", invalid)));
                ExpectInvalidContext(result, "unnerve_marks", what);
                Near(Delta(result, baseline, "weapon_damage_multiplier"), 0, 1e-12, what);
            }
        }
    }

    /// <summary>G48: clearing a talent removes its bonus under the same assumptions, and reselecting it restores the bonus.</summary>
    [Fact]
    public void G48TalentsClearAndReselect()
    {
        (string Name, string Stat, (string Key, string Value)[] Context, double Bonus)[] cases =
        [
            ("Precision Strike", "weapon_damage_multiplier", [("precision_strike_active", "true"), ("precision_strike_stacks", "3")], .60),
            ("Perfect Precision Strike", "weapon_damage_multiplier", [("precision_strike_active", "true"), ("precision_strike_stacks", "3")], .70),
            ("Swift", "weapon_damage", [("swift_active", "true")], .20),
            ("Foam at the Mouth", "weapon_damage_multiplier", [("target_ensnared", "true")], .25),
            ("High Priority Target", "weapon_damage_multiplier", [("high_priority_target_active", "true")], 1.25),
            ("You can look...but you can't touch", "weapon_damage_multiplier", [("prima_donna_stacks", "10")], 1.1),
            ("Agonizing Bite", "weapon_damage_multiplier", [("agonizing_bite_marked_shot", "true")], .20),
            ("Unnerve", "weapon_damage_multiplier", [("unnerve_marks", "5")], .75),
        ];
        foreach (var (name, stat, context, bonus) in cases)
        {
            var talentId = TalentId(name);
            foreach (var slot in LegalSlots(talentId))
            {
                var b = WeaponBuild(talentId, slot);
                var baseline = Resolve(b, On(slot));
                var cleared = SetTalent(b, slot, talentId, "");
                Near(Delta(Resolve(cleared, On(slot, context)), baseline, stat), 0, 1e-12, $"{name} cleared on {slot}");
                var reselected = SetTalent(cleared, slot, talentId, talentId);
                Near(Delta(Resolve(reselected, On(slot, context)), baseline, stat), bonus, 1e-12, $"{name} reselected on {slot}");
            }
        }
    }

    /// <summary>G48: combat assumptions aren't part of a build, so they don't survive sharing it.</summary>
    [Fact]
    public void G48ContextAssumptionsStayTransient()
    {
        var talentId = TalentId("Precision Strike");
        var b = WeaponBuild(talentId, "primary_weapon");
        var active = Resolve(b, On("primary_weapon", ("precision_strike_active", "true"), ("precision_strike_stacks", "3")));
        var decoded = ShareCode.Decode(ShareCode.Encode(b));
        var serialized = System.Text.Encoding.UTF8.GetString(BuildDocument.Encode(decoded));
        foreach (var key in new[] { "precision_strike_active", "precision_strike_stacks", "swift_active", "target_ensnared", "high_priority_target_active", "prima_donna_stacks", "agonizing_bite_marked_shot", "unnerve_marks" })
        {
            Assert.False(serialized.Contains(key, StringComparison.Ordinal), $"context assumption {key} leaked into decoded build: {serialized}");
        }
        var restored = Resolve(decoded, On("primary_weapon"));
        Near(restored.Calculation.Stats.Of("weapon_damage_multiplier"), 1, 1e-12, "restored multiplier");
        Near(active.Calculation.Stats.Of("weapon_damage_multiplier"), 1.60, 1e-12, "active multiplier");
    }
}
