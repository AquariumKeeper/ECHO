using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Expressions;
using CharacterPlanner.Core.Packs;
using Codes = CharacterPlanner.Application.Commands.OperationErrorCodes;

namespace CharacterPlanner.Application.Commands;

/// <summary>
/// Applies operations in order against one pack, all or none. Each operation is checked against the pack
/// and the state so far; the first failure rejects the batch and returns the original state. Edits go through
/// BuildSnapshot's helpers, so changing an item, mod, skill or specialization drops its stale configuration.
/// </summary>
public sealed class OperationApplier : IOperationApplier
{
    /// <summary>The stat a hand-entered giver's armor stands for when an ally effect's value is a formula.</summary>
    public const string GiverArmorStat = "character_armor";
    /// <summary>A party holds at most three other agents.</summary>
    public const int PartyLimit = PartyRules.MaxMembers;

    private readonly CompiledPackExpressions compiled;
    private readonly GamePack pack;
    private readonly ImmutableDictionary<string, SlotDefinition> slots;
    private readonly ImmutableDictionary<string, ItemDefinition> items;
    private readonly ImmutableDictionary<string, AttributeDefinition> attributes;
    private readonly ImmutableDictionary<string, ModDefinition> mods;
    private readonly ImmutableDictionary<string, TalentDefinition> talents;
    private readonly ImmutableDictionary<string, ActiveSkillSlotDefinition> skillSlots;
    private readonly ImmutableDictionary<string, ActiveSkillDefinition> skills;
    private readonly ImmutableDictionary<string, SpecializationDefinition> specializations;
    private readonly ImmutableDictionary<string, AccountUpgradeDefinition> accountUpgrades;
    private readonly ImmutableDictionary<string, NumericContextInputDefinition> numericInputs;
    private readonly ImmutableDictionary<string, SelectorDefinition> selectors;
    private readonly ImmutableDictionary<string, (TalentDefinition Talent, AllyEffect Effect)> allyEffects;
    private readonly ImmutableDictionary<string, double> zeroStats;
    private readonly Func<string, BuildSnapshot> readShareCode;

    /// <param name="readShareCode">
    /// Reads a party member's share code, throwing <see cref="ShareCodeException"/> (and nothing else) for one it
    /// can't read. Defaults to the app's own format; a composition root that should also accept testers' Go-era
    /// codes passes Legacy's <c>ShareCodeReader</c>.
    /// </param>
    public OperationApplier(CompiledPackExpressions compiled, Func<string, BuildSnapshot>? readShareCode = null)
    {
        this.compiled = compiled;
        this.readShareCode = readShareCode ?? ShareCode.Decode;
        pack = compiled.Pack;
        slots = ById(pack.Slots, slot => slot.ID);
        items = ById(pack.Items, item => item.ID);
        attributes = ById(pack.Attributes, attribute => attribute.ID);
        mods = ById(pack.Mods, mod => mod.ID);
        talents = ById(pack.Talents, talent => talent.ID);
        skillSlots = ById(pack.ActiveSkillSlots, slot => slot.ID);
        skills = ById(pack.ActiveSkills, skill => skill.ID);
        specializations = ById(pack.Specializations, specialization => specialization.ID);
        accountUpgrades = ById(pack.AccountUpgrades, upgrade => upgrade.ID);
        numericInputs = ById(pack.NumericContextInputs, input => input.ID);
        selectors = ById(pack.Selectors, selector => selector.ID);
        allyEffects = pack.Talents.SelectMany(talent => talent.AllyEffects.Select(effect => (Talent: talent, Effect: effect)))
            .GroupBy(pair => pair.Effect.ID, StringComparer.Ordinal).ToImmutableDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        zeroStats = pack.Stats.Select(stat => stat.ID).Distinct(StringComparer.Ordinal).ToImmutableDictionary(id => id, _ => 0d, StringComparer.Ordinal);
    }

    private static ImmutableDictionary<string, T> ById<T>(ImmutableArray<T> values, Func<T, string> id) =>
        values.GroupBy(id, StringComparer.Ordinal).ToImmutableDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

    public OperationOutcome Apply(PlannerState state, ImmutableArray<BuildOperation> operations)
    {
        var current = state;
        var changes = ImmutableArray.CreateBuilder<OperationChange>();
        var before = DocumentPaths.Flatten(current);
        var index = 0;
        foreach (var operation in operations.IsDefault ? [] : operations)
        {
            index++;
            if (operation is null) { return new(state, [], [new(index, "", Codes.KindUnsupported, $"operation {index} is missing")]); }
            try { current = ApplyOne(current, operation); }
            catch (Rejection rejection)
            { return new(state, [], [new(index, operation.Kind, rejection.Code, $"operation {index} ({operation.Kind}): {rejection.Message}")]); }
            var after = DocumentPaths.Flatten(current);
            var (set, cleared) = DocumentPaths.Diff(before, after);
            changes.Add(new(index, operation.Kind, set, cleared));
            before = after;
        }
        return new(current, changes.ToImmutable(), []);
    }

