using System.Collections.Immutable;
using CharacterPlanner.Application.Commands;
using Codes = CharacterPlanner.Application.Commands.OperationErrorCodes;

namespace CharacterPlanner.Application.Tests.Commands;

public sealed class OperationJsonTests
{
    [Fact]
    public void ReadsEveryKindAndItsClearForm()
    {
        var operations = Division.Read("""
            [
              {"kind": "name", "text": "Rifle"},
              {"kind": "notes", "text": ""},
              {"kind": "equipment", "slot": "mask", "id": "mask_x"},
              {"kind": "equipment", "slot": "mask", "clear": true},
              {"kind": "attribute", "slot": "mask", "target": "core_1", "id": "armor_gear_core", "value": 170000},
              {"kind": "attribute", "slot": "mask", "target": "core_1", "id": "armor_gear_core"},
              {"kind": "attribute", "slot": "mask", "target": "core_1", "clear": true},
              {"kind": "mod", "slot": "mask", "target": "mod_1", "id": "m"},
              {"kind": "mod", "slot": "mask", "target": "mod_1", "clear": true},
              {"kind": "mod_roll", "slot": "mask", "target": "mod_1", "value": 0.05},
              {"kind": "mod_roll", "slot": "mask", "target": "mod_1"},
              {"kind": "mod_roll", "slot": "mask", "target": "mod_1", "clear": true},
              {"kind": "talent", "slot": "chest", "target": "talent", "id": "t"},
              {"kind": "talent", "slot": "chest", "target": "talent", "clear": true},
              {"kind": "active_skill", "slot": "skill_slot_1", "id": "s"},
              {"kind": "active_skill", "slot": "skill_slot_1", "clear": true},
              {"kind": "skill_upgrade", "slot": "skill_slot_1", "rank": 10.0},
              {"kind": "skill_upgrade", "slot": "skill_slot_1", "clear": true},
              {"kind": "skill_mod", "slot": "skill_slot_1", "target": "housing", "id": "ammo_box", "value": 0.1},
              {"kind": "skill_mod", "slot": "skill_slot_1", "target": "housing", "clear": true},
              {"kind": "specialization", "id": "gunner"},
              {"kind": "specialization", "clear": true},
              {"kind": "specialization_node", "id": "n", "enabled": false},
              {"kind": "account_upgrade", "id": "shd_level", "rank": 0},
              {"kind": "equipment_upgrade", "slot": "mask", "id": "mask_x", "rank": 3},
              {"kind": "prototype", "slot": "chest", "id": "augment_echo", "value": 7},
              {"kind": "prototype", "slot": "chest"},
              {"kind": "prototype", "slot": "chest", "clear": true},
              {"kind": "context", "id": "enemy_target", "value": 2},
              {"kind": "context", "id": "weapon_slot", "text": "sidearm"},
              {"kind": "context", "id": "enemy_target", "clear": true},
              {"kind": "party_member", "target": "Ally", "value": 5000},
              {"kind": "party_member", "target": "Ally", "text": "code"},
              {"kind": "party_member", "target": "Ally"},
              {"kind": "party_member", "target": "Ally", "clear": true},
              {"kind": "party_buff", "target": "Ally", "id": "overwatch", "enabled": true},
              {"kind": "equipment", "slot": "mask", "id": "mask_x", "clear": false}
            ]
            """);
        BuildOperation[] expected =
        [
            new NameOperation("Rifle"),
            new NotesOperation(""),
            new EquipmentOperation("mask", "mask_x"),
            EquipmentOperation.Cleared("mask"),
            new AttributeOperation("mask", "core_1", "armor_gear_core", 170000),
            new AttributeOperation("mask", "core_1", "armor_gear_core"),
            AttributeOperation.Cleared("mask", "core_1"),
            new ModOperation("mask", "mod_1", "m"),
            ModOperation.Cleared("mask", "mod_1"),
            new ModRollOperation("mask", "mod_1", 0.05),
            new ModRollOperation("mask", "mod_1"),
            ModRollOperation.Cleared("mask", "mod_1"),
            new TalentOperation("chest", "talent", "t"),
            TalentOperation.Cleared("chest", "talent"),
            new ActiveSkillOperation("skill_slot_1", "s"),
            ActiveSkillOperation.Cleared("skill_slot_1"),
            new SkillUpgradeOperation("skill_slot_1", 10),
            SkillUpgradeOperation.Cleared("skill_slot_1"),
            new SkillModOperation("skill_slot_1", "housing", "ammo_box", 0.1),
            SkillModOperation.Cleared("skill_slot_1", "housing"),
            new SpecializationOperation("gunner"),
            SpecializationOperation.Cleared(),
            new SpecializationNodeOperation("n", false),
            new AccountUpgradeOperation("shd_level", 0),
            new EquipmentUpgradeOperation("mask", "mask_x", 3),
            new PrototypeOperation("chest", "augment_echo", 7),
            new PrototypeOperation("chest"),
            PrototypeOperation.Cleared("chest"),
            new ContextOperation("enemy_target", 2),
            new ContextOperation("weapon_slot", Text: "sidearm"),
            ContextOperation.Cleared("enemy_target"),
            new PartyMemberOperation("Ally", Armor: 5000),
            new PartyMemberOperation("Ally", Text: "code"),
            new PartyMemberOperation("Ally"),
            PartyMemberOperation.Cleared("Ally"),
            new PartyBuffOperation("Ally", "overwatch", true),
            new EquipmentOperation("mask", "mask_x")
        ];
        Assert.Equal(expected, operations);
        Assert.Equal(OperationKinds.Supported.Order(), operations.Select(operation => operation.Kind).Distinct().Order());
    }

