using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using static CharacterPlanner.Core.Tests.GameMaths.Division;

namespace CharacterPlanner.Core.Tests.GameMaths;

/// <summary>
/// Gear-set four-piece talents and their chest and backpack upgrades (F05 set and brand bonuses), ported from Go's
/// set_talents_g42_test.go, set_talents_g43_test.go and set_talents_g44_test.go.
/// </summary>
public sealed class SetTalentTests
{
    private sealed record SetCase(string Id, string Input, string Gate, string[] Stats, (string Slot, string Talent)[] Upgrades);

    private static readonly SetCase[] G42Cases =
    [
        new("gear_set_striker_s_battlegear", "striker_stacks", "", ["weapon_damage_multiplier"], [("chest", "gear_talent_press_the_advantage"), ("backpack", "gear_talent_risk_management")]),
        new("gear_set_future_initiative", "", "full_armor", ["total_weapon_damage", "total_skill_damage"], [("chest", "gear_talent_tactical_superiority")]),
        new("gear_set_hard_wired", "", "feedback_loop_active", ["total_skill_damage", "total_skill_repair"], [("chest", "gear_talent_positive_reinforcement")]),
        new("gear_set_tipping_scales", "tipping_scales_stacks", "", ["weapon_handling", "critical_hit_damage"], [("chest", "gear_talent_sustainability"), ("backpack", "gear_talent_snowball")]),
    ];

    private static readonly SetCase[] G43Cases =
    [
        new("gear_set_heartbreaker", "heartbreaker_stacks", "target_pulsed", ["weapon_damage_multiplier"], [("chest", "gear_talent_max_bpm")]),
        new("gear_set_ongoing_directive", "", "hollow_point_ammo_active", ["weapon_damage_multiplier"], [("chest", "gear_talent_parabellum_rounds")]),
        new("gear_set_aces_eights", "", "aces_eights_empowered_shot", ["weapon_damage_multiplier"], [("chest", "gear_talent_no_limit")]),
        new("gear_set_concentrated_company", "camaraderie_stacks", "", ["weapon_damage", "critical_hit_damage"], [("backpack", "gear_talent_one_for_all")]),
        new("gear_set_negotiator_s_dilemma", "negotiator_chd_stacks", "", ["critical_hit_damage"], []),
        new("gear_set_umbra_initiative", "umbra_light_stacks", "umbra_light_active", ["critical_hit_damage", "rate_of_fire"], [("chest", "gear_talent_from_the_shadows")]),
    ];

    private static readonly SetCase[] G44Cases =
    [
        new("gear_set_rigger", "", "rigger_skill_buff_active", ["skill_damage"], [("chest", "gear_talent_best_buds")]),
        new("gear_set_tip_of_the_spear", "", "aggressive_recon_active", ["signature_weapon_damage", "reload_speed"], [("chest", "gear_talent_specialized_destruction"), ("backpack", "gear_talent_signature_moves")]),
        new("gear_set_measured_assembly", "huddle_qualifying_allies", "", ["skill_tier"], []),
        new("gear_set_breaking_point", "on_point_stacks", "on_point_active", ["weapon_damage", "weapon_handling"], [("backpack", "gear_talent_point_of_honor")]),
        new("gear_set_hunter_s_fury", "", "hunter_fury_target_within_15m", ["weapon_damage_multiplier"], []),
    ];

    [Theory]
    [InlineData(3, false)]
    [InlineData(4, true)]
    public void FullSetTalentsJoinAtTheSetsTopBonus(int pieces, bool expected)
    {
        // Apex Engine has no talent slot, so a fight can only reach it through the set.
        var resolved = BuildResolver.Resolve(Compiled, SetBuild("gear_set_apex_protocol", pieces));
        Assert.Equal(expected, SetBonusSelection.FullSetTalents(Compiled.Pack, resolved.ActiveSets).Contains("gear_talent_apex_engine"));
    }

    [Fact]
    public void FullSetTalentsLeaveTalentsAnItemOffers()
    {
        // Veer Override is in the set, but the chest offers it, so it's selected there, not through the set.
        var resolved = BuildResolver.Resolve(Compiled, SetBuild("gear_set_apex_protocol", 4));
        Assert.DoesNotContain("gear_talent_veer_override", SetBonusSelection.FullSetTalents(Compiled.Pack, resolved.ActiveSets));
    }

    /// <summary>
    /// The first pieces of a set, each in its own slot, with ACRs in both weapon slots, as Go's g42SetBuild,
    /// g43SetBuild and g44SetBuild. Aces &amp; Eights (G43) and Breaking Point (G44) take the first rifle as primary.
    /// </summary>
    private static BuildSnapshot SetBuild(string setId, int pieces)
    {
        var set = Compiled.Pack.Sets.FirstOrDefault(set => set.ID == setId);
        Assert.True(set is not null, $"missing set {setId}");
        var b = BuildSnapshot.Create(Compiled.Pack, "Set talent regression: " + setId);
        foreach (var itemId in set.Items.Take(pieces))
        {
            var item = Compiled.Pack.Items.FirstOrDefault(item => item.ID == itemId);
            Assert.True(item is not null && item.Slot.Length > 0, $"set item {itemId} does not occupy one slot");
            b = b.WithEquipment(item.Slot, itemId);
        }
        b = b.WithEquipment("primary_weapon", "primary_weapon_acr").WithEquipment("secondary_weapon", "secondary_weapon_acr");
        if (setId is "gear_set_aces_eights" or "gear_set_breaking_point")
        {
            var rifle = Compiled.Pack.Items.FirstOrDefault(item => item.Slot == "primary_weapon" && item.Tags.Contains("weapon_type_rifle"));
            if (rifle is not null) { b = b.WithEquipment("primary_weapon", rifle.ID); }
        }
        return b;
    }

    /// <summary>Go's g42SelectedUpgrade: the talent selected in the slot's "talent" talent slot.</summary>
    private static BuildSnapshot Upgrade(BuildSnapshot b, string slot, string talent) => b.WithTalentSelection(slot, "talent", talent);

    private static IEnumerable<Contribution> ContributionsTo(ResolvedBuild result, string stat) =>
        result.Calculation.Breakdowns.TryGetValue(stat, out var breakdown) ? breakdown.Contributions : [];

    private static bool FromSet(Contribution contribution, string setId, bool fourPiece) =>
        contribution.Source.Kind == "set" && contribution.Source.Id == setId && (!fourPiece || contribution.Source.Name.EndsWith("(4pc)", StringComparison.Ordinal));