    private PlannerState ApplyOne(PlannerState state, BuildOperation operation)
    {
        if (operation is ClearableOperation { Clear: true, HasValues: true })
        { throw new Rejection(Codes.ClearWithValue, "a clear takes only the fields that say what to clear"); }
        var build = state.Build;
        return operation switch
        {
            NameOperation name => string.IsNullOrWhiteSpace(name.Text)
                ? throw new Rejection(Codes.NameBlank, "the build name must not be blank")
                : state with { Build = build with { Meta = build.Meta with { Name = name.Text } } },
            NotesOperation notes => state with { Build = build with { Meta = build.Meta with { Notes = notes.Text } } },
            ContextOperation context => state with { Assumptions = Context(state.Assumptions, context) },
            _ => state with { Build = Edit(build, operation) }
        };
    }

    private BuildSnapshot Edit(BuildSnapshot build, BuildOperation operation) => operation switch
    {
        EquipmentOperation equipment => Equipment(build, equipment),
        AttributeOperation attribute => Attribute(build, attribute),
        ModOperation mod => Mod(build, mod),
        ModRollOperation roll => ModRoll(build, roll),
        TalentOperation talent => Talent(build, talent),
        ActiveSkillOperation skill => ActiveSkill(build, skill),
        SkillUpgradeOperation upgrade => SkillUpgrade(build, upgrade),
        SkillModOperation mod => SkillMod(build, mod),
        SpecializationOperation specialization => Specialization(build, specialization),
        SpecializationNodeOperation node => SpecializationNode(build, node),
        AccountUpgradeOperation upgrade => AccountUpgrade(build, upgrade),
        EquipmentUpgradeOperation upgrade => EquipmentUpgrade(build, upgrade),
        PrototypeOperation prototype => Prototype(build, prototype),
        PartyMemberOperation member => build.WithParty(PartyMember(build.Party, member)),
        PartyBuffOperation buff => build.WithParty(PartyBuff(build.Party, buff)),
        _ => throw new Rejection(Codes.KindUnsupported, $"operation kind {Q(operation.Kind)} is not supported")
    };

    // Equipment and item configuration.

    private BuildSnapshot Equipment(BuildSnapshot build, EquipmentOperation operation)
    {
        var slot = Slot(operation.Slot);
        if (operation.Clear) { return build.WithEquipment(slot.ID, ""); }
        var item = Item(operation.Id);
        if (item.Slot != slot.ID) { throw new Rejection(Codes.ItemWrongSlot, $"item {Q(item.ID)} cannot be equipped in slot {Q(slot.ID)}; it belongs in {Q(item.Slot)}"); }
        return FillFixedChoices(build.WithEquipment(slot.ID, item.ID), slot.ID, item);
    }

    /// <summary>Selects the only choice of each still-empty attribute, mod and talent slot, at the highest legal roll.</summary>
    private BuildSnapshot FillFixedChoices(BuildSnapshot build, string slotId, ItemDefinition item)
    {
        foreach (var slot in item.AttributeSlots.Where(slot => slot.Choices.Length == 1))
        {
            var choice = slot.Choices[0];
            if (build.AttributeRolls.GetValueOrDefault(slotId)?.ContainsKey(slot.ID) == true || !attributes.TryGetValue(choice.AttributeID, out var attribute)) { continue; }
            build = build.WithAttributeRoll(slotId, slot.ID, new(attribute.ID, Bounds(build, slotId, item, attribute, choice).Highest()));
        }
        foreach (var slot in item.ModSlots.Where(slot => slot.Choices.Length == 1))
        {
            if (build.ModSelections.GetValueOrDefault(slotId)?.ContainsKey(slot.ID) == true || !mods.TryGetValue(slot.Choices[0], out var mod)) { continue; }
            build = SelectMod(build, slotId, slot.ID, mod);
        }
        foreach (var slot in item.TalentSlots.Where(slot => slot.Choices.Length == 1))
        {
            if (build.TalentSelections.GetValueOrDefault(slotId)?.ContainsKey(slot.ID) == true || !talents.ContainsKey(slot.Choices[0])) { continue; }
            build = build.WithTalentSelection(slotId, slot.ID, slot.Choices[0]);
        }
        return build;
    }

