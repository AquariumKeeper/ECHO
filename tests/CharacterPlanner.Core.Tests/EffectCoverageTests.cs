using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Core.Tests;

public sealed class EffectCoverageTests
{
    [Fact]
    public void AKeyASetTalentDrivesIsntReportedAsAnAssumption()
    {
        // Veer Override reads Apex Engine's stacks, which fights build; only its mark is assumed.
        var channels = Committed("gear_talent_veer_override").Channels;
        Assert.Contains("apex_engine_bonus_stacks", channels.Single(channel => channel.Kind == EffectCoverage.DrivenConditions).Sources);
        Assert.DoesNotContain("apex_engine_bonus_stacks", channels.Single(channel => channel.Kind == EffectCoverage.AssumptionConditions).Sources);
    }

    [Fact]
    public void ACountedKeySaysWhichFightsCountIt()
    {
        var pack = GameMaths.Division.Compiled.Pack;
        var channel = Committed("weapon_talent_incessant_chatter").Channels.Single(channel => channel.Kind == EffectCoverage.DrivenConditions);
        Assert.Contains("chatterbox_stacks", channel.Sources);
        Assert.Contains("several enemies at once", channel.Note, StringComparison.Ordinal);
        // A key with a reading as well is driven in every fight, so it's not one only some fights count.
        const string Edge = "gear_talent_bleeding_edge";
        var both = pack with { Simulation = pack.Simulation with { Triggers = [.. pack.Simulation.Triggers, new() { Talent = Edge, Key = "bleeding_edge_bleeding_enemies", Kind = "count", On = "second", Status = "bleeding", Max = 5 }] } };
        Assert.DoesNotContain("several enemies at once", Assert.Single(Committed(Edge, pack: both).Channels, entry => entry.Kind == EffectCoverage.DrivenConditions).Note, StringComparison.Ordinal);
        // Another talent's count on a key says nothing of the rules this one has on it.
        var other = pack.Talents.First(candidate => candidate.ID != "gear_talent_apex_engine");
        var elsewhere = pack with { Simulation = pack.Simulation with { Triggers = [.. pack.Simulation.Triggers, new() { Talent = other.ID, Key = "apex_engine_bonus_stacks", Kind = "count", Max = 5 }] } };
        Assert.DoesNotContain("several enemies at once", Assert.Single(Committed("gear_talent_veer_override", pack: elsewhere).Channels, entry => entry.Kind == EffectCoverage.DrivenConditions).Note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(4, true)]
    public void ASetTalentWithAFightRuleIsActiveAtTheSetsTopBonus(int pieces, bool active)
    {
        // Apex Engine has a stacks rule, so coverage says when a fight runs it, not just that the set is worn.
        var pack = GameMaths.Division.Compiled.Pack;
        var build = BuildSnapshot.Create(pack);
        foreach (var id in new[] { "mask_zenith_mask", "chest_apogee_chest_plate", "backpack_wolf_pack", "gloves_pinnacle_gloves" }.Take(pieces))
        { build = build.WithEquipment(pack.Items.Single(item => item.ID == id).Slot, id); }
        var row = Assert.Single(EffectCoverage.Describe(pack, build, ImmutableDictionary<string, string>.Empty).Talents, talent => talent.Talent == "gear_talent_apex_engine");
        Assert.Equal(active, row.Active);
        Assert.Contains(row.Channels, channel => channel.Kind == "set-bonuses" && channel.Note.Contains("once the set's top bonus applies", StringComparison.Ordinal));
    }

    [Fact]
    public void ReceivedPartyCoverageUsesTheSameSelectedSnapshotAsTheResolver()
    {
        var pack = TestPacks.Tiny;
        var build = BuildSnapshot.Create(pack) with
        {
            Party = [new("Giver", "", 100,
            [new("party_boost", ImmutableDictionary<string, double>.Empty.Add("weapon_damage", 0.25), Active: true)])]
        };
        var report = EffectCoverage.Describe(pack, build, Context);
        var buff = Assert.Single(report.Party);
        Assert.Equal("Giver", buff.Member);
        Assert.Equal("Party", buff.Effect.Source);
        Assert.Equal(0.25, Assert.Single(buff.Effect.Modifiers).Value);
        Assert.Empty(report.Talents);
    }

    [Theory]
    [InlineData("gear_talent_resourceful", "set-piece-credit", "complete")]
    [InlineData("gear_talent_abridged", "talent-copy", "complete")]
    // T2's acceptance: Tardigrade (Ablative Nano-Plating) reports its party projection and the ally cooldown it omits, and
    // Aces & Eights (Dead Man's Hand) its set preview and the card simulation it omits.
    [InlineData("gear_talent_ablative_nano_plating", "party-projection", "partial")]
    [InlineData("gear_talent_dead_man_s_hand", "set-bonuses", "partial")]
    // Resolved has had a combat rule since the Resolved candidate (R36), Perfect Tag Team since R76 and Perfect Calculated since R80, partly;
    // Perfect Efficient since R85; Bewildered has a redirect since R96, partly.
    [InlineData("gear_talent_resolved", "authored-combat-rules", "complete")]
    [InlineData("gear_talent_perfect_tag_team", "authored-combat-rules", "partial")]
    [InlineData("gear_talent_perfect_calculated", "authored-combat-rules", "partial")]
    [InlineData("gear_talent_bewildered", "talent-redirect", "partial")]
    public void CommittedPackNamedEffectsHaveEvidenceBackedChannels(string id, string channel, string audit)
    {
        var pack = GameMaths.Division.Compiled.Pack;
        var item = pack.Items.FirstOrDefault(item => item.TalentSlots.Any(slot => slot.Choices.Contains(id)));
        var build = BuildSnapshot.Create(pack);
        if (item is not null)
        {
            var slot = item.TalentSlots.First(slot => slot.Choices.Contains(id));
            build = build.WithEquipment(item.Slot, item.ID).WithTalentSelection(item.Slot, slot.ID, id);
        }
        else
        {
            var talent = pack.Talents.Single(talent => talent.ID == id);
            var set = pack.Sets.First(set => talent.ModeledBySets.Contains(set.ID));
            item = pack.Items.First(item => set.Items.Contains(item.ID));
            build = build.WithEquipment(item.Slot, item.ID);
        }
        var row = EffectCoverage.Describe(pack, build, Context).Talents.Single(talent => talent.Talent == id);
        Assert.Equal(audit, row.AuditStatus);
        // A partial talent's row names what the pack leaves out, so its number doesn't read as a full evaluation.
        if (audit == "partial")
        {
            Assert.Contains(EffectCoverage.SourceAudit, row.Channels.Select(entry => entry.Kind));
            Assert.All(pack.Talents.Single(talent => talent.ID == id).Coverage!.Omits, omitted => Assert.Contains(omitted, row.Description, StringComparison.Ordinal));
        }
        // The committed pack's R35 audit marks the absent ones unmodeled: nothing is modeled, and the audit says what is missing.
        if (channel.Length == 0)
        {
            Assert.False(row.Modeled);
            Assert.Equal("unmodeled", row.AuditStatus);
            Assert.Equal([EffectCoverage.SourceAudit], row.Channels.Select(entry => entry.Kind));
            Assert.StartsWith(EffectCoverage.NoModel + " Source audit: the pack does not model the mechanic.", row.Description, StringComparison.Ordinal);
        }
        else { Assert.Contains(row.Channels, entry => entry.Kind == channel); }
    }

    private static readonly ImmutableDictionary<string, string> Context = ImmutableDictionary<string, string>.Empty.Add(GamePack.WeaponSlotSelector, "primary_weapon");
    private static BuildSnapshot CopyBuild => BuildSnapshot.Create(TestPacks.Tiny).WithEquipment("mask", "mask_a")
        .WithEquipment("primary_weapon", "rifle_a").WithEquipment("secondary_weapon", "rifle_b")
        .WithTalentSelection("mask", "talent", "talent_copier").WithTalentSelection("secondary_weapon", "talent", "talent_killer");

    [Fact]
    public void IndirectChannelsRemainDistinctFromDirectModifiers()
    {
        var pack = TestPacks.Tiny;
        var build = BuildSnapshot.Create(pack).WithEquipment("mask", "mask_a").WithTalentSelection("mask", "talent", "talent_party");
        var report = EffectCoverage.Describe(pack, build, Context);
        var row = Assert.Single(report.Talents);
        Assert.Equal(["set-bonuses", "set-piece-credit", "party-projection"], row.Channels.Select(channel => channel.Kind));
        Assert.Equal(["set_x"], row.Channels[0].Sources);
        Assert.Contains("other recipients", row.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("No modeled effect", row.Description, StringComparison.Ordinal);
        Assert.Contains("not measured", report.InputBasis, StringComparison.Ordinal);
        Assert.Equal("0", report.Inputs["stacks"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopyCoverageUsesEngineEligibilityAndPreservesSourceIdentity(bool excluded)
    {
        var pack = TestPacks.Tiny;
        if (excluded) { pack = pack with { Items = [.. pack.Items.Select(item => item.ID == "rifle_b" ? item with { Tags = [.. item.Tags, "exotic"] } : item)] }; }
        var build = CopyBuild;
        var copies = TalentCopySelection.Sources(pack, build, LoadoutResolver.Resolve(pack, build), Context);
        var row = EffectCoverage.Describe(pack, build, Context).Talents.Single(talent => talent.Talent == "talent_copier");
        var channel = Assert.Single(row.Channels);
        Assert.Equal(copies.Select(copy => copy.Talent.ID), channel.Sources);
        Assert.Equal(excluded, channel.Sources.IsEmpty);
        Assert.Contains(excluded ? "No eligible source" : "Eligible source: Killer", channel.Note, StringComparison.Ordinal);
        Assert.Contains("not modeled", channel.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyChannelsDoNotPresentAZeroAsFullEvaluation()
    {
        var pack = TestPacks.Tiny with
        {
            Talents = [.. TestPacks.Tiny.Talents.Select(talent => talent.ID == "talent_party" ? talent with
            { Modifiers = [], ModeledBySets = [], SetPieceCredit = 0, AllyEffects = [] } : talent)]
        };
        var build = BuildSnapshot.Create(pack).WithEquipment("mask", "mask_a").WithTalentSelection("mask", "talent", "talent_party");
        var report = EffectCoverage.Describe(pack, build, Context);
        Assert.Empty(Assert.Single(report.Talents).Channels);
        Assert.Contains("does not evaluate", report.Talents[0].Description, StringComparison.Ordinal);
        Assert.Contains("Party", EffectCoverage.ComparisonDisclosure([report]), StringComparison.Ordinal);
    }

    [Fact]
    public void AuthoredRulesAndInactiveWeaponsAreReportedWithoutClaimingRuntimeSupport()
    {
        var pack = TestPacks.Tiny with
        {
            Simulation = TestPacks.Tiny.Simulation with
            { Triggers = [new() { Talent = "talent_killer", Key = "test_rule" }] }
        };
        var report = EffectCoverage.Describe(pack, CopyBuild, Context);
        var row = report.Talents.Single(talent => talent.Talent == "talent_killer");
        Assert.False(row.Active);
        Assert.Contains(row.Channels, channel => channel.Kind == "authored-combat-rules" && channel.Sources.Contains("test_rule"));
        Assert.Contains("handler restrictions", row.Description, StringComparison.Ordinal);
        Assert.Contains("holstered", row.Description, StringComparison.Ordinal);
        Assert.Equal("primary_weapon", report.Inputs[GamePack.WeaponSlotSelector]);
    }
    private static TalentEffectCoverage Committed(string id, ImmutableDictionary<string, string>? context = null, GamePack? pack = null)
    {
        pack ??= GameMaths.Division.Compiled.Pack;
        var item = pack.Items.First(item => item.TalentSlots.Any(slot => slot.Choices.Contains(id)));
        var slot = item.TalentSlots.First(slot => slot.Choices.Contains(id));
        var build = BuildSnapshot.Create(pack).WithEquipment(item.Slot, item.ID).WithTalentSelection(item.Slot, slot.ID, id);
        return EffectCoverage.Describe(pack, build, (context ?? Context).SetItem(GamePack.WeaponSlotSelector, pack.Slots.Single(definition => definition.ID == item.Slot).Weapon ? item.Slot : "primary_weapon"))
            .Talents.Single(talent => talent.Talent == id);
    }

    [Theory]
    [InlineData("gear_talent_glass_cannon", new[] { EffectCoverage.StaticModifiers })]
    [InlineData("gear_talent_headhunter", new[] { EffectCoverage.StaticModifiers, EffectCoverage.ExpectedValueModifiers })]
    [InlineData("weapon_talent_killer", new[] { EffectCoverage.DrivenConditions, "authored-combat-rules" })]
    [InlineData("gear_talent_focus", new[] { EffectCoverage.AssumptionConditions })]
    // Clutch's previews stay assumption-driven; its fight repairs are pack rules since the R43 candidate.
    [InlineData("gear_talent_clutch", new[] { EffectCoverage.StaticModifiers, EffectCoverage.AssumptionConditions, "authored-combat-rules" })]
    public void ModifiersAreClassifiedByWhatDecidesWhetherTheyApply(string id, string[] kinds)
    {
        // Every committed talent also carries the R35 source audit, reported first.
        var channels = Committed(id).Channels.Select(channel => channel.Kind).ToArray();
        Assert.Equal(EffectCoverage.SourceAudit, channels[0]);
        Assert.Equal(kinds, channels[1..]);
    }

    [Fact]
    public void ConditionNotesNameTheInputWhatItNeedsAndItsCurrentValue()
    {
        var off = Committed("weapon_talent_killer");
        var driven = Assert.Single(off.Channels, channel => channel.Kind == EffectCoverage.DrivenConditions);
        Assert.Equal(["killer_active"], driven.Sources);
        Assert.Contains("(needs true, now unset)", driven.Note, StringComparison.Ordinal);
        var on = Committed("weapon_talent_killer", Context.Add("killer_active", "true"));
        Assert.Contains("(needs true, now true)", on.Channels.Single(channel => channel.Kind == EffectCoverage.DrivenConditions).Note, StringComparison.Ordinal);
        var focus = Committed("gear_talent_focus", Context.Add("focus_stacks", "4"));
        Assert.Contains("now 4)", focus.Channels.Single(channel => channel.Kind == EffectCoverage.AssumptionConditions).Note, StringComparison.Ordinal);
    }

    [Fact]
    public void ComparisonsDiscloseAssumptionDependentTalents()
    {
        var focus = new EffectCoverageReport([Committed("gear_talent_focus"), Committed("weapon_talent_killer")], ImmutableDictionary<string, string>.Empty);
        var cannon = new EffectCoverageReport([Committed("gear_talent_glass_cannon")], ImmutableDictionary<string, string>.Empty);
        Assert.Contains("(talent-trigger simulations drive some): Focus, Killer.", EffectCoverage.ComparisonDisclosure([focus, cannon]), StringComparison.Ordinal);
        Assert.DoesNotContain("Depends on chosen assumptions", EffectCoverage.ComparisonDisclosure([cannon]), StringComparison.Ordinal);
    }
}