    /// <summary>Go's g42HasSetContribution (any piece count) and g43/g44 four-piece variants.</summary>
    private static bool HasSetContribution(ResolvedBuild result, string stat, string setId, bool fourPiece) =>
        ContributionsTo(result, stat).Any(contribution => FromSet(contribution, setId, fourPiece));

    /// <summary>Go's g42SetContributionValue (any piece count) and g43ContributionValue/g44ContributionValue (four-piece).</summary>
    private static void SetContributionValue(ResolvedBuild result, string stat, string setId, double want, bool fourPiece = true)
    {
        var contribution = ContributionsTo(result, stat).FirstOrDefault(contribution => FromSet(contribution, setId, fourPiece));
        Assert.True(contribution is not null, $"{stat} has no {(fourPiece ? "four-piece " : "")}contribution from set {setId}");
        Near(contribution.Value, want, 1e-12, $"{setId} contribution to {stat}");
    }

    /// <summary>Go's g43NoTalentDuplicate and g44NoUpgradeDuplicate.</summary>
    private static void NoTalentDuplicate(ResolvedBuild result, IEnumerable<string> stats, string talentId)
    {
        foreach (var stat in stats)
        {
            Assert.False(ContributionsTo(result, stat).Any(contribution => contribution.Source.Kind == "talent" && contribution.Source.Id == talentId),
                $"upgrade {talentId} was double-counted as an independent talent for {stat}");
        }
    }

    private static bool HasDiagnostic(ResolvedBuild result, string code, string subject) =>
        result.Diagnostics.Any(diagnostic => diagnostic.Code == code && diagnostic.Subject == subject);

    private static void Near(double got, double want, double tolerance, string what)
    {
        if (Math.Abs(got - want) > tolerance) { Assert.Fail($"{what}: got {got}, want {want} (within {tolerance})"); }
    }

    private static double Stat(BuildSnapshot b, string stat, params (string Key, string Value)[] context) => Stats(b, Context(context)).Of(stat);

    /// <summary>Go's g44PreviewValue.</summary>
    private static double PreviewValue(ResolvedBuild result, string slot, string outputId)
    {
        Assert.True(result.ActiveSkillPreviews.TryGetValue(slot, out var preview), $"no active-skill preview for {slot}");
        var output = preview.Outputs.FirstOrDefault(output => output.Id == outputId);
        Assert.True(output is not null, $"preview {slot} is missing output {outputId}");
        return output.Value;
    }

    /// <summary>Go's g41ExpectedWeaponDamage.</summary>
    private static double ExpectedWeaponDamage(ImmutableDictionary<string, double> stats, IEnumerable<string> tags)
    {
        var byType = 0.0;
        foreach (var tag in tags)
        {
            byType += tag switch
            {
                "weapon_type_assault_rifle" => stats.Of("assault_rifle_damage"),
                "weapon_type_lmg" => stats.Of("lmg_damage"),
                "weapon_type_smg" => stats.Of("smg_damage"),
                "weapon_type_rifle" => stats.Of("rifle_damage"),
                "weapon_type_marksman_rifle" => stats.Of("marksman_rifle_damage"),
                "weapon_type_shotgun" => stats.Of("shotgun_damage"),
                "weapon_type_pistol" => stats.Of("pistol_damage"),
                _ => 0,
            };
        }
        return stats.Of("weapon_base_damage") * (1 + stats.Of("weapon_damage") + byType) * stats.Of("weapon_damage_multiplier") * (1 + stats.Of("total_weapon_damage"));
    }

    // Go compared nonDamagePreviews(...) with reflect.DeepEqual: previews without damage channels and assumed_ outputs.
    private static bool IsDamagePreviewChannel(string id) => id is "damage" or "average_damage" or "cloud_damage" or "burn_damage" or "bleed_damage" or "explosion_damage";