    private BuildSnapshot Attribute(BuildSnapshot build, AttributeOperation operation)
    {
        var (slotId, item) = Equipped(build, operation.Slot);
        var slot = item.AttributeSlots.FirstOrDefault(slot => slot.ID == operation.Target)
            ?? throw new Rejection(Codes.UnknownAttributeSlot, $"{Q(item.ID)} has no attribute slot {Q(operation.Target)}");
        if (operation.Clear) { return build.WithAttributeRoll(slotId, slot.ID, null); }
        var attribute = attributes.GetValueOrDefault(operation.Id) ?? throw new Rejection(Codes.UnknownAttribute, $"unknown attribute {Q(operation.Id)}");
        var choice = slot.Choices.FirstOrDefault(choice => choice.AttributeID == attribute.ID)
            ?? throw new Rejection(Codes.AttributeNotAllowed, $"attribute {Q(attribute.ID)} is not a choice in {Q(item.ID)}'s attribute slot {Q(slot.ID)}");
        var bounds = Bounds(build, slotId, item, attribute, choice);
        var value = operation.Value ?? bounds.Highest();
        if (!bounds.Accepts(value)) { throw new Rejection(Codes.AttributeRollInvalid, $"{Q(attribute.ID)} roll {Number(value)} is not a legal roll: {Describe(bounds)}"); }
        return build.WithAttributeRoll(slotId, slot.ID, new(attribute.ID, bounds.Snap(value)));
    }

    private BuildSnapshot Mod(BuildSnapshot build, ModOperation operation)
    {
        var (slotId, item) = Equipped(build, operation.Slot);
        var slot = ModSlot(item, operation.Target);
        if (operation.Clear) { return build.WithModSelection(slotId, slot.ID, ""); }
        var mod = mods.GetValueOrDefault(operation.Id) ?? throw new Rejection(Codes.UnknownMod, $"unknown mod {Q(operation.Id)}");
        if (!slot.Choices.Contains(mod.ID)) { throw new Rejection(Codes.ModNotAllowed, $"mod {Q(mod.ID)} is not a choice in {Q(item.ID)}'s mod slot {Q(slot.ID)}"); }
        return SelectMod(build, slotId, slot.ID, mod);
    }

    /// <summary>Selects a mod; a rolled mod without a roll gets its highest legal roll (reselecting the same mod keeps its roll).</summary>
    private BuildSnapshot SelectMod(BuildSnapshot build, string slotId, string modSlotId, ModDefinition mod)
    {
        build = build.WithModSelection(slotId, modSlotId, mod.ID);
        return attributes.GetValueOrDefault(mod.RollAttributeID) is { } roll && build.ModRolls.GetValueOrDefault(slotId)?.ContainsKey(modSlotId) != true
            ? build.WithModRoll(slotId, modSlotId, Bounds(roll).Highest()) : build;
    }

    private BuildSnapshot ModRoll(BuildSnapshot build, ModRollOperation operation)
    {
        var (slotId, item) = Equipped(build, operation.Slot);
        var slot = ModSlot(item, operation.Target);
        if (operation.Clear) { return build.WithModRoll(slotId, slot.ID, null); }
        var selected = build.ModSelections.GetValueOrDefault(slotId)?.GetValueOrDefault(slot.ID) ?? "";
        if (selected.Length == 0) { throw new Rejection(Codes.ModUnselected, $"{Q(item.ID)}'s mod slot {Q(slot.ID)} has no selected mod to roll"); }
        var mod = mods.GetValueOrDefault(selected) ?? throw new Rejection(Codes.UnknownMod, $"unknown mod {Q(selected)}");
        var attribute = attributes.GetValueOrDefault(mod.RollAttributeID) ?? throw new Rejection(Codes.ModRollNotAllowed, $"mod {Q(mod.ID)} has no roll");
        var bounds = Bounds(attribute);
        var value = operation.Value ?? bounds.Highest();
        if (!bounds.Accepts(value)) { throw new Rejection(Codes.ModRollInvalid, $"{Q(mod.ID)} roll {Number(value)} is not a legal roll: {Describe(bounds)}"); }
        return build.WithModRoll(slotId, slot.ID, bounds.Snap(value));
    }

