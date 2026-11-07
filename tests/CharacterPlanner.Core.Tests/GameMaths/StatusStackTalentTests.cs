using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// Conditional modifiers (F05): target-status, pellet, magazine and stack-gated talent amplifiers, their owner scope,
/// rejected inputs and transience, ported from Go's status_stack_talents_g45_test.go.
/// </summary>
public sealed class StatusStackTalentTests
{
    /// <summary>
    /// G45: Thunder Strike, Immobilize and Head Scratcher amplify their own weapon only against a target with their
    /// status, and multiply with Glass Cannon with provenance.
    /// </summary>
    [Fact]
    public void G45TargetStatusWeaponTalents()
    {
        var tests = new (string Id, string Gate, string Slot, double Value)[]
        {
            ("weapon_talent_thunder_strike", "target_shocked", "primary_weapon", .30),
            ("weapon_talent_perfect_thunder_strike", "target_shocked", "secondary_weapon", .35),
            ("weapon_talent_immobilize", "target_ensnared", "secondary_weapon", .20),
            ("weapon_talent_perfect_immobilize", "target_ensnared", "primary_weapon", .25),
            ("weapon_talent_head_scratcher", "target_confused", "primary_weapon", .30),
            ("weapon_talent_perfect_head_scratcher", "target_confused", "secondary_weapon", .35),
        };
        foreach (var tc in tests)
        {
            var b = WeaponTalentBuild(tc.Id, tc.Slot);
            Multiplier(tc.Id + " inactive", Stats(b, Division.Weapon(tc.Slot)), 1);
            foreach (var flag in new[] { "false", "invalid" })
            {
                Multiplier($"{tc.Id} {tc.Gate}={flag}", Stats(b, Context((GamePack.WeaponSlotSelector, tc.Slot), (tc.Gate, flag))), 1);
            }
            Multiplier(tc.Id + " active", Stats(b, Context((GamePack.WeaponSlotSelector, tc.Slot), (tc.Gate, "true"))), 1 + tc.Value);
            var otherSlot = tc.Slot == "primary_weapon" ? "secondary_weapon" : "primary_weapon";
            Multiplier(tc.Id + " wrong slot", Stats(b, Context((GamePack.WeaponSlotSelector, otherSlot), (tc.Gate, "true"))), 1);
            Multiplier(tc.Id + " cleared", Stats(ClearTalent(b, tc.Slot, tc.Id), Context((GamePack.WeaponSlotSelector, tc.Slot), (tc.Gate, "true"))), 1);
            var composition = b.WithEquipment("chest", "chest_n_5_11_tactical_chest").WithTalentSelection("chest", "talent", "gear_talent_glass_cannon");
            var composed = Resolve(composition, Context((GamePack.WeaponSlotSelector, tc.Slot), (tc.Gate, "true")));
            Multiplier(tc.Id + " with Glass Cannon", composed.Calculation.Stats, 1.25 * (1 + tc.Value));
            Assert.True(composed.Calculation.Stats.Of("equipped_weapon_damage") > 0 && HasTalentContribution(composed, "weapon_damage_multiplier", tc.Id),
                $"{tc.Id} composition lost weapon preview or talent provenance");
        }
    }

