using System.Collections.Immutable;
using CharacterPlanner.Core.Packs;
using static CharacterPlanner.Core.Tests.TestPacks;

namespace CharacterPlanner.Core.Tests;

public sealed class PackValidatorTests
{
    private static readonly GamePack Pack = Tiny;
    // Tiny with a numeric input no rule drives, for defaults in fights with enemy fire.
    private static readonly GamePack Rated = Tiny with { NumericContextInputs = [.. Tiny.NumericContextInputs, new() { ID = "enemy_target", Name = "Enemy target", Min = 0, Max = 2, Step = 1, Default = 2, Options = [new(0, "No target"), new(1, "Normal"), new(2, "Elite")] }, new() { ID = "rate", Name = "Rate", Min = 0, Max = 100, Step = 1, Default = 50 }] };

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
        ["reserved fight context"] = (() => Pack with { Selectors = [.. Pack.Selectors, new() { ID = "fight_context", Name = "Fight", Values = ["static", "fight"] }] }, "reserved-selector", "selectors.fight_context"),
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
        ["pass-on twice for a talent"] = (() => Pack with { Simulation = Pack.Simulation with { TalentDamage = [new() { Talent = Pack.Simulation.Triggers[0].Talent, On = "hit", Total = Pack.Stats[0].ID, Duration = 10, Period = 1 }], PassOns = [new() { Talent = Pack.Simulation.Triggers[0].Talent, Within = 25 }, new() { Talent = Pack.Simulation.Triggers[0].Talent, Within = 10 }] } }, "bad-sim-pass-on", "simulation.passOns[1].talent"),
        ["pass-on beyond 50 m"] = (() => Pack with { Simulation = Pack.Simulation with { TalentDamage = [new() { Talent = Pack.Simulation.Triggers[0].Talent, On = "hit", Total = Pack.Stats[0].ID, Duration = 10, Period = 1 }], PassOns = [new() { Talent = Pack.Simulation.Triggers[0].Talent, Within = 51 }] } }, "bad-sim-pass-on", "simulation.passOns[0].within"),
        ["pass-on reaching nowhere"] = (() => Pack with { Simulation = Pack.Simulation with { TalentDamage = [new() { Talent = Pack.Simulation.Triggers[0].Talent, On = "hit", Total = Pack.Stats[0].ID, Duration = 10, Period = 1 }], PassOns = [new() { Talent = Pack.Simulation.Triggers[0].Talent, Within = 0 }] } }, "bad-sim-pass-on", "simulation.passOns[0].within"),
        ["status pass-on of a burn without a tick row"] = (() => Pack with { Simulation = Pack.Simulation with { StatusDamage = [], PassOns = [new() { Talent = Pack.Simulation.Triggers[0].Talent, Within = 10, Status = "burning", Share = 0.5 }] } }, "bad-sim-pass-on", "simulation.passOns[0].status"),
        ["blast with nothing to do"] = (() => Pack with { Simulation = Pack.Simulation with { Blasts = [new() { Talent = Pack.Simulation.Triggers[0].Talent, On = "kill", Within = 10 }] } }, "bad-sim-blast", "simulation.blasts[0].amount"),
        ["blast marking a rule not held per enemy"] = (() => Pack with { Simulation = Pack.Simulation with { Blasts = [new() { Talent = Pack.Simulation.Triggers[0].Talent, On = "kill", Within = 10, Mark = Pack.Simulation.Triggers[0].Key }] } }, "bad-sim-blast", "simulation.blasts[0].mark"),
        ["per-enemy stacks with no event and no blast marking them"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [new() { Talent = Pack.Simulation.Triggers[0].Talent, Key = "marks", Kind = "stacks", Max = 5, ResetOn = "target_death" }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["trap of an unknown talent"] = (() => Pack with { Simulation = Pack.Simulation with { Traps = [new() { Talent = "nobody", TagAfter = 0.5, MaxTags = 10, Status = "shocked", StatusDuration = 10 }] } }, "bad-sim-trap", "simulation.traps[0].talent"),
        ["trap tagging eleven"] = (() => Pack with { Simulation = Pack.Simulation with { Traps = [new() { Talent = Pack.Simulation.Triggers[0].Talent, TagAfter = 0.5, MaxTags = 11, Status = "shocked", StatusDuration = 10 }] } }, "bad-sim-trap", "simulation.traps[0].maxTags"),
        ["trap with an unknown status"] = (() => Pack with { Simulation = Pack.Simulation with { Traps = [new() { Talent = Pack.Simulation.Triggers[0].Talent, TagAfter = 0.5, MaxTags = 10, Status = "frozen", StatusDuration = 10 }] } }, "bad-sim-trap", "simulation.traps[0].status"),
        ["trap burning without a tick row"] = (() => Pack with { Simulation = Pack.Simulation with { StatusDamage = [], Traps = [new() { Talent = Pack.Simulation.Triggers[0].Talent, TagAfter = 0.5, MaxTags = 10, Status = "burning", StatusDuration = 10 }] } }, "bad-sim-trap", "simulation.traps[0].status"),
        ["trap status without a duration"] = (() => Pack with { Simulation = Pack.Simulation with { Traps = [new() { Talent = Pack.Simulation.Triggers[0].Talent, TagAfter = 0.5, MaxTags = 10, Status = "shocked" }] } }, "bad-sim-trap", "simulation.traps[0].statusDuration"),
        ["trap with a negative cut"] = (() => Pack with { Simulation = Pack.Simulation with { Traps = [new() { Talent = Pack.Simulation.Triggers[0].Talent, TagAfter = 0.5, MaxTags = 10, Status = "shocked", StatusDuration = 10, Cut = -1 }] } }, "bad-sim-trap", "simulation.traps[0].cut"),
        ["redirect of an unknown talent"] = (() => Pack with { Simulation = Pack.Simulation with { Redirects = [new() { Talent = "nobody", Share = 0.5, Within = 30 }] } }, "bad-sim-redirect", "simulation.redirects[0].talent"),
        ["redirect of all of a hit"] = (() => Pack with { Simulation = Pack.Simulation with { Redirects = [new() { Talent = Pack.Simulation.Triggers[0].Talent, Share = 1, Within = 30 }] } }, "bad-sim-redirect", "simulation.redirects[0].share"),
        ["redirect beyond 50 m"] = (() => Pack with { Simulation = Pack.Simulation with { Redirects = [new() { Talent = Pack.Simulation.Triggers[0].Talent, Share = 0.5, Within = 51 }] } }, "bad-sim-redirect", "simulation.redirects[0].within"),
        ["two redirects for a talent"] = (() => Pack with { Simulation = Pack.Simulation with { Redirects = [new() { Talent = Pack.Simulation.Triggers[0].Talent, Share = 0.5, Within = 30 }, new() { Talent = Pack.Simulation.Triggers[0].Talent, Share = 0.5, Within = 30 }] } }, "bad-sim-redirect", "simulation.redirects[1].talent"),
        ["status pass-on without a share"] = (() => Pack with { Simulation = Pack.Simulation with { PassOns = [new() { Talent = Pack.Simulation.Triggers[0].Talent, Within = 10, Status = "bleeding" }] } }, "bad-sim-pass-on", "simulation.passOns[0].status"),
        ["status pass-on to some"] = (() => Pack with { Simulation = Pack.Simulation with { PassOns = [new() { Talent = Pack.Simulation.Triggers[0].Talent, Within = 10, Status = "any", Share = 0.5, To = "some" }] } }, "bad-sim-pass-on", "simulation.passOns[0].status"),
        ["debuff pass-on to all"] = (() => Pack with { Simulation = Pack.Simulation with { TalentDamage = [new() { Talent = Pack.Simulation.Triggers[0].Talent, On = "hit", Total = Pack.Stats[0].ID, Duration = 10, Period = 1 }], PassOns = [new() { Talent = Pack.Simulation.Triggers[0].Talent, Within = 25, To = "all" }] } }, "bad-sim-pass-on", "simulation.passOns[0].status"),
        ["pass-on without a debuff"] = (() => Pack with { Simulation = Pack.Simulation with { PassOns = [new() { Talent = Pack.Simulation.Triggers[0].Talent, Within = 25 }] } }, "bad-sim-pass-on", "simulation.passOns[0].talent"),
        ["pass-on with within and withinStat"] = (() => Pack with { Simulation = Pack.Simulation with { PassOns = [new() { Talent = Pack.Simulation.Triggers[0].Talent, Within = 10, WithinStat = Pack.Stats[0].ID, Status = "any", Share = 0.5 }] } }, "bad-sim-pass-on", "simulation.passOns[0].within"),
        ["pass-on with an unknown withinStat"] = (() => Pack with { Simulation = Pack.Simulation with { PassOns = [new() { Talent = Pack.Simulation.Triggers[0].Talent, WithinStat = "missing", Status = "any", Share = 0.5 }] } }, "unknown-stat", "simulation.passOns[0].withinStat"),
        ["status pass-on with share and shareStat"] = (() => Pack with { Simulation = Pack.Simulation with { PassOns = [new() { Talent = Pack.Simulation.Triggers[0].Talent, Within = 10, Status = "any", Share = 0.5, ShareStat = Pack.Stats[0].ID }] } }, "bad-sim-pass-on", "simulation.passOns[0].status"),
        ["status pass-on with an unknown shareStat"] = (() => Pack with { Simulation = Pack.Simulation with { PassOns = [new() { Talent = Pack.Simulation.Triggers[0].Talent, Within = 10, Status = "any", ShareStat = "missing" }] } }, "unknown-stat", "simulation.passOns[0].shareStat"),
        ["control status on a shield"] = (() => Pack with { Simulation = Pack.Simulation with { Skills = [Pack.Simulation.Skills[0] with { Status = "shocked", ControlStatus = true, Shield = Pack.Simulation.Skills[0].Cooldown }] } }, "bad-sim-skill", "simulation.skills[0].controlStatus"),
        ["falloff on a control area"] = (() => Pack with { Simulation = Pack.Simulation with { Skills = [Pack.Simulation.Skills[0] with { Rate = "", Impact = "", Status = "shocked", ControlStatus = true, Area = Pack.Simulation.Skills[0].Window, AreaFull = Pack.Simulation.Skills[0].Window, AreaEdge = 0.5 }] } }, "bad-sim-skill", "simulation.skills[0].areaFull"),
        ["control status on a burn"] = (() => Pack with { Simulation = Pack.Simulation with { Skills = [Pack.Simulation.Skills[0] with { Status = "burning", ControlStatus = true }] } }, "bad-sim-skill", "simulation.skills[0].controlStatus"),
        ["control status without a window"] = (() => Pack with { Simulation = Pack.Simulation with { Skills = [Pack.Simulation.Skills[0] with { Rate = "", Window = "", Status = "shocked", ControlStatus = true }] } }, "bad-sim-skill", "simulation.skills[0].controlStatus"),
        ["skill area falloff without an edge"] = (() => Pack with { Simulation = Pack.Simulation with { Skills = [Pack.Simulation.Skills[0] with { Area = Pack.Simulation.Skills[0].Cooldown, AreaFull = Pack.Simulation.Skills[0].Cooldown }] } }, "bad-sim-skill", "simulation.skills[0].areaEdge"),
        ["skill area edge over 1"] = (() => Pack with { Simulation = Pack.Simulation with { Skills = [Pack.Simulation.Skills[0] with { Area = Pack.Simulation.Skills[0].Cooldown, AreaFull = Pack.Simulation.Skills[0].Cooldown, AreaEdge = 1.5 }] } }, "bad-sim-skill", "simulation.skills[0].areaEdge"),
        ["skill area edge without a full radius"] = (() => Pack with { Simulation = Pack.Simulation with { Skills = [Pack.Simulation.Skills[0] with { Area = Pack.Simulation.Skills[0].Cooldown, AreaEdge = 0.5 }] } }, "bad-sim-skill", "simulation.skills[0].areaFull"),
        ["skill area falloff without an area"] = (() => Pack with { Simulation = Pack.Simulation with { Skills = [Pack.Simulation.Skills[0] with { AreaFull = Pack.Simulation.Skills[0].Cooldown, AreaEdge = 0.5 }] } }, "bad-sim-skill", "simulation.skills[0].areaFull"),
        ["skill area on a shield"] = (() => Pack with { Simulation = Pack.Simulation with { Skills = [Pack.Simulation.Skills[0] with { Shield = Pack.Simulation.Skills[0].Cooldown, Area = Pack.Simulation.Skills[0].Cooldown }] } }, "bad-sim-skill", "simulation.skills[0].area"),
        ["skill area without an impact"] = (() => Pack with { Simulation = Pack.Simulation with { Skills = [Pack.Simulation.Skills[0] with { Impact = "", Area = Pack.Simulation.Skills[0].Cooldown }] } }, "bad-sim-skill", "simulation.skills[0].area"),
        ["shock pass-on with skillStatuses"] = (() => Pack with { Simulation = Pack.Simulation with { PassOns = [new() { Talent = Pack.Simulation.Triggers[0].Talent, Within = 10, Status = "shocked", Share = 0.5, SkillStatuses = true }] } }, "bad-sim-pass-on", "simulation.passOns[0].skillStatuses"),
        ["skill status spread without tick rows"] = (() => Pack with { Simulation = Pack.Simulation with { StatusDamage = [], Spreads = [new() { Talent = Pack.Simulation.Triggers[0].Talent, Within = 8, SkillStatuses = true }] } }, "bad-sim-spread", "simulation.spreads[0].skillStatuses"),
        ["debuff pass-on with skillStatuses"] = (() => Pack with { Simulation = Pack.Simulation with { TalentDamage = [new() { Talent = Pack.Simulation.Triggers[0].Talent, On = "hit", Total = Pack.Stats[0].ID, Duration = 10, Period = 1 }], PassOns = [new() { Talent = Pack.Simulation.Triggers[0].Talent, Within = 25, SkillStatuses = true }] } }, "bad-sim-pass-on", "simulation.passOns[0].skillStatuses"),
        ["debuff pass-on with a shareStat"] = (() => Pack with { Simulation = Pack.Simulation with { TalentDamage = [new() { Talent = Pack.Simulation.Triggers[0].Talent, On = "hit", Total = Pack.Stats[0].ID, Duration = 10, Period = 1 }], PassOns = [new() { Talent = Pack.Simulation.Triggers[0].Talent, Within = 25, ShareStat = Pack.Stats[0].ID }] } }, "bad-sim-pass-on", "simulation.passOns[0].status"),
        ["spread unknown talent"] = (() => Pack with { Simulation = Pack.Simulation with { Spreads = [new() { Talent = "nobody", Within = 8, Every = 15 }] } }, "bad-sim-spread", "simulation.spreads[0].talent"),
        ["spread twice for a talent"] = (() => Pack with { Simulation = Pack.Simulation with { Spreads = [new() { Talent = Pack.Simulation.Triggers[0].Talent, Within = 8 }, new() { Talent = Pack.Simulation.Triggers[0].Talent, Within = 10 }] } }, "bad-sim-spread", "simulation.spreads[1].talent"),
        ["spread reaching nowhere"] = (() => Pack with { Simulation = Pack.Simulation with { Spreads = [new() { Talent = Pack.Simulation.Triggers[0].Talent, Within = 0 }] } }, "bad-sim-spread", "simulation.spreads[0].within"),
        ["spread cooldown negative"] = (() => Pack with { Simulation = Pack.Simulation with { Spreads = [new() { Talent = Pack.Simulation.Triggers[0].Talent, Within = 8, Every = -1 }] } }, "bad-sim-spread", "simulation.spreads[0].every"),
        ["spread beyond 50 m"] = (() => Pack with { Simulation = Pack.Simulation with { Spreads = [new() { Talent = Pack.Simulation.Triggers[0].Talent, Within = 51 }] } }, "bad-sim-spread", "simulation.spreads[0].within"),
        ["spread cooldown above 600"] = (() => Pack with { Simulation = Pack.Simulation with { Spreads = [new() { Talent = Pack.Simulation.Triggers[0].Talent, Within = 8, Every = 601 }] } }, "bad-sim-spread", "simulation.spreads[0].every"),
        ["blast unknown talent"] = (() => Pack with { Simulation = Pack.Simulation with { Blasts = [new() { Talent = "nobody", On = "kill", Amount = Pack.Stats[0].ID, Within = 5 }] } }, "bad-sim-blast", "simulation.blasts[0].talent"),
        ["blast on a whole second"] = (() => Pack with { Simulation = Pack.Simulation with { Blasts = [new() { Talent = Pack.Simulation.Triggers[0].Talent, On = "second", Amount = Pack.Stats[0].ID, Within = 5 }] } }, "bad-sim-blast", "simulation.blasts[0].on"),
        ["blast on a reload"] = (() => Pack with { Simulation = Pack.Simulation with { Blasts = [new() { Talent = Pack.Simulation.Triggers[0].Talent, On = "reload", Amount = Pack.Stats[0].ID, Within = 5 }] } }, "bad-sim-blast", "simulation.blasts[0].on"),
        ["blast burn without a tick row"] = (() => Pack with { Simulation = Pack.Simulation with { StatusDamage = [], Blasts = [new() { Talent = Pack.Simulation.Triggers[0].Talent, On = "kill", Amount = Pack.Stats[0].ID, Within = 5, Status = "burning", StatusDuration = 5 }] } }, "bad-sim-blast", "simulation.blasts[0].status"),
        ["blast status unknown"] = (() => Pack with { Simulation = Pack.Simulation with { Blasts = [new() { Talent = Pack.Simulation.Triggers[0].Talent, On = "kill", Amount = Pack.Stats[0].ID, Within = 5, Status = "glowing", StatusDuration = 5 }] } }, "bad-sim-blast", "simulation.blasts[0].status"),
        ["blast status without a duration"] = (() => Pack with { Simulation = Pack.Simulation with { Blasts = [new() { Talent = Pack.Simulation.Triggers[0].Talent, On = "kill", Amount = Pack.Stats[0].ID, Within = 5, Status = "bleeding" }] } }, "bad-sim-blast", "simulation.blasts[0].status"),
        ["blast duration without a status"] = (() => Pack with { Simulation = Pack.Simulation with { Blasts = [new() { Talent = Pack.Simulation.Triggers[0].Talent, On = "kill", Amount = Pack.Stats[0].ID, Within = 5, StatusDuration = 5 }] } }, "bad-sim-blast", "simulation.blasts[0].status"),
        ["blast beyond 50 m"] = (() => Pack with { Simulation = Pack.Simulation with { Blasts = [new() { Talent = Pack.Simulation.Triggers[0].Talent, On = "kill", Amount = Pack.Stats[0].ID, Within = 51 }] } }, "bad-sim-blast", "simulation.blasts[0].within"),
        ["hold delay on temporary armor"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Grant = "temporary_armor", GrantAmount = 0.5, GrantDuration = 10, GrantHoldDelay = 1 }] } }, "bad-sim-trigger", "simulation.triggers[0].grant"),
        ["hold delay above 60"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Grant = "skill_cooldown", GrantAmount = 1, GrantHoldDelay = 61 }] } }, "bad-sim-trigger", "simulation.triggers[0].grant"),
        ["hold delay without a grant"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { GrantHoldDelay = 1 }] } }, "bad-sim-trigger", "simulation.triggers[0].grant"),
        ["armor kit refund on a kill"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { On = "kill", Grant = "armor_kit_refund", GrantAmount = 1 }] } }, "bad-sim-trigger", "simulation.triggers[0].grant"),
        ["armor kit refund over time"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Grant = "armor_kit_refund", GrantAmount = 1, GrantDuration = 5 }] } }, "bad-sim-trigger", "simulation.triggers[0].grant"),
        ["skill cooldown over time"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Grant = "skill_cooldown", GrantAmount = 0.1, GrantDuration = 5 }] } }, "bad-sim-trigger", "simulation.triggers[0].grant"),
        ["refreshOn on untimed stacks"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[1] with { RefreshOn = "kill", Duration = 0 }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["primeHolstered on a buff"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { PrimeHolstered = 5 }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["primeHolstered with an event"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Kind = "ready", Duration = 0, PrimeHolstered = 5 }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["primeHolstered with a grant"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Kind = "ready", On = "", Duration = 0, PrimeHolstered = 5, Grant = "skill_cooldown", GrantAmount = 0.5 }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["primeHolstered with a refill"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Kind = "ready", On = "", Duration = 0, PrimeHolstered = 5, Refill = true }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["shareCopies on a ready rule"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Kind = "ready", ShareCopies = true }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["weaponTypes alone"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { WeaponTypes = ["rifle"] }] } }, "bad-sim-trigger", "simulation.triggers[0].weaponTypes"),
        ["differentTypes without weaponTypes"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { DifferentTypes = true }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["everyPerType without every"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { EveryPerType = true, WeaponTypes = ["rifle"] }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["assumeWithoutDistance without bands"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Kind = "distance", On = "", Duration = 0, Within = 10, AssumeWithoutDistance = true }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["assumeWithoutDistance on a buff"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { AssumeWithoutDistance = true }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["a selector default outside its values"] = (() => Pack with { Selectors = [.. Pack.Selectors.Select(selector => selector with { Default = "nowhere" })] }, "selector-default", $"selectors.{Pack.Selectors[0].ID}.default"),
        ["lockoutAlways without a lockout"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { LockoutAlways = true }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["gainAtLockoutEnd with a consume"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { CancelOn = "swap", CancelFor = 5, GainAtLockoutEnd = true, ConsumeOn = "reload" }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["gainAtLockoutEnd with a chance"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { CancelOn = "swap", CancelFor = 5, GainAtLockoutEnd = true, Chance = 0.5 }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["one talent's stacks on a key shaped two ways"] = (() => Pack with
        {
            Simulation = Pack.Simulation with
            {
                Triggers = [new() { Talent = Pack.Simulation.Triggers[0].Talent, Key = "stacks", Kind = "stacks", On = "hit", Max = 5, Duration = 5 },
                    new() { Talent = Pack.Simulation.Triggers[0].Talent, Key = "stacks", Kind = "stacks", On = "kill", Max = 4, Duration = 5 }]
            }
        }, "bad-sim-trigger", "simulation.triggers[1]"),
        ["refillBoth without refill"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { RefillBoth = true }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["primeHolstered too long"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Kind = "ready", On = "", Duration = 0, PrimeHolstered = 601 }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["fromFirstGain on a buff"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { FromFirstGain = true }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["fromFirstGain untimed"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[1] with { FromFirstGain = true, Duration = 0 }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["refreshOn on decaying stacks"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[1] with { RefreshOn = "kill", Refresh = true, Decay = 1 }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["refreshOn on stacks on each enemy"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[1] with { RefreshOn = "kill", ResetOn = "target_death" }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["fromFirstGain on a shared key"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[1] with { FromFirstGain = true }, Pack.Simulation.Triggers[1] with { Talent = Pack.Simulation.Triggers[0].Talent }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["fromFirstGain with refresh"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[1] with { FromFirstGain = true, Refresh = true }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["refreshOn its own on"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { RefreshOn = "kill" }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["refreshOn an unknown event"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { RefreshOn = "sneeze" }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["refreshOn on an untimed buff"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Duration = 0, ConsumeOn = "shot", RefreshOn = "hit" }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["refreshOn with cooldownFromEnd"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { RefreshOn = "hit", CooldownFromEnd = true, Every = 5 }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["refreshOn an also event"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { RefreshOn = "hit", Also = [new() { On = "hit" }] }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["refreshOn its resetOn"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { RefreshOn = "reload", ResetOn = "reload" }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["refreshOn on a gated rule"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { RefreshOn = "hit", WhileStatus = "burning" }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["refreshOn its cancelOn"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { RefreshOn = "swap", CancelOn = "swap" }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["whilePlayerStatus unknown status"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { WhilePlayerStatus = "sneezing" }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["whilePlayerStatus with whileStatus"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { WhilePlayerStatus = "bleeding", WhileStatus = "burning" }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["whilePlayerStatus on a ready rule"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Kind = "ready", WhilePlayerStatus = "bleeding" }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["bonus_armor with another field"] = (() => Rated with { Simulation = Rated.Simulation with { Triggers = [new() { Talent = "test", Key = "rate", Kind = "bonus_armor", Duration = 5 }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["bonus_armor on a selector"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Kind = "bonus_armor", On = "", Duration = 0 }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["anyStatusExcept without any"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { WhileStatus = "burning", AnyStatusExcept = ["ensnared"] }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["anyStatusExcept unknown status"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { WhileStatus = "any", AnyStatusExcept = ["sneezing"] }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["anyStatusExcept every status"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { WhileStatus = "any", AnyStatusExcept = ["burning", "bleeding", "poisoned", "shocked", "ensnared", "blinded", "disoriented", "disrupted"] }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["anyStatusExcept repeated"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { WhilePlayerStatus = "any", AnyStatusExcept = ["ensnared", "ensnared"] }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["refreshOn with whilePlayerStatus"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { RefreshOn = "hit", WhilePlayerStatus = "bleeding" }] } }, "bad-sim-trigger", "simulation.triggers[0]"),
        ["enemy status unlisted target"] = (() => Rated with { Simulation = Rated.Simulation with { EnemyStatuses = [new() { Target = 3, Status = "bleeding", Duration = 2 }] } }, "bad-sim-enemy-status", "simulation.enemyStatuses[0].target"),
        ["enemy status no target"] = (() => Rated with { Simulation = Rated.Simulation with { EnemyStatuses = [new() { Target = 0, Status = "bleeding", Duration = 2 }] } }, "bad-sim-enemy-status", "simulation.enemyStatuses[0].target"),
        ["enemy status unknown status"] = (() => Rated with { Simulation = Rated.Simulation with { EnemyStatuses = [new() { Target = 1, Status = "sneezing", Duration = 2 }] } }, "bad-sim-enemy-status", "simulation.enemyStatuses[0].status"),
        ["enemy status twice"] = (() => Rated with { Simulation = Rated.Simulation with { EnemyStatuses = [new() { Target = 1, Status = "bleeding", Duration = 2 }, new() { Target = 1, Status = "bleeding", Duration = 3 }] } }, "bad-sim-enemy-status", "simulation.enemyStatuses[1]"),
        ["enemy status no duration"] = (() => Rated with { Simulation = Rated.Simulation with { EnemyStatuses = [new() { Target = 1, Status = "bleeding", Duration = 0 }] } }, "bad-sim-enemy-status", "simulation.enemyStatuses[0].duration"),
        ["enemy status control tick"] = (() => Rated with { Simulation = Rated.Simulation with { EnemyStatuses = [new() { Target = 1, Status = "shocked", Duration = 2, Tick = "armor", Period = 0.5 }] } }, "bad-sim-enemy-status", "simulation.enemyStatuses[0].tick"),
        ["enemy status unknown tick"] = (() => Rated with { Simulation = Rated.Simulation with { EnemyStatuses = [new() { Target = 1, Status = "bleeding", Duration = 2, Tick = "missing", Period = 0.5 }] } }, "bad-sim-enemy-status", "simulation.enemyStatuses[0].tick"),
        ["enemy status tick without period"] = (() => Rated with { Simulation = Rated.Simulation with { EnemyStatuses = [new() { Target = 1, Status = "bleeding", Duration = 2, Tick = "armor" }] } }, "bad-sim-enemy-status", "simulation.enemyStatuses[0].period"),
        ["enemy status period without tick"] = (() => Rated with { Simulation = Rated.Simulation with { EnemyStatuses = [new() { Target = 1, Status = "bleeding", Duration = 2, Period = 0.5 }] } }, "bad-sim-enemy-status", "simulation.enemyStatuses[0].tick"),
        ["enemy status unknown resistance"] = (() => Rated with { Simulation = Rated.Simulation with { EnemyStatuses = [new() { Target = 1, Status = "bleeding", Duration = 2, Resistances = ["missing"] }] } }, "bad-sim-enemy-status", "simulation.enemyStatuses[0].resistances"),
        ["enemy status floor without resistance"] = (() => Rated with { Simulation = Rated.Simulation with { EnemyStatuses = [new() { Target = 1, Status = "bleeding", Duration = 2, MinDuration = 0.5 }] } }, "bad-sim-enemy-status", "simulation.enemyStatuses[0].minDuration"),
        ["enemy status floor above duration"] = (() => Rated with { Simulation = Rated.Simulation with { EnemyStatuses = [new() { Target = 1, Status = "bleeding", Duration = 2, Resistances = ["armor"], MinDuration = 3 }] } }, "bad-sim-enemy-status", "simulation.enemyStatuses[0].minDuration"),
        ["incoming default on a driven key"] = (() => Pack with { Simulation = Pack.Simulation with { IncomingDefaults = [new() { Input = "stacks", Value = 1 }] } }, "bad-sim-incoming-default", "simulation.incomingDefaults[0].input"),
        ["incoming default unknown input"] = (() => Pack with { Simulation = Pack.Simulation with { IncomingDefaults = [new() { Input = "missing", Value = 1 }] } }, "bad-sim-incoming-default", "simulation.incomingDefaults[0].input"),
        ["incoming default twice"] = (() => Rated with { Simulation = Rated.Simulation with { IncomingDefaults = [new() { Input = Rated.NumericContextInputs[1].ID, Value = 1 }, new() { Input = Rated.NumericContextInputs[1].ID, Value = 1 }] } }, "bad-sim-incoming-default", "simulation.incomingDefaults[1].input"),
        ["incoming default off its range"] = (() => Rated with { Simulation = Rated.Simulation with { IncomingDefaults = [new() { Input = Rated.NumericContextInputs[1].ID, Value = Rated.NumericContextInputs[1].Max + 1 }] } }, "bad-sim-incoming-default", "simulation.incomingDefaults[0].value"),
        ["incoming default not the target"] = (() => Rated with { Simulation = Rated.Simulation with { IncomingDefaults = [new() { Input = "rate", Value = 1 }] } }, "bad-sim-incoming-default", "simulation.incomingDefaults[0].input"),
        ["incoming default no target"] = (() => Rated with { Simulation = Rated.Simulation with { IncomingDefaults = [new() { Input = "enemy_target", Value = 0 }] } }, "bad-sim-incoming-default", "simulation.incomingDefaults[0].value"),
        ["incoming default not a listed profile"] = (() => Rated with { Simulation = Rated.Simulation with { IncomingDefaults = [new() { Input = "enemy_target", Value = 3 }] } }, "bad-sim-incoming-default", "simulation.incomingDefaults[0].value"),
        ["incoming default by distance too"] = (() => Rated with { Simulation = Rated.Simulation with { IncomingDefaults = [new() { Input = Rated.NumericContextInputs[1].ID, Value = 1 }], DistanceDefaults = [new() { Input = Rated.NumericContextInputs[1].ID, Bands = [new() { Value = 1 }] }] } }, "bad-sim-incoming-default", "simulation.incomingDefaults[0].input"),
        ["requires on stacks with no event"] = (() => Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[1] with { On = "", GainWhileHolstered = 1, RequiresContext = ImmutableDictionary<string, string>.Empty.Add("overwatch_active", "true") }] } }, "bad-sim-trigger", "simulation.triggers[0].requires"),
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
    public void ADisruptedControlStatusIsAccepted()
    {
        // ADR 0035: disrupted is a fight status, and a control skill may put it on.
        var pack = Pack with { Simulation = Pack.Simulation with { Skills = [Pack.Simulation.Skills[0] with { Status = "disrupted", ControlStatus = true }] } };
        Assert.DoesNotContain(PackValidator.Validate(pack).Diagnostics, diagnostic => diagnostic.Path == "simulation.skills[0].controlStatus");
        Assert.Contains("disrupted", GamePack.FightStatuses);
    }

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

    [Fact]
    public void AKitAbilityAndInstantKitValidateAndRoundTrip()
    {
        // ADR 0021: a replacing talent's ability on a cooldown stat, and the instant kit.
        var talent = Pack.Talents[0].ID;
        var pack = Pack with
        {
            Simulation = Pack.Simulation with
            {
                ArmorKit = Kit with { ReplacedByTalents = [talent], Abilities = [new() { Talent = talent, Cooldown = Pack.Stats[0].ID }], Instant = new() { Repair = 0.25, Cooldown = 5 } }
            }
        };
        var result = PackValidator.Load(PackWriter.Write(pack));
        Assert.Empty(result.Diagnostics);
        var kit = result.Compiled!.Pack.Simulation.ArmorKit!;
        Assert.Equal((talent, Pack.Stats[0].ID), (Assert.Single(kit.Abilities).Talent, kit.Abilities[0].Cooldown));
        Assert.Equal((0.25, 5d), (kit.Instant!.Repair, kit.Instant.Cooldown));
        // Unset, neither is written.
        Assert.Empty(PackValidator.Load(PackWriter.Write(Pack with { Simulation = Pack.Simulation with { ArmorKit = Kit } })).Compiled!.Pack.Simulation.ArmorKit!.Abilities);
    }

    [Fact]
    public void ABonusArmorRowReadFromAFileIsAccepted()
    {
        // R127: rows read from a file carry their own empty lists, which a record compares by reference.
        var row = new SimTriggerDefinition { Talent = Pack.Talents[0].ID, Key = "rate", Kind = "bonus_armor" };
        var read = PackValidator.Load(PackWriter.Write(Rated with { Simulation = Rated.Simulation with { Triggers = [row] } }));
        Assert.Empty(read.Diagnostics);
        // Lists that are present but empty, as a file can write them, set no field.
        var empty = row with
        {
            Phases = System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(new SimPhase[0]),
            Also = System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(new SimGain[0]),
            WeaponTypes = System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(new string[0]),
            AnyStatusExcept = System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(new string[0]),
            WeaponTags = System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(new string[0])
        };
        Assert.DoesNotContain(PackValidator.Validate(Rated with { Simulation = Rated.Simulation with { Triggers = [empty] } }).Diagnostics, diagnostic => diagnostic.Code == "bad-sim-trigger");
        // Another field is still refused from a file.
        var tagged = PackValidator.Load(PackWriter.Write(Rated with { Simulation = Rated.Simulation with { Triggers = [row with { WeaponTags = ["mmr"] }] } }));
        Assert.Contains(tagged.Diagnostics, diagnostic => diagnostic.Code == "bad-sim-trigger" && diagnostic.Path == "simulation.triggers[0]");
    }

    [Fact]
    public void SkillsAsideAreCheckedAndRoundTrip()
    {
        // ADR 0022: a known skill, once, with no fight rule, and a one-line reason.
        var skill = Pack.ActiveSkills[0];
        var aside = new SimSkillAside { Skill = skill.ID, Why = "it marks enemies" };
        var ruled = Pack.Simulation.Skills.FirstOrDefault(rule => rule.Skill == skill.ID);
        GamePack With(params SimSkillAside[] rows) => Pack with
        {
            Simulation = Pack.Simulation with { Skills = [.. Pack.Simulation.Skills.Where(rule => rule != ruled)], SkillsAside = [.. rows] }
        };
        var result = PackValidator.Load(PackWriter.Write(With(aside)));
        Assert.Empty(result.Diagnostics);
        Assert.Equal(aside, Assert.Single(result.Compiled!.Pack.Simulation.SkillsAside));
        int Errors(GamePack pack) => PackValidator.Validate(pack).Diagnostics.Count(diagnostic => diagnostic.Code == "bad-sim-skill-aside");
        Assert.Equal(1, Errors(With(aside with { Skill = "nothing" })));
        Assert.Equal(1, Errors(With(aside, aside)));
        Assert.Equal(1, Errors(With(aside with { Why = "" })));
        Assert.Equal(1, Errors(With(aside with { Why = "two\nlines" })));
        Assert.Equal(1, Errors(With(aside with { Why = new string('x', 201) })));
        // A skill with a fight rule doesn't step aside.
        Assert.NotNull(ruled);
        Assert.Equal(1, Errors(Pack with { Simulation = Pack.Simulation with { SkillsAside = [aside] } }));
    }

    [Fact]
    public void ASkillRulesRequiresIsCheckedAndRoundTrips()
    {
        // ADR 0027: a skill rule gated on known selector values, read once per fight, round-trips.
        var rule = Pack.Simulation.Skills.Single();
        GamePack With(SimSkillDefinition gated) => Pack with { Simulation = Pack.Simulation with { Skills = [gated] } };
        var gated = rule with { RequiresContext = ImmutableDictionary<string, string>.Empty.Add("headshot_mode", "headshot") };
        var loaded = PackValidator.Load(PackWriter.Write(With(gated)));
        Assert.Empty(loaded.Diagnostics);
        Assert.Equal(gated.RequiresContext, Assert.Single(loaded.Compiled!.Pack.Simulation.Skills).RequiresContext);
        // Without a gate nothing is written, so a pack without one is unchanged.
        Assert.Null(Assert.Single(PackValidator.Load(PackWriter.Write(With(rule))).Compiled!.Pack.Simulation.Skills).RequiresContext);
        int Errors(ImmutableDictionary<string, string> requires, SimSkillDefinition? on = null) => PackValidator.Validate(With((on ?? rule) with { RequiresContext = requires }))
            .Diagnostics.Count(diagnostic => diagnostic.Path.StartsWith("simulation.skills[0].requires", StringComparison.Ordinal));
        var none = ImmutableDictionary<string, string>.Empty;
        Assert.Equal(1, Errors(none.Add("nothing", "true")));
        Assert.Equal(1, Errors(none.Add("headshot_mode", "elbow")));
        Assert.Equal(1, Errors(none));
        // The fight context gates modifiers only, and a key a rule drives changes during the fight.
        Assert.Equal(1, Errors(none.Add(GamePack.FightContextSelector, GamePack.FightContextFight)));
        Assert.Equal(1, Errors(none.Add("overwatch_active", "true")));
        // A shield is placed by the fight's own scheduler.
        Assert.Equal(1, Errors(none.Add("headshot_mode", "headshot"), rule with { Shield = "damage", Rate = "", Window = "", Impact = "" }));
        // A headshot cycle's key and the cover a cover cycle holds change during a fight too.
        ImmutableArray<SelectorDefinition> flags = [.. Pack.Selectors, new() { ID = "lit", Name = "Lit", Values = ["true"] }, new() { ID = "in_cover", Name = "In cover", Values = ["true"] }];
        int FightErrors(string key, ImmutableArray<SimHeadshotCycle> cycles) => PackValidator.Validate(Pack with
        {
            Selectors = flags,
            Simulation = Pack.Simulation with { Skills = [rule with { RequiresContext = none.Add(key, "true") }], HeadshotCycles = cycles }
        }).Diagnostics.Count(diagnostic => diagnostic.Path.StartsWith("simulation.skills[0].requires", StringComparison.Ordinal));
        Assert.Equal(0, FightErrors("lit", []));
        Assert.Equal(1, FightErrors("lit", [new() { Talent = "talent_killer", Key = "lit" }]));
        Assert.Equal(1, FightErrors("in_cover", []));
    }

    [Fact]
    public void ASkillRulesTargetsIsCheckedAndRoundTrips()
    {
        // ADR 0027: an impact on several distinct enemies names an output, with no impacts, area, status or rate beside it.
        var rule = Pack.Simulation.Skills.Single() with { Rate = "", Window = "", Cooldown = "duration" };
        GamePack With(SimSkillDefinition row) => Pack with { Simulation = Pack.Simulation with { Skills = [row] } };
        var targeted = rule with { Targets = "damage" };
        var loaded = PackValidator.Load(PackWriter.Write(With(targeted)));
        Assert.Empty(loaded.Diagnostics);
        Assert.Equal("damage", Assert.Single(loaded.Compiled!.Pack.Simulation.Skills).Targets);
        int Errors(SimSkillDefinition row) => PackValidator.Validate(With(row)).Diagnostics.Count(diagnostic => diagnostic.Code == "bad-sim-skill");
        Assert.Equal(1, Errors(targeted with { Targets = "nothing" }));
        Assert.Equal(1, Errors(targeted with { Impacts = "damage" }));
        Assert.Equal(1, Errors(targeted with { Status = "burning", Window = "duration" }));
        Assert.Equal(1, Errors(targeted with { Rate = "dps", Window = "duration" }));
        Assert.True(Errors(targeted with { Impact = "", Rate = "dps", Window = "duration" }) >= 1);
    }

    [Fact]
    public void ASkillRulesImpactRepairAndEndDelayAreCheckedAndRoundTrip()
    {
        // ADR 0028: a repair per impact names an output beside an impact; an end delay needs a cooldown from the use's end.
        var rule = Pack.Simulation.Skills.Single() with { CooldownFromEnd = true };
        GamePack With(SimSkillDefinition row) => Pack with { Simulation = Pack.Simulation with { Skills = [row] } };
        var leech = rule with { ImpactRepair = "damage", EndDelay = 2 };
        var loaded = PackValidator.Load(PackWriter.Write(With(leech)));
        Assert.Empty(loaded.Diagnostics);
        var back = Assert.Single(loaded.Compiled!.Pack.Simulation.Skills);
        Assert.Equal(("damage", 2d), (back.ImpactRepair, back.EndDelay));
        int Errors(SimSkillDefinition row) => PackValidator.Validate(With(row)).Diagnostics.Count(diagnostic => diagnostic.Code == "bad-sim-skill");
        Assert.Equal(1, Errors(leech with { ImpactRepair = "nothing" }));
        Assert.Equal(1, Errors(leech with { Impact = "" }));
        // Impacts landing together would repair once for them all.
        Assert.Equal(1, Errors(leech with { Impacts = "damage" }));
        Assert.Equal(0, Errors(leech with { Impacts = "damage", ImpactInterval = "duration" }));
        Assert.Equal(1, Errors(leech with { CooldownFromEnd = false }));
        Assert.Equal(1, Errors(leech with { EndDelay = -1 }));
        Assert.Equal(1, Errors(leech with { EndDelay = SimSkillDefinition.MaximumEndDelay + 1 }));
        Assert.Equal(1, Errors(leech with { EndDelay = double.NaN }));
    }

    [Fact]
    public void AChargingRuleAndItsSkillsTriggerAreCheckedAndRoundTrip()
    {
        // ADR 0031: a buff hive's rule times its charges as a repair skill's, with chargeFor; the skill alone owns a buff rule gaining on
        // skill_charge.
        var rule = Pack.Simulation.Skills.Single();
        var charge = new SimSkillDefinition
        {
            Skill = rule.Skill,
            ChargeFor = "duration",
            RepairEvery = 0.2,
            RepairLag = 0.35,
            RepairCount = "damage",
            Window = "duration",
            Cooldown = rule.Cooldown,
            CooldownFromEnd = true,
            EndDelay = 2
        };
        var stim = new SimTriggerDefinition { Talent = rule.Skill, Kind = "buff", Key = "stim_on", On = "skill_charge", Duration = 5 };
        GamePack With(SimSkillDefinition row, params SimTriggerDefinition[] triggers) => Pack with
        {
            Selectors = [.. Pack.Selectors, new() { ID = "stim_on", Name = "Stim", Values = ["true"] }],
            Simulation = Pack.Simulation with { Skills = [row], Triggers = [.. Pack.Simulation.Triggers, .. triggers] }
        };
        var loaded = PackValidator.Load(PackWriter.Write(With(charge, stim)));
        Assert.Empty(loaded.Diagnostics);
        Assert.Equal("duration", Assert.Single(loaded.Compiled!.Pack.Simulation.Skills).ChargeFor);
        int Errors(SimSkillDefinition row, params SimTriggerDefinition[] triggers) =>
            PackValidator.Validate(With(row, triggers)).Diagnostics.Count(diagnostic => diagnostic.Code is "bad-sim-skill" or "bad-sim-trigger");
        // No armor wait is needed; a repair, an armor share or a cloud isn't a charge's.
        Assert.Contains(PackValidator.Validate(With(charge with { Repair = "damage", UseArmorBelow = 1 }, stim)).Diagnostics, diagnostic => diagnostic.Message == "A rule repairs or charges, not both");
        Assert.Equal(1, Errors(charge with { RepairBelow = 0.9 }, stim));
        Assert.Equal(1, Errors(charge with { RepairCloud = "duration" }, stim));
        Assert.Equal(1, Errors(charge with { ChargeFor = "nothing" }, stim));
        // A skill's rule must be a buff on skill_charge, of a skill that charges; only a skill's rule gains on skill_charge.
        Assert.Equal(1, Errors(charge, stim with { Kind = "stacks", Max = 2 }));
        Assert.Equal(1, Errors(charge, stim with { On = "hit" }));
        Assert.Equal(1, Errors(charge with { ChargeFor = "", RepairEvery = 0, RepairLag = 0, RepairCount = "", Impact = "damage", Window = "", CooldownFromEnd = false, EndDelay = 0 }, stim));
        var talent = Pack.Talents.First().ID;
        Assert.Equal(1, Errors(charge, stim, stim with { Talent = talent, Key = "stim_on" }));
        // Nor as another gain, or to end, reset or consume a talent's rule; and a charge doesn't wait for armor.
        Assert.Equal(1, Errors(charge, stim, stim with { Talent = talent, On = "kill", Also = [new() { On = "skill_charge" }] }));
        Assert.Equal(1, Errors(charge, stim, stim with { Talent = talent, On = "kill", ResetOn = "skill_charge" }));
        Assert.Equal(1, Errors(charge with { UseArmorBelow = 1 }, stim));
        // A hive charging skills (ADR 0031): chargeSkills needs chargeFor, and refresh needs chargeSkills; both round-trip.
        var artificer = PackValidator.Load(PackWriter.Write(With(charge with { ChargeSkills = true, Refresh = "duration" }, stim)));
        Assert.Empty(artificer.Diagnostics);
        var read = Assert.Single(artificer.Compiled!.Pack.Simulation.Skills);
        Assert.True(read.ChargeSkills);
        Assert.Equal("duration", read.Refresh);
        Assert.Equal(1, Errors(charge with { Refresh = "duration" }, stim));
        // Holstered pulses (stage 2c): holsteredEvery needs chargeSkills and a span; holsteredLag needs holsteredEvery.
        var pulsing = PackValidator.Load(PackWriter.Write(With(charge with { ChargeSkills = true, HolsteredEvery = 0.5, HolsteredLag = 5 }, stim)));
        Assert.Empty(pulsing.Diagnostics);
        Assert.Equal(5, Assert.Single(pulsing.Compiled!.Pack.Simulation.Skills).HolsteredLag);
        Assert.Equal(1, Errors(charge with { HolsteredEvery = 0.5 }, stim));
        Assert.Equal(1, Errors(charge with { ChargeSkills = true, HolsteredEvery = 11 }, stim));
        Assert.Equal(1, Errors(charge with { ChargeSkills = true, HolsteredLag = 5 }, stim));
        // A hive is holstered from its cooldown's start: one whose cooldown runs from its use, or with charges, can't pulse; nor faster
        // than every 0.05 s.
        string[] Messages(SimSkillDefinition row) => [.. PackValidator.Validate(With(row, stim)).Diagnostics.Select(diagnostic => diagnostic.Message)];
        const string pulseRule = "holsteredEvery needs chargeSkills, cooldownFromEnd and no stock, and is 0.05 up to 10 s";
        Assert.Contains(pulseRule, Messages(charge with { ChargeSkills = true, HolsteredEvery = 0.5, CooldownFromEnd = false }));
        Assert.Contains(pulseRule, Messages(charge with { ChargeSkills = true, HolsteredEvery = 0.5, Stock = "damage" }));
        Assert.Contains(pulseRule, Messages(charge with { ChargeSkills = true, HolsteredEvery = 0.01 }));
        Assert.Contains(pulseRule, Messages(charge with { ChargeSkills = true, HolsteredEvery = double.NaN }));
        Assert.Equal(1, Errors(charge with { ChargeSkills = true, HolsteredEvery = 0.5, HolsteredLag = 11 }, stim));
        Assert.Equal(1, Errors(charge with { ChargeSkills = true, Refresh = "nothing" }, stim));
        Assert.Contains(PackValidator.Validate(With(charge with { ChargeFor = "", ChargeSkills = true, RepairEvery = 0, RepairLag = 0, RepairCount = "", Impact = "damage", Window = "", CooldownFromEnd = false, EndDelay = 0 })).Diagnostics,
            diagnostic => diagnostic.Message == "chargeSkills needs chargeFor");
    }

    [Fact]
    public void ASkillOutRuleIsChecked()
    {
        // ADR 0032: a skill with a window owns a buff on skill_out, held for its use, with no duration; nothing else gains on it.
        var rule = Pack.Simulation.Skills.Single();
        var held = new SimSkillDefinition { Skill = rule.Skill, Window = "duration", Cooldown = rule.Cooldown, CooldownFromEnd = true };
        var buff = new SimTriggerDefinition { Talent = rule.Skill, Kind = "buff", Key = "out_on", On = "skill_out" };
        GamePack With(SimSkillDefinition row, params SimTriggerDefinition[] triggers) => Pack with
        {
            Selectors = [.. Pack.Selectors, new() { ID = "out_on", Name = "Out", Values = ["true"] }],
            Simulation = Pack.Simulation with { Skills = [row], Triggers = [.. Pack.Simulation.Triggers, .. triggers] }
        };
        Assert.Empty(PackValidator.Load(PackWriter.Write(With(held, buff))).Diagnostics);
        int Errors(SimSkillDefinition row, params SimTriggerDefinition[] triggers) =>
            PackValidator.Validate(With(row, triggers)).Diagnostics.Count(diagnostic => diagnostic.Code is "bad-sim-skill" or "bad-sim-trigger");
        // Without its skill_out buff, a window alone is no skill rule.
        Assert.Equal(1, Errors(held));
        Assert.Equal(1, Errors(held, buff with { Duration = 5 }));
        Assert.Equal(1, Errors(held with { Window = "", Impact = "damage" }, buff));
        var talent = Pack.Talents.First().ID;
        Assert.Equal(1, Errors(held, buff, buff with { Talent = talent, Key = "out_on", On = "kill", ResetOn = "skill_out", Duration = 5 }));
        Assert.Equal(1, Errors(held, buff, buff with { Talent = talent, Key = "out_on", On = "kill", Also = [new() { On = "skill_out" }], Duration = 5 }));
        Assert.Equal(1, Errors(held, buff with { Kind = "stacks", Max = 2, Duration = 5 }));
        // Held while out: nothing gates, spaces or ends it; not on a repair rule, nor one whose effects start late.
        Assert.Equal(1, Errors(held, buff with { ConsumeOn = "target_death" }));
        Assert.Equal(1, Errors(held, buff with { Chance = 0.5 }));
        // A refill of the weapon put away on swaps (ADR 0033) goes on a skill_out buff alone, and round-trips.
        Assert.Equal(0, Errors(held, buff with { RefillOnSwap = true }));
        Assert.True(Assert.Single(PackValidator.Load(PackWriter.Write(With(held, buff with { RefillOnSwap = true }))).Compiled!.Pack.Simulation.Triggers, rule => rule.On == "skill_out").RefillOnSwap);
        bool RefillError(params SimTriggerDefinition[] triggers) =>
            PackValidator.Validate(With(held, triggers)).Diagnostics.Any(diagnostic => diagnostic.Path.EndsWith(".refillOnSwap", StringComparison.Ordinal));
        var talentRule = new SimTriggerDefinition { Talent = talent, Kind = "buff", Key = "talent_on", On = "kill", Duration = 5 };
        Assert.Equal(0, Errors(held, buff, talentRule));
        Assert.True(RefillError(buff, talentRule with { RefillOnSwap = true }));
        // Nor with another rule holding its key.
        Assert.True(RefillError(buff with { RefillOnSwap = true }, talentRule with { Key = "out_on" }));
        Assert.Contains(PackValidator.Validate(With(held with { ImpactDelay = 1, CooldownFromEnd = false }, buff)).Diagnostics,
            diagnostic => diagnostic.Code == "bad-sim-trigger" && diagnostic.Message.StartsWith("Only a skill owns a trigger rule", StringComparison.Ordinal));
    }

    [Fact]
    public void ADecoyRuleIsCheckedAndRoundTrips()
    {
        // ADR 0034: a skill drawing fire holds a window and a cooldown, and draws hits on its health, dealing nothing.
        var rule = Pack.Simulation.Skills.Single();
        var decoy = new SimSkillDefinition { Skill = rule.Skill, Window = "duration", Cooldown = rule.Cooldown, CooldownFromEnd = true, DecoyHealth = "damage" };
        GamePack With(SimSkillDefinition row) => Pack with { Simulation = Pack.Simulation with { Skills = [row] } };
        var loaded = PackValidator.Load(PackWriter.Write(With(decoy)));
        Assert.Empty(loaded.Diagnostics);
        Assert.Equal("damage", Assert.Single(loaded.Compiled!.Pack.Simulation.Skills).DecoyHealth);
        bool Refused(SimSkillDefinition row) => PackValidator.Validate(With(row)).Diagnostics.Any(diagnostic => diagnostic.Path.EndsWith(".decoyHealth", StringComparison.Ordinal));
        Assert.True(Refused(decoy with { Window = "" }));
        Assert.True(Refused(decoy with { Impact = "damage" }));
        Assert.True(Refused(decoy with { Stock = "damage" }));
        Assert.True(Refused(decoy with { CooldownFromEnd = false }));
        Assert.True(Refused(decoy with { Cooldown = "" }));
        Assert.True(Refused(decoy with { Rate = "damage" }));
        Assert.True(Refused(decoy with { ImpactDelay = 1 }));
        Assert.True(Refused(decoy with { Area = "duration" }));
        Assert.True(Refused(decoy with { UseArmorBelow = 1 }));
        // Its health names an output the skill has.
        Assert.Contains(PackValidator.Validate(With(decoy with { DecoyHealth = "nothing" })).Diagnostics, diagnostic => diagnostic.Code == "bad-sim-skill");
    }

    [Fact]
    public void AZonesRuleIsCheckedAndRoundTrips()
    {
        // ADR 0036: a skill marking zones holds a window and a cooldown, and names its outputs, stats and a free selector.
        var rule = Pack.Simulation.Skills.Single();
        var zones = new SimSkillZones { Count = "duration", Health = "damage", Factor = "damage", Share = "armor", Multiplier = "total_damage", Key = "zone_marked" };
        var marking = new SimSkillDefinition { Skill = rule.Skill, Window = "duration", Cooldown = rule.Cooldown, CooldownAfter = 5, Zones = zones };
        GamePack With(SimSkillDefinition row) => Pack with
        {
            Selectors = [.. Pack.Selectors, new() { ID = "zone_marked", Name = "Marked", Values = ["true"] }],
            Simulation = Pack.Simulation with { Skills = [row] }
        };
        var loaded = PackValidator.Load(PackWriter.Write(With(marking)));
        Assert.Empty(loaded.Diagnostics);
        Assert.Equal(zones, Assert.Single(loaded.Compiled!.Pack.Simulation.Skills).Zones);
        // The factor and key may be left out.
        Assert.Empty(PackValidator.Validate(With(marking with { Zones = zones with { Factor = "", Key = "" } })).Diagnostics);
        bool Refused(SimSkillDefinition row, string at = ".zones") => PackValidator.Validate(With(row)).Diagnostics.Any(diagnostic => diagnostic.Path.EndsWith(at, StringComparison.Ordinal));
        Assert.True(Refused(marking with { Window = "" }));
        Assert.True(Refused(marking with { Cooldown = "" }));
        Assert.True(Refused(marking with { Impact = "damage" }));
        Assert.True(Refused(marking with { Rate = "damage" }));
        Assert.True(Refused(marking with { Stock = "damage" }));
        Assert.True(Refused(marking with { ImpactDelay = 1 }));
        Assert.True(Refused(marking with { Area = "duration" }));
        Assert.True(Refused(marking with { DecoyHealth = "damage" }));
        Assert.True(Refused(marking with { Zones = zones with { Count = "" } }));
        Assert.True(Refused(marking with { Zones = zones with { Health = "nothing" } }));
        Assert.True(Refused(marking with { Zones = zones with { Factor = "nothing" } }));
        Assert.True(Refused(marking with { Zones = zones with { Share = "nothing" } }, ".zones.share"));
        Assert.True(Refused(marking with { Zones = zones with { Multiplier = "" } }, ".zones.multiplier"));
        Assert.True(Refused(marking with { Zones = zones with { Key = "nothing" } }, ".zones.key"));
        // The key is true-only, and gates no trigger or skill, since a gate is read once and the mark sets it as it goes.
        GamePack Valued(params string[] values) => With(marking) with { Selectors = [.. Pack.Selectors, new() { ID = "zone_marked", Name = "Marked", Values = [.. values] }] };
        Assert.Contains(PackValidator.Validate(Valued("true", "off")).Diagnostics, diagnostic => diagnostic.Path.EndsWith(".zones.key", StringComparison.Ordinal));
        var gate = Map("zone_marked", "true");
        Assert.True(Refused(marking with { RequiresContext = gate }, ".requires.zone_marked"));
        var gated = With(marking) with { Simulation = With(marking).Simulation with { Triggers = [new() { Talent = "talent_killer", Key = Pack.Simulation.Triggers[0].Key, Kind = "buff", On = "kill", Duration = 5, RequiresContext = gate }] } };
        Assert.Contains(PackValidator.Validate(gated).Diagnostics, diagnostic => diagnostic.Path.EndsWith(".requires.zone_marked", StringComparison.Ordinal));
        // Nor does its use hold a skill_out buff.
        var holding = With(marking) with { Simulation = With(marking).Simulation with { Triggers = [new() { Talent = rule.Skill, Key = Pack.Simulation.Triggers[0].Key, Kind = "buff", On = "skill_out" }] } };
        Assert.Contains(PackValidator.Validate(holding).Diagnostics, diagnostic => diagnostic.Code == "bad-sim-trigger");
        // Nor a key a trigger rule holds.
        var triggered = With(marking) with { Simulation = With(marking).Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Key = "zone_marked" }] } };
        Assert.Contains(PackValidator.Validate(triggered).Diagnostics, diagnostic => diagnostic.Path.EndsWith(".zones.key", StringComparison.Ordinal));
        // One skill marks zones.
        var second = Pack.ActiveSkills.First(skill => skill.ID == rule.Skill) with { ID = "second_marker" };
        var both = With(marking) with
        {
            ActiveSkills = [.. Pack.ActiveSkills, second],
            Simulation = Pack.Simulation with { Skills = [marking, marking with { Skill = second.ID }] }
        };
        Assert.Contains(PackValidator.Validate(both).Diagnostics, diagnostic => diagnostic.Message == "Only one skill marks zones");
    }

    [Fact]
    public void AReviveRuleIsCheckedAndRoundTrips()
    {
        // ADR 0037: a skill reviving its user holds a window and a cooldown from its end, and names its outputs.
        var rule = Pack.Simulation.Skills.Single();
        var revive = new SimSkillRevive { Armor = "damage", Seconds = "duration", PoolShare = 0.5, DamageTaken = 0.5, BleedSeconds = 45, ImmuneSeconds = 5, EndAfter = 4 };
        var reviving = new SimSkillDefinition { Skill = rule.Skill, Window = "duration", Cooldown = rule.Cooldown, CooldownFromEnd = true, Revive = revive };
        GamePack With(SimSkillDefinition row) => Pack with { Simulation = Pack.Simulation with { Skills = [row], Triggers = [] } };
        var loaded = PackValidator.Load(PackWriter.Write(With(reviving)));
        Assert.Empty(loaded.Diagnostics);
        Assert.Equal(revive, Assert.Single(loaded.Compiled!.Pack.Simulation.Skills).Revive);
        Assert.Empty(PackValidator.Validate(With(reviving with { Revive = revive with { ImmuneSeconds = 0, EndAfter = 0 } })).Diagnostics);
        bool Refused(SimSkillDefinition row) => PackValidator.Validate(With(row)).Diagnostics.Any(diagnostic => diagnostic.Path.EndsWith(".revive", StringComparison.Ordinal));
        Assert.True(Refused(reviving with { Window = "" }));
        Assert.True(Refused(reviving with { Cooldown = "" }));
        Assert.True(Refused(reviving with { CooldownFromEnd = false }));
        Assert.True(Refused(reviving with { Impact = "damage" }));
        Assert.True(Refused(reviving with { Rate = "damage" }));
        Assert.True(Refused(reviving with { Stock = "damage" }));
        Assert.True(Refused(reviving with { DecoyHealth = "damage" }));
        Assert.True(Refused(reviving with { Zones = new() }));
        Assert.True(Refused(reviving with { Revive = revive with { Armor = "" } }));
        Assert.True(Refused(reviving with { Revive = revive with { Seconds = "nothing" } }));
        Assert.True(Refused(reviving with { Revive = revive with { PoolShare = 0 } }));
        Assert.True(Refused(reviving with { Revive = revive with { PoolShare = 1.1 } }));
        Assert.True(Refused(reviving with { Revive = revive with { DamageTaken = 0 } }));
        Assert.True(Refused(reviving with { Revive = revive with { BleedSeconds = 0 } }));
        Assert.True(Refused(reviving with { Revive = revive with { ImmuneSeconds = -1 } }));
        Assert.True(Refused(reviving with { Revive = revive with { EndAfter = 61 } }));
        // Nor does its use hold a skill_out buff.
        var holding = With(reviving) with { Simulation = With(reviving).Simulation with { Triggers = [new() { Talent = rule.Skill, Key = Pack.Simulation.Triggers[0].Key, Kind = "buff", On = "skill_out" }] } };
        Assert.Contains(PackValidator.Validate(holding).Diagnostics, diagnostic => diagnostic.Code == "bad-sim-trigger");
        // One skill revives its user.
        var second = Pack.ActiveSkills.First(skill => skill.ID == rule.Skill) with { ID = "second_reviver" };
        var both = With(reviving) with
        {
            ActiveSkills = [.. Pack.ActiveSkills, second],
            Simulation = Pack.Simulation with { Skills = [reviving, reviving with { Skill = second.ID }] }
        };
        Assert.Contains(PackValidator.Validate(both).Diagnostics, diagnostic => diagnostic.Message == "Only one skill revives its user");
    }

    [Fact]
    public void ARepairRuleIsCheckedAndRoundTrips()
    {
        // ADR 0030: a repair skill's rule repairs and deals nothing, waits for armor, and checks through a window with a cooldown.
        var rule = Pack.Simulation.Skills.Single();
        GamePack With(SimSkillDefinition row) => Pack with { Simulation = Pack.Simulation with { Skills = [row] } };
        var repair = new SimSkillDefinition
        {
            Skill = rule.Skill,
            Repair = "damage",
            RepairEvery = 0.2,
            RepairLag = 1.55,
            RepairBelow = 0.9,
            RepairCount = "damage",
            RepairCloud = "duration",
            Window = "duration",
            Cooldown = rule.Cooldown,
            CooldownFromEnd = true,
            EndDelay = 2,
            UseArmorBelow = 1
        };
        var loaded = PackValidator.Load(PackWriter.Write(With(repair)));
        Assert.Empty(loaded.Diagnostics);
        var back = Assert.Single(loaded.Compiled!.Pack.Simulation.Skills);
        Assert.Equal(("damage", 0.2, 1.55, 0.9, "damage", "duration"), (back.Repair, back.RepairEvery, back.RepairLag, back.RepairBelow, back.RepairCount, back.RepairCloud));
        int Errors(SimSkillDefinition row) => PackValidator.Validate(With(row)).Diagnostics.Count(diagnostic => diagnostic.Code == "bad-sim-skill");
        Assert.Equal(0, Errors(repair with { RepairLag = 0, RepairBelow = 0, RepairCount = "", RepairCloud = "" }));
        // A repair may be read live (the owner's call after R151, for the Fixer).
        Assert.Equal(0, Errors(repair with { RateRead = "live" }));
        Assert.Equal(1, Errors(repair with { RateRead = "deploy" }));
        Assert.Equal(1, Errors(repair with { Repair = "nothing" }));
        Assert.Equal(1, Errors(repair with { UseArmorBelow = 0 }));
        Assert.Equal(1, Errors(repair with { RepairEvery = 0 }));
        Assert.Equal(1, Errors(repair with { RepairEvery = SimSkillDefinition.MaximumRepairEvery + 1 }));
        Assert.Equal(1, Errors(repair with { RepairLag = -1 }));
        Assert.Equal(1, Errors(repair with { RepairBelow = 1.5 }));
        Assert.Equal(1, Errors(repair with { RepairCloud = "nothing" }));
        // Its end comes from its repairs: no delayed use or cooldown after it, and no count with charges.
        Assert.Equal(1, Errors(repair with { CooldownFromEnd = false, EndDelay = 0, CooldownAfter = 5 }));
        Assert.True(Errors(repair with { ImpactDelay = 1 }) >= 1);
        Assert.Equal(1, Errors(repair with { CooldownFromEnd = false, EndDelay = 0, Stock = "damage", StockInterval = 1 }));
        Assert.Equal(0, Errors(repair with { CooldownFromEnd = false, EndDelay = 0, RepairCount = "", Stock = "damage", StockInterval = 1 }));
        // It deals nothing, and it needs a window and a cooldown.
        Assert.True(Errors(repair with { Impact = "damage" }) >= 1);
        Assert.Equal(1, Errors(repair with { Window = "" }));
        Assert.Equal(1, Errors(repair with { Cooldown = "" }));
        // Its timing fields need it (and without it the rule deals nothing at all, which is refused too).
        Assert.Contains(PackValidator.Validate(With(repair with { Repair = "" })).Diagnostics, diagnostic => diagnostic.Code == "bad-sim-skill" && diagnostic.Path == "simulation.skills[0].repair");
    }

    [Fact]
    public void ASkillRulesRepairDelayAndArmorWaitAreCheckedAndRoundTrip()
    {
        // ADR 0030: a repair landing after its impact needs that repair; a skill used below an armor share takes a share up to 1.
        var rule = Pack.Simulation.Skills.Single() with { CooldownFromEnd = true };
        GamePack With(SimSkillDefinition row) => Pack with { Simulation = Pack.Simulation with { Skills = [row] } };
        var leech = rule with { ImpactRepair = "damage", RepairDelay = 1.2, UseArmorBelow = 1 };
        var loaded = PackValidator.Load(PackWriter.Write(With(leech)));
        Assert.Empty(loaded.Diagnostics);
        var back = Assert.Single(loaded.Compiled!.Pack.Simulation.Skills);
        Assert.Equal((1.2, 1d), (back.RepairDelay, back.UseArmorBelow));
        int Errors(SimSkillDefinition row) => PackValidator.Validate(With(row)).Diagnostics.Count(diagnostic => diagnostic.Code == "bad-sim-skill");
        Assert.Equal(0, Errors(leech with { UseArmorBelow = 0.9 }));
        Assert.Equal(1, Errors(leech with { ImpactRepair = "" }));
        Assert.Equal(1, Errors(leech with { RepairDelay = -1 }));
        Assert.Equal(1, Errors(leech with { RepairDelay = SimSkillDefinition.MaximumUseDelay + 1 }));
        Assert.Equal(1, Errors(leech with { RepairDelay = double.NaN }));
        Assert.Equal(1, Errors(leech with { UseArmorBelow = -0.5 }));
        Assert.Equal(1, Errors(leech with { UseArmorBelow = 1.5 }));
        Assert.Equal(1, Errors(leech with { UseArmorBelow = double.PositiveInfinity }));
    }

    [Fact]
    public void ASkillRulesArmorOnlyDelaysAndCooldownAfterAreCheckedAndRoundTrip()
    {
        // ADR 0029: an armor-only impact, a delay from the use (longer beyond a distance), and a cooldown starting after the use.
        var rule = Pack.Simulation.Skills.Single();
        GamePack With(SimSkillDefinition row) => Pack with { Simulation = Pack.Simulation with { Skills = [row] } };
        var howl = rule with { ArmorOnly = true, ImpactDelay = 0.7, FarImpactDelay = 1.2, FarFrom = 12.5, CooldownAfter = 1.2 };
        var loaded = PackValidator.Load(PackWriter.Write(With(howl)));
        Assert.Empty(loaded.Diagnostics);
        Assert.Equal(howl, Assert.Single(loaded.Compiled!.Pack.Simulation.Skills));
        int Errors(SimSkillDefinition row) => PackValidator.Validate(With(row)).Diagnostics.Count(diagnostic => diagnostic.Code == "bad-sim-skill");
        Assert.Equal(1, Errors(howl with { Impact = "", Rate = "dps" }) - Errors(rule with { Impact = "", Rate = "dps" }));
        Assert.Equal(1, Errors(howl with { ImpactDelay = -1, FarImpactDelay = 0, FarFrom = 0 }));
        Assert.Equal(1, Errors(howl with { CooldownAfter = SimSkillDefinition.MaximumUseDelay + 1 }));
        Assert.Equal(1, Errors(howl with { FarFrom = 0 }));
        Assert.Equal(1, Errors(howl with { FarImpactDelay = 0 }));
        // cooldownAfter is for a cooldown from the use, and nothing delayed goes with one from the end: both say so.
        Assert.Equal(2, Errors(howl with { ImpactDelay = 0, FarImpactDelay = 0, FarFrom = 0, CooldownFromEnd = true }));
        // A delay with a cooldown from the use's end would start the cooldown before the effects land.
        Assert.Equal(1, Errors(howl with { CooldownAfter = 0, CooldownFromEnd = true }));
    }

    [Fact]
    public void ASkillRulesOutrightKillIsCheckedAndRoundTrips()
    {
        // ADR 0029: a chance output, enemy_target profiles once each, and a delay, together beside an impact.
        var rule = Pack.Simulation.Skills.Single();
        GamePack With(SimSkillDefinition row) => Rated with { Simulation = Rated.Simulation with { Skills = [row] } };
        var howl = rule with { KillChance = "damage", KillProfiles = [1], KillDelay = 1.5 };
        var loaded = PackValidator.Load(PackWriter.Write(With(howl)));
        Assert.Empty(loaded.Diagnostics);
        var back = Assert.Single(loaded.Compiled!.Pack.Simulation.Skills);
        Assert.Equal(("damage", 1.5), (back.KillChance, back.KillDelay));
        Assert.Equal([1], back.KillProfiles);
        int Errors(SimSkillDefinition row) => PackValidator.Validate(With(row)).Diagnostics.Count(diagnostic => diagnostic.Path.EndsWith(".killChance", StringComparison.Ordinal));
        Assert.Equal(1, Errors(howl with { KillProfiles = [] }));
        Assert.Equal(1, Errors(howl with { KillProfiles = [1, 1] }));
        Assert.Equal(1, Errors(howl with { KillProfiles = [0] }));
        Assert.Equal(1, Errors(howl with { KillProfiles = [7] }));
        Assert.Equal(1, Errors(howl with { KillDelay = -1 }));
        Assert.Equal(1, Errors(howl with { KillChance = "" }));
        Assert.Equal(1, Errors(howl with { Impact = "", Rate = "dps" }));
        Assert.Equal(1, Errors(rule with { KillDelay = 1 }));
        // Once per enemy per use: not with several impacts, charges or other targets.
        Assert.Equal(1, Errors(howl with { Impacts = "damage", ImpactInterval = "duration" }));
        Assert.Equal(1, Errors(howl with { Targets = "damage", Rate = "", Window = "" }));
        // A pack with no enemy_target profiles has none to name.
        Assert.Contains(PackValidator.Validate(Pack with { Simulation = Pack.Simulation with { Skills = [howl] } }).Diagnostics,
            diagnostic => diagnostic.Path.EndsWith(".killChance", StringComparison.Ordinal));
    }

    [Fact]
    public void ANextHitBonusIsCheckedAndRoundTrips()
    {
        // ADR 0023 (Headhunter): a known talent, once, a share above 0 up to 10, a known cap stat, a window above 0 up to 600 s.
        var bonus = new SimNextHitBonus { Talent = "talent_killer", Share = 1.25, Cap = "weapon_damage", Window = 30 };
        GamePack With(params SimNextHitBonus[] rows) => Pack with { Simulation = Pack.Simulation with { NextHitBonuses = [.. rows] } };
        var loaded = PackValidator.Load(PackWriter.Write(With(bonus)));
        Assert.Empty(loaded.Diagnostics);
        Assert.Equal(bonus, Assert.Single(loaded.Compiled!.Pack.Simulation.NextHitBonuses));
        Assert.Contains("talent_killer", loaded.Compiled.Pack.Simulation.RuleTalents);
        int Errors(GamePack pack, string code) => PackValidator.Validate(pack).Diagnostics.Count(diagnostic => diagnostic.Code == code);
        Assert.Equal(1, Errors(With(bonus with { Talent = "nothing" }), "bad-sim-next-hit-bonus"));
        Assert.Equal(1, Errors(With(bonus, bonus), "bad-sim-next-hit-bonus"));
        Assert.Equal(1, Errors(With(bonus with { Share = 0 }), "bad-sim-next-hit-bonus"));
        Assert.Equal(1, Errors(With(bonus with { Share = 11 }), "bad-sim-next-hit-bonus"));
        Assert.Equal(1, Errors(With(bonus with { Window = 0 }), "bad-sim-next-hit-bonus"));
        Assert.Equal(1, Errors(With(bonus with { Window = 601 }), "bad-sim-next-hit-bonus"));
        Assert.Equal(1, Errors(With(bonus with { Cap = "nothing" }), "unknown-stat"));
    }

    [Fact]
    public void AForcedShotsShapeIsCheckedAndRoundTrips()
    {
        // ADR 0023: forceCritical and forceWholeShot shape a forced headshot, so they need forceHeadshot.
        var forcing = new SimTriggerDefinition
        {
            Talent = "talent_killer",
            Key = "stacks",
            On = "headshot_kill",
            Kind = "stacks",
            Max = 1,
            ConsumeOn = "hit",
            ForceHeadshot = true,
            ForceCritical = true,
            ForceWholeShot = true
        };
        GamePack With(SimTriggerDefinition rule) => Pack with { Simulation = Pack.Simulation with { Triggers = [.. Pack.Simulation.Triggers.Where(entry => entry.Key != "stacks"), rule] } };
        var loaded = PackValidator.Load(PackWriter.Write(With(forcing)));
        Assert.Empty(loaded.Diagnostics);
        var read = loaded.Compiled!.Pack.Simulation.Triggers.Single(entry => entry.Key == "stacks");
        Assert.Equal((true, true, true), (read.ForceHeadshot, read.ForceCritical, read.ForceWholeShot));
        // Each names its own field.
        var paths = PackValidator.Validate(With(forcing with { ForceHeadshot = false })).Diagnostics.Where(diagnostic => diagnostic.Code == "bad-sim-trigger")
            .Select(diagnostic => diagnostic.Path[diagnostic.Path.LastIndexOf('.')..]).Order(StringComparer.Ordinal);
        Assert.Equal([".forceCritical", ".forceWholeShot"], paths);
    }

    [Fact]
    public void AFightContextGateIsCheckedAndRoundTrips()
    {
        // ADR 0023: a modifier may require the built-in fight_context, static or fight; a static gate on a talent with no fight rule warns.
        var rules = Pack.Simulation.RuleTalents.ToHashSet(StringComparer.Ordinal);
        var ruled = Pack.Talents.First(talent => rules.Contains(talent.ID) && !talent.Modifiers.IsDefaultOrEmpty);
        GamePack Gate(TalentDefinition talent, string value) => Pack with
        {
            Talents = [.. Pack.Talents.Select(entry => entry.ID != talent.ID ? entry : entry with
            {
                Modifiers = [.. entry.Modifiers.Select((modifier, index) => index > 0 ? modifier : modifier with
                {
                    RequiresContext = (modifier.RequiresContext ?? ImmutableDictionary<string, string>.Empty).SetItem("fight_context", value)
                })]
            })]
        };
        var loaded = PackValidator.Load(PackWriter.Write(Gate(ruled, "static")));
        Assert.Empty(loaded.Diagnostics);
        Assert.Equal("static", loaded.Compiled!.Pack.Talents.Single(talent => talent.ID == ruled.ID).Modifiers[0].RequiresContext!["fight_context"]);
        Assert.Empty(PackValidator.Validate(Gate(ruled, "fight")).Diagnostics);
        Assert.Contains(PackValidator.Validate(Gate(ruled, "never")).Diagnostics, diagnostic => diagnostic.Code == "unknown-selector-value" && diagnostic.Severity == "error");
        // Without its fight rule the same gate warns.
        var unruled = Gate(ruled, "static");
        unruled = unruled with { Simulation = unruled.Simulation with { Triggers = [.. unruled.Simulation.Triggers.Where(rule => rule.Talent != ruled.ID)] } };
        var warned = Assert.Single(PackValidator.Validate(unruled).Diagnostics);
        Assert.Equal(("warning", "static-gate-unruled"), (warned.Severity, warned.Code));
        // Only a talent's rule can play it, so a gear item's static gate warns too.
        var item = Pack.Items.First(entry => !entry.Modifiers.IsDefaultOrEmpty);
        var gated = Pack with
        {
            Items = [.. Pack.Items.Select(entry => entry.ID != item.ID ? entry : entry with
            {
                Modifiers = [.. entry.Modifiers.Select((modifier, index) => index > 0 ? modifier : modifier with
                {
                    RequiresContext = (modifier.RequiresContext ?? ImmutableDictionary<string, string>.Empty).SetItem("fight_context", "static")
                })]
            })]
        };
        Assert.Equal("static-gate-unruled", Assert.Single(PackValidator.Validate(gated).Diagnostics).Code);
        // A fight rule always runs in a fight, so it can't take the gate.
        var rule = Pack.Simulation.Triggers.First(entry => entry.Kind is "buff" && entry.On.Length > 0);
        var fightGated = Pack with
        {
            Simulation = Pack.Simulation with
            {
                Triggers = [.. Pack.Simulation.Triggers.Select(entry => entry != rule ? entry : entry with { RequiresContext = ImmutableDictionary<string, string>.Empty.Add("fight_context", "fight") })]
            }
        };
        Assert.Contains(PackValidator.Validate(fightGated).Diagnostics, diagnostic => diagnostic.Code == "bad-sim-trigger" && diagnostic.Path.EndsWith(".requires.fight_context", StringComparison.Ordinal));
    }

    [Fact]
    public void AHeadshotCycleIsCheckedAndRoundTrips()
    {
        // ADR 0021: one cycle per talent, on a tag some weapon carries, a true-only selector, known stats and a kill threshold from 1.
        var weapon = Pack.Items.First(item => Pack.Slots.Any(slot => slot.ID == item.Slot && slot.Weapon));
        var selector = new SelectorDefinition { ID = "waiting", Name = "Waiting", Values = ["true"] };
        var stat = Pack.Stats[0].ID;
        var cycle = new SimHeadshotCycle { Talent = Pack.Talents[0].ID, WeaponTag = "mmr", Key = "waiting", Repair = stat, BonusArmorCap = stat, Allowance = stat, KillFrom = 3 };
        GamePack With(SimHeadshotCycle row) => Pack with
        {
            Items = [.. Pack.Items.Select(item => item.ID == weapon.ID ? item with { Tags = [.. item.Tags, "mmr"] } : item)],
            Selectors = [.. Pack.Selectors, selector],
            Simulation = Pack.Simulation with { HeadshotCycles = [row] }
        };
        var result = PackValidator.Load(PackWriter.Write(With(cycle)));
        Assert.Empty(result.Diagnostics);
        Assert.Equal(cycle, Assert.Single(result.Compiled!.Pack.Simulation.HeadshotCycles));
        int Errors(SimHeadshotCycle row) => PackValidator.Validate(With(row)).Diagnostics.Count(diagnostic => diagnostic.Code is "bad-sim-headshot-cycle" or "unknown-stat");
        Assert.Equal(1, Errors(cycle with { WeaponTag = "nothing" }));
        Assert.Equal(1, Errors(cycle with { Key = "nothing" }));
        Assert.Equal(1, Errors(cycle with { Repair = "nothing" }));
        Assert.Equal(1, Errors(cycle with { KillFrom = 0 }));
        Assert.Equal(1, Errors(cycle with { Talent = "nothing" }));
    }

    [Fact]
    public void AKitAbilityAndInstantKitAreChecked()
    {
        var talent = Pack.Talents[0].ID;
        int Errors(SimArmorKit kit) => PackValidator.Validate(Pack with { Simulation = Pack.Simulation with { ArmorKit = kit } }).Diagnostics
            .Count(diagnostic => diagnostic.Code is "bad-sim-armor-kit" or "unknown-stat");
        // Its talent must replace the kit, once; its cooldown must be a stat.
        Assert.Equal(1, Errors(Kit with { Abilities = [new() { Talent = talent, Cooldown = Pack.Stats[0].ID }] }));
        Assert.Equal(1, Errors(Kit with { ReplacedByTalents = [talent], Abilities = [new() { Talent = talent, Cooldown = Pack.Stats[0].ID }, new() { Talent = talent, Cooldown = Pack.Stats[0].ID }] }));
        Assert.Equal(1, Errors(Kit with { ReplacedByTalents = [talent], Abilities = [new() { Talent = talent, Cooldown = "nothing" }] }));
        // An instant kit without an ability is never used, which the validator warns of.
        Assert.Contains(PackValidator.Validate(Pack with { Simulation = Pack.Simulation with { ArmorKit = Kit with { Instant = new() { Repair = 0.25, Cooldown = 5 } } } }).Diagnostics,
            diagnostic => diagnostic.Code == "sim-instant-kit-unused" && diagnostic.Severity == "warning");
        // An instant kit's repair is a share above 0 up to 1, and its cooldown 0 to 600 s.
        Assert.Equal(1, Errors(Kit with { Instant = new() { Repair = 0, Cooldown = 5 } }));
        Assert.Equal(1, Errors(Kit with { Instant = new() { Repair = 0.25, Cooldown = 601 } }));
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
        // ADR 0013: a pass-on, a spread and a blast keep their shapes through a write and load.
        var passingOn = Pack with { Simulation = Pack.Simulation with { TalentDamage = [new() { Talent = Pack.Simulation.Triggers[0].Talent, On = "hit", Total = Pack.Stats[0].ID, Duration = 10, Period = 1 }], PassOns = [new() { Talent = Pack.Simulation.Triggers[0].Talent, Within = 25 }] } };
        var loadedPassOn = PackValidator.Load(PackWriter.Write(passingOn));
        Assert.Empty(loadedPassOn.Diagnostics);
        Assert.Equal(25d, Assert.Single(loadedPassOn.Compiled!.Pack.Simulation.PassOns).Within);
        var trapping = Pack with { Simulation = Pack.Simulation with { Traps = [new() { Talent = Pack.Simulation.Triggers[0].Talent, TagAfter = 0.5, MaxTags = 10, Status = "shocked", StatusDuration = 10, Every = 30, Cut = 10 }] } };
        var loadedTrap = PackValidator.Load(PackWriter.Write(trapping));
        Assert.Empty(loadedTrap.Diagnostics);
        var trap = Assert.Single(loadedTrap.Compiled!.Pack.Simulation.Traps);
        Assert.Equal((0.5, 10, "shocked", 10d, 30d, 10d), (trap.TagAfter, trap.MaxTags, trap.Status, trap.StatusDuration, trap.Every, trap.Cut));
        var redirecting = Pack with { Simulation = Pack.Simulation with { Redirects = [new() { Talent = Pack.Simulation.Triggers[0].Talent, Share = 0.5, Within = 30 }] } };
        var loadedRedirect = PackValidator.Load(PackWriter.Write(redirecting));
        Assert.Empty(loadedRedirect.Diagnostics);
        var redirect = Assert.Single(loadedRedirect.Compiled!.Pack.Simulation.Redirects);
        Assert.Equal((0.5, 30d), (redirect.Share, redirect.Within));
        var statusPassingOn = Pack with { Simulation = Pack.Simulation with { PassOns = [new() { Talent = Pack.Simulation.Triggers[0].Talent, Within = 10, Status = "any", Share = 0.5, To = "all" }] } };
        var loadedStatusPassOn = PackValidator.Load(PackWriter.Write(statusPassingOn));
        Assert.Empty(loadedStatusPassOn.Diagnostics);
        var statusPassOn = Assert.Single(loadedStatusPassOn.Compiled!.Pack.Simulation.PassOns);
        Assert.Equal(("any", 0.5, "all"), (statusPassOn.Status, statusPassOn.Share, statusPassOn.To));
        // Proliferation's shape: the reach and share from stats, and neither number written.
        var statPassingOn = Pack with { Simulation = Pack.Simulation with { PassOns = [new() { Talent = Pack.Simulation.Triggers[0].Talent, WithinStat = Pack.Stats[0].ID, Status = "any", ShareStat = Pack.Stats[1].ID, To = "all" }] } };
        var statPassOnBytes = PackWriter.Write(statPassingOn);
        var loadedStatPassOn = PackValidator.Load(statPassOnBytes);
        Assert.Empty(loadedStatPassOn.Diagnostics);
        var statPassOn = Assert.Single(loadedStatPassOn.Compiled!.Pack.Simulation.PassOns);
        Assert.Equal((0d, Pack.Stats[0].ID, 0d, Pack.Stats[1].ID), (statPassOn.Within, statPassOn.WithinStat, statPassOn.Share, statPassOn.ShareStat));
        var statPassOnJson = System.Text.Encoding.UTF8.GetString(statPassOnBytes[PackFiles.Simulation].Span);
        Assert.DoesNotContain("\"within\"", statPassOnJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"share\"", statPassOnJson, StringComparison.Ordinal);
        var spreading = Pack with { Simulation = Pack.Simulation with { Spreads = [new() { Talent = Pack.Simulation.Triggers[0].Talent, Within = 8, Every = 15 }] } };
        var loadedSpread = PackValidator.Load(PackWriter.Write(spreading));
        Assert.Empty(loadedSpread.Diagnostics);
        Assert.Equal((8d, 15d, false), (loadedSpread.Compiled!.Pack.Simulation.Spreads[0].Within, loadedSpread.Compiled.Pack.Simulation.Spreads[0].Every, loadedSpread.Compiled.Pack.Simulation.Spreads[0].SkillStatuses));
        // ADR 0015: a spread and a status pass-on that take skills' own statuses keep the field; without it, it isn't written.
        var copying = Pack with
        {
            Simulation = Pack.Simulation with
            {
                StatusDamage = [new() { Status = "burning", Tick = Pack.Stats[0].ID, Period = 1 }, new() { Status = "bleeding", Tick = Pack.Stats[0].ID, Period = 1 }],
                Spreads = [new() { Talent = Pack.Simulation.Triggers[0].Talent, Within = 8, Every = 15, SkillStatuses = true }],
                PassOns = [new() { Talent = Pack.Simulation.Triggers[1].Talent, Within = 10, Status = "any", Share = 0.5, SkillStatuses = true }]
            }
        };
        // ADR 0016: a skill's area keeps its output through a write and load.
        var withArea = Pack with { Simulation = Pack.Simulation with { Skills = [Pack.Simulation.Skills[0] with { Area = Pack.Simulation.Skills[0].Cooldown, AreaFull = Pack.Simulation.Skills[0].Cooldown, AreaEdge = 0.5 }] } };
        var loadedArea = PackValidator.Load(PackWriter.Write(withArea));
        Assert.Empty(loadedArea.Diagnostics);
        var areaRule = loadedArea.Compiled!.Pack.Simulation.Skills[0];
        Assert.Equal((Pack.Simulation.Skills[0].Cooldown, Pack.Simulation.Skills[0].Cooldown, 0.5), (areaRule.Area, areaRule.AreaFull, areaRule.AreaEdge));
        // ADR 0017: a control skill with an area and no impact keeps its field.
        var control = Pack with { Simulation = Pack.Simulation with { Skills = [Pack.Simulation.Skills[0] with { Rate = "", Impact = "", Status = "shocked", ControlStatus = true, Area = Pack.Simulation.Skills[0].Window }] } };
        var loadedControl = PackValidator.Load(PackWriter.Write(control));
        Assert.Empty(loadedControl.Diagnostics);
        Assert.True(loadedControl.Compiled!.Pack.Simulation.Skills[0].ControlStatus);
        var copyingBytes = PackWriter.Write(copying);
        var loadedCopying = PackValidator.Load(copyingBytes);
        Assert.Empty(loadedCopying.Diagnostics);
        Assert.True(loadedCopying.Compiled!.Pack.Simulation.Spreads[0].SkillStatuses);
        Assert.True(loadedCopying.Compiled.Pack.Simulation.PassOns[0].SkillStatuses);
        Assert.Contains("\"skillStatuses\": true", System.Text.Encoding.UTF8.GetString(copyingBytes[PackFiles.Simulation].Span), StringComparison.Ordinal);
        Assert.DoesNotContain("skillStatuses", System.Text.Encoding.UTF8.GetString(PackWriter.Write(spreading)[PackFiles.Simulation].Span), StringComparison.Ordinal);
        var blasting = Pack with { Simulation = Pack.Simulation with { StatusDamage = [new() { Status = "bleeding", Tick = Pack.Stats[0].ID, Period = 1 }], Blasts = [new() { Talent = Pack.Simulation.Triggers[0].Talent, On = "headshot_kill", Amount = Pack.Stats[0].ID, Within = 5, Status = "bleeding", StatusDuration = 8 }] } };
        var loadedBlast = PackValidator.Load(PackWriter.Write(blasting));
        Assert.Empty(loadedBlast.Diagnostics);
        var blast = loadedBlast.Compiled!.Pack.Simulation.Blasts[0];
        Assert.Equal(("headshot_kill", 5d, "bleeding", 8d), (blast.On, blast.Within, blast.Status, blast.StatusDuration));
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

    [Fact]
    public void ASelectorWithADefaultMayBeDrivenOnlyByAPhasedDistanceRule()
    {
        // A buff rule turns its key off by removing it, which would expose the default; a phased distance rule always sets a value.
        var rule = Pack.Simulation.Triggers[0];
        var selector = Pack.Selectors.First(entry => entry.ID == rule.Key);
        var withDefault = Pack with { Selectors = [.. Pack.Selectors.Select(entry => entry.ID == rule.Key ? entry with { Default = selector.Values[0] } : entry)] };
        Assert.Contains(PackValidator.Validate(withDefault).Diagnostics, diagnostic => diagnostic.Code == "selector-default");
        var banded = withDefault with
        {
            Simulation = withDefault.Simulation with
            {
                Triggers = [rule with { Kind = "distance", On = "", Duration = 0, Phases = [new() { Until = 15, Value = selector.Values[0] }] }]
            }
        };
        Assert.DoesNotContain(PackValidator.Validate(banded).Diagnostics, diagnostic => diagnostic.Code == "selector-default");
    }

    [Fact]
    public void AcceptsChemicalAgentsTwoStacksRulesOnOneKey()
    {
        // Its enemy part and its player part: one talent's, shaped alike, each gaining its own way.
        var talent = Pack.Simulation.Triggers[0].Talent;
        var enemy = new SimTriggerDefinition { Talent = talent, Key = "stacks", Kind = "stacks", On = "second", Max = 5, Duration = 5, Refresh = true, Decay = 1, WhileStatus = "any" };
        var shared = Pack with { Simulation = Pack.Simulation with { Triggers = [enemy, enemy with { Gain = 2, WhileStatus = "", WhilePlayerStatus = "any" }] } };
        Assert.DoesNotContain(PackValidator.Validate(shared).Diagnostics, diagnostic => diagnostic.Severity == "error");
    }

    [Fact]
    public void AcceptsASharedBuffRuleAndRefusesItOnlyForShareCopies()
    {
        var shared = Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { ShareCopies = true }] } };
        Assert.DoesNotContain(PackValidator.Validate(shared).Diagnostics, diagnostic => diagnostic.Severity == "error");
        // The ready rule refused above is valid without shareCopies, so that refusal is shareCopies' own.
        var ready = Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Kind = "ready" }] } };
        Assert.DoesNotContain(PackValidator.Validate(ready).Diagnostics, diagnostic => diagnostic.Severity == "error");
        var refused = Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Kind = "ready", ShareCopies = true }] } };
        Assert.Contains(PackValidator.Validate(refused).Diagnostics, diagnostic => diagnostic.Message.Contains("shareCopies", StringComparison.Ordinal));
    }

    [Fact]
    public void AcceptsAPrimedReadyRule()
    {
        var rules = Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { Kind = "ready", On = "", Duration = 0, PrimeHolstered = 10 }] } };
        Assert.DoesNotContain(PackValidator.Validate(rules).Diagnostics, diagnostic => diagnostic.Severity == "error");
    }

    [Fact]
    public void AcceptsCoverShootersSingleTimerAndKillRefresh()
    {
        // R112: stacks per shot, one 15 s timer from the first, restarted by a kill.
        var rules = Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[1] with { Duration = 15, FromFirstGain = true, RefreshOn = "kill" }] } };
        Assert.DoesNotContain(PackValidator.Validate(rules).Diagnostics, diagnostic => diagnostic.Severity == "error");
    }

    [Fact]
    public void AcceptsARefreshOnATimedBuff()
    {
        var rules = Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { On = "swapped", RefreshOn = "kill" }] } };
        Assert.DoesNotContain(PackValidator.Validate(rules).Diagnostics, diagnostic => diagnostic.Severity == "error");
    }

    [Fact]
    public void AcceptsARuleGatedOnAStatusOnThePlayerAndWarnsWhenNothingPutsItOn()
    {
        var rules = Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[0] with { WhilePlayerStatus = "any" }] } };
        var diagnostics = PackValidator.Validate(rules).Diagnostics;
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == "error");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "sim-player-status-unapplied" && diagnostic.Path == "simulation.triggers[0].whilePlayerStatus");
    }

    [Fact]
    public void WarnsWhenEveryStatusOnThePlayerIsLeftOut()
    {
        // R122: with ensnared left out of any, a pack whose enemies only ensnare the player never opens the rule.
        var diagnosticsFor = (string status) => PackValidator.Validate(Rated with
        {
            Simulation = Rated.Simulation with
            {
                EnemyStatuses = [new() { Target = 1, Status = status, Duration = 2 }],
                Triggers = [Pack.Simulation.Triggers[0] with { WhilePlayerStatus = "any", AnyStatusExcept = ["ensnared"] }]
            }
        }).Diagnostics;
        Assert.Contains(diagnosticsFor("ensnared"), diagnostic => diagnostic.Code == "sim-player-status-unapplied");
        Assert.DoesNotContain(diagnosticsFor("blinded"), diagnostic => diagnostic.Code == "sim-player-status-unapplied");
    }

    [Fact]
    public void AcceptsStatusesEnemiesPutOnThePlayer()
    {
        // ADR 0018: a damaging status with its tick and resistances, and a control status with a duration only.
        var rules = Rated with
        {
            Simulation = Rated.Simulation with
            {
                EnemyStatuses = [new() { Target = 1, Status = "bleeding", Duration = 2, Tick = "armor", Period = 0.5, TickOnApply = true, Resistances = ["armor", "skill_tier"], MinDuration = 0.5 },
                    new() { Target = 2, Status = "shocked", Duration = 3 }]
            }
        };
        Assert.DoesNotContain(PackValidator.Validate(rules).Diagnostics, diagnostic => diagnostic.Severity == "error");
    }

    [Fact]
    public void AcceptsAnIncomingDefault()
    {
        var rules = Rated with { Simulation = Rated.Simulation with { IncomingDefaults = [new() { Input = "enemy_target", Value = 1 }] } };
        Assert.DoesNotContain(PackValidator.Validate(rules).Diagnostics, diagnostic => diagnostic.Severity == "error");
    }

    // Cover Shooter's shape: a stacks rule with an event may be gated on a selector too (P13).
    [Fact]
    public void AcceptsRequiresOnAStacksRuleWithAnEvent()
    {
        var rules = Pack with { Simulation = Pack.Simulation with { Triggers = [Pack.Simulation.Triggers[1] with { RequiresContext = ImmutableDictionary<string, string>.Empty.Add("overwatch_active", "true") }] } };
        var result = PackValidator.Validate(rules);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == "error");
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