    private BuildSnapshot Talent(BuildSnapshot build, TalentOperation operation)
    {
        var (slotId, item) = Equipped(build, operation.Slot);
        var slot = item.TalentSlots.FirstOrDefault(slot => slot.ID == operation.Target)
            ?? throw new Rejection(Codes.UnknownTalentSlot, $"{Q(item.ID)} has no talent slot {Q(operation.Target)}");
        if (operation.Clear) { return build.WithTalentSelection(slotId, slot.ID, ""); }
        var talent = talents.GetValueOrDefault(operation.Id) ?? throw new Rejection(Codes.UnknownTalent, $"unknown talent {Q(operation.Id)}");
        if (!slot.Choices.Contains(talent.ID)) { throw new Rejection(Codes.TalentNotAllowed, $"talent {Q(talent.ID)} is not a choice in {Q(item.ID)}'s talent slot {Q(slot.ID)}"); }
        return build.WithTalentSelection(slotId, slot.ID, talent.ID);
    }

    private BuildSnapshot EquipmentUpgrade(BuildSnapshot build, EquipmentUpgradeOperation operation)
    {
        var (slotId, item) = Equipped(build, operation.Slot);
        if (item.ID != operation.Id)
        {
            _ = Item(operation.Id);
            throw new Rejection(Codes.ItemNotEquipped, $"item {Q(operation.Id)} is not equipped in slot {Q(slotId)}");
        }
        var upgrade = item.Upgrade ?? throw new Rejection(Codes.EquipmentUpgradeUnavailable, $"{Q(item.ID)} has no ranked upgrade");
        if (operation.Rank < 0 || operation.Rank > upgrade.MaxRank)
        { throw new Rejection(Codes.EquipmentUpgradeRankInvalid, $"{Q(item.ID)} upgrade rank {Number(operation.Rank)} is outside 0 to {Number(upgrade.MaxRank)}"); }
        return build.WithEquipmentUpgrade(slotId, item.ID, operation.Rank);
    }

    private BuildSnapshot Prototype(BuildSnapshot build, PrototypeOperation operation)
    {
        var prototype = pack.Prototype ?? throw new Rejection(Codes.PrototypeUnavailable, "this pack has no Prototype quality");
        var (slotId, item) = Equipped(build, operation.Slot);
        if (operation.Clear) { return MoveRolls(build.WithPrototype(slotId, null), slotId, item); }
        if (!PrototypeRules.Eligible(pack, item, slotId)) { throw new Rejection(Codes.PrototypeNotEligible, $"{Q(item.ID)} can't be Prototype"); }
        PrototypeSelection selection;
        if (operation.Augment is null)
        {
            if (operation.Level is not null) { throw new Rejection(Codes.PrototypeLevelWithoutAugment, "a level needs an augment \"id\""); }
            selection = new(item.ID, "", 0);
        }
        else
        {
            var augment = prototype.Augments.FirstOrDefault(augment => augment.ID == operation.Augment)
                ?? throw new Rejection(Codes.UnknownAugment, $"unknown augment {Q(operation.Augment)}");
            var level = operation.Level ?? augment.Levels;
            if (level < 1 || level > augment.Levels) { throw new Rejection(Codes.PrototypeLevelInvalid, $"{Q(augment.ID)} level {Number(level)} is outside 1 to {Number(augment.Levels)}"); }
            selection = new(item.ID, augment.ID, level);
        }
        return MoveRolls(build.WithPrototype(slotId, selection), slotId, item);
    }

    /// <summary>Clamps the item's attribute rolls into its current (normal or Prototype) ranges.</summary>
    private BuildSnapshot MoveRolls(BuildSnapshot build, string slotId, ItemDefinition item)
    {
        foreach (var (target, roll) in build.AttributeRolls.GetValueOrDefault(slotId) ?? ImmutableDictionary<string, AttributeRoll>.Empty)
        {
            var choice = item.AttributeSlots.FirstOrDefault(slot => slot.ID == target)?.Choices.FirstOrDefault(choice => choice.AttributeID == roll.AttributeId);
            if (choice is null || !attributes.TryGetValue(roll.AttributeId, out var attribute)) { continue; }
            var bounds = Bounds(build, slotId, item, attribute, choice);
            // Clamp into the new range, then onto its step grid (a Prototype minimum need not share the normal grid).
            var value = bounds.Snap(roll.Value < bounds.Min ? bounds.Min : roll.Value > bounds.Max ? bounds.Highest() : roll.Value);
            if (!bounds.Accepts(value)) { value = bounds.Highest(); }
            if (value != roll.Value) { build = build.WithAttributeRoll(slotId, target, roll with { Value = value }); }
        }
        return build;
    }

    // Skills, specialization and account.

