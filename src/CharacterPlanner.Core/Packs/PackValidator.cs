using System.Collections.Immutable;
using CharacterPlanner.Core.Diagnostics;

namespace CharacterPlanner.Core.Packs;

/// <summary>Diagnostics in traversal order, and compiled expressions only when there are no errors.</summary>
public sealed record PackValidationResult(ImmutableArray<PackDiagnostic> Diagnostics, CompiledPackExpressions? Compiled, PackManifest? Manifest = null);

/// <summary>
/// Semantic validation of a domain pack (docs/PACK_SCHEMA_V2.md §8, stage 2). It applies to packs read
/// from v2 files and to packs converted from v1. Diagnostics are emitted in a fixed traversal order:
/// identity, rules (in rules.json order), expressions, simulation, presentation, share codes.
/// </summary>
public static partial class PackValidator
{
    /// <summary>Reads v2 files (see <see cref="PackReader"/>) and validates the result.</summary>
    public static PackValidationResult Load(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files) => Validate(PackReader.Read(files));

    /// <summary>Validates a read result, keeping the reader's diagnostics first.</summary>
    public static PackValidationResult Validate(PackReadResult read)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (read.Pack is null) { return new(read.Diagnostics, null, read.Manifest); }
        var validation = Validate(read.Pack);
        return new(read.Diagnostics.AddRange(validation.Diagnostics), validation.Compiled, read.Manifest);
    }

    public static PackValidationResult Validate(GamePack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var checker = new Checker(pack);
        checker.Identity();
        checker.Rules();
        var expressions = PackExpressionCompiler.Compile(pack);
        checker.Diagnostics.AddRange(expressions.Diagnostics);
        checker.Simulation();
        checker.Presentation();
        checker.ShareCodes();
        var diagnostics = checker.Diagnostics.ToImmutableArray();
        return new(diagnostics, diagnostics.Any(diagnostic => diagnostic.Severity == "error") ? null : expressions.Compiled);
    }

    private enum ModifierRole { Plain, Talent, Ranked, Ally }

    private sealed partial class Checker
    {
        private const long MaxSafeInteger = 9007199254740991;
        private readonly GamePack pack;
        private readonly HashSet<string> stats;
        private readonly HashSet<string> slots;
        private readonly HashSet<string> weaponSlots;
        private readonly HashSet<string> numericInputs;
        private readonly Dictionary<string, SelectorDefinition> selectors = new(StringComparer.Ordinal);
        // Talents that can be chosen on an item in a weapon slot: the only place whileHolstered means anything.
        private readonly HashSet<string> weaponTalents;

        public Checker(GamePack pack)
        {
            this.pack = pack;
            stats = pack.Stats.Select(stat => stat.ID).ToHashSet(StringComparer.Ordinal);
            slots = pack.Slots.Select(slot => slot.ID).ToHashSet(StringComparer.Ordinal);
            weaponSlots = pack.Slots.Where(slot => slot.Weapon).Select(slot => slot.ID).ToHashSet(StringComparer.Ordinal);
            numericInputs = pack.NumericContextInputs.Select(input => input.ID).ToHashSet(StringComparer.Ordinal);
            foreach (var selector in pack.Selectors) { selectors.TryAdd(selector.ID, selector); }
            weaponTalents = pack.Items.Where(item => weaponSlots.Contains(item.Slot))
                .SelectMany(item => item.TalentSlots).SelectMany(slot => slot.Choices).ToHashSet(StringComparer.Ordinal);
        }

        public List<PackDiagnostic> Diagnostics { get; } = [];

        private static string Q(string value) => ReferenceText.Quote(value);
        private void Error(string code, string path, string message) => Diagnostics.Add(new("error", code, path, message));
        private void Warning(string code, string path, string message) => Diagnostics.Add(new("warning", code, path, message));

        private void Id(string id, string path, string kind)
        { if (!PackIds.IsSlug(id)) { Error("invalid-id", path, $"{kind} id {Q(id)} must be lower_snake_case starting with a letter"); } }

        private void Name(string name, string path)
        { if (string.IsNullOrWhiteSpace(name)) { Error("empty-name", path + ".name", "Names must not be blank"); } }

        /// <summary>Reports each repeated id once, at its first repetition.</summary>
        private void Unique(IEnumerable<string> ids, string kind, string path)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var reported = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in ids)
            { if (!seen.Add(id) && reported.Add(id)) { Error("duplicate-id", path, $"Duplicate {kind} id {Q(id)}"); } }
        }

        /// <summary>Id pattern, unique ids and non-blank names for one collection.</summary>
        private void Collection<T>(ImmutableArray<T> values, Func<T, string> id, Func<T, string> name, string kind, string path)
        {
            Unique(values.Select(id), kind, path);
            foreach (var value in values)
            {
                Id(id(value), $"{path}.{id(value)}", kind);
                Name(name(value), $"{path}.{id(value)}");
            }
        }

        private void Reference(string id, ICollection<string> known, string code, string path, string kind)
        { if (!known.Contains(id)) { Error(code, path, $"Unknown {kind} {Q(id)}"); } }

        private void Stat(string id, string path) => Reference(id, stats, "unknown-stat", path, "stat");

        private void Integer(long value, string path, string what, long min = 0)
        { if (value < min || value > MaxSafeInteger) { Error("invalid-integer", path, $"{what} must be a whole number from {min} to 9007199254740991"); } }

        private void Finite(double value, string path)
        { if (!double.IsFinite(value)) { Error("invalid-number", path, "Numbers must be finite"); } }

        private void Format(string format, string path)
        { if (!StatFormats.IsKnown(format)) { Error("invalid-format", path, $"Format {Q(format)} is not integer, decimal or percent"); } }

        private void Cycle(Dictionary<string, ImmutableHashSet<string>> dependencies, string code, string path, string message)
        {
            var order = DependencyOrder.Create(dependencies);
            if (order.Blocked.Length > 0) { Error(code, path, message + string.Join(", ", order.Blocked)); }
        }

        public void Identity()
        {
            Id(pack.ID, "manifest.id", "Pack");
            if (string.IsNullOrWhiteSpace(pack.Name)) { Error("empty-name", "manifest.name", "Names must not be blank"); }
            if (string.IsNullOrWhiteSpace(pack.Version)) { Error("empty-version", "manifest.version", "The pack version must not be blank"); }
            var declared = new HashSet<string>(StringComparer.Ordinal);
            foreach (var capability in pack.Capabilities)
            {
                if (!declared.Add(capability)) { Error("duplicate-capability", "manifest.capabilities", $"Capability {Q(capability)} is declared more than once"); }
                else if (!PackCapabilities.Implemented.Contains(capability))
                { Error("unknown-capability", "manifest.capabilities", $"The pack needs capability {Q(capability)}, which this app does not implement"); }
            }
            foreach (var capability in PackCapabilities.Used(pack).Where(capability => !declared.Contains(capability)))
            { Warning("capability-undeclared", "manifest.capabilities", $"The pack uses {Q(capability)} without declaring it"); }
        }

        public void Rules()
        {
            Integer(pack.Level, "level", "The level", 1);
            Stats();
            Curves();
            Selectors();
            BuildOutputs();
            Attributes();
            Collection(pack.Slots, slot => slot.ID, slot => slot.Name, "slot", "slots");
            EquipmentLimits();
            Items();
            Mods();
            Talents();
            Sets();
            Prototype();
            AccountUpgrades();
            Skills();
            Specializations();
            RenamedIds();
        }

        private void EquipmentLimits()
        {
            Collection(pack.EquipmentLimits, limit => limit.ID, limit => limit.Name, "equipment limit", "equipmentLimits");
            var tags = pack.Items.SelectMany(item => item.Tags).ToHashSet(StringComparer.Ordinal);
            foreach (var limit in pack.EquipmentLimits)
            {
                var path = "equipmentLimits." + limit.ID;
                Reference(limit.Tag, tags, "unknown-tag", path + ".tag", "item tag");
                Integer(limit.Maximum, path + ".maximum", "The equipment limit");
                if (limit.Slots.IsEmpty) { Error("empty-slots", path + ".slots", "Equipment limits must name at least one slot"); }
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var slot in limit.Slots)
                {
                    Reference(slot, slots, "unknown-slot", path + ".slots", "slot");
                    if (!seen.Add(slot)) { Error("duplicate-slot", path + ".slots", $"Slot {Q(slot)} is repeated in this equipment limit"); }
                }
            }
        }

        private void Stats()
        {
            foreach (var stat in pack.Stats)
            {
                var path = "stats." + stat.ID;
                Id(stat.ID, path, "Stat");
                Name(stat.Name, path);
                Format(stat.Format, path + ".format");
                if (stat.Min is { } min) { Finite(min, path + ".min"); }
                if (stat.Max is { } max) { Finite(max, path + ".max"); }
                if (stat.Min > stat.Max) { Error("bad-range", path, $"min ({ReferenceText.Number(stat.Min)}) exceeds max ({ReferenceText.Number(stat.Max)})"); }
            }
        }

        private void Curves()
        {
            foreach (var (name, points) in pack.Curves.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                Id(name, "curves." + name, "Curve");
                if (points.IsDefault) { continue; }
                for (var i = 0; i < points.Length; i++)
                {
                    Finite(points[i].X, $"curves.{name}[{i}]");
                    Finite(points[i].Y, $"curves.{name}[{i}]");
                }
            }
        }

        private void Selectors()
        {
            Collection(pack.Selectors, selector => selector.ID, selector => selector.Name, "selector", "selectors");
            foreach (var selector in pack.Selectors)
            {
                var path = "selectors." + selector.ID;
                if (selector.ID == GamePack.WeaponSlotSelector)
                { Error("reserved-selector", path, "weapon_slot is built in: the active weapon is chosen by the slots marked weapon"); }
                if (numericInputs.Contains(selector.ID))
                { Error("context-id-conflict", path, $"{Q(selector.ID)} is both a selector and a numeric input"); }
                if (selector.Values.Length == 0) { Error("selector-values", path + ".values", "A selector needs at least one value"); }
                if (selector.Values.Any(string.IsNullOrWhiteSpace) || selector.Values.Distinct(StringComparer.Ordinal).Count() != selector.Values.Length)
                { Error("selector-values", path + ".values", "Selector values must be non-blank and distinct"); }
            }
        }

        private void BuildOutputs()
        {
            foreach (var output in pack.BuildOutputs)
            {
                var path = "buildOutputs." + output.ID;
                if (!PackIds.IsSlug(output.ID) || string.IsNullOrWhiteSpace(output.Name) || string.IsNullOrWhiteSpace(output.Formula))
                { Error("invalid-build-output", path, "A build output needs a lower_snake_case id, a name and a formula"); }
                Format(output.Format, path + ".format");
            }
        }

        private void Attributes()
        {
            Collection(pack.Attributes, attribute => attribute.ID, attribute => attribute.Name, "attribute", "attributes");
            foreach (var attribute in pack.Attributes)
            {
                var path = "attributes." + attribute.ID;
                Stat(attribute.Stat, path + ".stat");
                if (attribute.CountStat.Length > 0) { Stat(attribute.CountStat, path + ".countStat"); }
                Finite(attribute.Min, path + ".min");
                Finite(attribute.Max, path + ".max");
                if (attribute.Min > attribute.Max) { Error("bad-range", path, $"min ({ReferenceText.Number(attribute.Min)}) exceeds max ({ReferenceText.Number(attribute.Max)})"); }
                if (!(attribute.Step > 0) || !double.IsFinite(attribute.Step)) { Error("bad-step", path + ".step", "Attribute step must be positive"); }
                Format(attribute.Format, path + ".format");
                if (attribute.PrototypeMax is { } max)
                {
                    var min = attribute.PrototypeMin ?? attribute.Max;
                    if (!double.IsFinite(max) || !double.IsFinite(min)) { Error("invalid-number", path + ".prototype", "Numbers must be finite"); }
                    else if (min > max) { Error("bad-range", path + ".prototype", $"Prototype min ({ReferenceText.Number(min)}) exceeds Prototype max ({ReferenceText.Number(max)})"); }
                }
                else if (attribute.PrototypeMin is not null) { Error("bad-range", path + ".prototype", "A Prototype min needs a Prototype max"); }
            }
        }

        private void Items()
        {
            Collection(pack.Items, item => item.ID, item => item.Name, "item", "items");
            var attributes = pack.Attributes.GroupBy(attribute => attribute.ID, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var mods = pack.Mods.Select(mod => mod.ID).ToHashSet(StringComparer.Ordinal);
            var talents = pack.Talents.Select(talent => talent.ID).ToHashSet(StringComparer.Ordinal);
            foreach (var item in pack.Items)
            {
                var path = "items." + item.ID;
                Reference(item.Slot, slots, "unknown-slot", path + ".slot", "slot");
                if (pack.Slots.FirstOrDefault(slot => slot.ID == item.Slot) is { Accepts.Length: > 0 } slot && !slot.Accepts.Intersect(item.Tags).Any())
                { Error("tag-mismatch", path + ".tags", $"Slot {slot.ID} accepts [{string.Join(", ", slot.Accepts)}] but the item has [{string.Join(", ", item.Tags)}]"); }
                if (!item.Weapon.IsDefaultOrEmpty)
                {
                    if (!weaponSlots.Contains(item.Slot)) { Error("weapon-block-slot", path + ".weapon", "Only items in weapon slots have a weapon block"); }
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var stat in item.Weapon)
                    {
                        Stat(stat.Stat, $"{path}.weapon.{stat.Stat}");
                        if (!seen.Add(stat.Stat)) { Error("duplicate-weapon-stat", $"{path}.weapon.{stat.Stat}", $"Weapon stat {Q(stat.Stat)} is listed twice"); }
                        Finite(stat.Value, $"{path}.weapon.{stat.Stat}");
                    }
                }
                Modifiers(item.Modifiers, path, ModifierRole.Plain);
                if (item.Upgrade is { } upgrade)
                {
                    Name(upgrade.Name, path + ".upgrade");
                    Integer(upgrade.MaxRank, path + ".upgrade.maxRank", "Maximum rank");
                    Modifiers(upgrade.ModifiersPerRank, path + ".upgrade", ModifierRole.Ranked);
                }
                Unique(item.AttributeSlots.Select(value => value.ID), "attribute slot", path + ".attributeSlots");
                foreach (var attributeSlot in item.AttributeSlots)
                {
                    var owner = $"{path}.attributeSlots.{attributeSlot.ID}";
                    Id(attributeSlot.ID, owner, "Attribute slot");
                    Name(attributeSlot.Name, owner);
                    if (attributeSlot.Choices.Length == 0) { Error("no-attribute-choices", owner + ".choices", "An attribute slot must allow at least one attribute"); }
                    Unique(attributeSlot.Choices.Select(choice => choice.AttributeID), "attribute choice", owner + ".choices");
                    for (var i = 0; i < attributeSlot.Choices.Length; i++)
                    {
                        var choice = attributeSlot.Choices[i];
                        var location = $"{owner}.choices[{i}]";
                        if (choice.CountStat.Length > 0) { Stat(choice.CountStat, location + ".countStat"); }
                        if (!attributes.TryGetValue(choice.AttributeID, out var attribute)) { Error("unknown-attribute", location + ".attribute", $"Unknown attribute {Q(choice.AttributeID)}"); continue; }
                        var (min, max) = (choice.Min ?? attribute.Min, choice.Max ?? attribute.Max);
                        if (!double.IsFinite(min) || !double.IsFinite(max)) { Error("invalid-number", location, "Numbers must be finite"); }
                        else if (min > max) { Error("bad-range", location, $"min ({ReferenceText.Number(min)}) exceeds max ({ReferenceText.Number(max)})"); }
                        if (choice.Step is { } step && (!(step > 0) || !double.IsFinite(step))) { Error("bad-step", location + ".step", "Attribute step must be positive"); }
                    }
                }
                ChoiceSlots(item.ModSlots.Select(value => (value.ID, value.Name, value.Choices)), mods, "mod", path + ".modSlots");
                ChoiceSlots(item.TalentSlots.Select(value => (value.ID, value.Name, value.Choices)), talents, "talent", path + ".talentSlots");
            }
        }

        private void ChoiceSlots(IEnumerable<(string ID, string Name, ImmutableArray<string> Choices)> values, HashSet<string> known, string kind, string path)
        {
            var list = values.ToList();
            Unique(list.Select(value => value.ID), kind + " slot", path);
            foreach (var (id, name, choices) in list)
            {
                var owner = $"{path}.{id}";
                Id(id, owner, char.ToUpperInvariant(kind[0]) + kind[1..] + " slot");
                Name(name, owner);
                if (choices.Length == 0) { Error($"no-{kind}-choices", owner + ".choices", $"A {kind} slot must allow at least one {kind}"); }
                Unique(choices, kind + " choice", owner + ".choices");
                foreach (var choice in choices) { Reference(choice, known, $"unknown-{kind}", owner + ".choices", kind); }
            }
        }

        private void Mods()
        {
            Collection(pack.Mods, mod => mod.ID, mod => mod.Name, "mod", "mods");
            var attributes = pack.Attributes.Select(attribute => attribute.ID).ToHashSet(StringComparer.Ordinal);
            foreach (var mod in pack.Mods)
            {
                Modifiers(mod.Modifiers, "mods." + mod.ID, ModifierRole.Plain);
                if (mod.RollAttributeID.Length > 0) { Reference(mod.RollAttributeID, attributes, "unknown-attribute", $"mods.{mod.ID}.rollAttribute", "attribute"); }
            }
        }

        private void Talents()
        {
            Collection(pack.Talents, talent => talent.ID, talent => talent.Name, "talent", "talents");
            var sets = pack.Sets.Select(set => set.ID).ToHashSet(StringComparer.Ordinal);
            var talents = pack.Talents.Select(talent => talent.ID).ToHashSet(StringComparer.Ordinal);
            Unique(pack.Talents.SelectMany(talent => talent.AllyEffects).Select(effect => effect.ID), "ally effect", "talents");
            foreach (var talent in pack.Talents)
            {
                var path = "talents." + talent.ID;
                Modifiers(talent.Modifiers, path, ModifierRole.Talent, talent.ID);
                Integer(talent.SetPieceCredit, path + ".setPieceCredit", "Set piece credit");
                foreach (var id in talent.ModeledBySets) { Reference(id, sets, "unknown-set", path + ".modeledBySets", "set"); }
                if (talent.CopiesTalent is { } copy)
                {
                    Reference(copy.From, slots, "unknown-slot", path + ".copiesTalent.from", "slot");
                    Reference(copy.To, slots, "unknown-slot", path + ".copiesTalent.to", "slot");
                    foreach (var id in copy.ExcludeTalents) { Reference(id, talents, "unknown-talent", path + ".copiesTalent.excludeTalents", "talent"); }
                }
                if (talent.Coverage is { } coverage) { Coverage(coverage, path + ".coverage"); }
                foreach (var effect in talent.AllyEffects)
                {
                    var owner = $"{path}.allyEffects.{effect.ID}";
                    Id(effect.ID, owner, "Ally effect");
                    Name(effect.Name, owner);
                    if (!PackIds.IsSlug(effect.Stack)) { Error("invalid-id", owner + ".stack", $"Ally effect stack {Q(effect.Stack)} must be lower_snake_case starting with a letter"); }
                    if (effect.Modifiers.Length == 0) { Error("bad-ally-effect", owner + ".modifiers", "An ally effect needs at least one modifier"); }
                    Modifiers(effect.Modifiers, owner, ModifierRole.Ally);
                }
            }
        }

        // R35's shape: omits only where something is left out, and settleBy only for an unknown.
        private void Coverage(TalentCoverage coverage, string path)
        {
            if (!TalentCoverage.Statuses.Contains(coverage.Status))
            { Error("bad-coverage", path + ".status", $"Coverage status {Q(coverage.Status)} must be one of {string.Join(", ", TalentCoverage.Statuses)}"); }
            if (string.IsNullOrWhiteSpace(coverage.Evidence)) { Error("bad-coverage", path + ".evidence", "Coverage needs its evidence"); }
            var omits = coverage.Status is "partial" or "unmodeled" or "unknown";
            if (omits && coverage.Omits.IsDefaultOrEmpty) { Error("bad-coverage", path + ".omits", $"A {coverage.Status} coverage entry must name what it leaves out"); }
            if (!omits && !coverage.Omits.IsDefaultOrEmpty) { Error("bad-coverage", path + ".omits", $"A {coverage.Status} coverage entry leaves nothing out"); }
            if (!coverage.Omits.IsDefaultOrEmpty && coverage.Omits.Any(string.IsNullOrWhiteSpace)) { Error("bad-coverage", path + ".omits", "Omissions must be non-empty phrases"); }
            if (coverage.Status == "unknown" && string.IsNullOrWhiteSpace(coverage.SettleBy))
            { Error("bad-coverage", path + ".settleBy", "An unknown coverage entry must name the reading that would settle it"); }
            if (coverage.Status != "unknown" && coverage.SettleBy.Length > 0)
            { Error("bad-coverage", path + ".settleBy", "Only an unknown coverage entry has a settleBy"); }
        }

        private void Sets()
        {
            Collection(pack.Sets, set => set.ID, set => set.Name, "set", "sets");
            var items = pack.Items.Select(item => item.ID).ToHashSet(StringComparer.Ordinal);
            foreach (var set in pack.Sets)
            {
                foreach (var id in set.Items) { Reference(id, items, "unknown-item", $"sets.{set.ID}.items", "item"); }
                Unique(set.Items, "set item", $"sets.{set.ID}.items");
                for (var i = 0; i < set.Bonuses.Length; i++)
                {
                    var bonus = set.Bonuses[i];
                    var path = $"sets.{set.ID}.bonuses[{i}]";
                    Integer(bonus.Pieces, path + ".pieces", "A bonus piece count", 1);
                    if (bonus.Pieces > set.Items.Length)
                    { Warning("unreachable-bonus", path, $"The bonus needs {ReferenceText.Number(bonus.Pieces)} pieces but the set has {ReferenceText.Number((long)set.Items.Length)}"); }
                    Modifiers(bonus.Modifiers, path, ModifierRole.Plain);
                }
            }
        }

        private void Prototype()
        {
            if (pack.Prototype is not { } prototype) { return; }
            for (var i = 0; i < prototype.Slots.Length; i++) { Reference(prototype.Slots[i], slots, "unknown-slot", $"prototype.slots[{i}]", "slot"); }
            if (prototype.CountStat.Length > 0) { Stat(prototype.CountStat, "prototype.countStat"); }
            if (prototype.NormalizeInput.Length > 0) { Reference(prototype.NormalizeInput, numericInputs, "unknown-numeric-input", "prototype.normalizeInput", "numeric input"); }
            Collection(prototype.Augments, augment => augment.ID, augment => augment.Name, "augment", "prototype.augments");
            foreach (var augment in prototype.Augments)
            {
                var path = "prototype.augments." + augment.ID;
                Stat(augment.Stat, path + ".stat");
                Finite(augment.Base, path + ".base");
                Finite(augment.PerLevel, path + ".perLevel");
                Integer(augment.Levels, path + ".levels", "Augment levels", 1);
            }
        }

        private void AccountUpgrades()
        {
            Collection(pack.AccountUpgrades, upgrade => upgrade.ID, upgrade => upgrade.Name, "account upgrade", "accountUpgrades");
            foreach (var upgrade in pack.AccountUpgrades)
            {
                var path = "accountUpgrades." + upgrade.ID;
                Integer(upgrade.MaxRank, path + ".maxRank", "Maximum rank");
                if (upgrade.EffectCap is { } cap && (cap < 0 || cap > upgrade.MaxRank)) { Error("bad-upgrade-effect-cap", path + ".effectCap", "The effect cap must be between zero and maxRank"); }
                Modifiers(upgrade.ModifiersPerRank, path, ModifierRole.Ranked);
            }
        }

        private void Skills()
        {
            Collection(pack.ActiveSkillSlots, slot => slot.ID, slot => slot.Name, "skill slot", "skillSlots");
            var skills = pack.ActiveSkills.Select(skill => skill.ID).ToHashSet(StringComparer.Ordinal);
            foreach (var slot in pack.ActiveSkillSlots)
            {
                var owner = "skillSlots." + slot.ID;
                if (slot.Choices.Length == 0) { Error("no-skill-choices", owner + ".choices", "A skill slot must allow at least one skill"); }
                Unique(slot.Choices, "skill choice", owner + ".choices");
                foreach (var id in slot.Choices) { Reference(id, skills, "unknown-skill", owner + ".choices", "skill"); }
            }
            Collection(pack.ActiveSkills, skill => skill.ID, skill => skill.Name, "skill", "skills");
            foreach (var skill in pack.ActiveSkills)
            {
                var path = "skills." + skill.ID;
                if (skill.Family.Length > 0 && !PackIds.IsSlug(skill.Family)) { Error("invalid-id", path + ".family", $"Skill family {Q(skill.Family)} must be lower_snake_case starting with a letter"); }
                Modifiers(skill.Modifiers, path, ModifierRole.Plain);
                if (skill.Upgrade is { } upgrade)
                {
                    Name(upgrade.Name, path + ".upgrade");
                    Integer(upgrade.MaxRank, path + ".upgrade.maxRank", "Maximum rank");
                }
                SkillMods(skill, path);
                var headlines = 0;
                foreach (var output in skill.PreviewOutputs)
                {
                    var owner = $"{path}.previewOutputs.{output.ID}";
                    Id(output.ID, owner, "Preview output");
                    Name(output.Name, owner);
                    if (string.IsNullOrWhiteSpace(output.Formula)) { Error("empty-formula", owner + ".formula", "A preview output needs a formula"); }
                    Format(output.Format, owner + ".format");
                    if (!PreviewOutputKinds.IsKnown(output.Kind))
                    { Error("invalid-preview-kind", owner + ".kind", $"Preview output kind {Q(output.Kind)} is not damage, rate, shield or regeneration"); }
                    if (!output.Headline) { continue; }
                    if (output.Kind != "rate") { Error("invalid-preview-headline", owner + ".headline", "Only a rate output can be the headline"); }
                    if (++headlines == 2) { Error("invalid-preview-headline", path + ".previewOutputs", "A skill has at most one headline rate"); }
                }
            }
        }

        private void SkillMods(ActiveSkillDefinition skill, string path)
        {
            Unique(skill.ModSlots.Select(slot => slot.ID), "skill mod slot", path + ".modSlots");
            var formats = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var slot in skill.ModSlots)
            {
                var owner = $"{path}.modSlots.{slot.ID}";
                Id(slot.ID, owner, "Skill mod slot");
                Name(slot.Name, owner);
                if (slot.Choices.Length == 0) { Error("no-skill-mod-choices", owner + ".choices", "A skill mod slot must allow at least one mod"); }
                Unique(slot.Choices.Select(choice => choice.ID), "skill mod", owner + ".choices");
                foreach (var choice in slot.Choices)
                {
                    var location = $"{owner}.choices.{choice.ID}";
                    Id(choice.ID, location, "Skill mod");
                    Name(choice.Name, location);
                    if (!choice.Input.StartsWith("skill_mod_", StringComparison.Ordinal) || !PackIds.IsSlug(choice.Input))
                    { Error("invalid-skill-mod-input", location + ".input", $"Skill mod input {Q(choice.Input)} must be a lower_snake_case name starting with skill_mod_"); }
                    Finite(choice.Min, location + ".min");
                    Finite(choice.Max, location + ".max");
                    if (choice.Min > choice.Max) { Error("bad-range", location, $"min ({ReferenceText.Number(choice.Min)}) exceeds max ({ReferenceText.Number(choice.Max)})"); }
                    if (choice.Step is { } step && (!(step > 0) || !double.IsFinite(step))) { Error("bad-step", location + ".step", "Skill mod step must be positive"); }
                    Format(choice.Format, location + ".format");
                    if (formats.TryGetValue(choice.Input, out var previous) && previous != choice.Format)
                    { Error("skill-mod-input-format", location + ".format", "Mods sharing an input must use the same format"); }
                    formats.TryAdd(choice.Input, choice.Format);
                }
            }
        }

        private void Specializations()
        {
            Collection(pack.Specializations, specialization => specialization.ID, specialization => specialization.Name, "specialization", "specializations");
            foreach (var specialization in pack.Specializations)
            {
                var owner = "specializations." + specialization.ID;
                Modifiers(specialization.Modifiers, owner, ModifierRole.Plain);
                Collection(specialization.Budgets, budget => budget.ID, budget => budget.Name, "budget", owner + ".budgets");
                var budgetIds = specialization.Budgets.Select(budget => budget.ID).ToHashSet(StringComparer.Ordinal);
                var budgets = new Dictionary<string, ImmutableHashSet<string>>(StringComparer.Ordinal);
                foreach (var budget in specialization.Budgets)
                {
                    var path = $"{owner}.budgets.{budget.ID}";
                    Integer(budget.Points, path + ".points", "Budget points");
                    if (budget.ParentID.Length > 0) { Reference(budget.ParentID, budgetIds, "unknown-budget", path + ".parentId", "budget"); }
                    budgets[budget.ID] = budget.ParentID.Length == 0 ? [] : [budget.ParentID];
                }
                Cycle(budgets, "budget-cycle", owner + ".budgets", "Budgets form a parent cycle: ");
                Collection(specialization.Nodes, node => node.ID, node => node.Name, "node", owner + ".nodes");
                var nodeIds = specialization.Nodes.Select(node => node.ID).ToHashSet(StringComparer.Ordinal);
                var nodes = new Dictionary<string, ImmutableHashSet<string>>(StringComparer.Ordinal);
                foreach (var node in specialization.Nodes)
                {
                    var path = $"{owner}.nodes.{node.ID}";
                    Integer(node.Cost, path + ".cost", "Node cost");
                    Integer(node.Tier, path + ".tier", "Node tier");
                    Reference(node.BudgetID, budgetIds, "unknown-budget", path + ".budgetId", "budget");
                    foreach (var id in node.Prerequisites) { Reference(id, nodeIds, "unknown-node", path + ".prerequisites", "node"); }
                    nodes[node.ID] = node.Prerequisites.ToImmutableHashSet(StringComparer.Ordinal);
                    Modifiers(node.Modifiers, path, ModifierRole.Plain);
                }
                Cycle(nodes, "node-cycle", owner + ".nodes", "Nodes form a prerequisite cycle: ");
            }
        }

        private void RenamedIds()
        {
            foreach (var (collection, renames) in pack.RenamedIds.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var path = "renamedIds." + collection;
                if (RenameTargets(collection) is not { } current) { Error("unknown-rename-collection", path, $"Ids in {Q(collection)} cannot be renamed"); continue; }
                foreach (var (old, target) in renames.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    Id(old, $"{path}.{old}", "Renamed");
                    if (current.Contains(old)) { Error("renamed-id-exists", $"{path}.{old}", $"{Q(old)} is renamed but still exists"); }
                    if (!current.Contains(target)) { Error("unknown-rename-target", $"{path}.{old}", $"{Q(old)} is renamed to {Q(target)}, which does not exist"); }
                }
            }
        }

        private HashSet<string>? RenameTargets(string collection) => collection switch
        {
            "stat" => stats,
            "selector" => selectors.Keys.ToHashSet(StringComparer.Ordinal),
            "numericInput" => numericInputs,
            "attribute" => pack.Attributes.Select(value => value.ID).ToHashSet(StringComparer.Ordinal),
            "slot" => slots,
            "item" => pack.Items.Select(value => value.ID).ToHashSet(StringComparer.Ordinal),
            "mod" => pack.Mods.Select(value => value.ID).ToHashSet(StringComparer.Ordinal),
            "talent" => pack.Talents.Select(value => value.ID).ToHashSet(StringComparer.Ordinal),
            "set" => pack.Sets.Select(value => value.ID).ToHashSet(StringComparer.Ordinal),
            "augment" => (pack.Prototype?.Augments ?? []).Select(value => value.ID).ToHashSet(StringComparer.Ordinal),
            "accountUpgrade" => pack.AccountUpgrades.Select(value => value.ID).ToHashSet(StringComparer.Ordinal),
            "skillSlot" => pack.ActiveSkillSlots.Select(value => value.ID).ToHashSet(StringComparer.Ordinal),
            "skill" => pack.ActiveSkills.Select(value => value.ID).ToHashSet(StringComparer.Ordinal),
            "specialization" => pack.Specializations.Select(value => value.ID).ToHashSet(StringComparer.Ordinal),
            "node" => pack.Specializations.SelectMany(value => value.Nodes).Select(value => value.ID).ToHashSet(StringComparer.Ordinal),
            _ => null
        };

        // Expressions, stat ids and curves are checked by PackExpressionCompiler.
        private void Modifiers(ImmutableArray<StatModifier> modifiers, string owner, ModifierRole role, string talent = "")
        {
            var list = role == ModifierRole.Ranked ? "modifiersPerRank" : "modifiers";
            for (var i = 0; i < modifiers.Length; i++)
            {
                var modifier = modifiers[i];
                var path = $"{owner}.{list}[{i}]";
                if (!ModifierOps.IsKnown(modifier.Op)) { Error("invalid-modifier-op", path + ".op", $"Modifier op {Q(modifier.Op)} is not flat, mult or override"); }
                if (!modifier.Value.IsExpression) { Finite(modifier.Value.Number, path + ".value"); }
                if (role == ModifierRole.Ranked)
                {
                    if (modifier.Value.IsExpression) { Error("invalid-upgrade-modifier", path + ".value", "Upgrade modifiers apply per rank and need a numeric value"); }
                    if (modifier.When.Length > 0) { Error("invalid-upgrade-modifier", path + ".when", "Upgrade modifiers apply per rank and may not have a when condition"); }
                    if (modifier.Op == ModifierOps.Override) { Error("invalid-upgrade-modifier", path + ".op", "Upgrade modifiers apply per rank and may not override"); }
                }
                if (role == ModifierRole.Ally && (modifier.Op != ModifierOps.Flat || modifier.When.Length > 0 || modifier.RequiresContext is not null || modifier.WhileHolstered))
                { Error("bad-ally-effect", path, "Ally effect modifiers are unconditional flat values"); }
                if (modifier.WhileHolstered && (role != ModifierRole.Talent || !weaponTalents.Contains(talent)))
                { Error("invalid-while-holstered", path + ".whileHolstered", "whileHolstered applies only to talents that can be chosen on items in weapon slots"); }
                Requires(modifier.RequiresContext, path + ".requires");
            }
        }

        private void Requires(ImmutableDictionary<string, string>? requires, string path)
        {
            if (requires is null) { return; }
            if (requires.Count == 0) { Error("empty-requires", path, "requires must list at least one selector"); }
            foreach (var (key, value) in requires.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (key == GamePack.WeaponSlotSelector)
                { Error("weapon-slot-gate", $"{path}.{key}", "The active weapon is built in; use the item's weapon block instead of a weapon_slot gate"); }
                else if (!selectors.TryGetValue(key, out var selector)) { Error("unknown-selector", $"{path}.{key}", $"Unknown selector {Q(key)}"); }
                else if (!selector.Values.Contains(value)) { Error("unknown-selector-value", $"{path}.{key}", $"Selector {Q(key)} has no value {Q(value)}"); }
            }
        }
    }
}