    /// <summary>
    /// G45: Brazen amplifies the next shot by pellets landed above its threshold (six, or four for Perfect Brazen), only
    /// when flagged on its own weapon, rejecting invalid pellet counts.
    /// </summary>
    [Fact]
    public void G45BrazenPelletThresholdAndGate()
    {
        var ordinary = WeaponTalentBuild("weapon_talent_brazen", "primary_weapon");
        var perfect = WeaponTalentBuild("weapon_talent_perfect_brazen", "secondary_weapon");
        foreach (var pellets in new[] { "0", "3", "4", "5" })
        {
            Multiplier($"Brazen at {pellets} pellets", Stats(ordinary, Brazen("primary_weapon", "true", pellets)), 1);
        }
        foreach (var pellets in new[] { "6", "7", "100" })
        {
            var result = Resolve(ordinary, Brazen("primary_weapon", "true", pellets));
            Multiplier($"Brazen at {pellets} pellets", result.Calculation.Stats, 1 + .035 * Parse(pellets));
            Assert.True(HasTalentContribution(result, "weapon_damage_multiplier", "weapon_talent_brazen"), $"ordinary Brazen lost contribution provenance at {pellets} pellets");
        }
        foreach (var pellets in new[] { "4", "5", "100" })
        {
            var result = Resolve(perfect, Brazen("secondary_weapon", "true", pellets));
            Multiplier($"Perfect Brazen at {pellets} pellets", result.Calculation.Stats, 1 + .035 * Parse(pellets));
            Assert.True(HasTalentContribution(result, "weapon_damage_multiplier", "weapon_talent_perfect_brazen"), $"Perfect Brazen lost contribution provenance at {pellets} pellets");
        }
        foreach (var flag in new[] { "", "false", "invalid" })
        {
            var context = Context((GamePack.WeaponSlotSelector, "primary_weapon"), ("brazen_previous_shot_pellets", "7"));
            if (flag.Length > 0) { context = context.SetItem("brazen_next_shot_active", flag); }
            Multiplier($"Brazen with next shot \"{flag}\"", Stats(ordinary, context), 1);
        }
        Multiplier("cleared Brazen", Stats(ClearTalent(ordinary, "primary_weapon", "weapon_talent_brazen"), Brazen("primary_weapon", "true", "7")), 1);
        Multiplier("Brazen on the other weapon", Stats(ordinary, Brazen("secondary_weapon", "true", "7")), 1);
        foreach (var invalid in new[] { "-1", "1.5", "101", "NaN" })
        {
            var result = Resolve(ordinary, Brazen("primary_weapon", "true", invalid));
            Assert.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == "context-value-invalid"), $"invalid Brazen input \"{invalid}\" did not produce a diagnostic");
            Multiplier($"Brazen at invalid {invalid}", result.Calculation.Stats, 1);
        }
    }

    /// <summary>
    /// G45: Frenzy gives weapon damage and rate of fire per full ten (or eight, for Perfect Frenzy) rounds of the
    /// equipped magazine, only when active on its own weapon; the deprecated capacity input is ignored.
    /// </summary>
    [Fact]
    public void G45FrenzyMagazineIntervals()
    {
        var ordinary = WeaponTalentBuild("weapon_talent_frenzy", "primary_weapon");
        var perfect = WeaponTalentBuild("weapon_talent_perfect_frenzy", "secondary_weapon");
        foreach (var (capacity, want) in new[] { ("0", 0.0), ("9", 0), ("10", .03), ("19", .03), ("20", .06), ("200", .6) })
        {
            var result = BuildResolver.Resolve(CapacityPack(capacity), ordinary, Context((GamePack.WeaponSlotSelector, "primary_weapon"), ("frenzy_active", "true")));
            CloseTo(result.Calculation.Stats.Of("weapon_damage"), want, 1e-12);
            CloseTo(result.Calculation.Stats.Of("rate_of_fire"), want, 1e-12);
            Assert.False(want > 0 && (!HasTalentContribution(result, "weapon_damage", "weapon_talent_frenzy") || !HasTalentContribution(result, "rate_of_fire", "weapon_talent_frenzy")),
                $"ordinary Frenzy lost damage/rate provenance at capacity {capacity}");
        }
        foreach (var (capacity, want) in new[] { ("7", 0.0), ("8", .03), ("15", .03), ("16", .06), ("200", .75) })
        {
            var result = BuildResolver.Resolve(CapacityPack(capacity), perfect, Context((GamePack.WeaponSlotSelector, "secondary_weapon"), ("frenzy_active", "true")));
            CloseTo(result.Calculation.Stats.Of("weapon_damage"), want, 1e-12);
            CloseTo(result.Calculation.Stats.Of("rate_of_fire"), want, 1e-12);
            Assert.False(want > 0 && (!HasTalentContribution(result, "weapon_damage", "weapon_talent_perfect_frenzy") || !HasTalentContribution(result, "rate_of_fire", "weapon_talent_perfect_frenzy")),
                $"Perfect Frenzy lost damage/rate provenance at capacity {capacity}");
        }
        foreach (var flag in new[] { "", "false", "invalid" })
        {
            var context = Context((GamePack.WeaponSlotSelector, "primary_weapon"), ("frenzy_magazine_capacity", "20"));
            if (flag.Length > 0) { context = context.SetItem("frenzy_active", flag); }
            CloseTo(Stats(ordinary, context).Of("weapon_damage"), 0, 1e-12);
        }
        CloseTo(Stats(ClearTalent(ordinary, "primary_weapon", "weapon_talent_frenzy"), Frenzy("primary_weapon", "20")).Of("weapon_damage"), 0, 1e-12);
        CloseTo(Stats(ordinary, Frenzy("secondary_weapon", "20")).Of("weapon_damage"), 0, 1e-12);
        foreach (var invalid in new[] { "-1", "1.5", "201", "NaN" })
        {
            var result = Resolve(ordinary, Frenzy("primary_weapon", invalid));
            Assert.False(result.Diagnostics.Any(diagnostic => diagnostic.Code == "context-value-invalid"), "deprecated capacity input should be ignored");
            CloseTo(result.Calculation.Stats.Of("weapon_damage"), .03 * Math.Floor(result.Calculation.Stats.Of("equipped_weapon_magazine_size") / 10), 1e-12);
        }
    }

    /// <summary>
    /// G45: Intimidate's eligible stacks amplify either weapon only while active, up to nine; Perfect Intimidate reaches
    /// 40%; it multiplies with Thunder Strike and rejects invalid stacks.
    /// </summary>
    [Fact]
    public void G45IntimidateEligibleStacks()
    {
        var ordinary = GearTalentBuild("gear_talent_intimidate");
        var perfect = GearTalentBuild("gear_talent_perfect_intimidate");
        foreach (var flag in new[] { "", "false", "invalid" })
        {
            var context = Context((GamePack.WeaponSlotSelector, "primary_weapon"), ("intimidate_stacks", "10"));
            if (flag.Length > 0) { context = context.SetItem("intimidate_active", flag); }
            Multiplier($"Intimidate with active \"{flag}\"", Stats(ordinary, context), 1);
        }
        foreach (var (stacks, want) in new[] { ("0", 1.0), ("1", 1.04), ("9", 1.36), ("10", 1.36) })
        {
            Multiplier($"Intimidate at {stacks} stacks", Stats(ordinary, Intimidate("primary_weapon", stacks)), want);
        }
        var perfectResult = Resolve(perfect, Intimidate("primary_weapon", "10"));
        Multiplier("Perfect Intimidate", perfectResult.Calculation.Stats, 1.4);
        Assert.True(HasTalentContribution(perfectResult, "weapon_damage_multiplier", "gear_talent_perfect_intimidate"), "Perfect Intimidate contribution missing provenance");
        Multiplier("cleared Intimidate", Stats(ClearTalent(ordinary, "chest", "gear_talent_intimidate"), Intimidate("primary_weapon", "9")), 1);
        Multiplier("Intimidate on the other weapon", Stats(ordinary, Intimidate("secondary_weapon", "9")), 1.36);
        Assert.True(Stats(ordinary, Intimidate("primary_weapon", "9")).Of("equipped_weapon_damage") > 0, "active Intimidate preview must retain positive equipped weapon damage");
        var thunderHost = Compiled.Pack.Items.FirstOrDefault(item => item.Slot == "primary_weapon" && item.TalentSlots.Any(slot => slot.Choices.Contains("weapon_talent_thunder_strike")));
        Assert.True(thunderHost is not null, "no primary weapon host allows Thunder Strike");
        var thunderTalentSlot = thunderHost.TalentSlots.First(slot => slot.Choices.Contains("weapon_talent_thunder_strike")).ID;
        var combinedBuild = ordinary.WithEquipment("primary_weapon", thunderHost.ID).WithTalentSelection("primary_weapon", thunderTalentSlot, "weapon_talent_thunder_strike");
        var combined = Resolve(combinedBuild, Intimidate("primary_weapon", "9").SetItem("target_shocked", "true"));
        Multiplier("Intimidate with Thunder Strike", combined.Calculation.Stats, 1.36 * 1.3);
        Assert.True(combined.Calculation.Stats.Of("equipped_weapon_damage") > 0 && HasTalentContribution(combined, "weapon_damage_multiplier", "weapon_talent_thunder_strike"),
            "Intimidate plus Thunder Strike did not produce a positive, provenance-backed weapon preview");
        foreach (var invalid in new[] { "-1", "1.5", "11", "NaN" })
        {
            var result = Resolve(ordinary, Intimidate("primary_weapon", invalid));
            Assert.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == "context-value-invalid"), $"invalid Intimidate input \"{invalid}\" did not produce a diagnostic");
            Multiplier($"Intimidate at invalid {invalid}", result.Calculation.Stats, 1);
        }
    }

    /// <summary>G45: talent assumptions stay out of a shared build, so the decoded build resolves without them.</summary>
    [Fact]
    public void G45TalentAssumptionsAreTransientAcrossSharing()
    {
        var b = WeaponTalentBuild("weapon_talent_thunder_strike", "primary_weapon");
        var encoded = ShareCode.Encode(b);
        var decoded = ShareCode.Decode(encoded);
        Assert.False(encoded.Contains("target_shocked", StringComparison.Ordinal) || encoded.Contains("frenzy_magazine_capacity", StringComparison.Ordinal)
            || encoded.Contains("intimidate_stacks", StringComparison.Ordinal), $"view assumptions leaked into share encoding: {encoded}");
        Multiplier("decoded Thunder Strike", Stats(decoded, Division.Weapon("primary_weapon")), 1);
    }

    private static ImmutableDictionary<string, string> Brazen(string slot, string active, string pellets) =>
        Context((GamePack.WeaponSlotSelector, slot), ("brazen_next_shot_active", active), ("brazen_previous_shot_pellets", pellets));

    private static ImmutableDictionary<string, string> Frenzy(string slot, string capacity) =>
        Context((GamePack.WeaponSlotSelector, slot), ("frenzy_active", "true"), ("frenzy_magazine_capacity", capacity));

    private static ImmutableDictionary<string, string> Intimidate(string slot, string stacks) =>
        Context((GamePack.WeaponSlotSelector, slot), ("intimidate_active", "true"), ("intimidate_stacks", stacks));

    private static double Parse(string value) => double.Parse(value, CultureInfo.InvariantCulture);

    private static void Multiplier(string label, ImmutableDictionary<string, double> stats, double want)
    {
        var got = stats.Of("weapon_damage_multiplier");
        Assert.True(Math.Abs(got - want) <= 1e-12, $"{label}: weapon_damage_multiplier = {got}, want {want}");
    }

    /// <summary>The talent selected on the first item in a weapon slot that offers it, as Go's g45WeaponTalentBuild.</summary>
    private static BuildSnapshot WeaponTalentBuild(string talentId, string slot)
    {
        foreach (var item in Compiled.Pack.Items.Where(item => item.Slot == slot))
        {
            var talentSlot = item.TalentSlots.FirstOrDefault(candidate => candidate.Choices.Contains(talentId));
            if (talentSlot is not null) { return Build().WithEquipment(slot, item.ID).WithTalentSelection(slot, talentSlot.ID, talentId); }
        }
        throw new InvalidOperationException($"no {slot} host allows {talentId}");
    }

    /// <summary>The talent on the first chest offering it, with two ACRs, as Go's g45GearTalentBuild.</summary>
    private static BuildSnapshot GearTalentBuild(string talentId) =>
        WeaponTalentBuild(talentId, "chest").WithEquipment("primary_weapon", "primary_weapon_acr").WithEquipment("secondary_weapon", "secondary_weapon_acr");

    private static BuildSnapshot ClearTalent(BuildSnapshot b, string slot, string talentId)
    {
        var itemId = b.Equipment.GetValueOrDefault(slot, "");
        var talentSlot = Compiled.Pack.Items.Where(item => item.ID == itemId).SelectMany(item => item.TalentSlots).FirstOrDefault(candidate => candidate.Choices.Contains(talentId))
            ?? throw new InvalidOperationException($"{talentId} is not selected on {slot}");
        return b.WithTalentSelection(slot, talentSlot.ID, "");
    }

    private static bool HasTalentContribution(ResolvedBuild result, string stat, string talentId) =>
        result.Calculation.Breakdowns.TryGetValue(stat, out var breakdown)
            && breakdown.Contributions.Any(contribution => contribution.Source.Kind == "talent" && contribution.Source.Id == talentId);

    /// <summary>The pack with the equipped magazine size fixed, as Go's g107CapacityPack.</summary>
    private static CompiledPackExpressions CapacityPack(string capacity)
    {
        var pack = Compiled.Pack with
        {
            Stats = [.. Compiled.Pack.Stats.Select(stat => stat.ID == "equipped_weapon_magazine_size" ? stat with { Formula = capacity } : stat)],
        };
        var result = PackValidator.Validate(pack);
        return result.Compiled ?? throw new InvalidOperationException($"capacity fixture: {string.Join("; ", result.Diagnostics)}");
    }
}