    private BuildSnapshot ActiveSkill(BuildSnapshot build, ActiveSkillOperation operation)
    {
        var slot = skillSlots.GetValueOrDefault(operation.Slot) ?? throw new Rejection(Codes.UnknownSkillSlot, $"unknown skill slot {Q(operation.Slot)}");
        if (operation.Clear) { return build.WithActiveSkillSelection(slot.ID, ""); }
        var skill = skills.GetValueOrDefault(operation.Id) ?? throw new Rejection(Codes.UnknownSkill, $"unknown skill {Q(operation.Id)}");
        if (!slot.Choices.Contains(skill.ID)) { throw new Rejection(Codes.SkillNotAllowed, $"skill {Q(skill.ID)} is not a choice in skill slot {Q(slot.ID)}"); }
        return build.WithActiveSkillSelection(slot.ID, skill.ID);
    }

    private (string Slot, ActiveSkillDefinition Skill) SelectedSkill(BuildSnapshot build, string slotId)
    {
        var slot = skillSlots.GetValueOrDefault(slotId) ?? throw new Rejection(Codes.UnknownSkillSlot, $"unknown skill slot {Q(slotId)}");
        var id = build.ActiveSkillSelections.GetValueOrDefault(slot.ID, "");
        if (id.Length == 0) { throw new Rejection(Codes.SkillUnselected, $"skill slot {Q(slot.ID)} has no selected skill"); }
        return (slot.ID, skills.GetValueOrDefault(id) ?? throw new Rejection(Codes.UnknownSkill, $"unknown skill {Q(id)}"));
    }

    private BuildSnapshot SkillUpgrade(BuildSnapshot build, SkillUpgradeOperation operation)
    {
        if (operation.Clear)
        {
            var slot = skillSlots.GetValueOrDefault(operation.Slot) ?? throw new Rejection(Codes.UnknownSkillSlot, $"unknown skill slot {Q(operation.Slot)}");
            return build.WithSkillUpgrade(slot.ID, "", 0);
        }
        var (slotId, skill) = SelectedSkill(build, operation.Slot);
        var upgrade = skill.Upgrade ?? throw new Rejection(Codes.SkillUpgradeUnavailable, $"skill {Q(skill.ID)} has no Expertise rank");
        if (operation.Rank < 0 || operation.Rank > upgrade.MaxRank)
        { throw new Rejection(Codes.SkillUpgradeRankInvalid, $"{Q(skill.ID)} Expertise rank {Number(operation.Rank)} is outside 0 to {Number(upgrade.MaxRank)}"); }
        return build.WithSkillUpgrade(slotId, skill.ID, operation.Rank);
    }

    private BuildSnapshot SkillMod(BuildSnapshot build, SkillModOperation operation)
    {
        var (slotId, skill) = SelectedSkill(build, operation.Slot);
        var slot = skill.ModSlots.FirstOrDefault(slot => slot.ID == operation.Target)
            ?? throw new Rejection(Codes.UnknownSkillModSlot, $"skill {Q(skill.ID)} has no mod slot {Q(operation.Target)}");
        if (operation.Clear) { return build.WithSkillMod(slotId, skill.ID, slot.ID, null); }
        var choice = slot.Choices.FirstOrDefault(choice => choice.ID == operation.Id)
            ?? throw new Rejection(Codes.UnknownSkillMod, $"skill mod {Q(operation.Id)} is not a choice in {Q(skill.ID)}'s mod slot {Q(slot.ID)}");
        var bounds = new AttributeRollBounds(choice.Min, choice.Max, choice.Step);
        var value = operation.Value ?? bounds.Highest();
        if (!bounds.Accepts(value)) { throw new Rejection(Codes.SkillModRollInvalid, $"{Q(choice.ID)} roll {Number(value)} is not a legal roll: {Describe(bounds)}"); }
        return build.WithSkillMod(slotId, skill.ID, slot.ID, new(choice.ID, bounds.Snap(value)));
    }

    private BuildSnapshot Specialization(BuildSnapshot build, SpecializationOperation operation)
    {
        if (operation.Clear) { return build.WithSpecialization(""); }
        var specialization = specializations.GetValueOrDefault(operation.Id) ?? throw new Rejection(Codes.UnknownSpecialization, $"unknown specialization {Q(operation.Id)}");
        return build.WithSpecialization(specialization.ID);
    }