    private static bool SameNonDamagePreviews(ImmutableDictionary<string, SkillPreview> a, ImmutableDictionary<string, SkillPreview> b)
    {
        static SkillPreviewOutput[] Kept(SkillPreview preview) =>
            [.. preview.Outputs.Where(output => !IsDamagePreviewChannel(output.Id) && !output.Id.StartsWith("assumed_", StringComparison.Ordinal))];
        return a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && pair.Value.SkillId == other.SkillId
            && Kept(pair.Value).Length == Kept(other).Length && Kept(pair.Value).Zip(Kept(other)).All(outputs => SameOutput(outputs.First, outputs.Second)));
    }

    private static bool SameOutput(SkillPreviewOutput a, SkillPreviewOutput b) =>
        a.Id == b.Id && a.Name == b.Name && a.Description == b.Description && a.Format == b.Format && a.Formula == b.Formula && a.When == b.When
            && a.Value.Equals(b.Value) && a.Kind == b.Kind && a.Inputs.Count == b.Inputs.Count && a.Inputs.All(pair => b.Inputs.TryGetValue(pair.Key, out var value) && value.Equals(pair.Value));

    private static void CheckThresholds(SetCase tc, ImmutableDictionary<string, string> context, bool fourPiece)
    {
        foreach (var count in new[] { 2, 3, 4, 5, 6 })
        {
            var result = Resolve(SetBuild(tc.Id, count), context);
            foreach (var stat in tc.Stats)
            {
                Assert.True(HasSetContribution(result, stat, tc.Id, fourPiece) == count >= 4,
                    $"{tc.Id} {count} pieces: set contribution for {stat} present={!(count >= 4)}, want {count >= 4}");
            }
        }
    }

    /// <summary>G42: Striker, Future Initiative, Hard Wired and Tipping Scales contribute only from four pieces.</summary>
    [Fact]
    public void G42SetTalentFourPieceThresholds()
    {
        foreach (var tc in G42Cases)
        {
            var context = new List<(string, string)>();
            if (tc.Gate.Length > 0) { context.Add((tc.Gate, "true")); }
            if (tc.Input.Length > 0) { context.Add((tc.Input, "75")); }
            CheckThresholds(tc, Context([.. context]), fourPiece: false);
        }
    }

    /// <summary>
    /// G42: Striker's stacks cap at 100 (150 with Press the Advantage and Risk Management selected), reject invalid
    /// counts, and an unsupported backpack talent is withheld with a diagnostic.
    /// </summary>
    [Fact]
    public void G42StrikerStackCapAndSelectedUpgrades()
    {
        var b = SetBuild("gear_set_striker_s_battlegear", 4);
        const string primary = "primary_weapon";
        var at100 = Stat(b, "weapon_damage_multiplier", ("weapon_slot", primary), ("striker_stacks", "100"));
        var at150 = Stat(b, "weapon_damage_multiplier", ("weapon_slot", primary), ("striker_stacks", "150"));
        Near(at100, 1 + .0065 * 100, 1e-12, "100 stacks");
        Near(at150, at100, 1e-12, "150 stacks cap at 100");
        Near(Stat(b, "weapon_damage_multiplier", ("weapon_slot", primary)), 1, 1e-12, "no stacks");

        var upgraded = Upgrade(Upgrade(b, "chest", "gear_talent_press_the_advantage"), "backpack", "gear_talent_risk_management");
        var active = Resolve(upgraded, Context(("weapon_slot", primary), ("striker_stacks", "150")));
        Near(active.Calculation.Stats.Of("weapon_damage_multiplier"), 1 + .009 * 150, 1e-12, "upgraded at 150 stacks");
        SetContributionValue(active, "weapon_damage_multiplier", "gear_set_striker_s_battlegear", .009 * 150, fourPiece: false);
        foreach (var invalid in new[] { "-1", "201", "2.5", "NaN" })
        {
            var result = Resolve(upgraded, Context(("weapon_slot", primary), ("striker_stacks", invalid)));
            Near(result.Calculation.Stats.Of("weapon_damage_multiplier"), 1, 1e-12, $"invalid striker_stacks={invalid}");
            Assert.True(HasDiagnostic(result, "context-value-invalid", "striker_stacks"), $"invalid striker_stacks=\"{invalid}\" lacks context diagnostic");
        }

        var clearedChest = upgraded.WithTalentSelection("chest", "talent", "");
        var invalidBackpack = upgraded.WithTalentSelection("backpack", "talent", "gear_talent_glass_cannon");
        Near(Stat(clearedChest, "weapon_damage_multiplier", ("weapon_slot", primary), ("striker_stacks", "150")), 1 + .009 * 100, 1e-12, "cleared chest");
        var wrong = Resolve(invalidBackpack, Context(("weapon_slot", primary), ("striker_stacks", "150")));
        Near(wrong.Calculation.Stats.Of("weapon_damage_multiplier"), 1 + .0065 * 150, 1e-12, "invalid backpack talent");
        Assert.True(HasDiagnostic(wrong, "talent-not-allowed", "backpack"), "invalid backpack talent selection has no talent-not-allowed diagnostic");
    }

    /// <summary>
    /// G42: Future Initiative and Hard Wired need their gate set true, and their chest upgrade raises the set's own
    /// contribution without also counting as an item talent.
    /// </summary>
    [Fact]
    public void G42FutureInitiativeAndHardWiredGatesAndUpgrades()
    {
        foreach (var tc in G42Cases[1..3])
        {
            var b = SetBuild(tc.Id, 4);
            var noContext = Stats(b);
            var falseGate = Stats(b, Context((tc.Gate, "false")));
            var invalidGate = Stats(b, Context((tc.Gate, "invalid")));
            foreach (var stat in tc.Stats)
            {
                Near(noContext.Of(stat), 0, 1e-12, $"{tc.Id} {stat} without context");
                Near(falseGate.Of(stat), 0, 1e-12, $"{tc.Id} {stat} gate false");
                Near(invalidGate.Of(stat), 0, 1e-12, $"{tc.Id} {stat} gate invalid");
            }
            var baseline = Stats(b, Context((tc.Gate, "true")));
            var (baseAmount, upgradeAmount) = tc.Id == "gear_set_hard_wired" ? (.10, .25) : (.15, .25);
            var active = Resolve(Upgrade(b, "chest", tc.Upgrades[0].Talent), Context((tc.Gate, "true")));
            foreach (var stat in tc.Stats)
            {
                Near(baseline.Of(stat), baseAmount, 1e-12, $"{tc.Id} {stat} base");
                Near(active.Calculation.Stats.Of(stat), upgradeAmount, 1e-12, $"{tc.Id} {stat} upgraded");
                SetContributionValue(active, stat, tc.Id, upgradeAmount, fourPiece: false);
            }
            NoTalentDuplicate(active, tc.Stats, tc.Upgrades[0].Talent);
        }
    }

    /// <summary>G42: Tipping Scales' partial stacks give weapon handling and critical hit damage, more with both upgrades.</summary>
    [Fact]
    public void G42TippingScalesPartialStackTotals()
    {
        var b = SetBuild("gear_set_tipping_scales", 4);
        var baseline = Stats(b, Context(("tipping_scales_stacks", "75")));
        Near(baseline.Of("weapon_handling"), .25, 1e-12, "base weapon handling");
        Near(baseline.Of("critical_hit_damage"), 2.5, 1e-12, "base critical hit damage");
        b = Upgrade(Upgrade(b, "chest", "gear_talent_sustainability"), "backpack", "gear_talent_snowball");
        var upgraded = Resolve(b, Context(("tipping_scales_stacks", "75")));
        Near(upgraded.Calculation.Stats.Of("weapon_handling"), .375, 1e-12, "upgraded weapon handling");
        Near(upgraded.Calculation.Stats.Of("critical_hit_damage"), 6, 1e-12, "upgraded critical hit damage");
        SetContributionValue(upgraded, "weapon_handling", "gear_set_tipping_scales", .375, fourPiece: false);
        SetContributionValue(upgraded, "critical_hit_damage", "gear_set_tipping_scales", 6, fourPiece: false);
        foreach (var invalid in new[] { "-1", "76", "2.5", "NaN" })
        {
            var result = Resolve(b, Context(("tipping_scales_stacks", invalid)));
            Near(result.Calculation.Stats.Of("weapon_handling"), 0, 1e-12, $"invalid stacks {invalid}");
            Near(result.Calculation.Stats.Of("critical_hit_damage"), 0, 1e-12, $"invalid stacks {invalid}");
            Assert.True(HasDiagnostic(result, "context-value-invalid", "tipping_scales_stacks"), $"invalid tipping_scales_stacks=\"{invalid}\" lacks context diagnostic");
        }
        Near(Stats(b).Of("weapon_handling"), 0, 1e-12, "no stacks");
    }

    /// <summary>
    /// G42: Striker's multiplier composes with Glass Cannon and Perfect Vigilance, the weapon damage formula holds,
    /// and weapon damage changes no non-damage skill preview.
    /// </summary>
    [Fact]
    public void G42GearWideCompositionAndSkillPreviewStability()
    {
        var set = SetBuild("gear_set_striker_s_battlegear", 6);
        // Keep four Striker pieces in mask/gloves/holster/knees while equipping both weapon-damage talents in the
        // dedicated chest and backpack slots.
        foreach (var slot in new[] { "chest", "backpack" }) { set = set.WithEquipment(slot, ""); }
        set = set.WithEquipment("chest", "chest_n_5_11_tactical_chest").WithTalentSelection("chest", "talent", "gear_talent_glass_cannon")
            .WithEquipment("backpack", "backpack_the_gift").WithTalentSelection("backpack", "talent", "gear_talent_perfect_vigilance")
            .WithActiveSkillSelection("skill_slot_1", "skill_assault_turret");
        var baseline = Resolve(set, Context(("weapon_slot", "secondary_weapon"), ("striker_stacks", "0")));
        var active = Resolve(set, Context(("weapon_slot", "secondary_weapon"), ("striker_stacks", "100"), ("vigilance_active", "true")));
        Near(active.Calculation.Stats.Of("weapon_damage_multiplier"), baseline.Calculation.Stats.Of("weapon_damage_multiplier") * 1.65, 1e-12, "multiplier");
        Near(active.Calculation.Stats.Of("total_weapon_damage"), .25, 1e-12, "total weapon damage");
        Near(active.Calculation.Stats.Of("equipped_weapon_damage"), ExpectedWeaponDamage(active.Calculation.Stats, ["weapon_type_assault_rifle"]), 1e-8, "equipped weapon damage");
        Assert.True(SameNonDamagePreviews(active.ActiveSkillPreviews, baseline.ActiveSkillPreviews), "weapon and total weapon damage effects changed skill previews");
    }

    /// <summary>G42: numeric inputs stay resolve assumptions only, and the default build selects no upgrade talent.</summary>
    [Fact]
    public void G42NumericInputsRemainResolveContextOnly()
    {
        var b = SetBuild("gear_set_striker_s_battlegear", 4);
        var withContext = Stat(b, "weapon_damage_multiplier", ("weapon_slot", "primary_weapon"), ("striker_stacks", "100"));
        Near(withContext, 1.65, 1e-12, "with stacks");
        Near(Stat(b, "weapon_damage_multiplier", ("weapon_slot", "primary_weapon")), 1, 1e-12, "without stacks");
        Assert.False(double.IsNaN(withContext), "context input produced NaN");
        Assert.True(b.TalentSelections.IsEmpty, $"default build unexpectedly selected set upgrade talents: {string.Join(", ", b.TalentSelections.Keys)}");
    }

    /// <summary>G43: Heartbreaker, Ongoing Directive, Aces &amp; Eights, Concentrated Company, Negotiator's Dilemma and Umbra contribute their four-piece bonus only from four pieces.</summary>
    [Fact]
    public void G43SetFourPieceThresholds()
    {
        foreach (var tc in G43Cases)
        {
            var context = new List<(string, string)>();
            if (tc.Input.Length > 0) { context.Add((tc.Input, "100")); }
            if (tc.Gate.Length > 0) { context.Add((tc.Gate, "true")); }
            if (tc.Id == "gear_set_aces_eights") { context.Add(("weapon_slot", "primary_weapon")); }
            CheckThresholds(tc, Context([.. context]), fourPiece: true);
        }
    }

    /// <summary>
    /// G43: Heartbreaker's stacks need a pulsed target and cap at 50 (100 with Max BPM), reject invalid counts and
    /// compose with Glass Cannon and Perfect Vigilance.
    /// </summary>
    [Fact]
    public void G43HeartbreakerCapUpgradeAndAmplificationComposition()
    {
        var b = SetBuild("gear_set_heartbreaker", 4);
        var at50 = Stat(b, "weapon_damage_multiplier", ("heartbreaker_stacks", "50"), ("target_pulsed", "true"));
        Near(at50, 1 + .011 * 50, 1e-12, "50 stacks");
        Near(Stat(b, "weapon_damage_multiplier", ("heartbreaker_stacks", "100"), ("target_pulsed", "true")), at50, 1e-12, "100 stacks cap at 50");
        foreach (var pulsed in new[] { "false", "invalid" })
        {
            Near(Stat(b, "weapon_damage_multiplier", ("heartbreaker_stacks", "50"), ("target_pulsed", pulsed)), 1, 1e-12, $"target_pulsed={pulsed}");
        }
        Near(Stat(b, "weapon_damage_multiplier", ("target_pulsed", "true")), 1, 1e-12, "no stacks");
        var upgraded = Upgrade(b, "chest", "gear_talent_max_bpm");
        var active = Resolve(upgraded, Context(("heartbreaker_stacks", "100"), ("target_pulsed", "true")));
        Near(active.Calculation.Stats.Of("weapon_damage_multiplier"), 2.1, 1e-12, "Max BPM at 100 stacks");
        SetContributionValue(active, "weapon_damage_multiplier", "gear_set_heartbreaker", 1.1);
        NoTalentDuplicate(active, G43Cases[0].Stats, "gear_talent_max_bpm");
        var cleared = upgraded.WithTalentSelection("chest", "talent", "");
        Near(Stat(cleared, "weapon_damage_multiplier", ("heartbreaker_stacks", "100"), ("target_pulsed", "true")), 1 + .011 * 50, 1e-12, "cleared Max BPM");
        foreach (var invalid in new[] { "-1", "101", "2.5", "NaN" })
        {
            var result = Resolve(upgraded, Context(("heartbreaker_stacks", invalid)));
            Near(result.Calculation.Stats.Of("weapon_damage_multiplier"), 1, 1e-12, $"invalid stacks {invalid}");
            Assert.True(HasDiagnostic(result, "context-value-invalid", "heartbreaker_stacks"), $"invalid heartbreaker_stacks=\"{invalid}\" lacks context diagnostic");
        }
        var composed = SetBuild("gear_set_heartbreaker", 6)
            .WithEquipment("chest", "chest_n_5_11_tactical_chest").WithTalentSelection("chest", "talent", "gear_talent_glass_cannon")
            .WithEquipment("backpack", "backpack_the_gift").WithTalentSelection("backpack", "talent", "gear_talent_perfect_vigilance");
        var withOtherPools = Stats(composed, Context(("weapon_slot", "primary_weapon"), ("heartbreaker_stacks", "50"), ("target_pulsed", "true"), ("vigilance_active", "true")));
        Near(withOtherPools.Of("total_weapon_damage"), .25, 1e-12, "total weapon damage");
        Near(withOtherPools.Of("weapon_damage_multiplier"), 1.25 * 1.55, 1e-12, "composed multiplier");
    }

    /// <summary>
    /// G43: Ongoing Directive's and Aces &amp; Eights' amplification needs its gate, the chest upgrade raises it, and
    /// the upgrade selected on the backpack does nothing.
    /// </summary>
    [Fact]
    public void G43ActiveAmplificationGatesAndSelectedUpgrades()
    {
        foreach (var (setId, gate, upgrade, baseAmount, improved) in new[]
        {
            ("gear_set_ongoing_directive", "hollow_point_ammo_active", "gear_talent_parabellum_rounds", .4, .6),
            ("gear_set_aces_eights", "aces_eights_empowered_shot", "gear_talent_no_limit", .75, 1.0),
        })
        {
            var b = SetBuild(setId, 4);
            var activeContext = setId == "gear_set_aces_eights" ? Context((gate, "true"), ("weapon_slot", "primary_weapon")) : Context((gate, "true"));
            foreach (var value in new[] { "", "false", "invalid" })
            {
                var context = value.Length > 0 ? Context((gate, value)) : Context();
                Near(Stats(b, context).Of("weapon_damage_multiplier"), 1, 1e-12, $"{setId} {gate}=\"{value}\"");
            }
            Near(Stats(b, activeContext).Of("weapon_damage_multiplier"), 1 + baseAmount, 1e-12, $"{setId} active");
            var upgraded = Upgrade(b, "chest", upgrade);
            var upgradedResult = Resolve(upgraded, activeContext);
            Near(upgradedResult.Calculation.Stats.Of("weapon_damage_multiplier"), 1 + improved, 1e-12, $"{setId} upgraded");
            SetContributionValue(upgradedResult, "weapon_damage_multiplier", setId, improved);
            NoTalentDuplicate(upgradedResult, G43Cases.First(profile => profile.Id == setId).Stats, upgrade);
            Near(Stats(upgraded.WithTalentSelection("chest", "talent", ""), activeContext).Of("weapon_damage_multiplier"), 1 + baseAmount, 1e-12, $"{setId} cleared");
            Near(Stats(b.WithTalentSelection("backpack", "talent", upgrade), activeContext).Of("weapon_damage_multiplier"), 1 + baseAmount, 1e-12, $"{setId} upgrade on backpack");
        }
    }

    /// <summary>G43: Aces &amp; Eights' empowered shot needs a rifle or marksman rifle in the active slot.</summary>
    [Fact]
    public void G43AcesRequiresSelectedRifleOrMarksmanRifle()
    {
        string rifleId = "", otherId = "";
        foreach (var item in Compiled.Pack.Items.Where(item => item.Slot == "primary_weapon"))
        {
            if (item.Tags.Contains("weapon_type_rifle") || item.Tags.Contains("weapon_type_marksman_rifle"))
            {
                if (rifleId.Length == 0) { rifleId = item.ID; }
            }
            else if (item.Tags.Contains("weapon_type_assault_rifle") && otherId.Length == 0) { otherId = item.ID; }
        }
        Assert.True(rifleId.Length > 0 && otherId.Length > 0, "could not find primary rifle and assault rifle hosts");
        var b = SetBuild("gear_set_aces_eights", 4).WithEquipment("primary_weapon", rifleId);
        Near(Stat(b, "weapon_damage_multiplier", ("weapon_slot", "primary_weapon"), ("aces_eights_empowered_shot", "true")), 1.75, 1e-12, $"rifle {rifleId}");
        b = b.WithEquipment("primary_weapon", otherId);
        Near(Stat(b, "weapon_damage_multiplier", ("weapon_slot", "primary_weapon"), ("aces_eights_empowered_shot", "true")), 1, 1e-12, $"assault rifle {otherId}");
    }

    /// <summary>
    /// G43: Camaraderie's and Negotiator's stack bonuses, One for All's ownership of the upgraded Camaraderie value,
    /// and invalid stack counts rejected with a diagnostic.
    /// </summary>
    [Fact]
    public void G43StackBonusesAndUpgradeOwnership()
    {
        var camaraderie = SetBuild("gear_set_concentrated_company", 4);
        var baseline = Stats(camaraderie, Context(("camaraderie_stacks", "35")));
        Near(baseline.Of("weapon_damage"), 1.15, 1e-12, "Camaraderie weapon damage");
        Near(baseline.Of("critical_hit_damage"), 1.05, 1e-12, "Camaraderie critical hit damage");
        var upgraded = Upgrade(camaraderie, "backpack", "gear_talent_one_for_all");
        var upgradedResult = Resolve(upgraded, Context(("camaraderie_stacks", "35")));
        Near(upgradedResult.Calculation.Stats.Of("weapon_damage"), 2.2, 1e-12, "One for All weapon damage");
        Near(upgradedResult.Calculation.Stats.Of("critical_hit_damage"), 1.05, 1e-12, "One for All critical hit damage");
        SetContributionValue(upgradedResult, "weapon_damage", "gear_set_concentrated_company", 2.1);
        NoTalentDuplicate(upgradedResult, G43Cases[3].Stats, "gear_talent_one_for_all");
        Near(Stat(upgraded.WithTalentSelection("backpack", "talent", ""), "weapon_damage", ("camaraderie_stacks", "35")), 1.15, 1e-12, "cleared One for All");

        var negotiator = Resolve(SetBuild("gear_set_negotiator_s_dilemma", 4), Context(("negotiator_chd_stacks", "10")));
        Near(negotiator.Calculation.Stats.Of("critical_hit_damage"), 1.2, 1e-12, "Negotiator critical hit damage");
        SetContributionValue(negotiator, "critical_hit_damage", "gear_set_negotiator_s_dilemma", 1);
        foreach (var input in new[] { "camaraderie_stacks", "negotiator_chd_stacks" })
        {
            var negotiatorInput = input == "negotiator_chd_stacks";
            var (setId, stat, baseValue) = negotiatorInput ? ("gear_set_negotiator_s_dilemma", "critical_hit_damage", 0.2) : ("gear_set_concentrated_company", "weapon_damage", 0.1);
            foreach (var value in new[] { "-1", "36", "1.5", "NaN" })
            {
                var invalid = negotiatorInput && value == "36" ? "11" : value;
                var result = Resolve(SetBuild(setId, 4), Context((input, invalid)));
                Near(result.Calculation.Stats.Of(stat), baseValue, 1e-12, $"invalid {input}={invalid}");
                Assert.True(HasDiagnostic(result, "context-value-invalid", input), $"invalid {input}=\"{invalid}\" has no context diagnostic");
            }
        }
    }

    /// <summary>
    /// G43: Umbra's Light bonus needs From the Shadows and the active flag, From the Shadows raises its stack cap to
    /// 100, and invalid stack counts are rejected.
    /// </summary>
    [Fact]
    public void G43UmbraIntoLightEligibilityAndUpgradedStackCap()
    {
        var b = SetBuild("gear_set_umbra_initiative", 4);
        Near(Stat(b, "critical_hit_damage", ("umbra_light_stacks", "50")), 0, 1e-12, "no flag");
        Near(Stat(b, "critical_hit_damage", ("umbra_light_stacks", "50"), ("umbra_light_active", "false")), 0, 1e-12, "flag false");
        var active = Upgrade(b, "chest", "gear_talent_from_the_shadows");
        var result = Resolve(active, Context(("umbra_light_stacks", "100"), ("umbra_light_active", "true")));
        Near(result.Calculation.Stats.Of("critical_hit_damage"), 1.2, 1e-12, "upgraded critical hit damage");
        Near(result.Calculation.Stats.Of("rate_of_fire"), .4, 1e-12, "upgraded rate of fire");
        SetContributionValue(result, "critical_hit_damage", "gear_set_umbra_initiative", 1.2);
        SetContributionValue(result, "rate_of_fire", "gear_set_umbra_initiative", .4);
        NoTalentDuplicate(result, G43Cases[5].Stats, "gear_talent_from_the_shadows");
        var cleared = Stats(active.WithTalentSelection("chest", "talent", ""), Context(("umbra_light_stacks", "100"), ("umbra_light_active", "true")));
        Near(cleared.Of("critical_hit_damage"), .6, 1e-12, "cleared critical hit damage");
        Near(cleared.Of("rate_of_fire"), .2, 1e-12, "cleared rate of fire");
        foreach (var invalid in new[] { "-1", "101", "1.5", "NaN" })
        {
            var bad = Resolve(active, Context(("umbra_light_stacks", invalid), ("umbra_light_active", "true")));
            Near(bad.Calculation.Stats.Of("critical_hit_damage"), 0, 1e-12, $"invalid stacks {invalid}");
            Assert.True(HasDiagnostic(bad, "context-value-invalid", "umbra_light_stacks"), $"invalid umbra_light_stacks=\"{invalid}\" lacks context diagnostic");
        }
    }

    /// <summary>G43: Camaraderie and Negotiator raise the secondary weapon's damage and change no non-damage skill preview.</summary>
    [Fact]
    public void G43GearWideCompositionAndPreviewIsolation()
    {
        var camaraderie = Upgrade(SetBuild("gear_set_concentrated_company", 5), "backpack", "gear_talent_one_for_all")
            .WithActiveSkillSelection("skill_slot_1", "skill_assault_turret");
        var camBase = Resolve(camaraderie, Context(("weapon_slot", "secondary_weapon"), ("camaraderie_stacks", "0")));
        var camActive = Resolve(camaraderie, Context(("weapon_slot", "secondary_weapon"), ("camaraderie_stacks", "35")));
        double baseDamage = camBase.Calculation.Stats.Of("equipped_weapon_damage"), activeDamage = camActive.Calculation.Stats.Of("equipped_weapon_damage");
        Assert.True(baseDamage > 0, "secondary-weapon baseline preview is not configured");
        Assert.True(activeDamage > baseDamage, $"Camaraderie did not increase the secondary-weapon preview: base={baseDamage} active={activeDamage}");
        Assert.True(SameNonDamagePreviews(camActive.ActiveSkillPreviews, camBase.ActiveSkillPreviews), "gear-set weapon damage changed selected skill previews");

        var b = SetBuild("gear_set_negotiator_s_dilemma", 5).WithActiveSkillSelection("skill_slot_1", "skill_assault_turret");
        var baseline = Resolve(b, Context(("weapon_slot", "secondary_weapon"), ("negotiator_chd_stacks", "0")));
        var active = Resolve(b, Context(("weapon_slot", "secondary_weapon"), ("negotiator_chd_stacks", "10")));
        Near(active.Calculation.Stats.Of("critical_hit_damage"), baseline.Calculation.Stats.Of("critical_hit_damage") + 1, 1e-12, "Negotiator critical hit damage");
        Assert.True(SameNonDamagePreviews(active.ActiveSkillPreviews, baseline.ActiveSkillPreviews), "gear-set bonus totals changed selected skill previews");
    }

    /// <summary>Go's g44Context: the case's gate and inputs set active.</summary>
    private static List<(string, string)> G44Context(SetCase tc)
    {
        var context = new List<(string, string)>();
        if (tc.Gate.Length > 0) { context.Add((tc.Gate, "true")); }
        if (tc.Input == "huddle_qualifying_allies") { context.Add((tc.Input, "2")); }
        if (tc.Input == "on_point_stacks")
        {
            context.Add((tc.Input, "8"));
            context.Add(("on_point_magazine_capacity", "12"));
            context.Add(("weapon_slot", "primary_weapon"));
        }
        if (tc.Id == "gear_set_tip_of_the_spear") { context.Add(("aggressive_recon_reload_active", "true")); }
        return context;
    }

    /// <summary>G44: Rigger, Tip of the Spear, Measured Assembly, Breaking Point and Hunter's Fury contribute their four-piece bonus only from four pieces.</summary>
    [Fact]
    public void G44SetFourPieceThresholds()
    {
        foreach (var tc in G44Cases)
        {
            var context = G44Context(tc);
            if (tc.Id == "gear_set_tip_of_the_spear") { context.Add(("signature_moves_active", "true")); }
            // Go set on_point_magazine_capacity again here; g44Context already set it to the same value.
            CheckThresholds(tc, Context([.. context]), fourPiece: true);
        }
    }

    /// <summary>
    /// G44: Rigger's skill damage needs its buff active, Best Buds doubles it on the chest only, and neither changes a
    /// non-damage skill preview.
    /// </summary>
    [Fact]
    public void G44RiggerActiveSkillDamageAndBestBuds()
    {
        var b = SetBuild("gear_set_rigger", 4).WithActiveSkillSelection("skill_slot_1", "skill_assault_turret");
        var baseline = Resolve(b, Context(("rigger_skill_buff_active", "true")));
        Near(baseline.Calculation.Stats.Of("skill_damage"), .25, 1e-12, "active skill damage");
        foreach (var flag in new[] { "", "false", "invalid" })
        {
            var context = flag.Length > 0 ? Context(("rigger_skill_buff_active", flag)) : Context();
            Near(Stats(b, context).Of("skill_damage"), 0, 1e-12, $"rigger_skill_buff_active=\"{flag}\"");
        }
        var upgraded = Upgrade(b, "chest", "gear_talent_best_buds");
        var perfect = Resolve(upgraded, Context(("rigger_skill_buff_active", "true")));
        Near(perfect.Calculation.Stats.Of("skill_damage"), .5, 1e-12, "Best Buds skill damage");
        SetContributionValue(perfect, "skill_damage", "gear_set_rigger", .5);
        NoTalentDuplicate(perfect, G44Cases[0].Stats, "gear_talent_best_buds");
        Assert.True(SameNonDamagePreviews(baseline.ActiveSkillPreviews, perfect.ActiveSkillPreviews), "Rigger Skill Damage total changed selected-skill previews");
        Near(Stat(upgraded.WithTalentSelection("chest", "talent", ""), "skill_damage", ("rigger_skill_buff_active", "true")), .25, 1e-12, "cleared Best Buds");
        Near(Stat(b.WithTalentSelection("backpack", "talent", "gear_talent_best_buds"), "skill_damage", ("rigger_skill_buff_active", "true")), .25, 1e-12, "Best Buds on backpack");
    }

    /// <summary>
    /// G44: Tip of the Spear's signature weapon damage, reload speed and Signature Moves multiplier read independent
    /// assumptions, its upgrades raise the set's own values, and the multiplier composes with Glass Cannon.
    /// </summary>
    [Fact]
    public void G44TipOfSpearIndependentAssumptionsAndPools()
    {
        var b = SetBuild("gear_set_tip_of_the_spear", 5);
        var none = Stats(b);
        Near(none.Of("signature_weapon_damage"), .2, 1e-12, "base signature weapon damage");
        Near(none.Of("reload_speed"), 0, 1e-12, "base reload speed");
        Near(none.Of("weapon_damage_multiplier"), 1, 1e-12, "base multiplier");
        var recon = Stats(b, Context(("aggressive_recon_active", "true")));
        Near(recon.Of("signature_weapon_damage"), .35, 1e-12, "recon signature weapon damage");
        Near(recon.Of("reload_speed"), 0, 1e-12, "recon reload speed");
        var reload = Stats(b, Context(("aggressive_recon_reload_active", "true")));
        Near(reload.Of("signature_weapon_damage"), .2, 1e-12, "reload signature weapon damage");
        Near(reload.Of("reload_speed"), .25, 1e-12, "reload reload speed");
        var upgraded = Resolve(Upgrade(b, "chest", "gear_talent_specialized_destruction"), Context(("aggressive_recon_active", "true")));
        Near(upgraded.Calculation.Stats.Of("signature_weapon_damage"), .5, 1e-12, "Specialized Destruction signature weapon damage");
        SetContributionValue(upgraded, "signature_weapon_damage", "gear_set_tip_of_the_spear", .3);
        NoTalentDuplicate(upgraded, G44Cases[1].Stats, "gear_talent_specialized_destruction");
        var sigMoves = Resolve(Upgrade(b, "backpack", "gear_talent_signature_moves"), Context(("signature_moves_active", "true")));
        Near(sigMoves.Calculation.Stats.Of("weapon_damage_multiplier"), 1.5, 1e-12, "Signature Moves multiplier");
        SetContributionValue(sigMoves, "weapon_damage_multiplier", "gear_set_tip_of_the_spear", .5);
        NoTalentDuplicate(sigMoves, ["weapon_damage_multiplier"], "gear_talent_signature_moves");
        foreach (var key in new[] { "aggressive_recon_active", "aggressive_recon_reload_active", "signature_moves_active" })
        {
            Near(Stat(b, "weapon_damage_multiplier", (key, "invalid")), 1, 1e-12, $"{key}=invalid");
        }

        var composition = Upgrade(SetBuild("gear_set_tip_of_the_spear", 5)
            .WithEquipment("chest", "chest_n_5_11_tactical_chest").WithTalentSelection("chest", "talent", "gear_talent_glass_cannon"), "backpack", "gear_talent_signature_moves");
        var combined = Stats(composition, Context(("weapon_slot", "primary_weapon"), ("signature_moves_active", "true")));
        Near(combined.Of("weapon_damage_multiplier"), 1.25 * 1.5, 1e-12, "composed multiplier");
        Assert.True(combined.Of("equipped_weapon_damage") > 0, "Tip of the Spear amplified weapon preview is not configured");
    }

    /// <summary>
    /// G44: Huddle adds a skill tier per qualifying ally, the preview's effective tier caps at 6, and invalid ally
    /// counts are rejected.
    /// </summary>
    [Fact]
    public void G44HuddleSkillTierAndCappedPreviews()
    {
        var b = SetBuild("gear_set_measured_assembly", 4).WithActiveSkillSelection("skill_slot_1", "skill_assault_turret");
        var baseTier = Stats(b).Of("skill_tier");
        var three = Resolve(b, Context(("huddle_qualifying_allies", "3")));
        Near(three.Calculation.Stats.Of("skill_tier"), baseTier + 3, 1e-12, "three allies");
        Assert.True(PreviewValue(three, "skill_slot_1", "effective_skill_tier") == 3, $"Huddle effective skill tier={PreviewValue(three, "skill_slot_1", "effective_skill_tier")}, want 3");
        var seven = Resolve(b.WithActiveSkillSelection("skill_slot_1", "skill_reviver_hive"), Context(("huddle_qualifying_allies", "7")));
        Near(seven.Calculation.Stats.Of("skill_tier"), baseTier + 7, 1e-12, "seven allies");
        Assert.True(PreviewValue(seven, "skill_slot_1", "effective_skill_tier") == 6, $"Huddle effective skill tier={PreviewValue(seven, "skill_slot_1", "effective_skill_tier")}, want 6 cap");
        Assert.True(PreviewValue(seven, "skill_slot_1", "revive_charges") == 7, $"Reviver Hive charge preview={PreviewValue(seven, "skill_slot_1", "revive_charges")}, want 7");
        foreach (var invalid in new[] { "-1", "8", "1.5", "NaN" })
        {
            var bad = Resolve(b, Context(("huddle_qualifying_allies", invalid)));
            Near(bad.Calculation.Stats.Of("skill_tier"), baseTier, 1e-12, $"invalid ally count {invalid}");
            Assert.True(HasDiagnostic(bad, "context-value-invalid", "huddle_qualifying_allies"), $"invalid Huddle count \"{invalid}\" lacks input diagnostic");
        }
    }

    /// <summary>
    /// G44: On Point's stacks cap at the equipped magazine, need the active flag (except its base weapon handling),
    /// Point of Honor raises the damage, and a non-rifle primary gets nothing.
    /// </summary>
    [Fact]
    public void G44OnPointEquippedMagazineCapAndWeaponGate()
    {
        var b = SetBuild("gear_set_breaking_point", 4);
        var context = Context(("weapon_slot", "primary_weapon"), ("on_point_active", "true"), ("on_point_stacks", "200"), ("on_point_magazine_capacity", "12"));
        var active = Resolve(b, context);
        Near(active.Calculation.Stats.Of("weapon_damage"), 1.2, 1e-12, "active weapon damage");
        Near(active.Calculation.Stats.Of("weapon_handling"), .9, 1e-12, "active weapon handling");
        SetContributionValue(active, "weapon_damage", "gear_set_breaking_point", 1.2);
        SetContributionValue(active, "weapon_handling", "gear_set_breaking_point", .6);
        var upgraded = Upgrade(b, "backpack", "gear_talent_point_of_honor");
        var perfect = Resolve(upgraded, context);
        Near(perfect.Calculation.Stats.Of("weapon_damage"), 2.7, 1e-12, "Point of Honor weapon damage");
        Near(perfect.Calculation.Stats.Of("weapon_handling"), .9, 1e-12, "Point of Honor weapon handling");
        NoTalentDuplicate(perfect, G44Cases[3].Stats, "gear_talent_point_of_honor");
        Near(Stats(upgraded.WithTalentSelection("backpack", "talent", ""), context).Of("weapon_damage"), 1.2, 1e-12, "cleared Point of Honor");

        foreach (var flag in new[] { "", "false", "invalid" })
        {
            var noGate = flag.Length > 0
                ? Context(("weapon_slot", "primary_weapon"), ("on_point_active", flag), ("on_point_stacks", "200"), ("on_point_magazine_capacity", "12"))
                : Context(("weapon_slot", "primary_weapon"), ("on_point_stacks", "200"), ("on_point_magazine_capacity", "12"));
            var result = Stats(upgraded, noGate);
            Near(result.Of("weapon_damage"), 0, 1e-12, $"on_point_active=\"{flag}\" weapon damage");
            Near(result.Of("weapon_handling"), .3, 1e-12, $"on_point_active=\"{flag}\" weapon handling");
        }
        var capacityCap = Stats(b, Context(("weapon_slot", "primary_weapon"), ("on_point_active", "true"), ("on_point_stacks", "5"), ("on_point_magazine_capacity", "3")));
        Near(capacityCap.Of("weapon_damage"), .2, 1e-12, "magazine-capped weapon damage");
        Near(capacityCap.Of("weapon_handling"), .4, 1e-12, "magazine-capped weapon handling");
        foreach (var value in new[] { "201", "1.5" })
        {
            var bad = Resolve(upgraded, Context(("weapon_slot", "primary_weapon"), ("on_point_active", "true"), ("on_point_stacks", value), ("on_point_magazine_capacity", "12")));
            Assert.True(HasDiagnostic(bad, "context-value-invalid", "on_point_stacks"), $"invalid on_point_stacks=\"{value}\" lacks context diagnostic");
        }
        Near(Stats(b.WithEquipment("primary_weapon", "primary_weapon_acr"), context).Of("weapon_damage"), 0, 1e-12, "assault rifle primary");
    }

    /// <summary>G44: Hunter's Fury's multiplier needs a target within 15 m and composes with Glass Cannon.</summary>
    [Fact]
    public void G44HunterFuryProximityOnlyAndMultiplierComposition()
    {
        var b = SetBuild("gear_set_hunter_s_fury", 4);
        foreach (var value in new[] { "", "false", "invalid" })
        {
            var context = value.Length > 0 ? Context(("hunter_fury_target_within_15m", value)) : Context();
            Near(Stats(b, context).Of("weapon_damage_multiplier"), 1, 1e-12, $"hunter_fury_target_within_15m=\"{value}\"");
        }
        var active = Resolve(b, Context(("hunter_fury_target_within_15m", "true")));
        Near(active.Calculation.Stats.Of("weapon_damage_multiplier"), 1.2, 1e-12, "active multiplier");
        SetContributionValue(active, "weapon_damage_multiplier", "gear_set_hunter_s_fury", .2);
        var composition = SetBuild("gear_set_hunter_s_fury", 5)
            .WithEquipment("chest", "chest_n_5_11_tactical_chest").WithTalentSelection("chest", "talent", "gear_talent_glass_cannon");
        Near(Stat(composition, "weapon_damage_multiplier", ("weapon_slot", "primary_weapon"), ("hunter_fury_target_within_15m", "true")), 1.25 * 1.2, 1e-12, "composed multiplier");
    }

    /// <summary>G44: view assumptions change neither the build nor any non-damage skill preview.</summary>
    [Fact]
    public void G44ViewAssumptionsDoNotChangeBuildOrUnrelatedSkillPreviews()
    {
        var b = SetBuild("gear_set_rigger", 4).WithActiveSkillSelection("skill_slot_1", "skill_assault_turret");
        var baseline = Resolve(b);
        var active = Resolve(b, Context(("rigger_skill_buff_active", "true")));
        Assert.True(SameNonDamagePreviews(baseline.ActiveSkillPreviews, active.ActiveSkillPreviews), "Rigger skill-damage bonus changed an unsupported absolute skill output");
        Assert.True(b.TalentSelections.IsEmpty, $"default build unexpectedly selected talent upgrades: {string.Join(", ", b.TalentSelections.Keys)}");
    }
}
