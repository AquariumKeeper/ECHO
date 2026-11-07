using System.Collections.Immutable;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using static CharacterPlanner.Application.Tests.Commands.Division;
using Codes = CharacterPlanner.Application.Commands.OperationErrorCodes;

namespace CharacterPlanner.Application.Tests.Commands;

public sealed class OperationApplierTests
{
    private sealed record Unknown : BuildOperation
    {
        public override string Kind => "unknown";
        public override string Label => "unknown";
    }

    [Fact]
    public void TheFrozenInvalidOperationIsRejectedWithItsReason()
    {
        var fixture = Json("errors", "invalid-operation.json");
        var start = Decode(Json("builds", "striker-rifle", "apply.json").GetProperty("response").GetProperty("document"));
        var error = Rejected(start, [.. Read(fixture.GetProperty("stdin").GetString()!)]);
        Assert.Equal((1, "equipment", Codes.ItemWrongSlot), (error.Operation, error.Kind, error.Code));
        // The frozen wording: item "primary_weapon_police_m4" cannot be equipped in primary slot "chest".
        Assert.Contains("\"primary_weapon_police_m4\" cannot be equipped in slot \"chest\"", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABatchIsAppliedWhollyOrNotAtAll()
    {
        var state = State(Geared());
        var error = Rejected(state,
            new NameOperation("Half"),
            new AttributeOperation("mask", "core_1", "armor_gear_core"),
            new ContextOperation("enemy_target", 3),
            new EquipmentOperation("gloves", "no_such_gloves"));
        Assert.Equal((4, Codes.UnknownItem), (error.Operation, error.Code));
        Assert.Equal("operation 4 (equipment): unknown item \"no_such_gloves\"", error.Message);

        var outcome = Apply(state);
        Assert.True(outcome.Succeeded);
        Assert.Same(state, outcome.State);
        Assert.Empty(outcome.Changes);
    }

    public static TheoryData<string, BuildOperation> Rejections => new()
    {
        { Codes.UnknownSlot, new EquipmentOperation("hat", "mask_striker_s_battlegear_mask") },
        { Codes.UnknownItem, new EquipmentOperation("mask", "mask_nothing") },
        { Codes.ItemWrongSlot, new EquipmentOperation("chest", "mask_striker_s_battlegear_mask") },
        { Codes.SlotEmpty, new AttributeOperation("gloves", "core_1", "armor_gear_core") },
        { Codes.UnknownAttributeSlot, new AttributeOperation("mask", "core_9", "armor_gear_core") },
        { Codes.UnknownAttribute, new AttributeOperation("mask", "core_1", "luck") },
        { Codes.AttributeNotAllowed, new AttributeOperation("mask", "core_1", "damage_to_armor_weapon_minor") },
        { Codes.AttributeRollInvalid, new AttributeOperation("mask", "core_1", "armor_gear_core", 170001) },
        { Codes.AttributeRollInvalid, new AttributeOperation("mask", "minor_1", "weapon_handling_gear_minor", 0.0805) },
        { Codes.UnknownModSlot, new ModOperation("mask", "optics", "critical_hit_chance_gear_mod") },
        { Codes.UnknownMod, new ModOperation("mask", "mod_1", "lucky_charm") },
        { Codes.ModNotAllowed, new ModOperation("mask", "mod_1", "weapon_mod_optics_acog_scope_4x") },
        { Codes.ModUnselected, new ModRollOperation("mask", "mod_1") },
        { Codes.UnknownTalentSlot, new TalentOperation("mask", "talent", "gear_talent_press_the_advantage") },
        { Codes.UnknownTalent, new TalentOperation("chest", "talent", "luck") },
        { Codes.TalentNotAllowed, new TalentOperation("chest", "talent", "weapon_talent_breadbasket") },
        { Codes.UnknownSkillSlot, new ActiveSkillOperation("skill_slot_3", "skill_assault_turret") },
        { Codes.UnknownSkill, new ActiveSkillOperation("skill_slot_2", "skill_laser") },
        { Codes.SkillUnselected, new SkillUpgradeOperation("skill_slot_2", 1) },
        { Codes.SkillUpgradeRankInvalid, new SkillUpgradeOperation("skill_slot_1", 31) },
        { Codes.UnknownSkillModSlot, new SkillModOperation("skill_slot_1", "barrel", "magnetic_rail") },
        { Codes.UnknownSkillMod, new SkillModOperation("skill_slot_1", "firing_mechanism", "ammo_box") },
        { Codes.SkillModRollInvalid, new SkillModOperation("skill_slot_1", "firing_mechanism", "magnetic_rail", 0.06) },
        { Codes.UnknownSpecialization, new SpecializationOperation("bard") },
        { Codes.UnknownSpecializationNode, new SpecializationNodeOperation("technician_e_m_i_tier_1", true) },
        { Codes.UnknownAccountUpgrade, new AccountUpgradeOperation("watch_luck", 1) },
        { Codes.AccountUpgradeRankInvalid, new AccountUpgradeOperation("watch_critical_hit_chance", 51) },
        { Codes.AccountUpgradeRankInvalid, new AccountUpgradeOperation("watch_critical_hit_chance", -1) },
        { Codes.ItemNotEquipped, new EquipmentUpgradeOperation("mask", "mask_catharsis", 1) },
        { Codes.UnknownItem, new EquipmentUpgradeOperation("mask", "mask_nothing", 1) },
        { Codes.EquipmentUpgradeRankInvalid, new EquipmentUpgradeOperation("mask", "mask_striker_s_battlegear_mask", 31) },
        { Codes.UnknownAugment, new PrototypeOperation("chest", "augment_luck") },
        { Codes.PrototypeLevelInvalid, new PrototypeOperation("chest", "augment_echo", 11) },
        { Codes.PrototypeLevelInvalid, new PrototypeOperation("chest", "augment_echo", 0) },
        { Codes.PrototypeLevelWithoutAugment, new PrototypeOperation("chest", Level: 5) },
        { Codes.SlotEmpty, new PrototypeOperation("gloves") },
        { Codes.NameBlank, new NameOperation("  ") },
        { Codes.ClearWithValue, new EquipmentOperation("mask", "mask_catharsis") { Clear = true } },
        { Codes.ClearWithValue, new ModRollOperation("mask", "mod_1", 0.05) { Clear = true } },
        { Codes.KindUnsupported, new Unknown() }
    };

    [Theory]
    [MemberData(nameof(Rejections))]
    public void RejectsOperationsThePackOrStateDoesNotAllow(string code, BuildOperation operation)
    {
        var error = Rejected(Geared(), operation);
        Assert.Equal((1, operation.Kind, code), (error.Operation, error.Kind, error.Code));
    }

    [Fact]
    public void NodesNeedASpecializationAndPrototypeNeedsAnEligibleItem()
    {
        Assert.Equal(Codes.SpecializationUnselected, Rejected(Empty(), new SpecializationNodeOperation("gunner_e_m_i_tier_1", true)).Code);
        var exotic = Applied(Empty(), new EquipmentOperation("mask", "mask_catharsis"));
        Assert.Equal(Codes.PrototypeNotEligible, Rejected(exotic, new PrototypeOperation("mask")).Code);
    }

    [Fact]
    public void EquippingFillsFixedChoicesAtTheHighestRoll()
    {
        var outcome = Apply(Empty(), new EquipmentOperation("mask", "mask_catharsis"));
        Assert.True(outcome.Succeeded);
        var build = outcome.State.Build;
        var item = Rules.Items.Single(item => item.ID == "mask_catharsis");
        foreach (var slot in item.AttributeSlots.Where(slot => slot.Choices.Length == 1))
        {
            var attribute = Rules.Attributes.Single(attribute => attribute.ID == slot.Choices[0].AttributeID);
            Assert.Equal(new AttributeRoll(attribute.ID, slot.Choices[0].Max ?? attribute.Max), build.AttributeRolls["mask"][slot.ID]);
        }
        Assert.Equal(item.TalentSlots.Single().Choices.Single(), build.TalentSelections["mask"]["talent"]);
        Assert.Equal(["attributeRolls.mask.core_1", "attributeRolls.mask.minor_1", "attributeRolls.mask.minor_2", "equipment.mask", "talentSelections.mask.talent"],
            outcome.Changes.Single().Set);
    }

    [Fact]
    public void OmittedRollsAreTheHighestLegalRollAndExplicitRollsAreKept()
    {
        var build = Applied(Geared(),
            new AttributeOperation("mask", "core_1", "armor_gear_core"),
            new AttributeOperation("mask", "minor_1", "weapon_handling_gear_minor", 0.05),
            new ModOperation("mask", "mod_1", "critical_hit_chance_gear_mod"),
            new SkillModOperation("skill_slot_1", "firing_mechanism", "magnetic_rail"),
            new SkillModOperation("skill_slot_1", "housing", "ammo_box", 0.03));
        Assert.Equal(new AttributeRoll("armor_gear_core", 170000), build.AttributeRolls["mask"]["core_1"]);
        Assert.Equal(new AttributeRoll("weapon_handling_gear_minor", 0.05), build.AttributeRolls["mask"]["minor_1"]);
        Assert.Equal(0.06, build.ModRolls["mask"]["mod_1"]);
        Assert.Equal(new SkillModRoll("magnetic_rail", 0.05), build.SkillMods["skill_slot_1"].Mods["firing_mechanism"]);
        Assert.Equal(new SkillModRoll("ammo_box", 0.03), build.SkillMods["skill_slot_1"].Mods["housing"]);

        build = Applied(build, new ModRollOperation("mask", "mod_1", 0.03), new ModOperation("mask", "mod_1", "critical_hit_chance_gear_mod"));
        Assert.Equal(0.03, build.ModRolls["mask"]["mod_1"]);
        build = Applied(build, new ModRollOperation("mask", "mod_1"));
        Assert.Equal(0.06, build.ModRolls["mask"]["mod_1"]);
        Assert.Equal(Codes.ModRollInvalid, Rejected(build, new ModRollOperation("mask", "mod_1", 0.07)).Code);
    }

    [Fact]
    public void PrototypeRaisesTheHighestRollAndMovesRollsIntoItsRange()
    {
        var build = Applied(Geared(), new AttributeOperation("chest", "core_1", "armor_gear_core", 100000));
        var outcome = Apply(build, new PrototypeOperation("chest", "augment_echo"));
        Assert.True(outcome.Succeeded);
        build = outcome.State.Build;
        Assert.Equal(new PrototypeSelection("chest_striker_s_battlegear_chest", "augment_echo", 10), build.Prototypes["chest"]);
        // The Prototype range starts at the normal maximum.
        Assert.Equal(170000, build.AttributeRolls["chest"]["core_1"].Value);
        Assert.Equal(["attributeRolls.chest.core_1", "prototypes.chest.augment", "prototypes.chest.itemId", "prototypes.chest.level"], outcome.Changes.Single().Set);

        build = Applied(build, new AttributeOperation("chest", "core_1", "armor_gear_core"));
        Assert.Equal(255000, build.AttributeRolls["chest"]["core_1"].Value);
        outcome = Apply(build, PrototypeOperation.Cleared("chest"));
        Assert.Equal(170000, outcome.State.Build.AttributeRolls["chest"]["core_1"].Value);
        Assert.Empty(outcome.State.Build.Prototypes);
        Assert.Equal(["attributeRolls.chest.core_1"], outcome.Changes.Single().Set);
        Assert.Equal(["prototypes.chest.augment", "prototypes.chest.itemId", "prototypes.chest.level"], outcome.Changes.Single().Cleared);
    }

    [Fact]
    public void ChangingAnItemSkillOrSpecializationClearsItsStaleConfiguration()
    {
        var build = Applied(Geared(),
            new AttributeOperation("mask", "core_1", "armor_gear_core"),
            new ModOperation("mask", "mod_1", "critical_hit_chance_gear_mod"),
            new EquipmentUpgradeOperation("mask", "mask_striker_s_battlegear_mask", 5),
            new SkillUpgradeOperation("skill_slot_1", 3),
            new SkillModOperation("skill_slot_1", "housing", "ammo_box"),
            new SpecializationNodeOperation("gunner_e_m_i_tier_1", true));
        var outcome = Apply(build,
            new EquipmentOperation("mask", "mask_catharsis"),
            new ActiveSkillOperation("skill_slot_1", "skill_striker_drone"),
            new SpecializationOperation("technician"));
        Assert.True(outcome.Succeeded);
        // Catharsis's fixed core is the same roll as before, so only the rest of the old configuration changes.
        Assert.Equal(["equipmentUpgrades.mask.itemId", "equipmentUpgrades.mask.rank", "modRolls.mask.mod_1", "modSelections.mask.mod_1"], outcome.Changes[0].Cleared);
        Assert.Null(outcome.State.Build.EquipmentUpgrades.GetValueOrDefault("mask"));
        Assert.Equal(["skillMods.skill_slot_1.mods", "skillMods.skill_slot_1.skillId", "skillUpgrades.skill_slot_1.rank", "skillUpgrades.skill_slot_1.skillId"],
            outcome.Changes[1].Cleared);
        Assert.Equal(["specializationNodeSelections"], outcome.Changes[2].Cleared);
        Assert.Equal(["specializationID"], outcome.Changes[2].Set);

        // Reselecting the same item keeps its configuration.
        var same = Apply(build, new EquipmentOperation("mask", "mask_striker_s_battlegear_mask"));
        Assert.Equal(build, same.State.Build);
        Assert.Empty(same.Changes.Single().Set);
    }

    [Fact]
    public void ClearsRemoveWhatTheyName()
    {
        var build = Applied(Geared(),
            new ModOperation("mask", "mod_1", "critical_hit_chance_gear_mod"),
            new AttributeOperation("mask", "core_1", "armor_gear_core"),
            new SkillUpgradeOperation("skill_slot_1", 3),
            new AccountUpgradeOperation("shd_level", 10));
        var outcome = Apply(build,
            ModRollOperation.Cleared("mask", "mod_1"),
            AttributeOperation.Cleared("mask", "core_1"),
            TalentOperation.Cleared("chest", "talent"),
            SkillUpgradeOperation.Cleared("skill_slot_1"),
            new AccountUpgradeOperation("shd_level", 0),
            SpecializationOperation.Cleared(),
            ActiveSkillOperation.Cleared("skill_slot_1"),
            EquipmentOperation.Cleared("mask"));
        Assert.True(outcome.Succeeded, string.Join(", ", outcome.Errors));
        Assert.Equal<string[]>(
        [
            ["modRolls.mask.mod_1"], ["attributeRolls.mask.core_1"], ["talentSelections.chest.talent"],
            ["skillUpgrades.skill_slot_1.rank", "skillUpgrades.skill_slot_1.skillId"], ["accountUpgrades.shd_level"], ["specializationID"],
            ["activeSkillSelections.skill_slot_1"], ["equipment.mask", "modSelections.mask.mod_1"]
        ], outcome.Changes.Select(change => change.Cleared.ToArray()));
        Assert.All(outcome.Changes, change => Assert.Empty(change.Set));
        var after = outcome.State.Build;
        Assert.Equal("", after.Equipment["mask"]);
        Assert.Empty(after.ActiveSkillSelections);
        Assert.Equal("", after.SpecializationId);
    }

    [Fact]
    public void TheHighestRollIsOnAStep()
    {
        Assert.Equal(0.9, new AttributeRollBounds(0, 1, 0.3).Highest());
        Assert.Equal(1, new AttributeRollBounds(0, 1, 0.25).Highest());
        Assert.Equal(0.08, new AttributeRollBounds(0.01, 0.08, 0.001).Highest());
        Assert.Equal(0.05, new AttributeRollBounds(0.025, 0.05, null).Highest());
    }

    [Fact]
    public void AppliedBuildsResolveWithoutConfigurationErrors()
    {
        // Every value the applier fills in must be one the resolver accepts.
        var build = Applied(Geared(),
            new AttributeOperation("mask", "core_1", "armor_gear_core"),
            new AttributeOperation("mask", "minor_1", "weapon_handling_gear_minor"),
            new ModOperation("mask", "mod_1", "critical_hit_chance_gear_mod"),
            new PrototypeOperation("chest", "augment_entropy", 5),
            new AttributeOperation("chest", "core_1", "armor_gear_core"),
            new SkillModOperation("skill_slot_1", "firing_mechanism", "magnetic_rail"));
        var resolved = BuildResolver.Resolve(Pack, build, ImmutableDictionary<string, string>.Empty);
        Assert.DoesNotContain(resolved.Diagnostics, diagnostic => diagnostic.Severity == "error");
    }
}