    private BuildSnapshot SpecializationNode(BuildSnapshot build, SpecializationNodeOperation operation)
    {
        if (build.SpecializationId.Length == 0) { throw new Rejection(Codes.SpecializationUnselected, "select a specialization before its nodes"); }
        var specialization = specializations.GetValueOrDefault(build.SpecializationId)
            ?? throw new Rejection(Codes.UnknownSpecialization, $"unknown specialization {Q(build.SpecializationId)}");
        if (!specialization.Nodes.Any(node => node.ID == operation.Id))
        { throw new Rejection(Codes.UnknownSpecializationNode, $"specialization {Q(specialization.ID)} has no node {Q(operation.Id)}"); }
        return build.WithSpecializationNode(operation.Id, operation.Enabled);
    }

    private BuildSnapshot AccountUpgrade(BuildSnapshot build, AccountUpgradeOperation operation)
    {
        var upgrade = accountUpgrades.GetValueOrDefault(operation.Id) ?? throw new Rejection(Codes.UnknownAccountUpgrade, $"unknown account upgrade {Q(operation.Id)}");
        if (operation.Rank < 0 || operation.Rank > upgrade.MaxRank)
        { throw new Rejection(Codes.AccountUpgradeRankInvalid, $"{Q(upgrade.ID)} rank {Number(operation.Rank)} is outside 0 to {Number(upgrade.MaxRank)}"); }
        return build.WithAccountUpgrade(upgrade.ID, operation.Rank);
    }

    // Assumptions.

    private ImmutableDictionary<string, string> Context(ImmutableDictionary<string, string> assumptions, ContextOperation operation)
    {
        var id = operation.Id;
        var numeric = numericInputs.GetValueOrDefault(id);
        var selector = selectors.GetValueOrDefault(id);
        var weaponSlot = id == GamePack.WeaponSlotSelector;
        // Removing the explicit value is what restores the default: the resolver falls back to it.
        // A value the pack no longer knows can still be cleared.
        if (operation.Clear && assumptions.ContainsKey(id)) { return assumptions.Remove(id); }
        if (numeric is null && selector is null && !weaponSlot) { throw new Rejection(Codes.UnknownContext, $"unknown assumption {Q(id)}"); }
        if (operation.Clear) { return assumptions; }
        if (numeric is not null)
        {
            if (operation.Text is not null || operation.Value is not { } value) { throw new Rejection(Codes.ContextNeedsValue, $"{Q(id)} is numeric and takes \"value\""); }
            if (!numeric.Accepts(value))
            { throw new Rejection(Codes.ContextValueInvalid, $"{Q(id)} must be from {Number(numeric.Min)} to {Number(numeric.Max)} in steps of {Number(numeric.Step)}; {Number(value)} isn't"); }
            return assumptions.SetItem(id, Number(numeric.Snap(value)));
        }
        if (operation.Value is not null || operation.Text is not { } text) { throw new Rejection(Codes.ContextNeedsText, $"{Q(id)} takes \"text\""); }
        if (weaponSlot)
        {
            if (!slots.TryGetValue(text, out var slot) || !slot.Weapon) { throw new Rejection(Codes.ContextValueInvalid, $"{Q(id)} takes a weapon slot id; {Q(text)} isn't one"); }
        }
        else if (!selector!.Values.Contains(text))
        { throw new Rejection(Codes.ContextValueInvalid, $"{Q(id)} takes {string.Join(" or ", selector.Values.Select(Q))}, not {Q(text)}"); }
        return assumptions.SetItem(id, text);
    }

    // Party.

    private ImmutableArray<PartyMember> PartyMember(ImmutableArray<PartyMember> party, PartyMemberOperation operation)
    {
        var name = MemberName(operation.Target);
        var index = Find(party, name);
        if (operation.Clear)
        {
            return index < 0 ? throw new Rejection(Codes.UnknownPartyMember, $"no party member is called {Q(name)}") : party.RemoveAt(index);
        }
        if (operation.Text is not null && operation.Armor is not null)
        { throw new Rejection(Codes.PartyCodeWithArmor, "a member from a share code has its giver's own armor; give \"text\" or \"value\", not both"); }
        if (index < 0)
        {
            if (party.Length >= PartyLimit) { throw new Rejection(Codes.PartyFull, $"a party has at most {PartyLimit} other members"); }
            return party.Add(operation.Text is { } code ? FromShareCode(name, code, []) : new(name, "", Armor(operation.Armor ?? 0), []));
        }
        var member = party[index];
        // A new code replaces the member's build; no fields re-read the current code. Either way ticked effects stay ticked.
        if (operation.Text is { } replacement) { return party.SetItem(index, FromShareCode(member.Name, replacement, member.Effects)); }
        if (member.Code.Length > 0)
        {
            return operation.Armor is not null
                ? throw new Rejection(Codes.PartyMemberNotHandEntered, $"{Q(member.Name)} comes from a share code; its armor is the giver's own")
                : party.SetItem(index, FromShareCode(member.Name, member.Code, member.Effects));
        }
        // By hand: set the armor, then re-read every effect against the pack, dropping effects it no longer has.
        var armor = Armor(operation.Armor ?? member.Armor);
        var effects = member.Effects.Where(effect => allyEffects.ContainsKey(effect.Id)).Select(effect => effect with { Values = EffectValues(effect.Id, armor) });
        return party.SetItem(index, member with { Armor = armor, Effects = [.. effects] });
    }