    [Fact]
    public void LabelsNameTheKindAndItsValues()
    {
        Assert.Equal("equipment primary_weapon primary_weapon_acr", new EquipmentOperation("primary_weapon", "primary_weapon_acr").Label);
        Assert.Equal("equipment mask clear", EquipmentOperation.Cleared("mask").Label);
        Assert.Equal("attribute mask core_1 armor_gear_core 0.5", new AttributeOperation("mask", "core_1", "armor_gear_core", 0.5).Label);
        Assert.Equal("context enemy_target 2", new ContextOperation("enemy_target", 2).Label);
        Assert.Equal("specialization clear", SpecializationOperation.Cleared().Label);
        Assert.Equal("name", new NameOperation("A long build name").Label);
        Assert.Equal("party_buff Ally overwatch on", new PartyBuffOperation("Ally", "overwatch", true).Label);
    }

    [Fact]
    public void ReadsEveryFrozenBatchExceptItsRemovedKinds()
    {
        foreach (var name in Division.Builds)
        {
            var read = OperationJson.Read(Division.Bytes("builds", name, "operations.json"));
            var removed = read.Errors.Select(error => error.Kind).ToArray();
            Assert.All(read.Errors, error => Assert.Equal(Codes.KindRemoved, error.Code));
            Assert.Equal(name == "hard-wired-skills" ? ["level"] : [], removed);
        }
    }

