using System.Collections.Immutable;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Tests.TestPacks;

namespace CharacterPlanner.Core.Tests;

public sealed class PackValidatorTests
{
    private static readonly GamePack Pack = Tiny;

    private static GamePack Item(string id, Func<ItemDefinition, ItemDefinition> edit) => Pack with { Items = [.. Pack.Items.Select(item => item.ID == id ? edit(item) : item)] };
    private static GamePack Talent(string id, Func<TalentDefinition, TalentDefinition> edit) => Pack with { Talents = [.. Pack.Talents.Select(talent => talent.ID == id ? edit(talent) : talent)] };
    private static GamePack Stat(string id, Func<StatDefinition, StatDefinition> edit) => Pack with { Stats = [.. Pack.Stats.Select(stat => stat.ID == id ? edit(stat) : stat)] };
    private static GamePack Skill(Func<ActiveSkillDefinition, ActiveSkillDefinition> edit) => Pack with { ActiveSkills = [edit(Pack.ActiveSkills[0])] };
    private static GamePack Mod(Func<StatModifier, StatModifier> edit) => Pack with { Mods = [Pack.Mods[0] with { Modifiers = [edit(Pack.Mods[0].Modifiers[0])] }] };
    private static GamePack Gate(ImmutableDictionary<string, string> requires) => Item("mask_a", item => item with { Modifiers = [item.Modifiers[0] with { RequiresContext = requires }] });
    private static GamePack Ally(Func<StatModifier, StatModifier> edit) => Talent("talent_party", talent => talent with
    { AllyEffects = [talent.AllyEffects[0] with { Modifiers = [edit(talent.AllyEffects[0].Modifiers[0])] }] });
    private static GamePack Layout(Func<PackLayout, PackLayout> edit) => Pack with { Presentation = Pack.Presentation with { Layout = edit(Pack.Presentation.Layout!) } };
    [Fact]
    public void ARuleMayBelongToASpecializationNode()
    {
        var node = Pack.Specializations[0].Nodes[0].ID;
        var rules = Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Talent = node }] } };
        Assert.DoesNotContain(PackValidator.Validate(rules).Diagnostics, diagnostic => diagnostic.Code == "bad-sim-trigger");
        // Another tree with the same node makes the name ambiguous.
        var ambiguous = rules with { Specializations = [.. rules.Specializations, rules.Specializations[0] with { ID = "other_tree" }] };
        Assert.Contains(PackValidator.Validate(ambiguous).Diagnostics, diagnostic => diagnostic.Code == "bad-sim-trigger");
    }

    private static GamePack Specialization(Func<SpecializationDefinition, SpecializationDefinition> edit) => Pack with { Specializations = [edit(Pack.Specializations[0])] };
    private static ImmutableDictionary<string, string> Map(string key, string value) => ImmutableDictionary<string, string>.Empty.Add(key, value);
    private static GamePack Formula(string formula) => Stat("total_damage", stat => stat with { Formula = formula });
    private static GamePack StatusDamage(params SimStatusDamageDefinition[] rules) => Pack with { Simulation = Pack.Simulation with { StatusDamage = [.. rules] } };
    private static GamePack When(string when) => Talent("talent_killer", talent => talent with { Modifiers = [talent.Modifiers[0] with { When = when }] });

    private static readonly Dictionary<string, (Func<GamePack> Pack, string Code, string Path)> Cases = new()
    {
        // Identity and ids
        ["unknown capability"] = (() => Pack with { Capabilities = ["weapons", "teleport"] }, "unknown-capability", "manifest.capabilities"),
        ["level"] = (() => Pack with { Level = 0 }, "invalid-integer", "level"),
        ["id pattern"] = (() => Pack with { Selectors = [Pack.Selectors[0] with { ID = "Headshot" }, Pack.Selectors[1]] }, "invalid-id", "selectors.Headshot"),
        ["id with trailing newline"] = (() => Pack with { Mods = [Pack.Mods[0] with { ID = "mod_crit\n" }] }, "invalid-id", "mods.mod_crit\n"),
        ["duplicate id"] = (() => Pack with { Mods = [Pack.Mods[0], Pack.Mods[0]] }, "duplicate-id", "mods"),
        ["blank name"] = (() => Pack with { Mods = [Pack.Mods[0] with { Name = " " }] }, "empty-name", "mods.mod_crit.name"),
        // Stats, curves, selectors, outputs, attributes
        ["stat format"] = (() => Stat("weapon_damage", stat => stat with { Format = "percentage" }), "invalid-format", "stats.weapon_damage.format"),
        ["stat range"] = (() => Stat("armor", stat => stat with { Min = 10, Max = 0 }), "bad-range", "stats.armor"),
        ["curve unsorted"] = (() => Pack with { Curves = Pack.Curves.SetItem("tiers", [new(6, 0), new(0, 1)]) }, "curve-unsorted", "curves.tiers[1]"),
        ["curve empty"] = (() => Pack with { Curves = Pack.Curves.SetItem("tiers", []) }, "curve-empty", "curves.tiers"),
        ["reserved selector"] = (() => Pack with { Selectors = [.. Pack.Selectors, new() { ID = "weapon_slot", Name = "Weapon", Values = ["primary_weapon"] }] }, "reserved-selector", "selectors.weapon_slot"),
        ["selector without values"] = (() => Pack with { Selectors = [Pack.Selectors[0] with { Values = [] }, Pack.Selectors[1]] }, "selector-values", "selectors.headshot_mode.values"),
        ["selector and input share an id"] = (() => Pack with { Selectors = [.. Pack.Selectors, new() { ID = "stacks", Name = "Stacks", Values = ["1"] }] }, "context-id-conflict", "selectors.stacks"),
        ["build output id"] = (() => Pack with { BuildOutputs = [Pack.BuildOutputs[0] with { ID = "Bad" }] }, "invalid-build-output", "buildOutputs.Bad"),
        ["build output format"] = (() => Pack with { BuildOutputs = [Pack.BuildOutputs[0] with { Format = "" }] }, "invalid-format", "buildOutputs.dps.format"),
        ["attribute format"] = (() => Pack with { Attributes = [Pack.Attributes[0] with { Format = "ratio" }, Pack.Attributes[1]] }, "invalid-format", "attributes.weapon_damage_core.format"),
        ["attribute stat"] = (() => Pack with { Attributes = [Pack.Attributes[0] with { Stat = "missing" }, Pack.Attributes[1]] }, "unknown-stat", "attributes.weapon_damage_core.stat"),
        ["attribute step"] = (() => Pack with { Attributes = [Pack.Attributes[0] with { Step = 0 }, Pack.Attributes[1]] }, "bad-step", "attributes.weapon_damage_core.step"),
        ["attribute prototype range"] = (() => Pack with { Attributes = [Pack.Attributes[0] with { PrototypeMax = 0.1 }, Pack.Attributes[1]] }, "bad-range", "attributes.weapon_damage_core.prototype"),
        ["numeric input default"] = (() => Pack with { NumericContextInputs = [Pack.NumericContextInputs[0] with { Default = 9 }] }, "numeric-context-default-invalid", "numericInputs[0]"),
        // Modifiers
        ["modifier op"] = (() => Mod(modifier => modifier with { Op = "inc" }), "invalid-modifier-op", "mods.mod_crit.modifiers[0].op"),
        ["modifier value"] = (() => Mod(modifier => modifier with { Value = new(double.NaN) }), "invalid-number", "mods.mod_crit.modifiers[0].value"),
        ["modifier stat"] = (() => Mod(modifier => modifier with { Stat = "missing" }), "unknown-stat", "mods.mod_crit.modifiers[0].stat"),
        ["undeclared selector"] = (() => Gate(Map("overwatch", "true")), "unknown-selector", "items.mask_a.modifiers[0].requires.overwatch"),
        ["undeclared selector value"] = (() => Gate(Map("overwatch_active", "yes")), "unknown-selector-value", "items.mask_a.modifiers[0].requires.overwatch_active"),
        ["weapon_slot gate"] = (() => Gate(Map("weapon_slot", "primary_weapon")), "weapon-slot-gate", "items.mask_a.modifiers[0].requires.weapon_slot"),
        ["empty requires"] = (() => Gate(ImmutableDictionary<string, string>.Empty), "empty-requires", "items.mask_a.modifiers[0].requires"),
        ["whileHolstered on a mod"] = (() => Mod(modifier => modifier with { WhileHolstered = true }), "invalid-while-holstered", "mods.mod_crit.modifiers[0].whileHolstered"),
        ["whileHolstered on a gear talent"] = (() => Talent("talent_copier", talent => talent with { Modifiers = [new() { Stat = "armor", Op = "flat", Value = new(1), WhileHolstered = true }] }),
            "invalid-while-holstered", "talents.talent_copier.modifiers[0].whileHolstered"),
        ["ranked expression"] = (() => Pack with { AccountUpgrades = [Pack.AccountUpgrades[0] with { ModifiersPerRank = [new() { Stat = "armor", Op = "flat", Value = new("1") }] }] },
            "invalid-upgrade-modifier", "accountUpgrades.shd_level.modifiersPerRank[0].value"),
        ["ranked override"] = (() => Item("rifle_a", item => item with { Upgrade = item.Upgrade! with { ModifiersPerRank = [item.Upgrade.ModifiersPerRank[0] with { Op = "override" }] } }),
            "invalid-upgrade-modifier", "items.rifle_a.upgrade.modifiersPerRank[0].op"),
        ["ranked when"] = (() => Item("rifle_a", item => item with { Upgrade = item.Upgrade! with { ModifiersPerRank = [item.Upgrade.ModifiersPerRank[0] with { When = "1" }] } }),
            "invalid-upgrade-modifier", "items.rifle_a.upgrade.modifiersPerRank[0].when"),
        ["effect cap"] = (() => Pack with { AccountUpgrades = [Pack.AccountUpgrades[0] with { EffectCap = 101 }] }, "bad-upgrade-effect-cap", "accountUpgrades.shd_level.effectCap"),
        ["negative rank"] = (() => Pack with { AccountUpgrades = [Pack.AccountUpgrades[0] with { MaxRank = -1, EffectCap = null }] }, "invalid-integer", "accountUpgrades.shd_level.maxRank"),
        // Expressions
        ["parse error"] = (() => Formula("1 +"), "parse-error", "stats.total_damage.formula"),
        ["unknown identifier"] = (() => Formula("missing + 1"), "unknown-stat", "stats.total_damage.formula"),
        ["cycle"] = (() => Stat("weapon_damage", stat => stat with { Formula = "total_damage" }), "cycle", "stats"),
        ["curve name not literal"] = (() => Formula("curve(armor, 1)"), "literal-argument-required", "stats.total_damage.formula"),
        ["has_tag not literal"] = (() => When("has_tag(1)"), "literal-argument-required", "talents.talent_killer.modifiers[0].when"),
        ["has_selected_tag not literal"] = (() => When("has_selected_tag(1 + 1)"), "literal-argument-required", "talents.talent_killer.modifiers[0].when"),
        ["has_talent not literal"] = (() => When("has_talent(1)"), "literal-argument-required", "talents.talent_killer.modifiers[0].when"),
        ["context_value not literal"] = (() => When("context_value(1) > 0"), "literal-argument-required", "talents.talent_killer.modifiers[0].when"),
        ["curve arity"] = (() => Formula("curve('tiers')"), "function-arity", "stats.total_damage.formula"),
        ["clamp arity"] = (() => Formula("clamp(1, 2)"), "function-arity", "stats.total_damage.formula"),
        ["unknown curve"] = (() => Formula("curve('nope', 1)"), "unknown-curve", "stats.total_damage.formula"),
        ["unknown function"] = (() => Formula("pow(1, 2)"), "unknown-function", "stats.total_damage.formula"),
        ["unknown numeric input"] = (() => When("context_value('nope') > 0"), "unknown-numeric-input", "talents.talent_killer.modifiers[0].when"),
        ["unknown talent"] = (() => When("has_talent('nope')"), "unknown-talent", "talents.talent_killer.modifiers[0].when"),
        ["stat in when"] = (() => When("armor > 0"), "unknown-stat", "talents.talent_killer.modifiers[0].when"),
        // Ally effects
        ["ally op"] = (() => Ally(modifier => modifier with { Op = "mult" }), "bad-ally-effect", "talents.talent_party.allyEffects.party_boost.modifiers[0]"),
        ["ally requires"] = (() => Ally(modifier => modifier with { RequiresContext = Map("overwatch_active", "true") }), "bad-ally-effect", "talents.talent_party.allyEffects.party_boost.modifiers[0]"),
        ["ally stat"] = (() => Ally(modifier => modifier with { Stat = "missing" }), "unknown-stat", "talents.talent_party.allyEffects.party_boost.modifiers[0].stat"),
        ["ally identifier"] = (() => Ally(modifier => modifier with { Value = new("missing * 2") }), "unknown-stat", "talents.talent_party.allyEffects.party_boost.modifiers[0].value"),
        ["ally curve"] = (() => Ally(modifier => modifier with { Value = new("curve('nope', 1)") }), "unknown-curve", "talents.talent_party.allyEffects.party_boost.modifiers[0].value"),
        ["ally function"] = (() => Ally(modifier => modifier with { Value = new("context_value('stacks')") }), "function-unavailable", "talents.talent_party.allyEffects.party_boost.modifiers[0].value"),
        ["ally parse"] = (() => Ally(modifier => modifier with { Value = new("(") }), "parse-error", "talents.talent_party.allyEffects.party_boost.modifiers[0].value"),
        ["ally stack"] = (() => Talent("talent_party", talent => talent with { AllyEffects = [talent.AllyEffects[0] with { Stack = "" }] }), "invalid-id", "talents.talent_party.allyEffects.party_boost.stack"),
        // Slots and items
        ["item slot"] = (() => Item("mask_a", item => item with { Slot = "hat" }), "unknown-slot", "items.mask_a.slot"),
        ["slot accepts"] = (() => Item("mask_a", item => item with { Tags = ["brand_x"] }), "tag-mismatch", "items.mask_a.tags"),
        ["weapon block outside weapon slot"] = (() => Item("mask_a", item => item with { Weapon = [new("armor", 1)] }), "weapon-block-slot", "items.mask_a.weapon"),
        ["weapon block stat"] = (() => Item("rifle_a", item => item with { Weapon = [new("missing", 1)] }), "unknown-stat", "items.rifle_a.weapon.missing"),
        ["weapon block duplicate"] = (() => Item("rifle_a", item => item with { Weapon = [new("weapon_rpm", 1), new("weapon_rpm", 2)] }), "duplicate-weapon-stat", "items.rifle_a.weapon.weapon_rpm"),
        ["no attribute choices"] = (() => Item("mask_a", item => item with { AttributeSlots = [item.AttributeSlots[0] with { Choices = [] }] }), "no-attribute-choices", "items.mask_a.attributeSlots.core.choices"),
        ["attribute choice"] = (() => Item("mask_a", item => item with { AttributeSlots = [item.AttributeSlots[0] with { Choices = [new() { AttributeID = "missing" }] }] }),
            "unknown-attribute", "items.mask_a.attributeSlots.core.choices[0].attribute"),
        ["attribute choice range"] = (() => Item("mask_a", item => item with { AttributeSlots = [item.AttributeSlots[0] with { Choices = [new() { AttributeID = "armor_core", Min = 5, Max = 1 }] }] }),
            "bad-range", "items.mask_a.attributeSlots.core.choices[0]"),
        ["mod choice"] = (() => Item("mask_a", item => item with { ModSlots = [item.ModSlots[0] with { Choices = ["missing"] }] }), "unknown-mod", "items.mask_a.modSlots.mod.choices"),
        ["no mod choices"] = (() => Item("mask_a", item => item with { ModSlots = [item.ModSlots[0] with { Choices = [] }] }), "no-mod-choices", "items.mask_a.modSlots.mod.choices"),
        ["talent choice"] = (() => Item("mask_a", item => item with { TalentSlots = [item.TalentSlots[0] with { Choices = ["missing"] }] }), "unknown-talent", "items.mask_a.talentSlots.talent.choices"),
        ["roll attribute"] = (() => Pack with { Mods = [Pack.Mods[0] with { RollAttributeID = "missing" }] }, "unknown-attribute", "mods.mod_crit.rollAttribute"),
        // Talents, sets, prototype
        ["modeled by sets"] = (() => Talent("talent_party", talent => talent with { ModeledBySets = ["missing"] }), "unknown-set", "talents.talent_party.modeledBySets"),
        ["set piece credit"] = (() => Talent("talent_party", talent => talent with { SetPieceCredit = -1 }), "invalid-integer", "talents.talent_party.setPieceCredit"),
        ["copy slot"] = (() => Talent("talent_copier", talent => talent with { CopiesTalent = talent.CopiesTalent! with { From = "hat" } }), "unknown-slot", "talents.talent_copier.copiesTalent.from"),
        ["copy exclusion"] = (() => Talent("talent_copier", talent => talent with { CopiesTalent = talent.CopiesTalent! with { ExcludeTalents = ["missing"] } }), "unknown-talent", "talents.talent_copier.copiesTalent.excludeTalents"),
        ["set item"] = (() => Pack with { Sets = [Pack.Sets[0] with { Items = ["missing"] }] }, "unknown-item", "sets.set_x.items"),
        ["set pieces"] = (() => Pack with { Sets = [Pack.Sets[0] with { Bonuses = [Pack.Sets[0].Bonuses[0] with { Pieces = 0 }] }] }, "invalid-integer", "sets.set_x.bonuses[0].pieces"),
        ["prototype slot"] = (() => Pack with { Prototype = Pack.Prototype! with { Slots = ["hat"] } }, "unknown-slot", "prototype.slots[0]"),
        ["prototype input"] = (() => Pack with { Prototype = Pack.Prototype! with { NormalizeInput = "missing" } }, "unknown-numeric-input", "prototype.normalizeInput"),
        ["augment levels"] = (() => Pack with { Prototype = Pack.Prototype! with { Augments = [Pack.Prototype.Augments[0] with { Levels = 0 }] } }, "invalid-integer", "prototype.augments.augment_echo.levels"),
        // Skills and specializations
        ["skill slot choice"] = (() => Pack with { ActiveSkillSlots = [Pack.ActiveSkillSlots[0] with { Choices = ["missing"] }] }, "unknown-skill", "skillSlots.skill_1.choices"),
        ["skill family"] = (() => Skill(skill => skill with { Family = "Turret" }), "invalid-id", "skills.turret.family"),
        ["preview kind"] = (() => Skill(skill => skill with { PreviewOutputs = [skill.PreviewOutputs[0] with { Kind = "burst" }, .. skill.PreviewOutputs[1..]] }), "invalid-preview-kind", "skills.turret.previewOutputs.damage.kind"),
        ["preview headline"] = (() => Skill(skill => skill with { PreviewOutputs = [skill.PreviewOutputs[0] with { Headline = true }, .. skill.PreviewOutputs[1..]] }), "invalid-preview-headline", "skills.turret.previewOutputs.damage.headline"),
        ["preview format"] = (() => Skill(skill => skill with { PreviewOutputs = [skill.PreviewOutputs[0] with { Format = "" }, .. skill.PreviewOutputs[1..]] }), "invalid-format", "skills.turret.previewOutputs.damage.format"),
        ["preview scope"] = (() => Skill(skill => skill with { PreviewOutputs = [skill.PreviewOutputs[0] with { Formula = "skill_mod_other" }, .. skill.PreviewOutputs[1..]] }), "unknown-stat", "skills.turret.previewOutputs.damage"),
        ["skill mod input"] = (() => Skill(skill => skill with { ModSlots = [skill.ModSlots[0] with { Choices = [skill.ModSlots[0].Choices[0] with { Input = "damage" }] }] }),
            "invalid-skill-mod-input", "skills.turret.modSlots.mod.choices.damage.input"),
        ["budget cycle"] = (() => Specialization(spec => spec with { Budgets = [spec.Budgets[0] with { ParentID = "hub" }, spec.Budgets[1]] }), "budget-cycle", "specializations.gunner.budgets"),
        ["node budget"] = (() => Specialization(spec => spec with { Nodes = [spec.Nodes[0] with { BudgetID = "missing" }, spec.Nodes[1]] }), "unknown-budget", "specializations.gunner.nodes.node_a.budgetId"),
        ["node prerequisite"] = (() => Specialization(spec => spec with { Nodes = [spec.Nodes[0], spec.Nodes[1] with { Prerequisites = ["missing"] }] }), "unknown-node", "specializations.gunner.nodes.node_b.prerequisites"),
        ["node cycle"] = (() => Specialization(spec => spec with { Nodes = [spec.Nodes[0] with { Prerequisites = ["node_b"] }, spec.Nodes[1]] }), "node-cycle", "specializations.gunner.nodes"),
        // Renamed ids
        ["rename collection"] = (() => Pack with { RenamedIds = Pack.RenamedIds.Add("weapon", Map("a", "b")) }, "unknown-rename-collection", "renamedIds.weapon"),
        ["rename existing"] = (() => Pack with { RenamedIds = Pack.RenamedIds.SetItem("item", Map("rifle_b", "rifle_a")) }, "renamed-id-exists", "renamedIds.item.rifle_b"),
        ["rename target"] = (() => Pack with { RenamedIds = Pack.RenamedIds.SetItem("item", Map("old_rifle", "missing")) }, "unknown-rename-target", "renamedIds.item.old_rifle"),
        // Simulation
        ["trigger talent"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Talent = "missing" }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["cooldown from end with a cancel"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { CooldownFromEnd = true, Every = 5, ConsumeOn = "kill", Duration = 0, CancelOn = "swapped" }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["cooldown from end with a duration"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { CooldownFromEnd = true, Every = 5, ConsumeOn = "kill", Duration = 3 }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["cooldown from end on one enemy"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { CooldownFromEnd = true, Every = 5, ConsumeOn = "target_death", Duration = 0 }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["cooldown from end without an event"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { CooldownFromEnd = true, Every = 5, ConsumeOn = "" }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["trigger kind"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Kind = "aura" }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["stacks key"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[1] with { Key = "overwatch_active" }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["cancel on stacks"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[1] with { CancelOn = "swapped" }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["status observer interval"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Kind = "status", Status = "burning", On = "", Duration = 0, Every = 5 }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["status negative interval"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Kind = "status", Status = "burning", On = "second", Duration = 7, Every = -1 }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["weapon tags kind"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Kind = "status", Status = "burning", On = "hit", Duration = 2, WeaponTags = ["weapon"] }] } }, "bad-sim-trigger", "simulation.triggers[0].weaponTags"),
        ["weapon tags unknown"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { WeaponTags = ["no_such_tag"] }] } }, "bad-sim-trigger", "simulation.triggers[0].weaponTags"),
        ["weapon tags repeated"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { WeaponTags = ["weapon", "weapon"] }] } }, "bad-sim-trigger", "simulation.triggers[0].weaponTags"),
        ["force headshot kind"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { On = "hit", ForceHeadshot = true }] } }, "bad-sim-trigger", "simulation.triggers[0].forceHeadshot"),
        ["force headshot event"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Kind = "ready", On = "kill", ForceHeadshot = true }] } }, "bad-sim-trigger", "simulation.triggers[0].forceHeadshot"),
        ["grant kind"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Grant = "healing", GrantAmount = 0.5, GrantDuration = 10 }] } }, "bad-sim-trigger", "simulation.triggers[0].grant"),
        ["grant amount"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Grant = "temporary_armor", GrantAmount = 1.5, GrantDuration = 10 }] } }, "bad-sim-trigger", "simulation.triggers[0].grant"),
        ["armor repair over time without instances"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Grant = "armor_repair", GrantAmount = 0.1, GrantDuration = 5 }] } }, "bad-sim-trigger", "simulation.triggers[0].grant"),
        ["instant armor repair with instances"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Grant = "armor_repair", GrantAmount = 0.5, GrantInstances = "independent" }] } }, "bad-sim-trigger", "simulation.triggers[0].grant"),
        ["armor repair unknown instances"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Grant = "armor_repair", GrantAmount = 0.1, GrantDuration = 5, GrantInstances = "refresh" }] } }, "bad-sim-trigger", "simulation.triggers[0].grant"),
        ["missing armor repair over time"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Grant = "missing_armor_repair", GrantAmount = 0.025, GrantDuration = 5 }] } }, "bad-sim-trigger", "simulation.triggers[0].grant"),
        ["temporary armor with instances"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Grant = "temporary_armor", GrantAmount = 0.5, GrantDuration = 10, GrantInstances = "independent" }] } }, "bad-sim-trigger", "simulation.triggers[0].grant"),
        ["instances without grant"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { GrantInstances = "independent" }] } }, "bad-sim-trigger", "simulation.triggers[0].grant"),
        ["skill cooldown share above one"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Grant = "skill_cooldown", GrantAmount = 1.5 }] } }, "bad-sim-trigger", "simulation.triggers[0].grant"),
        ["skill cooldown seconds above 600"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Grant = "skill_cooldown_seconds", GrantAmount = 601 }] } }, "bad-sim-trigger", "simulation.triggers[0].grant"),
        ["armor kit refund of half a kit"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Grant = "armor_kit_refund", GrantAmount = 0.5 }] } }, "bad-sim-trigger", "simulation.triggers[0].grant"),
        ["grant amount stat with an amount"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Grant = "temporary_armor", GrantAmount = 0.3, GrantAmountStat = Pack.Stats[0].ID, GrantDuration = 10 }] } }, "bad-sim-trigger", "simulation.triggers[0].grant"),
        ["grant amount stat on a kit refund"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { On = "armor_kit", Grant = "armor_kit_refund", GrantAmountStat = Pack.Stats[0].ID }] } }, "bad-sim-trigger", "simulation.triggers[0].grant"),
        ["grant amount stat unknown"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Grant = "temporary_armor", GrantAmountStat = "nowhere", GrantDuration = 10 }] } }, "unknown-stat", "simulation.triggers[0].grantAmountStat"),
        ["active after on stacks"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[1] with { ActiveAfter = 10 }] } }, "bad-sim-trigger", "simulation.triggers[0].activeAfter"),
        ["active after above 600"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { ActiveAfter = 601 }] } }, "bad-sim-trigger", "simulation.triggers[0].activeAfter"),
        ["status also without a count"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Kind = "status", Status = "burning", Duration = 7, Also = [new() { On = "skill_kill" }] }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["status also on its own event"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Kind = "status", Status = "burning", Duration = 7, Count = 4, Also = [new() { On = Pack.Simulation.Triggers[0].On }] }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["status also gaining two"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Kind = "status", Status = "burning", Duration = 7, Count = 4, Also = [new() { On = "skill_kill", Gain = 2 }] }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["status also with applyOnHit"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Kind = "status", Status = "burning", Duration = 7, On = "hit", Count = 4, ApplyOnHit = true, Also = [new() { On = "skill_kill" }] }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["status also with its own count"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Kind = "status", Status = "burning", Duration = 7, Count = 4, Also = [new() { On = "skill_kill", Count = 2 }] }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["status also twice"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Kind = "status", Status = "burning", Duration = 7, Count = 4, Also = [new() { On = "skill_kill" }, new() { On = "skill_kill" }] }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["status also on a whole second"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Kind = "status", Status = "burning", Duration = 7, Count = 4, Also = [new() { On = "second" }] }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["status also on status applied"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Kind = "status", Status = "burning", Duration = 7, Count = 4, Also = [new() { On = "status_applied" }] }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["status also on an unknown event"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Kind = "status", Status = "burning", Duration = 7, Count = 4, Also = [new() { On = "nowhere" }] }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["status also counted on a whole second"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Kind = "status", Status = "burning", Duration = 7, On = "second", Count = 4, Also = [new() { On = "kill" }] }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["hold delay on temporary armor"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Grant = "temporary_armor", GrantAmount = 0.5, GrantDuration = 10, GrantHoldDelay = 1 }] } }, "bad-sim-trigger", "simulation.triggers[0].grant"),
        ["hold delay above 60"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Grant = "skill_cooldown", GrantAmount = 1, GrantHoldDelay = 61 }] } }, "bad-sim-trigger", "simulation.triggers[0].grant"),
        ["hold delay without a grant"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { GrantHoldDelay = 1 }] } }, "bad-sim-trigger", "simulation.triggers[0].grant"),
        ["armor kit refund on a kill"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { On = "kill", Grant = "armor_kit_refund", GrantAmount = 1 }] } }, "bad-sim-trigger", "simulation.triggers[0].grant"),
        ["armor kit refund over time"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Grant = "armor_kit_refund", GrantAmount = 1, GrantDuration = 5 }] } }, "bad-sim-trigger", "simulation.triggers[0].grant"),
        ["skill cooldown over time"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Grant = "skill_cooldown", GrantAmount = 0.1, GrantDuration = 5 }] } }, "bad-sim-trigger", "simulation.triggers[0].grant"),
        ["requires on stacks"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[1] with { RequiresContext = ImmutableDictionary<string, string>.Empty.Add("overwatch_active", "true") }] } }, "bad-sim-trigger", "simulation.triggers[0].requires"),
        ["requires unknown selector"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { RequiresContext = ImmutableDictionary<string, string>.Empty.Add("nowhere", "true") }] } }, "unknown-selector", "simulation.triggers[0].requires.nowhere"),
        ["requires a driven key"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { RequiresContext = ImmutableDictionary<string, string>.Empty.Add(Pack.Simulation.Triggers[0].Key, "true") }] } }, "bad-sim-trigger", "simulation.triggers[0].requires." + Pack.Simulation.Triggers[0].Key),
        ["chance zero"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Chance = 0 }] } }, "bad-sim-trigger", "simulation.triggers[0].chance"),
        ["chance above one"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Chance = 1.5 }] } }, "bad-sim-trigger", "simulation.triggers[0].chance"),
        ["chance on stacks"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[1] with { Chance = 0.25 }] } }, "bad-sim-trigger", "simulation.triggers[0].chance"),
        ["grant without kind"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { GrantAmount = 0.5 }] } }, "bad-sim-trigger", "simulation.triggers[0].grant"),
        ["count window without count"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { CountWindow = 20 }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["count window negative"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Count = 3, CountWindow = -1 }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["max magazine kind"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { MaxMagazine = 0.5 }] } }, "bad-sim-trigger", "simulation.triggers[0].maxMagazine"),
        ["max magazine share"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[1] with { MaxMagazine = 1.5 }] } }, "bad-sim-trigger", "simulation.triggers[0].maxMagazine"),
        ["while key kind"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0], Pack.Simulation.Triggers[1] with { WhileKey = "overwatch_active" }] } }, "bad-sim-trigger", "simulation.triggers[1].whileKey"),
        ["target death on a status rule"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Kind = "status", Status = "burning", On = "target_death" }, Pack.Simulation.Triggers[1]] } }, "bad-sim-trigger", "simulation.triggers[0].on"),
        ["distance input unknown"] = (() => DistancePack with { Simulation = DistancePack.Simulation with { DistanceDefaults = [new() { Input = "luck", Bands = [new() { Value = 1 }] }] } }, "bad-sim-distance-default", "simulation.distanceDefaults[0].input"),
        ["distance on a driven key"] = (() => DistancePack with { Simulation = DistancePack.Simulation with { DistanceDefaults = [new() { Input = "stacks", Bands = [new() { Value = 1 }] }] } }, "bad-sim-distance-default", "simulation.distanceDefaults[0].input"),
        ["distance value off step"] = (() => DistancePack with { Simulation = DistancePack.Simulation with { DistanceDefaults = [new() { Input = "hit_rate", Bands = [new() { UpTo = 15, Value = 72 }, new() { Value = 30 }] }] } }, "bad-sim-distance-default", "simulation.distanceDefaults[0].bands[0].value"),
        ["distance bad band keeps the baseline"] = (() => DistancePack with { Simulation = DistancePack.Simulation with { DistanceDefaults = [new() { Input = "hit_rate", Bands = [new() { UpTo = 30, Value = 70 }, new() { UpTo = 2000, Value = 50 }, new() { UpTo = 20, Value = 40 }, new() { Value = 30 }] }] } }, "bad-sim-distance-default", "simulation.distanceDefaults[0].bands[2].upTo"),
        ["distance input twice"] = (() => DistancePack with { Simulation = DistancePack.Simulation with { DistanceDefaults = [new() { Input = "hit_rate", Bands = [new() { Value = 1 }] }, new() { Input = "hit_rate", Bands = [new() { Value = 2 }] }] } }, "bad-sim-distance-default", "simulation.distanceDefaults[1].input"),
        ["distance without bands"] = (() => DistancePack with { Simulation = DistancePack.Simulation with { DistanceDefaults = [new() { Input = "hit_rate" }] } }, "bad-sim-distance-default", "simulation.distanceDefaults[0].bands"),
        ["distance last band bounded"] = (() => DistancePack with { Simulation = DistancePack.Simulation with { DistanceDefaults = [new() { Input = "hit_rate", Bands = [new() { UpTo = 15, Value = 1 }] }] } }, "bad-sim-distance-default", "simulation.distanceDefaults[0].bands[0].upTo"),
        ["distance band unbounded early"] = (() => DistancePack with { Simulation = DistancePack.Simulation with { DistanceDefaults = [new() { Input = "hit_rate", Bands = [new() { Value = 1 }, new() { Value = 2 }] }] } }, "bad-sim-distance-default", "simulation.distanceDefaults[0].bands[0].upTo"),
        ["distance bands out of order"] = (() => DistancePack with { Simulation = DistancePack.Simulation with { DistanceDefaults = [new() { Input = "hit_rate", Bands = [new() { UpTo = 30, Value = 1 }, new() { UpTo = 15, Value = 2 }, new() { Value = 3 }] }] } }, "bad-sim-distance-default", "simulation.distanceDefaults[0].bands[1].upTo"),
        ["distance value out of range"] = (() => DistancePack with { Simulation = DistancePack.Simulation with { DistanceDefaults = [new() { Input = "hit_rate", Bands = [new() { UpTo = 15, Value = 101 }, new() { Value = 3 }] }] } }, "bad-sim-distance-default", "simulation.distanceDefaults[0].bands[0].value"),
        ["while key own"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { WhileKey = "overwatch_active" }, Pack.Simulation.Triggers[1]] } }, "bad-sim-trigger", "simulation.triggers[0].whileKey"),
        ["while armor on stacks"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0], Pack.Simulation.Triggers[1] with { WhileArmorBelow = 0.15 }] } }, "bad-sim-trigger", "simulation.triggers[1].whileArmorBelow"),
        ["while armor share"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { WhileArmorBelow = 1.5 }, Pack.Simulation.Triggers[1]] } }, "bad-sim-trigger", "simulation.triggers[0].whileArmorBelow"),
        ["every group without every"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Every = 0, EveryGroup = "g" }, Pack.Simulation.Triggers[1]] } }, "bad-sim-trigger", "simulation.triggers[0].everyGroup"),
        ["every group of one"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Every = 90, EveryGroup = "g" }, Pack.Simulation.Triggers[1]] } }, "bad-sim-trigger", "simulation.triggers[0].everyGroup"),
        ["every group of one talent"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Every = 90, EveryGroup = "g" }, Pack.Simulation.Triggers[0] with { Every = 90, EveryGroup = "g", On = "hit" }, Pack.Simulation.Triggers[1]] } }, "bad-sim-trigger", "simulation.triggers[0].everyGroup"),
        ["every group mixed"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Every = 90, EveryGroup = "g" }, Pack.Simulation.Triggers[1] with { Every = 10, EveryGroup = "g" }] } }, "bad-sim-trigger", "simulation.triggers[0].everyGroup"),
        ["stat gate unknown stat"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { WhileStatAtLeast = new() { Stat = "luck", Value = 6 } }, Pack.Simulation.Triggers[1]] } }, "bad-sim-trigger", "simulation.triggers[0].whileStatAtLeast"),
        ["stat gate value"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { WhileStatAtLeast = new() { Stat = "armor", Value = double.NaN } }, Pack.Simulation.Triggers[1]] } }, "bad-sim-trigger", "simulation.triggers[0].whileStatAtLeast"),
        ["stat gate on stacks"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0], Pack.Simulation.Triggers[1] with { WhileStatAtLeast = new() { Stat = "armor", Value = 6 } }] } }, "bad-sim-trigger", "simulation.triggers[1].whileStatAtLeast"),
        ["stat gate with another gate"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { WhileStatAtLeast = new() { Stat = "armor", Value = 6 } } with { WhileArmorBelow = 0.5 }, Pack.Simulation.Triggers[1]] } }, "bad-sim-trigger", "simulation.triggers[0].whileStatAtLeast"),
        ["duration stat with duration"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { DurationStat = "armor" }, Pack.Simulation.Triggers[1]] } }, "bad-sim-trigger", "simulation.triggers[0].durationStat"),
        ["duration stat unknown"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Duration = 0, DurationStat = "luck" }, Pack.Simulation.Triggers[1]] } }, "bad-sim-trigger", "simulation.triggers[0].durationStat"),
        ["duration stat on stacks"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0], Pack.Simulation.Triggers[1] with { Duration = 0, DurationStat = "armor" }] } }, "bad-sim-trigger", "simulation.triggers[1].durationStat"),
        ["while key unknown"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [.. Pack.Simulation.Triggers, Gated with { WhileKey = "headshot_mode" }] } }, "bad-sim-trigger", "simulation.triggers[2].whileKey"),
        ["while full on a buff"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [.. Pack.Simulation.Triggers, Gated with { WhileKey = "overwatch_active" }] } }, "bad-sim-trigger", "simulation.triggers[2].whileKey"),
        ["while full without key"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [.. Pack.Simulation.Triggers, Gated with { WhileKey = "" }] } }, "bad-sim-trigger", "simulation.triggers[2].whileFull"),
        ["apply on hit event"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Kind = "status", Status = "burning", On = "kill", Duration = 7, ApplyOnHit = true }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["apply on hit kind"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { On = "hit", ApplyOnHit = true }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["holstered gain kind"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { GainWhileHolstered = 0.3, WeaponTags = ["weapon"] }] } }, "bad-sim-trigger", "simulation.triggers[0].gainWhileHolstered"),
        ["holstered gain tags"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[1] with { GainWhileHolstered = 0.3 }] } }, "bad-sim-trigger", "simulation.triggers[0].gainWhileHolstered"),
        ["talent damage talent"] = (() => Pack with { Simulation = Pack.Simulation with { TalentDamage = [new() { Talent = "missing", On = "status_applied", Total = "armor", Duration = 10, Period = 0.5 }] } }, "bad-sim-talent-damage", "simulation.talentDamage[0].talent"),
        ["talent damage event"] = (() => Pack with { Simulation = Pack.Simulation with { TalentDamage = [new() { Talent = "talent_killer", On = "teleport", Total = "armor", Duration = 10, Period = 0.5 }] } }, "bad-sim-talent-damage", "simulation.talentDamage[0].on"),
        ["talent damage period"] = (() => Pack with { Simulation = Pack.Simulation with { TalentDamage = [new() { Talent = "talent_killer", On = "status_applied", Total = "armor", Duration = 10, Period = 0.3 }] } }, "bad-sim-talent-damage", "simulation.talentDamage[0].period"),
        ["talent damage no stacks"] = (() => Pack with { Simulation = Pack.Simulation with { TalentDamage = [new() { Talent = "talent_killer", On = "hit", Total = "armor", Duration = 10, Period = 0.5, MaxStacks = 0 }] } }, "bad-sim-talent-damage", "simulation.talentDamage[0].maxStacks"),
        ["talent damage too many stacks"] = (() => Pack with { Simulation = Pack.Simulation with { TalentDamage = [new() { Talent = "talent_killer", On = "hit", Total = "armor", Duration = 10, Period = 0.5, MaxStacks = 101 }] } }, "bad-sim-talent-damage", "simulation.talentDamage[0].maxStacks"),
        ["talent damage total"] = (() => Pack with { Simulation = Pack.Simulation with { TalentDamage = [new() { Talent = "talent_killer", On = "status_applied", Total = "missing", Duration = 10, Period = 0.5 }] } }, "unknown-stat", "simulation.talentDamage[0].total"),
        ["cancel event"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { CancelOn = "teleport" }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["cancelFor without cancelOn"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { CancelFor = 5 }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["negative cancelFor"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { CancelOn = "swapped", CancelFor = -1 }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["bank without pool"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[1] with { Bank = "stacks" }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["bank not a numeric input"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[1] with { Pool = "shared", PoolMax = 5, Bank = "overwatch_active" }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["clear grant kind"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[1] with { ResetOn = "reload", ClearGrant = "heal", ClearAmount = 0.05 }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["clear grant without a clear"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[1] with { ClearGrant = "shield_repair", ClearAmount = 0.03 }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["clear grant on a buff"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { ResetOn = "reload", ClearGrant = "shield_repair", ClearAmount = 0.03 }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["temporary armor without duration"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[1] with { ResetOn = "reload", ClearGrant = "temporary_armor", ClearAmount = 0.05 }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["clear amount above 1"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[1] with { ConsumeOn = "headshot", ClearGrant = "shield_repair", ClearAmount = 2 }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["trigger key not a selector"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Key = "no_such_selector" }] } }, "unknown-selector", "simulation.triggers[0].key"),
        ["trigger integer range"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0], Pack.Simulation.Triggers[1] with { Max = 9007199254740993 }] } }, "invalid-integer", "simulation.triggers[1].max"),
        ["phase value not a selector value"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [new() { Talent = "talent_killer", Key = "headshot_mode", Kind = "cycle", Phases = [new() { Until = 1, Value = "body" }, new() { Until = 2, Value = "torso" }] }] } },
            "unknown-selector-value", "simulation.triggers[0].phases[1].value"),
        ["count with no max"] = (() => CountRule(new() { Talent = "talent_killer", Key = "foes", Kind = "count" }), "bad-sim-trigger", "simulation.triggers[0]"),
        ["count with max and atLeast"] = (() => CountRule(new() { Talent = "talent_killer", Key = "foes", Kind = "count", Max = 5, AtLeast = 2 }), "bad-sim-trigger", "simulation.triggers[0]"),
        ["count on a condition with no atLeast"] = (() => CountRule(new() { Talent = "talent_killer", Key = "armed", Kind = "count" }), "bad-sim-trigger", "simulation.triggers[0]"),
        ["count on a condition with a max"] = (() => CountRule(new() { Talent = "talent_killer", Key = "armed", Kind = "count", AtLeast = 2, Max = 5 }), "bad-sim-trigger", "simulation.triggers[0]"),
        ["count with an unknown event"] = (() => CountRule(new() { Talent = "talent_killer", Key = "foes", Kind = "count", Max = 5, On = "sneeze" }), "bad-sim-trigger", "simulation.triggers[0]"),
        ["count on target_death"] = (() => CountRule(new() { Talent = "talent_killer", Key = "foes", Kind = "count", Max = 5, On = "target_death" }), "bad-sim-trigger", "simulation.triggers[0]"),
        ["count above its input"] = (() => CountRule(new() { Talent = "talent_killer", Key = "foes", Kind = "count", Max = 6 }), "bad-sim-trigger", "simulation.triggers[0]"),
        ["count on a choice"] = (() => CountRule(new() { Talent = "talent_killer", Key = "choice", Kind = "count", AtLeast = 2 }), "bad-sim-trigger", "simulation.triggers[0]"),
        ["count sharing its key"] = (() => CountRule(new() { Talent = "talent_killer", Key = "foes", Kind = "count", Max = 5 }, new() { Talent = "talent_killer", Key = "foes", Kind = "stacks", On = "hit", Max = 5, Duration = 5 }), "bad-sim-trigger", "simulation.triggers[0]"),
        ["count twice for a talent"] = (() => CountRule(new() { Talent = "talent_killer", Key = "foes", Kind = "count", Max = 5 }, new() { Talent = "talent_killer", Key = "foes", Kind = "count", Max = 5 }), "bad-sim-trigger", "simulation.triggers[0]"),
        ["count sharing its key with a reading"] = (() => CountRule(new() { Talent = "talent_killer", Key = "foes", Kind = "count", Max = 5 }, new() { Talent = "talent_party", Key = "foes", Kind = "pellets", Max = 5 }), "bad-sim-trigger", "simulation.triggers[0]"),
        ["counts differing in range"] = (() => CountRule(new() { Talent = "talent_killer", Key = "foes", Kind = "count", Max = 5 }, new() { Talent = "talent_party", Key = "foes", Kind = "count", Max = 5, Within = 10 }), "bad-sim-trigger", "simulation.triggers[0]"),
        ["counts differing in event"] = (() => CountRule(new() { Talent = "talent_killer", Key = "foes", Kind = "count", Max = 5 }, new() { Talent = "talent_party", Key = "foes", Kind = "count", Max = 5, On = "reload_end" }), "bad-sim-trigger", "simulation.triggers[0]"),
        ["counts differing in max"] = (() => CountRule(new() { Talent = "talent_killer", Key = "foes", Kind = "count", Max = 5 }, new() { Talent = "talent_party", Key = "foes", Kind = "count", Max = 4 }), "bad-sim-trigger", "simulation.triggers[0]"),
        ["counts differing in atLeast"] = (() => CountRule(new() { Talent = "talent_killer", Key = "armed", Kind = "count", AtLeast = 2 }, new() { Talent = "talent_party", Key = "armed", Kind = "count", AtLeast = 3 }), "bad-sim-trigger", "simulation.triggers[0]"),
        ["count of an unknown status"] = (() => CountRule(new() { Talent = "talent_killer", Key = "foes", Kind = "count", Max = 5, On = "second", Status = "sleepy" }), "bad-sim-trigger", "simulation.triggers[0]"),
        ["count of a status with no event"] = (() => CountRule(new() { Talent = "talent_killer", Key = "foes", Kind = "count", Max = 5, Status = "bleeding" }), "bad-sim-trigger", "simulation.triggers[0]"),
        ["counts differing in status"] = (() => CountRule(new() { Talent = "talent_killer", Key = "foes", Kind = "count", Max = 5, On = "second", Status = "bleeding" }, new() { Talent = "talent_party", Key = "foes", Kind = "count", Max = 5, On = "second", Status = "burning" }), "bad-sim-trigger", "simulation.triggers[0]"),
        ["count on another talent's reading"] = (() => CountRule(new() { Talent = "talent_killer", Key = "foes", Kind = "count", Max = 5, On = "second", Status = "bleeding" }, new() { Talent = "talent_party", Key = "foes", Kind = "status", Status = "bleeding" }), "bad-sim-trigger", "simulation.triggers[0]"),
        ["count on a reading of another status"] = (() => CountRule(new() { Talent = "talent_killer", Key = "foes", Kind = "count", Max = 5, On = "second", Status = "bleeding" }, new() { Talent = "talent_killer", Key = "foes", Kind = "status", Status = "burning" }), "bad-sim-trigger", "simulation.triggers[0]"),
        ["also reset with no reset"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[1] with { AlsoResetOn = "kill" }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["also reset the same event"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[1] with { ResetOn = "kill", AlsoResetOn = "kill" }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["also reset an unknown event"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[1] with { ResetOn = "kill", AlsoResetOn = "sneeze" }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["count with another field"] = (() => CountRule(new() { Talent = "talent_killer", Key = "foes", Kind = "count", Max = 5, Duration = 3 }), "bad-sim-trigger", "simulation.triggers[0]"),
        ["sim skill stock without an interval"] = (() => Pack with { Simulation = Pack.Simulation with { Skills = [Pack.Simulation.Skills[0] with { Stock = Pack.Simulation.Skills[0].Cooldown }] } }, "bad-sim-skill", "simulation.skills[0]"),
        ["sim skill interval without a stock"] = (() => Pack with { Simulation = Pack.Simulation with { Skills = [Pack.Simulation.Skills[0] with { StockInterval = 1 }] } }, "bad-sim-skill", "simulation.skills[0]"),
        ["sim skill rate read"] = (() => Pack with { Simulation = Pack.Simulation with { Skills = [Pack.Simulation.Skills[0] with { RateRead = "sometimes" }] } }, "bad-sim-skill", "simulation.skills[0]"),
        ["sim skill output"] = (() => Pack with { Simulation = Pack.Simulation with { Skills = [Pack.Simulation.Skills[0] with { Rate = "missing" }] } }, "bad-sim-skill", "simulation.skills[0]"),
        ["sim skill with no damage"] = (() => Pack with { Simulation = Pack.Simulation with { Skills = [Pack.Simulation.Skills[0] with { Rate = "", Impact = "" }] } }, "bad-sim-skill", "simulation.skills[0]"),
        ["sim skill rate without a window"] = (() => Pack with { Simulation = Pack.Simulation with { Skills = [Pack.Simulation.Skills[0] with { Window = "" }] } }, "bad-sim-skill", "simulation.skills[0]"),
        ["sim skill rate without a cycle"] = (() => Pack with { Simulation = Pack.Simulation with { Skills = [Pack.Simulation.Skills[0] with { Cooldown = "" }] } }, "bad-sim-skill", "simulation.skills[0]"),
        ["sim skill impact without a cooldown"] = (() => Pack with { Simulation = Pack.Simulation with { Skills = [Pack.Simulation.Skills[0] with { Rate = "", Window = "", Cooldown = "" }] } }, "bad-sim-skill", "simulation.skills[0]"),
        ["sim skill impact with a window and no rate"] = (() => Pack with { Simulation = Pack.Simulation with { Skills = [Pack.Simulation.Skills[0] with { Rate = "" }] } }, "bad-sim-skill", "simulation.skills[0]"),
        ["status damage status"] = (() => StatusDamage(new SimStatusDamageDefinition { Status = "shocked", Tick = "armor", Period = .5 }), "bad-sim-status-damage", "simulation.statusDamage[0].status"),
        ["status damage twice"] = (() => StatusDamage(new SimStatusDamageDefinition { Status = "burning", Tick = "armor", Period = .5 }, new SimStatusDamageDefinition { Status = "burning", Tick = "armor", Period = 1 }),
            "bad-sim-status-damage", "simulation.statusDamage[1].status"),
        ["status damage tick"] = (() => StatusDamage(new SimStatusDamageDefinition { Status = "bleeding", Tick = "missing", Period = .5 }), "unknown-stat", "simulation.statusDamage[0].tick"),
        ["status damage armor bonus"] = (() => StatusDamage(new SimStatusDamageDefinition { Status = "burning", Tick = "armor", Period = .5, ArmorBonus = "missing" }), "unknown-stat", "simulation.statusDamage[0].armorBonus"),
        ["impact interval with no impacts"] = (() => Pack with { Simulation = Pack.Simulation with { Skills = [Pack.Simulation.Skills[0] with { ImpactInterval = Pack.Simulation.Skills[0].Rate }] } }, "bad-sim-skill", "simulation.skills[0]"),
        ["impact interval output"] = (() => Pack with { Simulation = Pack.Simulation with { Skills = [Pack.Simulation.Skills[0] with { Impact = Pack.Simulation.Skills[0].Rate, Impacts = Pack.Simulation.Skills[0].Rate, ImpactInterval = "missing" }] } }, "bad-sim-skill", "simulation.skills[0]"),
        ["status rate with no window"] = (() => Pack with { Simulation = Pack.Simulation with { Skills = [Pack.Simulation.Skills[0] with { StatusRate = Pack.Simulation.Skills[0].Rate }] } }, "bad-sim-skill", "simulation.skills[0]"),
        ["status rate on no burn"] = (() => Pack with { Simulation = Pack.Simulation with { Skills = [Pack.Simulation.Skills[0] with { StatusRate = Pack.Simulation.Skills[0].Rate, StatusWindow = Pack.Simulation.Skills[0].Rate, Status = "shocked" }] } }, "bad-sim-skill", "simulation.skills[0]"),
        ["status armor bonus stat"] = (() => Pack with { Simulation = Pack.Simulation with { Skills = [Pack.Simulation.Skills[0] with { StatusRate = Pack.Simulation.Skills[0].Rate, StatusWindow = Pack.Simulation.Skills[0].Rate, Status = "burning", StatusArmorBonus = "nope" }] } }, "unknown-stat", "simulation.skills[0].statusArmorBonus"),
        ["rate armor bonus stat"] = (() => Pack with { Simulation = Pack.Simulation with { Skills = [Pack.Simulation.Skills[0] with { RateArmorBonus = "missing" }] } }, "unknown-stat", "simulation.skills[0].rateArmorBonus"),
        ["rate armor bonus with no rate"] = (() => Pack with { Simulation = Pack.Simulation with { Skills = [Pack.Simulation.Skills[0] with { Rate = "", Window = "", RateArmorBonus = "armor" }] } }, "bad-sim-skill", "simulation.skills[0]"),
        ["status damage period"] = (() => StatusDamage(new SimStatusDamageDefinition { Status = "burning", Tick = "armor", Period = 0 }), "bad-sim-status-damage", "simulation.statusDamage[0].period"),
        ["status damage period NaN"] = (() => StatusDamage(new SimStatusDamageDefinition { Status = "burning", Tick = "armor", Period = double.NaN }), "bad-sim-status-damage", "simulation.statusDamage[0].period"),
        // Presentation
        ["theme variant"] = (() => Pack with { Presentation = Pack.Presentation with { Theme = Pack.Presentation.Theme! with { Variant = "dim" } } }, "bad-theme-variant", "presentation.theme.variant"),
        ["theme color"] = (() => Pack with { Presentation = Pack.Presentation with { Theme = Pack.Presentation.Theme! with { Colors = Map("background", "black") } } }, "bad-color", "presentation.theme.colors.background"),
        ["color with trailing newline"] = (() => Pack with { Presentation = Pack.Presentation with { Theme = Pack.Presentation.Theme! with { Colors = Map("background", "#000000\n") } } }, "bad-color", "presentation.theme.colors.background"),
        ["theme color name"] = (() => Pack with { Presentation = Pack.Presentation with { Theme = Pack.Presentation.Theme! with { Colors = Map("typo", "#000000") } } }, "unknown-theme-color", "presentation.theme.colors.typo"),
        ["layout slot"] = (() => Layout(layout => layout with { SlotGroups = [layout.SlotGroups[0] with { Slots = ["hat"] }] }), "unknown-slot", "presentation.layout.slotGroups[0].slots[0]"),
        ["layout slot twice"] = (() => Layout(layout => layout with { SlotGroups = [layout.SlotGroups[0], layout.SlotGroups[1] with { Slots = ["primary_weapon"] }] }), "duplicate-layout-slot", "presentation.layout.slotGroups[1].slots[0]"),
        ["headline"] = (() => Layout(layout => layout with { Headline = [new() { Stat = "armor", SkillRates = true }] }), "invalid-layout-headline", "presentation.layout.headline[0]"),
        ["prototype colour"] = (() => Layout(layout => layout with { Prototype = new() { Name = "Prototype", Color = "purple" } }), "bad-color", "presentation.layout.prototype.color"),
        ["prototype name"] = (() => Layout(layout => layout with { Prototype = new() { Name = " ", Color = "#AD78F1" } }), "empty-name", "presentation.layout.prototype.name"),
        ["headline output"] = (() => Layout(layout => layout with { Headline = [new() { BuildOutput = "missing", Label = "X" }] }), "unknown-build-output", "presentation.layout.headline[0].buildOutput"),
        ["summary stat"] = (() => Layout(layout => layout with { Summary = [new() { Name = "S", Stats = ["missing"] }] }), "unknown-stat", "presentation.layout.summary[0].stats[0]"),
        ["default section"] = (() => Layout(layout => layout with { AssumptionSections = [new("Weapon", false), new("Talents", false)] }), "invalid-assumption-sections", "presentation.layout.assumptionSections"),
        ["selector section"] = (() => Pack with { Selectors = [Pack.Selectors[0] with { Section = "Nope" }, Pack.Selectors[1]] }, "unknown-assumption-section", "selectors.headshot_mode.section"),
        ["section without a layout"] = (() => Pack with { Selectors = [Pack.Selectors[0] with { Section = "Nope" }, Pack.Selectors[1]], Presentation = Pack.Presentation with { Layout = null } },
            "unknown-assumption-section", "selectors.headshot_mode.section"),
        ["input section"] = (() => Pack with { NumericContextInputs = [Pack.NumericContextInputs[0] with { Section = "Nope" }] }, "unknown-assumption-section", "numericInputs.stacks.section"),
        ["attribute category"] = (() => Pack with { Attributes = [Pack.Attributes[0] with { Category = "utility" }, Pack.Attributes[1]] }, "unknown-attribute-category", "attributes.weapon_damage_core.category"),
        ["category color"] = (() => Layout(layout => layout with { AttributeCategories = [new("offensive", "Offensive", "red"), layout.AttributeCategories[1]] }), "bad-color", "presentation.layout.attributeCategories[0].color"),
        ["artwork path"] = (() => Pack with { Presentation = Pack.Presentation with { ArtworkAssets = [Pack.Presentation.ArtworkAssets[0] with { Path = "assets/../secret" }] } }, "bad-artwork-path", "presentation.artwork.assets[0].path"),
        ["artwork hash"] = (() => Pack with { Presentation = Pack.Presentation with { ArtworkAssets = [Pack.Presentation.ArtworkAssets[0] with { SHA256 = "ABC" }] } }, "bad-artwork-checksum", "presentation.artwork.assets[0].sha256"),
        ["artwork source"] = (() => Pack with { Presentation = Pack.Presentation with { ArtworkAssets = [Pack.Presentation.ArtworkAssets[0] with { SourceID = "missing" }] } }, "unknown-artwork-source", "presentation.artwork.assets[0].sourceId"),
        ["icon asset"] = (() => Pack with { Presentation = Pack.Presentation with { Icons = Pack.Presentation.Icons.SetItem("slots", Map("mask", "missing")) } }, "unknown-artwork-asset", "presentation.icons.slots.mask"),
        ["icon owner"] = (() => Pack with { Presentation = Pack.Presentation with { Icons = Pack.Presentation.Icons.SetItem("slots", Map("hat", "icon_mask")) } }, "unknown-icon-owner", "presentation.icons.slots.hat"),
        ["icon group"] = (() => Pack with { Presentation = Pack.Presentation with { Icons = Pack.Presentation.Icons.SetItem("weapons", Map("mask", "icon_mask")) } }, "unknown-icon-group", "presentation.icons.weapons"),
        ["item art"] = (() => Pack with { Presentation = Pack.Presentation with { ItemArt = Pack.Presentation.ItemArt.SetItem("rifle_a", new("missing", "")) } }, "unknown-artwork-asset", "presentation.itemArt.rifle_a.image"),
        // Share codes
        ["legacy slot"] = (() => Pack with { LegacyShareOrder = Pack.LegacyShareOrder with { Slots = ["hat"] } }, "unknown-slot", "shareCodes.legacyOrder.slots[0]"),
    };

    public static TheoryData<string> CaseNames => [.. Cases.Keys];

    [Fact]
    public void TheTestPackIsValidAndCompilesAllyEffects()
    {
        var result = PackValidator.Validate(Pack);
        Assert.Empty(result.Diagnostics);
        var compiled = Assert.IsType<CompiledPackExpressions>(result.Compiled);
        Assert.Contains("talents.talent_party.allyEffects.party_boost.modifiers[0].value", compiled.AllyEffectExpressions.Keys);
        Assert.Contains("items.mask_a.modifiers[0].requires", PackValidator.Validate(Gate(ImmutableDictionary<string, string>.Empty)).Diagnostics.Select(diagnostic => diagnostic.Path));
    }

    [Theory]
    [InlineData("second", 7, 35, "")]
    [InlineData("", 0, 5, "Only a status rule with an event takes a count, a duration or an interval")]
    [InlineData("second", 7, -1, "The trigger interval can't be negative")]
    public void StatusRulesTakeAnIntervalOnlyWithAnEvent(string on, double duration, double every, string message)
    {
        // Dragon's Glare's draft shape (R36) applies on whole seconds at most every 35 s.
        var pack = Pack with
        {
            Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Kind = "status", Status = "burning", On = on, Duration = duration, Every = every }] }
        };
        var errors = PackValidator.Validate(pack).Diagnostics.Where(diagnostic => diagnostic.Code == "bad-sim-trigger").Select(diagnostic => diagnostic.Message);
        if (message.Length == 0) { Assert.Empty(errors); }
        else { Assert.Equal([message], errors); }
    }

    // Ardent's shape (R39): a burn on hit while the stacks meter is full.
    private static SimTriggerDefinition Gated => new()
    { Talent = "talent_holster", Key = "headshot_mode", On = "hit", Kind = "status", Status = "burning", Duration = 7, ApplyOnHit = true, WhileKey = "stacks", WhileFull = true };

    [Fact]
    public void GatedStatusesAndMagazineCapsValidateAndRoundTrip()
    {
        var pack = Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0], Pack.Simulation.Triggers[1] with { MaxMagazine = 0.5 }, Gated] } };
        var result = PackValidator.Load(PackWriter.Write(pack));
        Assert.Empty(result.Diagnostics);
        var triggers = result.Compiled!.Pack.Simulation.Triggers;
        Assert.Equal(0.5, triggers[1].MaxMagazine);
        Assert.Equal(("stacks", true), (triggers[2].WhileKey, triggers[2].WhileFull));
        // A buff key gates without whileFull.
        Assert.Empty(PackValidator.Validate(Pack with { Simulation = Pack.Simulation with { Triggers = [.. Pack.Simulation.Triggers, Gated with { WhileKey = "overwatch_active", WhileFull = false }] } }).Diagnostics);
        var written = System.Text.Encoding.UTF8.GetString(PackWriter.Write(Pack)[PackFiles.Simulation].Span);
        Assert.DoesNotContain("maxMagazine", written, StringComparison.Ordinal);
        Assert.DoesNotContain("whileKey", written, StringComparison.Ordinal);
    }

    private static GamePack CountRule(SimTriggerDefinition rule, SimTriggerDefinition? other = null) => Pack with
    {
        NumericContextInputs = [.. Pack.NumericContextInputs, new() { ID = "foes", Name = "Foes", Min = 0, Max = 5, Step = 1 }],
        Selectors = [.. Pack.Selectors, new() { ID = "armed", Name = "Armed", Values = ["true"] }, new() { ID = "choice", Name = "Choice", Values = ["left", "right"] }],
        Simulation = Pack.Simulation with { Triggers = other is null ? [rule] : [rule, other] }
    };

    // A pack with an assumption that may follow the fight distance, as the enemy hit rate does (P12).
    private static GamePack DistancePack => Pack with
    { NumericContextInputs = [.. Pack.NumericContextInputs, new() { ID = "hit_rate", Name = "Hit rate", Min = 0, Max = 100, Step = 5, Default = 50, Section = "Talents" }] };

    private static SimArmorKit Kit => new() { Repair = 1, UseSeconds = 2.5, ExtendSeconds = 0.3, MaxUseSeconds = 4.9 };

    [Fact]
    public void AnArmorKitValidatesAndRoundTrips()
    {
        var pack = Pack with { Simulation = Pack.Simulation with { ArmorKit = Kit with { ReplacedByTalents = [Pack.Talents[0].ID] } } };
        var result = PackValidator.Load(PackWriter.Write(pack));
        Assert.Empty(result.Diagnostics);
        Assert.Equal(pack.Simulation.ArmorKit!.UseSeconds, result.Compiled!.Pack.Simulation.ArmorKit!.UseSeconds);
        Assert.Equal(pack.Simulation.ArmorKit.ReplacedByTalents, result.Compiled.Pack.Simulation.ArmorKit!.ReplacedByTalents);
    }

    [Theory]
    [InlineData(0, 2.5, 0.3, 4.9)]
    [InlineData(1.5, 2.5, 0.3, 4.9)]
    [InlineData(1, 0, 0.3, 4.9)]
    [InlineData(1, 2.5, -1, 4.9)]
    [InlineData(1, 2.5, 0.3, 2)]
    public void AnArmorKitsNumbersAreChecked(double repair, double use, double extend, double longest)
    {
        var kit = new SimArmorKit { Repair = repair, UseSeconds = use, ExtendSeconds = extend, MaxUseSeconds = longest };
        Assert.Contains(PackValidator.Validate(Pack with { Simulation = Pack.Simulation with { ArmorKit = kit } }).Diagnostics, diagnostic => diagnostic.Code == "bad-sim-armor-kit");
    }

    [Fact]
    public void AnArmorKitsReplacementsMustExist()
    {
        var kit = Kit with { ReplacedBySpecializations = ["nobody"], ReplacedByTalents = ["nothing"] };
        Assert.Equal(2, PackValidator.Validate(Pack with { Simulation = Pack.Simulation with { ArmorKit = kit } }).Diagnostics.Count(diagnostic => diagnostic.Code == "bad-sim-armor-kit"));
    }

    [Fact]
    public void DistanceDefaultsValidateAndRoundTrip()
    {
        // P12's shape: an input's default by fight distance, up to 15 m, then beyond.
        var pack = DistancePack with { Simulation = DistancePack.Simulation with { DistanceDefaults = [new() { Input = "hit_rate", Bands = [new() { UpTo = 15, Value = 70 }, new() { Value = 30 }] }] } };
        var result = PackValidator.Load(PackWriter.Write(pack));
        Assert.Empty(result.Diagnostics);
        var entry = Assert.Single(result.Compiled!.Pack.Simulation.DistanceDefaults);
        Assert.Equal("hit_rate", entry.Input);
        Assert.Equal(new (double?, double)[] { (15, 70), (null, 30) }, entry.Bands.Select(band => (band.UpTo, band.Value)));
        // A repeated input ID is reported by the input checks without the distance check failing on it.
        var repeated = pack with { NumericContextInputs = [.. pack.NumericContextInputs, pack.NumericContextInputs[^1]] };
        Assert.Contains(PackValidator.Validate(repeated).Diagnostics, diagnostic => diagnostic.Code == "duplicate-id");
        Assert.DoesNotContain("distanceDefaults", System.Text.Encoding.UTF8.GetString(PackWriter.Write(Pack)[PackFiles.Simulation].Span), StringComparison.Ordinal);
    }

    [Fact]
    public void CritPelletIsAnEventAndAHitEvent()
    {
        // R43: Clutch's repair on each critical pellet; a status may also apply on it.
        var pellet = Pack.Simulation.Triggers[0] with { On = "crit_pellet" };
        Assert.Empty(PackValidator.Validate(Pack with { Simulation = Pack.Simulation with { Triggers = [pellet, Pack.Simulation.Triggers[1]] } }).Diagnostics);
    }

    [Fact]
    public void HitPelletIsAnEventAndAHitEvent()
    {
        // Afterburn on a shotgun counts the pellets that land, not the shots; a status may also apply on one.
        var pellet = Pack.Simulation.Triggers[0] with { On = "hit_pellet" };
        Assert.Empty(PackValidator.Validate(Pack with { Simulation = Pack.Simulation with { Triggers = [pellet, Pack.Simulation.Triggers[1]] } }).Diagnostics);
        var status = pellet with { Kind = "status", Status = "burning", ApplyOnHit = true };
        Assert.DoesNotContain(PackValidator.Validate(Pack with { Simulation = Pack.Simulation with { Triggers = [status, Pack.Simulation.Triggers[1]] } }).Diagnostics,
            diagnostic => diagnostic.Message.Contains("applyOnHit", StringComparison.Ordinal));
    }

    [Fact]
    public void ArmorGatesAndStatDurationsValidateAndRoundTrip()
    {
        // Clutch's shape (R41): a post-kill flag lasting a stat's value, and a crit repair while armor is low or the flag holds.
        var repair = Pack.Simulation.Triggers[0] with
        {
            On = "crit",
            Duration = 1,
            Grant = "missing_armor_repair",
            GrantAmount = 0.025,
            WhileArmorBelow = 0.15,
            WhileKey = "stacks"
        };
        var gated = repair with { WhileKey = "", WhileArmorBelow = 0, WhileStatAtLeast = new() { Stat = "armor", Value = 6.5 }, Every = 90, EveryGroup = "shared" };
        var pack = Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Duration = 0, DurationStat = "armor" }, Pack.Simulation.Triggers[1], repair, gated, gated with { On = "hit", Talent = Pack.Simulation.Triggers[1].Talent }] } };
        var result = PackValidator.Load(PackWriter.Write(pack));
        Assert.Empty(result.Diagnostics);
        var triggers = result.Compiled!.Pack.Simulation.Triggers;
        Assert.Equal(("armor", 0.0), (triggers[0].DurationStat, triggers[0].Duration));
        Assert.Equal((0.15, "stacks"), (triggers[2].WhileArmorBelow, triggers[2].WhileKey));
        Assert.Equal(new SimStatGate { Stat = "armor", Value = 6.5 }, triggers[3].WhileStatAtLeast);
        Assert.Null(triggers[2].WhileStatAtLeast);
        Assert.Equal(("shared", ""), (triggers[3].EveryGroup, triggers[2].EveryGroup));
        var written = System.Text.Encoding.UTF8.GetString(PackWriter.Write(Pack)[PackFiles.Simulation].Span);
        Assert.DoesNotContain("whileArmorBelow", written, StringComparison.Ordinal);
        Assert.DoesNotContain("whileStatAtLeast", written, StringComparison.Ordinal);
        Assert.DoesNotContain("everyGroup", written, StringComparison.Ordinal);
        Assert.DoesNotContain("durationStat", written, StringComparison.Ordinal);
    }

    [Fact]
    public void CountWindowsValidateAndRoundTrip()
    {
        // Pummel's 20 s drop-off (R38) on a counted buff.
        var pack = Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Count = 3, CountWindow = 20 }] } };
        var result = PackValidator.Load(PackWriter.Write(pack));
        Assert.Empty(result.Diagnostics);
        Assert.Equal(20, result.Compiled!.Pack.Simulation.Triggers[0].CountWindow);
        Assert.DoesNotContain("countWindow", System.Text.Encoding.UTF8.GetString(PackWriter.Write(Pack)[PackFiles.Simulation].Span), StringComparison.Ordinal);
    }

    [Fact]
    public void HolsteredGainsValidateAndRoundTrip()
    {
        // Quick Draw's shape (R38): a stacks rule with no event that gains while no tagged weapon is in hand, forcing a headshot
        // while it holds stacks and spent by a tagged shot.
        var stacks = Pack.Simulation.Triggers[1];
        var pack = Pack with
        {
            Simulation = Pack.Simulation with
            { Triggers = [stacks with { On = "", GainWhileHolstered = 0.3, ConsumeOn = "shot", WeaponTags = ["weapon_type_rifle"], ForceHeadshot = true }] }
        };
        var result = PackValidator.Load(PackWriter.Write(pack));
        Assert.Empty(result.Diagnostics);
        var rule = result.Compiled!.Pack.Simulation.Triggers[0];
        Assert.Equal((0.3, true, ""), (rule.GainWhileHolstered, rule.ForceHeadshot, rule.On));
    }

    [Fact]
    public void TalentDamageValidatesAndRoundTrips()
    {
        // Toxic Delivery's channel (R38): a total spread over 0.5 s ticks for 10 s.
        var pack = Pack with { Simulation = Pack.Simulation with { TalentDamage = [new() { Talent = "talent_killer", On = "status_applied", Total = "armor", Duration = 10, Period = 0.5 }] } };
        var result = PackValidator.Load(PackWriter.Write(pack));
        Assert.Empty(result.Diagnostics);
        Assert.Equal(pack.Simulation.TalentDamage, result.Compiled!.Pack.Simulation.TalentDamage);
        Assert.DoesNotContain("talentDamage", System.Text.Encoding.UTF8.GetString(PackWriter.Write(Pack)[PackFiles.Simulation].Span), StringComparison.Ordinal);
        // One stack is the default, and the writer leaves it out.
        Assert.Equal(1, result.Compiled.Pack.Simulation.TalentDamage[0].MaxStacks);
        Assert.DoesNotContain("maxStacks", System.Text.Encoding.UTF8.GetString(PackWriter.Write(pack)[PackFiles.Simulation].Span), StringComparison.Ordinal);
    }

    [Fact]
    public void StackedTalentDamageValidatesAndRoundTrips()
    {
        // Plague of the Outcasts' shape: every hit starts a debuff, up to 50 on a target.
        var pack = Pack with { Simulation = Pack.Simulation with { TalentDamage = [new() { Talent = "talent_killer", On = "hit", Total = "armor", Duration = 10, Period = 0.5, MaxStacks = 50 }] } };
        var files = PackWriter.Write(pack);
        Assert.Contains("\"maxStacks\": 50", System.Text.Encoding.UTF8.GetString(files[PackFiles.Simulation].Span), StringComparison.Ordinal);
        var result = PackValidator.Load(files);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(pack.Simulation.TalentDamage, result.Compiled!.Pack.Simulation.TalentDamage);
    }

    [Fact]
    public void ARulesRequiredContextValidatesAndRoundTrips()
    {
        // Calculated's shape: a rule that takes part only in a fight whose context holds a selector value.
        var pack = Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { RequiresContext = ImmutableDictionary<string, string>.Empty.Add("headshot_mode", "headshot") }] } };
        var result = PackValidator.Load(PackWriter.Write(pack));
        Assert.Empty(result.Diagnostics);
        Assert.Equal("headshot", result.Compiled!.Pack.Simulation.Triggers[0].RequiresContext!["headshot_mode"]);
        Assert.Null(PackValidator.Load(PackWriter.Write(Pack)).Compiled!.Pack.Simulation.Triggers[0].RequiresContext);
    }

    [Fact]
    public void AChanceAndTheSkillKillEventValidateAndRoundTrip()
    {
        // Skilled's shape: a skill kill has a 25% chance to reset skill cooldowns.
        var pack = Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { On = "skill_kill", Chance = 0.25, Grant = "skill_cooldown", GrantAmount = 1 }] } };
        var files = PackWriter.Write(pack);
        Assert.Contains("\"chance\": 0.25", System.Text.Encoding.UTF8.GetString(files[PackFiles.Simulation].Span), StringComparison.Ordinal);
        var result = PackValidator.Load(files);
        Assert.Empty(result.Diagnostics);
        Assert.Equal((0.25, "skill_kill"), (result.Compiled!.Pack.Simulation.Triggers[0].Chance, result.Compiled.Pack.Simulation.Triggers[0].On));
        // 1 is the default, and the writer leaves it out.
        Assert.Equal(1, PackValidator.Load(PackWriter.Write(Pack)).Compiled!.Pack.Simulation.Triggers[0].Chance);
        Assert.DoesNotContain("\"chance\"", System.Text.Encoding.UTF8.GetString(PackWriter.Write(Pack)[PackFiles.Simulation].Span), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePrototypeLayoutValidatesAndRoundTrips()
    {
        // R74: the game's loot quality colour for Prototype, beside the rarities.
        var pack = Layout(layout => layout with { Prototype = new() { Name = "Prototype", Color = "#AD78F1" } });
        var files = PackWriter.Write(pack);
        Assert.Contains("\"prototype\"", System.Text.Encoding.UTF8.GetString(files[PackFiles.Presentation].Span), StringComparison.Ordinal);
        var result = PackValidator.Load(files);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(new LayoutPrototype { Name = "Prototype", Color = "#AD78F1" }, result.Compiled!.Pack.Presentation.Layout!.Prototype);
        // A pack without one writes none, and reads back without one.
        Assert.DoesNotContain("\"prototype\"", System.Text.Encoding.UTF8.GetString(PackWriter.Write(Pack)[PackFiles.Presentation].Span), StringComparison.Ordinal);
        Assert.Null(PackValidator.Load(PackWriter.Write(Pack)).Compiled!.Pack.Presentation.Layout!.Prototype);
    }

    [Fact]
    public void GrantsValidateAndRoundTrip()
    {
        // Ablative Nano-Plating's self part (R36): temporary armor each time the rule fires.
        var pack = Pack with
        {
            Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Grant = "temporary_armor", GrantAmount = 0.8, GrantDuration = 10 }] }
        };
        var result = PackValidator.Load(PackWriter.Write(pack));
        Assert.Empty(result.Diagnostics);
        var rule = result.Compiled!.Pack.Simulation.Triggers[0];
        Assert.Equal(("temporary_armor", 0.8, 10.0), (rule.Grant, rule.GrantAmount, rule.GrantDuration));
        // R41: Preservation's repair over time, Clutch's share of missing armor and Doctor Home's instant kit.
        // P13: a reset (Skilled), a share (Calculated's 10%) and seconds (Tag Team's 6 s) off skill cooldowns, and a kit kept (Efficient).
        foreach (var (grant, amount, duration, instances) in new[] { ("armor_repair", 0.1, 5.0, "independent"), ("missing_armor_repair", 0.025, 0.0, ""), ("armor_repair", 0.5, 0.0, ""),
            ("skill_cooldown", 1.0, 0.0, ""), ("skill_cooldown", 0.1, 0.0, ""), ("skill_cooldown_seconds", 6.0, 0.0, ""), ("armor_kit_refund", 1.0, 0.0, "") })
        {
            var repaired = Pack with
            {
                Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { On = grant == "armor_kit_refund" ? "armor_kit" : Pack.Simulation.Triggers[0].On, Grant = grant, GrantAmount = amount, GrantDuration = duration, GrantInstances = instances }] }
            };
            var loaded = PackValidator.Load(PackWriter.Write(repaired));
            Assert.Empty(loaded.Diagnostics);
            var back = loaded.Compiled!.Pack.Simulation.Triggers[0];
            Assert.Equal((grant, amount, duration, instances), (back.Grant, back.GrantAmount, back.GrantDuration, back.GrantInstances));
        }
        // R85: Ignited's one count taking weapon and skill kills.
        var ignited = Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Kind = "status", Status = "burning", Duration = 7, Count = 4, Also = [new() { On = "skill_kill" }] }] } };
        var loadedIgnited = PackValidator.Load(PackWriter.Write(ignited));
        Assert.Empty(loadedIgnited.Diagnostics);
        Assert.Equal("skill_kill", Assert.Single(loadedIgnited.Compiled!.Pack.Simulation.Triggers[0].Also).On);
        // R86: the pack's JSON may leave the status also's gain out, as the schema says; a stacks also without one is refused.
        var bytes = PackWriter.Write(ignited).ToDictionary(pair => pair.Key, pair => pair.Value);
        var simulation = System.Text.Encoding.UTF8.GetString(bytes[PackFiles.Simulation].Span);
        Assert.DoesNotContain("\"gain\"", simulation, StringComparison.Ordinal);
        Assert.Empty(PackValidator.Load(bytes).Diagnostics);
        var stacksWithout = Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[1] with { Also = [new() { On = "headshot" }] }] } };
        Assert.Contains(PackValidator.Load(PackWriter.Write(stacksWithout)).Diagnostics, entry => entry.Code == "bad-sim-trigger" && entry.Message.Contains("positive gains", StringComparison.Ordinal));
        // R82: Efficient's activation delay, and a kit grant scaled by a stat.
        var efficient = Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { ActiveAfter = 10, Grant = "temporary_armor", GrantAmountStat = Pack.Stats[0].ID, GrantDuration = 10 }] } };
        var loadedEfficient = PackValidator.Load(PackWriter.Write(efficient));
        Assert.Empty(loadedEfficient.Diagnostics);
        Assert.Equal((10d, Pack.Stats[0].ID), (loadedEfficient.Compiled!.Pack.Simulation.Triggers[0].ActiveAfter, loadedEfficient.Compiled.Pack.Simulation.Triggers[0].GrantAmountStat));
        // P13: Skilled's banked reset, held until a cooldown starts and landing 1 s after.
        var holding = Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Grant = "skill_cooldown", GrantAmount = 1, GrantHoldDelay = 1 }] } };
        var held = PackValidator.Load(PackWriter.Write(holding));
        Assert.Empty(held.Diagnostics);
        Assert.Equal(1, held.Compiled!.Pack.Simulation.Triggers[0].GrantHoldDelay);
        Assert.DoesNotContain("grantHoldDelay", System.Text.Encoding.UTF8.GetString(PackWriter.Write(pack)[PackFiles.Simulation].Span), StringComparison.Ordinal);
        Assert.DoesNotContain("grantInstances", System.Text.Encoding.UTF8.GetString(PackWriter.Write(pack)[PackFiles.Simulation].Span), StringComparison.Ordinal);
        Assert.DoesNotContain("\"grant", System.Text.Encoding.UTF8.GetString(PackWriter.Write(Pack)[PackFiles.Simulation].Span), StringComparison.Ordinal);
    }

    [Fact]
    public void WeaponTagsAndForcedHeadshotsValidateAndRoundTrip()
    {
        // Resolved's and Quick Draw's shapes (R36): a rule that reacts only to events from weapons with these tags, and a ready
        // rule that forces the next hit to a headshot.
        var pack = Pack with
        {
            Simulation = Pack.Simulation with
            { Triggers = [Pack.Simulation.Triggers[0] with { Kind = "ready", On = "hit", Duration = 2, ForceHeadshot = true, WeaponTags = ["weapon_type_rifle"] }] }
        };
        var files = PackWriter.Write(pack);
        var result = PackValidator.Load(files);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(["weapon_type_rifle"], result.Compiled!.Pack.Simulation.Triggers[0].WeaponTags);
        Assert.True(result.Compiled.Pack.Simulation.Triggers[0].ForceHeadshot);
        Assert.DoesNotContain("weaponTags", System.Text.Encoding.UTF8.GetString(PackWriter.Write(Pack)[PackFiles.Simulation].Span), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void RejectsInvalidPacks(string name)
    {
        var (pack, code, path) = Cases[name];
        var result = PackValidator.Validate(pack());
        Assert.Null(result.Compiled);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Severity == "error" && diagnostic.Code == code && diagnostic.Path == path);
    }

    [Fact]
    public void CountRulesTakeAnInputOrACondition()
    {
        foreach (var rule in new SimTriggerDefinition[] {
            new() { Talent = "talent_killer", Key = "foes", Kind = "count", Max = 5 },
            new() { Talent = "talent_killer", Key = "foes", Kind = "count", Max = 5, On = "reload_end", Within = 15 },
            new() { Talent = "talent_killer", Key = "armed", Kind = "count", AtLeast = 2, Within = 10 },
            new() { Talent = "talent_killer", Key = "foes", Kind = "count", Max = 5, On = "second", Status = "bleeding", Within = 15 } })
        {
            Assert.DoesNotContain(PackValidator.Validate(CountRule(rule)).Diagnostics, diagnostic => diagnostic.Severity == "error");
            // A pack read from files holds its lists differently, and must pass too.
            Assert.DoesNotContain(PackValidator.Load(PackWriter.Write(CountRule(rule))).Diagnostics, diagnostic => diagnostic.Severity == "error");
            // The same rule for another talent may share the key, as a talent and its Perfect version do.
            var shared = CountRule(rule, rule with { Talent = Pack.Talents.First(talent => talent.ID != rule.Talent).ID });
            Assert.DoesNotContain(PackValidator.Load(PackWriter.Write(shared)).Diagnostics, diagnostic => diagnostic.Severity == "error");
        }
    }

    [Fact]
    public void TwoTalentsMayShareACountAndItsReading()
    {
        SimTriggerDefinition Count(string talent) => new() { Talent = talent, Key = "foes", Kind = "count", Max = 5, On = "second", Status = "bleeding" };
        SimTriggerDefinition Reading(string talent) => new() { Talent = talent, Key = "foes", Kind = "status", Status = "bleeding" };
        var pack = CountRule(Count("talent_killer"), Count("talent_party"));
        pack = pack with { Simulation = pack.Simulation with { Triggers = [.. pack.Simulation.Triggers, Reading("talent_killer"), Reading("talent_party")] } };
        Assert.DoesNotContain(PackValidator.Load(PackWriter.Write(pack)).Diagnostics, diagnostic => diagnostic.Severity == "error");
    }

    [Fact]
    public void ACountMayShareAKeyWithItsTalentsReading()
    {
        var pack = CountRule(new() { Talent = "talent_killer", Key = "foes", Kind = "count", Max = 5, On = "second", Status = "bleeding" }, new() { Talent = "talent_killer", Key = "foes", Kind = "status", Status = "bleeding" });
        Assert.DoesNotContain(PackValidator.Load(PackWriter.Write(pack)).Diagnostics, diagnostic => diagnostic.Severity == "error");
    }

    [Fact]
    public void AlsoResetOnSurvivesTheFiles()
    {
        var pack = Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[1] with { ResetOn = "kill", AlsoResetOn = "target_changed" }] } };
        Assert.Equal("target_changed", Assert.IsType<CompiledPackExpressions>(PackValidator.Load(PackWriter.Write(pack)).Compiled).Pack.Simulation.Triggers[0].AlsoResetOn);
    }

    [Fact]
    public void ArmorBonusesSurviveTheFiles()
    {
        var pack = Pack with
        {
            Simulation = Pack.Simulation with
            {
                Skills = [Pack.Simulation.Skills[0] with { RateArmorBonus = "armor" }],
                StatusDamage = [new() { Status = "burning", Tick = "armor", Period = .5, ArmorBonus = "armor" }]
            }
        };
        var read = Assert.IsType<CompiledPackExpressions>(PackValidator.Load(PackWriter.Write(pack)).Compiled).Pack;
        Assert.Equal("armor", read.Simulation.Skills[0].RateArmorBonus);
        Assert.Equal("armor", read.Simulation.StatusDamage[0].ArmorBonus);
    }

    [Fact]
    public void ASkillRuleMayBeAnImpactOnACooldown()
    {
        var pack = Pack with { Simulation = Pack.Simulation with { Skills = [new() { Skill = "turret", Impact = "damage", Cooldown = "duration" }] } };
        Assert.DoesNotContain(PackValidator.Validate(pack).Diagnostics, diagnostic => diagnostic.Severity == "error");
        // A window may come round by its own end, with no cooldown.
        var fromEnd = Pack with { Simulation = Pack.Simulation with { Skills = [new() { Skill = "turret", Rate = "dps", Window = "duration", CooldownFromEnd = true }] } };
        Assert.DoesNotContain(PackValidator.Validate(fromEnd).Diagnostics, diagnostic => diagnostic.Severity == "error");
    }

    [Fact]
    public void AdvisoryDiagnosticsKeepTheCompiledPack()
    {
        var unreachable = PackValidator.Validate(Pack with { Sets = [Pack.Sets[0] with { Bonuses = [Pack.Sets[0].Bonuses[0] with { Pieces = 2 }] }] });
        Assert.NotNull(unreachable.Compiled);
        Assert.Equal("unreachable-bonus", Assert.Single(unreachable.Diagnostics).Code);
        var undeclared = PackValidator.Validate(Pack with { Capabilities = [] });
        Assert.NotNull(undeclared.Compiled);
        Assert.Equal(Pack.Capabilities.Length, undeclared.Diagnostics.Count(diagnostic => diagnostic.Severity == "warning" && diagnostic.Code == "capability-undeclared"));
    }

    [Fact]
    public void DiagnosticsFollowTraversalOrder()
    {
        var pack = Pack with
        {
            Stats = [Pack.Stats[0] with { Format = "bad" }, .. Pack.Stats[1..]],
            Mods = [Pack.Mods[0] with { Modifiers = [Pack.Mods[0].Modifiers[0] with { Op = "inc", Value = new("missing") }] }],
            Presentation = Pack.Presentation with { Theme = Pack.Presentation.Theme! with { Variant = "dim" } },
            LegacyShareOrder = Pack.LegacyShareOrder with { Slots = ["hat"] }
        };
        var first = PackValidator.Validate(pack).Diagnostics;
        // Rules checks, then expressions, then presentation, then share codes; never re-sorted.
        Assert.Equal(new[] { "invalid-format", "invalid-modifier-op", "unknown-stat", "bad-theme-variant", "unknown-slot" }, Codes(first));
        Assert.Equal(first, PackValidator.Validate(pack).Diagnostics);
    }

    [Fact]
    public void RejectsWeaponTalentsWhileHolsteredOnlyOutsideWeaponSlots()
    {
        // talent_holster is choosable on rifles, so whileHolstered is meaningful there.
        Assert.Empty(PackValidator.Validate(Pack).Diagnostics.Where(diagnostic => diagnostic.Code == "invalid-while-holstered"));
        var gearOnly = Pack with { Items = [.. Pack.Items.Select(item => item.Slot == "mask" ? item : item with { TalentSlots = [] })] };
        Assert.Contains(PackValidator.Validate(gearOnly).Diagnostics, diagnostic => diagnostic.Code == "invalid-while-holstered" && diagnostic.Path == "talents.talent_holster.modifiers[0].whileHolstered");
    }
}