    private ImmutableArray<PartyMember> PartyBuff(ImmutableArray<PartyMember> party, PartyBuffOperation operation)
    {
        var name = MemberName(operation.Target);
        var index = Find(party, name);
        if (index < 0) { throw new Rejection(Codes.UnknownPartyMember, $"no party member is called {Q(name)}"); }
        var member = party[index];
        var position = -1;
        for (var i = 0; i < member.Effects.Length && position < 0; i++) { if (member.Effects[i].Id == operation.Id) { position = i; } }
        // An effect the pack no longer has can still be turned off, so stale data can be cleaned up.
        if (!allyEffects.ContainsKey(operation.Id) && !(position >= 0 && !operation.Enabled))
        { throw new Rejection(Codes.UnknownAllyEffect, $"unknown ally effect {Q(operation.Id)}"); }
        ImmutableArray<PartyEffect> effects;
        if (member.Code.Length > 0)
        {
            // A share-code member offers only the effects its giver's build has; ticking toggles them.
            if (position < 0) { throw new Rejection(Codes.PartyEffectNotOffered, $"{Q(member.Name)}'s build doesn't give {Q(operation.Id)}"); }
            effects = member.Effects.SetItem(position, member.Effects[position] with { Active = operation.Enabled });
        }
        else if (!operation.Enabled) { effects = position < 0 ? member.Effects : member.Effects.RemoveAt(position); }
        else
        {
            var effect = new PartyEffect(operation.Id, EffectValues(operation.Id, member.Armor), true);
            effects = position < 0 ? member.Effects.Add(effect) : member.Effects.SetItem(position, effect);
        }
        return party.SetItem(index, member with { Effects = effects });
    }

    /// <summary>
    /// A member from a share code: the giver's build is resolved on its own, and the member offers the ally effects
    /// its talents give, valued from the giver's stats. Effects start off unless the member already had them on.
    /// The member keeps the giver's build as an app code, so a Go-era code is converted, never stored (ADR 0002).
    /// </summary>
    private PartyMember FromShareCode(string name, string text, ImmutableArray<PartyEffect> previous)
    {
        BuildSnapshot giver;
        string code;
        try
        {
            giver = readShareCode(text);
            if (giver.PackId != pack.ID) { throw new Rejection(Codes.PartyShareCodeWrongPack, $"{Q(name)}'s share code is for pack {Q(giver.PackId)}, not {Q(pack.ID)}"); }
            giver = giver with { Party = [] };
            code = ShareCode.Encode(giver);
        }
        catch (ShareCodeException error) { throw new Rejection(Codes.PartyShareCodeInvalid, $"{Q(name)}'s share code can't be read: {error.Message}"); }
        catch (ArgumentException error) { throw new Rejection(Codes.PartyShareCodeInvalid, $"{Q(name)}'s build can't be shared: {error.Message}"); }

        // Weapon talents count only for the weapon in hand, which a code doesn't say; since the member's effects
        // start off, it offers every equipped weapon's as well as the gear's, and the player ticks what applies.
        var contexts = new List<ImmutableDictionary<string, string>> { ImmutableDictionary<string, string>.Empty };
        contexts.AddRange(EquippedWeaponSlots.Read(pack, giver)
            .Select(slot => ImmutableDictionary<string, string>.Empty.Add(GamePack.WeaponSlotSelector, slot.ID)));
        var offered = new List<EvaluatedAllyEffect>();
        foreach (var context in contexts)
        {
            GiverEffects giverEffects;
            try { giverEffects = PartyEffectSelection.Offered(compiled, giver, context); }
            catch (ExpressionException error) { throw new Rejection(Codes.PartyEffectInvalid, $"{Q(name)}'s build can't be evaluated: {error.Message}"); }
            // Effects are valued from the giver's stats, so a stat that failed to resolve would give silent wrong values.
            if (giverEffects.Failures.FirstOrDefault() is { } failure) { throw new Rejection(Codes.PartyEffectInvalid, $"{Q(name)}'s build can't be evaluated: {failure.Message}"); }
            offered.AddRange(giverEffects.Effects);
        }

        var on = previous.Where(effect => effect.Active).Select(effect => effect.Id).ToHashSet(StringComparer.Ordinal);
        // Two talents can give the same effect; the member offers it once, with the first talent's values.
        var effects = offered.DistinctBy(effect => effect.Id, StringComparer.Ordinal)
            .Select(effect => new PartyEffect(effect.Id, Values(effect.Id, effect.Modifiers), on.Contains(effect.Id)));
        return new(name, code, 0, [.. effects]);
    }