    [Theory]
    [InlineData("allocation", """{"kind": "allocation", "id": "firearms", "value": 5}""")]
    [InlineData("level", """{"kind": "level", "value": 40}""")]
    [InlineData("skill_node", """{"kind": "skill_node", "id": "x", "enabled": true}""")]
    [InlineData("scenario", """{"kind": "scenario", "id": "x", "enabled": true}""")]
    public void RejectsRemovedKinds(string kind, string operation)
    {
        var error = SingleError($"[{operation}]");
        Assert.Equal(new OperationError(1, kind, Codes.KindRemoved, error.Message), error);
        Assert.Contains("no longer supported", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{not json", 0, "", Codes.Malformed)]
    [InlineData("", 0, "", Codes.Malformed)]
    [InlineData("""{"kind": "name", "text": "x"}""", 0, "", Codes.NotArray)]
    [InlineData("""[[]]""", 1, "", Codes.NotObject)]
    [InlineData("""[{"slot": "mask"}]""", 1, "", Codes.KindMissing)]
    [InlineData("""[{"kind": 3}]""", 1, "", Codes.FieldType)]
    [InlineData("""[{"kind": "loadout"}]""", 1, "", Codes.KindUnknown)]
    [InlineData("""[{"kind": "equipment", "slot": "mask", "item": "x"}]""", 1, "", Codes.FieldUnknown)]
    [InlineData("""[{"kind": "equipment", "slot": "mask", "id": "x", "id": "y"}]""", 1, "", Codes.FieldDuplicate)]
    [InlineData("""[{"kind": "equipment", "slot": "mask", "id": "x", "rank": 1}]""", 1, "equipment", Codes.FieldNotAllowed)]
    [InlineData("""[{"kind": "equipment", "slot": "mask"}]""", 1, "equipment", Codes.FieldMissing)]
    [InlineData("""[{"kind": "equipment", "slot": 1, "id": "x"}]""", 1, "equipment", Codes.FieldType)]
    [InlineData("""[{"kind": "equipment", "slot": null, "id": "x"}]""", 1, "equipment", Codes.FieldType)]
    [InlineData("""[{"kind": "equipment", "slot": "mask", "id": ""}]""", 1, "equipment", Codes.FieldEmpty)]
    [InlineData("""[{"kind": "name", "text": ""}]""", 1, "name", Codes.FieldEmpty)]
    [InlineData("""[{"kind": "attribute", "slot": "mask", "target": "core_1", "id": "a", "value": "1"}]""", 1, "attribute", Codes.FieldType)]
    [InlineData("""[{"kind": "attribute", "slot": "mask", "target": "core_1", "id": "a", "value": 1e999}]""", 1, "attribute", Codes.FieldType)]
    [InlineData("""[{"kind": "skill_upgrade", "slot": "skill_slot_1", "rank": 1.5}]""", 1, "skill_upgrade", Codes.FieldType)]
    [InlineData("""[{"kind": "prototype", "slot": "chest", "id": "augment_echo", "value": 2.5}]""", 1, "prototype", Codes.FieldType)]
    [InlineData("""[{"kind": "specialization_node", "id": "n", "enabled": "yes"}]""", 1, "specialization_node", Codes.FieldType)]
    [InlineData("""[{"kind": "equipment", "slot": "mask", "clear": "yes"}]""", 1, "equipment", Codes.FieldType)]
    [InlineData("""[{"kind": "notes", "text": "x", "clear": true}]""", 1, "notes", Codes.ClearNotAllowed)]
    [InlineData("""[{"kind": "account_upgrade", "id": "x", "rank": 1, "clear": false}]""", 1, "account_upgrade", Codes.ClearNotAllowed)]
    [InlineData("""[{"kind": "equipment", "slot": "mask", "id": "x", "clear": true}]""", 1, "equipment", Codes.FieldNotAllowed)]
    [InlineData("""[{"kind": "attribute", "slot": "mask", "clear": true}]""", 1, "attribute", Codes.FieldMissing)]
    [InlineData("""[{"kind": "context", "id": "enemy_target"}]""", 1, "context", Codes.FieldMissing)]
    [InlineData("""[{"kind": "context", "id": "enemy_target", "value": 1, "text": "1"}]""", 1, "context", Codes.FieldConflict)]
    [InlineData("""[{"kind": "party_member", "target": "Ally", "value": 1, "text": "code"}]""", 1, "party_member", Codes.FieldConflict)]
    public void RejectsMalformedOperations(string json, int operation, string kind, string code)
    {
        var error = SingleError(json);
        Assert.Equal((operation, kind, code), (error.Operation, error.Kind, error.Code));
    }

    [Fact]
    public void ReportsEachBadOperationByIndexAndReturnsNoOperations()
    {
        var read = OperationJson.Read("""
            [{"kind": "name", "text": "ok"}, {"kind": "mod", "slot": "mask"}, {"kind": "notes", "text": "ok"}, {"kind": "level", "value": 40}]
            """);
        Assert.False(read.Succeeded);
        Assert.Empty(read.Operations);
        Assert.Equal([(2, "mod", Codes.FieldMissing), (4, "level", Codes.KindRemoved)], read.Errors.Select(error => (error.Operation, error.Kind, error.Code)));
        Assert.StartsWith("operation 2 (mod): ", read.Errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyArrayIsAnEmptyBatch() => Assert.Equal(ImmutableArray<BuildOperation>.Empty, Division.Read("[]"));

    private static OperationError SingleError(string json)
    {
        var read = OperationJson.Read(json);
        Assert.Empty(read.Operations);
        return Assert.Single(read.Errors);
    }
}
