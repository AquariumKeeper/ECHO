using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>A reviewed skill property: the skill, its base and its Tier 0 to 6 bonuses, as the Go skill-property tests' cases.</summary>
internal sealed record SkillProperty(string Id, double Base, double[] Tiers)
{
    /// <summary>The bonus at a skill tier, clamped to 0..6 and floored, as Go's tiers[int(min(6, max(0, floor(tier))))].</summary>
    public double At(double tier) => Tiers[(int)Math.Min(6, Math.Max(0, Math.Floor(tier)))];
}

/// <summary>
/// Helpers shared by the ported Go skill_property_g162..g187 tests: the pack with every active skill's own modifiers
/// replaced (as the Go tests edited Pack.ActiveSkills[i].Modifiers), the skill-tier sweep, and the Nanite builds.
/// </summary>
internal static class SkillProperties
{
    public static readonly string[] Slots = ["skill_slot_1", "skill_slot_2"];

    /// <summary>The skill tiers every reviewed-property test sweeps, out-of-range and fractional ones included.</summary>
    public static readonly double[] TierSweep = [-1, 0, 1, 2, 3, 4, 5, 6, 6.9, 8];

    /// <summary>The skill-haste values the recovery-speed boundary tests sweep.</summary>
    public static readonly double[] HasteSweep = [0, .9, 1, -1, -2];

    /// <summary>G162-G166's skill modifiers: the tier, then fixed skill stats.</summary>
    public static (string Stat, double Value)[] Tiered(double tier) =>
        [("skill_tier", tier), ("skill_health", .2), ("skill_duration", .2), ("skill_efficiency", .1), ("shield_health", 3), ("repair_skills", 3), ("skill_damage", 3), ("magazine_size", 3)];

    /// <summary>G167-G168's skill modifiers: skill haste ahead of G162's.</summary>
    public static (string Stat, double Value)[] Hasted(double tier) => [("skill_haste", .2), .. Tiered(tier)];

    /// <summary>G169-G187's skill modifiers: G167's with ammo capacity and status effects.</summary>
    public static (string Stat, double Value)[] Full(double tier) =>
        [("skill_haste", .2), ("skill_tier", tier), ("skill_health", .2), ("skill_duration", .2), ("skill_efficiency", .1), ("shield_health", 3), ("ammo_capacity", 3),
            ("status_effects", 3), ("repair_skills", 3), ("skill_damage", 3), ("magazine_size", 3)];

    /// <summary>The recovery-speed boundary tests' skill modifiers.</summary>
    public static (string Stat, double Value)[] Boundary(double haste) => [("skill_haste", haste), ("skill_efficiency", .1), ("skill_tier", 8), ("skill_damage", 3)];

    private static readonly ConcurrentDictionary<string, CompiledPackExpressions> Packs = new(StringComparer.Ordinal);

    /// <summary>The Division 2 pack with every active skill's modifiers replaced by flat ones, validated (cached per set).</summary>
    public static CompiledPackExpressions WithSkillModifiers((string Stat, double Value)[] modifiers)
    {
        var key = string.Join(";", modifiers.Select(modifier => modifier.Stat + "=" + modifier.Value.ToString("R", CultureInfo.InvariantCulture)));
        return Packs.GetOrAdd(key, _ =>
        {
            ImmutableArray<StatModifier> replaced = [.. modifiers.Select(modifier => new StatModifier { Stat = modifier.Stat, Op = ModifierOps.Flat, Value = new ModifierValue(modifier.Value) })];
            var pack = Compiled.Pack with { ActiveSkills = [.. Compiled.Pack.ActiveSkills.Select(skill => skill with { Modifiers = replaced })] };
            var result = PackValidator.Validate(pack);
            return result.Compiled ?? throw new InvalidOperationException($"invalid fixture: {string.Join("; ", result.Diagnostics)}");
        });
    }

    /// <summary>Go's assertNoErrorDiagnostics.</summary>
    public static void NoErrors(ResolvedBuild resolved, string what)
    {
        foreach (var diagnostic in resolved.Diagnostics)
        {
            Assert.False(diagnostic.Severity == "error", $"{what}: error diagnostic {diagnostic}");
        }
    }

    /// <summary>
    /// The Go reviewed-property loop: for each swept value, every active skill carries the given modifiers; each case's
    /// skill, in either slot, resolves without errors and shows the output at the expected value. When a factor output
    /// is named, it must, where present, equal the expectation divided by the case's base (Go's cooldown_factor check).
    /// </summary>
    public static void Check(double[] sweep, Func<double, (string Stat, double Value)[]> modifiers, SkillProperty[] cases, string output,
        Func<SkillProperty, double, double> want, double tolerance = 1e-6, string? factor = null)
    {
        foreach (var value in sweep)
        {
            var compiled = WithSkillModifiers(modifiers(value));
            foreach (var tc in cases)
            {
                foreach (var slot in Slots)
                {
                    var what = $"{tc.Id} in {slot} at {value.ToString(CultureInfo.InvariantCulture)}";
                    var resolved = BuildResolver.Resolve(compiled, BuildSnapshot.Create(compiled.Pack).WithActiveSkillSelection(slot, tc.Id), ImmutableDictionary<string, string>.Empty);
                    NoErrors(resolved, what);
                    var outputs = resolved.ActiveSkillPreviews.TryGetValue(slot, out var preview) ? preview.Outputs : [];
                    var expected = want(tc, value);
                    if (factor is not null)
                    {
                        foreach (var o in outputs.Where(o => o.Id == factor))
                        {
                            Assert.True(Math.Abs((o.Value * tc.Base) - expected) <= tolerance, $"{what}: {factor} × {tc.Base} = {o.Value * tc.Base}, want {expected}");
                        }
                    }
                    var found = outputs.Where(o => o.Id == output).ToArray();
                    Assert.True(found.Length > 0, $"{what}: missing {output}");
                    foreach (var o in found)
                    {
                        Assert.True(Math.Abs(o.Value - expected) <= tolerance, $"{what}: {output} = {o.Value}, want {expected} (within {tolerance})");
                    }
                }
            }
        }
    }

    /// <summary>Go's g52PreviewValue: a preview output that must be present.</summary>
    public static double Preview(ResolvedBuild resolved, string slot, string output)
    {
        Assert.True(resolved.ActiveSkillPreviews.TryGetValue(slot, out var preview), $"missing active skill preview {slot}");
        var found = preview.Outputs.FirstOrDefault(candidate => candidate.Id == output);
        Assert.True(found is not null, $"missing {output} output in {slot} preview");
        return found.Value;
    }

    /// <summary>
    /// The Nanite builds of Go's NaniteThresholdAndHost tests at a piece count: Ortiz Reficere alone, with Improved
    /// Dampeners on its backpack (the only upgraded case), with the talent on the chest instead, and with the backpack's
    /// talent cleared again.
    /// </summary>
    public static (BuildSnapshot Build, bool Upgraded)[] NaniteBuilds(int pieces)
    {
        var b = SetBuild("gear_set_ortiz_reficere", pieces);
        var legal = SelectUpgrade(b, "backpack", "gear_talent_improved_dampeners");
        return [(b, false), (legal, true), (b.WithTalentSelection("chest", "talent", "gear_talent_improved_dampeners"), false), (legal.WithTalentSelection("backpack", "talent", ""), false)];
    }

    /// <summary>The Nanite reduction: none below four pieces, 90%, or 25% with the backpack upgrade.</summary>
    public static double NaniteReduction(int pieces, bool upgraded) => pieces >= 4 ? upgraded ? .25 : .9 : 0;

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
}