    /// <summary>An ally effect's values for a hand-entered giver: its armor stands in for the giver's, other stats are 0.</summary>
    private ImmutableDictionary<string, double> EffectValues(string effectId, double armor)
    {
        var (talent, effect) = allyEffects[effectId];
        var scope = new EvaluationScope { Stats = zeroStats.SetItem(GiverArmorStat, armor), Curves = pack.Curves };
        try { return Values(effectId, PartyEffectSelection.Evaluate(compiled, talent, effect, scope)); }
        catch (ExpressionException error) { throw new Rejection(Codes.PartyEffectInvalid, $"{Q(effectId)} can't be evaluated for a hand-entered member: {error.Message}"); }
    }

    /// <summary>An effect's values by stat (a repeated stat keeps its last value), all finite.</summary>
    private static ImmutableDictionary<string, double> Values(string effectId, IEnumerable<AllyModifier> modifiers)
    {
        var values = ImmutableDictionary.CreateBuilder<string, double>(StringComparer.Ordinal);
        foreach (var modifier in modifiers)
        {
            if (!double.IsFinite(modifier.Value)) { throw new Rejection(Codes.PartyEffectInvalid, $"{Q(effectId)} gives a non-finite {Q(modifier.Stat)}"); }
            values[modifier.Stat] = modifier.Value;
        }
        return values.ToImmutable();
    }

    private static string MemberName(string target)
    {
        var name = target.Trim();
        return name.Length == 0 ? throw new Rejection(Codes.PartyMemberNameBlank, "a party member needs a name") : name;
    }

    private static double Armor(double armor) => double.IsFinite(armor) && armor >= 0 ? armor
        : throw new Rejection(Codes.PartyArmorInvalid, $"giver armor must be a finite, non-negative number; {Number(armor)} isn't");

    // Member names are matched case-insensitively, as the build document requires them to be unique.
    private static int Find(ImmutableArray<PartyMember> party, string name)
    {
        // The same rule documents use for unique names, so the editor and saved files agree on who is who.
        var key = PartyRules.Key(name);
        for (var index = 0; index < party.Length; index++) { if (PartyRules.Key(party[index].Name) == key) { return index; } }
        return -1;
    }

    // Lookups and rolls.

    private SlotDefinition Slot(string id) => slots.GetValueOrDefault(id) ?? throw new Rejection(Codes.UnknownSlot, $"unknown equipment slot {Q(id)}");

    private ItemDefinition Item(string id) => items.GetValueOrDefault(id) ?? throw new Rejection(Codes.UnknownItem, $"unknown item {Q(id)}");

    private (string Slot, ItemDefinition Item) Equipped(BuildSnapshot build, string slotId)
    {
        var slot = Slot(slotId);
        var id = build.Equipment.GetValueOrDefault(slot.ID, "");
        return id.Length == 0 ? throw new Rejection(Codes.SlotEmpty, $"nothing is equipped in slot {Q(slot.ID)}") : (slot.ID, Item(id));
    }

    private static ItemModSlotDefinition ModSlot(ItemDefinition item, string target) => item.ModSlots.FirstOrDefault(slot => slot.ID == target)
        ?? throw new Rejection(Codes.UnknownModSlot, $"{Q(item.ID)} has no mod slot {Q(target)}");

    private AttributeRollBounds Bounds(BuildSnapshot build, string slotId, ItemDefinition item, AttributeDefinition attribute, ItemAttributeChoice choice) =>
        PrototypeRules.RollBounds(attribute, choice, PrototypeRules.IsPrototype(pack, build, slotId, item));

    // A mod's roll uses its roll attribute's own range; Prototype quality doesn't change mods.
    private static AttributeRollBounds Bounds(AttributeDefinition attribute) => new(attribute.Min, attribute.Max, attribute.Step);

    private static string Describe(AttributeRollBounds bounds) =>
        $"{Number(bounds.Min)} to {Number(bounds.Max)}{(bounds.Step is { } step ? $" in steps of {Number(step)}" : "")}";

    private static string Q(string value) => $"\"{value}\"";
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed class Rejection(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
