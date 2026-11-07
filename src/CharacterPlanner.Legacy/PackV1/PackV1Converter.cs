using System.Collections.Immutable;
using V2 = CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Legacy.PackV1;

public sealed record PackConversionProblem(string Path, string Message)
{
    public override string ToString() => $"{Path}: {Message}";
}

/// <summary>Either a complete v2 pack with no problems, or the problems and no pack: never a partial pack.</summary>
public sealed record PackConversion(V2.GamePack? Pack, ImmutableArray<PackConversionProblem> Problems);

/// <summary>
/// One-way pack v1 → v2 conversion, following the mapping table in docs/PACK_SCHEMA_V2.md. It only
/// needs to handle the real v1 packs: anything v2 can't express with unchanged results is reported
/// as a problem rather than approximated. Semantic v2 validation (references, expressions) is separate.
/// </summary>
public static class PackV1Converter
{
    public static PackConversion Convert(GamePack source) => new Converter(source).Run();

    private sealed class Converter(GamePack source)
    {
        private const string WeaponTag = "weapon";
        private const string WeaponSlotKey = V2.GamePack.WeaponSlotSelector;
        private readonly List<PackConversionProblem> problems = [];
        private readonly SortedDictionary<string, SortedSet<string>> selectors = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, string>> icons = new(StringComparer.Ordinal);
        private readonly List<(string Path, string Asset)> assetReferences = [];

        private void Problem(string path, string message) => problems.Add(new(path, message));

        public PackConversion Run()
        {
            if (source.SchemaVersion != 1) { Problem("schemaVersion", $"expected a v1 pack, found schemaVersion {source.SchemaVersion}"); }
            var progression = source.Progression ?? new();
            if (progression.StartingLevel != progression.MaxLevel)
            { Problem("progression", $"v2 has one fixed level; startingLevel {progression.StartingLevel} differs from maxLevel {progression.MaxLevel}"); }
            if (progression.PointsPerLevel != 0 || progression.StartingPoints != 0 || progression.SkillPointsPerLevel != 0)
            { Problem("progression", "point allocation is removed in v2; points must be zero"); }
            if (!source.Scenarios.IsEmpty) { Problem("scenarios", "scenarios are removed in v2"); }
            if (!source.Skills.IsEmpty) { Problem("skills", "passive skill nodes are removed in v2"); }
            // Presentation maps are keyed by item and specialization id, so duplicates can't be converted.
            var duplicates = false;
            foreach (var (path, ids) in new[] { ("items", source.Items.Select(item => item.ID)), ("specializations", source.Specializations.Select(specialization => specialization.ID)) })
            {
                foreach (var id in ids.GroupBy(id => id, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key))
                { Problem(path, $"duplicate id {Quoted(id)}"); duplicates = true; }
            }
            if (duplicates) { return new(null, problems.ToImmutableArray()); }

            var slots = source.Slots.Select(Slot).ToImmutableArray();
            var weaponSlots = slots.Where(slot => slot.Weapon).Select(slot => slot.ID).ToImmutableHashSet(StringComparer.Ordinal);
            var pack = new V2.GamePack
            {
                ID = source.ID,
                Name = source.Name,
                Version = source.Version,
                GameVersion = source.GameVersion,
                Description = source.Description,
                Capabilities = Capabilities(weaponSlots),
                Level = progression.MaxLevel,
                Stats = source.Stats.Select(Stat).ToImmutableArray(),
                Curves = source.Curves,
                NumericContextInputs = source.NumericContextInputs.Select(NumericInput).ToImmutableArray(),
                BuildOutputs = source.BuildOutputs.Select(output => new V2.BuildOutputDefinition
                { ID = output.ID, Name = output.Name, Description = output.Description, Format = output.Format, Formula = output.Formula }).ToImmutableArray(),
                Attributes = source.Attributes.Select(Attribute).ToImmutableArray(),
                Slots = slots,
                Items = source.Items.Select(item => Item(item, weaponSlots)).ToImmutableArray(),
                Mods = source.Mods.Select(mod => new V2.ModDefinition
                {
                    ID = mod.ID,
                    Name = mod.Name,
                    Description = mod.Description,
                    RollAttributeID = mod.RollAttributeID,
                    Modifiers = Modifiers(mod.Modifiers, $"mods.{mod.ID}.modifiers")
                }).ToImmutableArray(),
                Talents = source.Talents.Select(Talent).ToImmutableArray(),
                Sets = source.Sets.Select(set => new V2.SetDefinition
                {
                    ID = set.ID,
                    Name = set.Name,
                    Description = set.Description,
                    Items = set.Items,
                    Bonuses = set.Bonuses.Select((bonus, index) => new V2.SetBonus
                    { Pieces = bonus.Pieces, Modifiers = Modifiers(bonus.Modifiers, $"sets.{set.ID}.bonuses[{index}].modifiers") }).ToImmutableArray()
                }).ToImmutableArray(),
                Prototype = source.Prototype is { } prototype ? new V2.PrototypeDefinition
                {
                    Slots = prototype.Slots,
                    ExcludeTags = prototype.ExcludeTags,
                    CountStat = prototype.CountStat,
                    NormalizeInput = prototype.NormalizeInput,
                    Augments = prototype.Augments.Select(augment => new V2.AugmentDefinition
                    {
                        ID = augment.ID,
                        Name = augment.Name,
                        Description = augment.Description,
                        Stat = augment.Stat,
                        Base = augment.Base,
                        PerLevel = augment.PerLevel,
                        Levels = augment.Levels
                    }).ToImmutableArray()
                } : null,
                AccountUpgrades = source.AccountUpgrades.Select(upgrade => new V2.AccountUpgradeDefinition
                {
                    ID = upgrade.ID,
                    Name = upgrade.Name,
                    Description = upgrade.Description,
                    MaxRank = upgrade.MaxRank,
                    EffectCap = upgrade.EffectCap,
                    ModifiersPerRank = Modifiers(upgrade.ModifiersPerRank, $"accountUpgrades.{upgrade.ID}.modifiersPerRank")
                }).ToImmutableArray(),
                ActiveSkillSlots = source.ActiveSkillSlots.Select(slot => new V2.ActiveSkillSlotDefinition { ID = slot.ID, Name = slot.Name, Choices = slot.Choices }).ToImmutableArray(),
                ActiveSkills = source.ActiveSkills.Select(ActiveSkill).ToImmutableArray(),
                Specializations = source.Specializations.Select(Specialization).ToImmutableArray(),
                Simulation = Simulation(),
                LegacyShareOrder = new()
                {
                    Slots = source.Slots.Select(slot => slot.ID).ToImmutableArray(),
                    Stats = source.Stats.Where(stat => stat.Kind == "primary").Select(stat => stat.ID).ToImmutableArray(),
                    Scenarios = source.Scenarios.Select(scenario => scenario.ID).ToImmutableArray(),
                    PassiveSkills = source.Skills.Select(skill => skill.ID).ToImmutableArray()
                }
            };
            // Selectors and presentation collect what the conversions above observed.
            pack = pack with { Selectors = Selectors(), Presentation = Presentation() };
            return problems.Count > 0 ? new(null, problems.ToImmutableArray()) : new(pack, []);
        }

        private ImmutableArray<string> Capabilities(ImmutableHashSet<string> weaponSlots)
        {
            var capabilities = ImmutableArray.CreateBuilder<string>();
            void Add(string name, bool used) { if (used) { capabilities.Add(name); } }
            Add("weapons", !weaponSlots.IsEmpty);
            Add("prototype", source.Prototype is not null);
            Add("skill-mods", source.ActiveSkills.Any(skill => !skill.ModSlots.IsEmpty));
            Add("party", source.Talents.Any(talent => !talent.AllyEffects.IsEmpty));
            Add("talent-copy", source.Talents.Any(talent => talent.CopiesTalent is not null));
            Add("simulation", !source.SimTriggers.IsEmpty || !source.SimSkills.IsEmpty);
            return capabilities.ToImmutable();
        }

        private V2.StatDefinition Stat(StatDefinition stat)
        {
            var path = $"stats.{stat.ID}";
            if (stat.Kind != "derived") { Problem(path + ".kind", $"only derived stats exist in v2, found kind {Quoted(stat.Kind)}"); }
            if (stat.Base is not null) { Problem(path + ".base", "base applies only to the removed primary stats"); }
            if (!V2.StatFormats.IsKnown(stat.Format)) { Problem(path + ".format", $"unknown format {Quoted(stat.Format)}"); }
            return new() { ID = stat.ID, Name = stat.Name, ShortName = stat.ShortName, Description = stat.Description, Format = stat.Format, Min = stat.Min, Max = stat.Max, Formula = stat.Formula };
        }

        private static V2.NumericContextInputDefinition NumericInput(NumericContextInputDefinition input) => new()
        {
            ID = input.ID,
            Name = input.Name,
            Description = input.Description,
            Min = input.Min,
            Max = input.Max,
            Step = input.Step,
            Default = input.Default,
            Global = input.Global,
            // A missing v1 options list stays missing; v2 represents that as empty.
            Options = input.Options.IsDefault ? [] : input.Options.Select(option => new V2.NumericContextOption(option.Value, option.Label)).ToImmutableArray(),
            Group = input.Group,
            GroupName = input.GroupName,
            Section = input.Section
        };

        private V2.AttributeDefinition Attribute(AttributeDefinition attribute)
        {
            var path = $"attributes.{attribute.ID}";
            if (attribute.Op != V2.ModifierOps.Flat) { Problem(path + ".op", $"v2 attributes are always flat, found {Quoted(attribute.Op)}"); }
            if (attribute.Bucket.Length > 0) { Problem(path + ".bucket", "stat buckets are removed in v2"); }
            if (attribute.Step is null) { Problem(path + ".step", "v2 attributes require a step"); }
            if (!V2.StatFormats.IsKnown(attribute.Format)) { Problem(path + ".format", $"unknown format {Quoted(attribute.Format)}"); }
            return new()
            {
                ID = attribute.ID,
                Name = attribute.Name,
                Stat = attribute.Stat,
                Min = attribute.Min,
                Max = attribute.Max,
                Step = attribute.Step ?? 0,
                Format = attribute.Format,
                Category = attribute.Category,
                Core = attribute.Core,
                PrototypeMin = attribute.PrototypeMin,
                PrototypeMax = attribute.PrototypeMax,
                CountStat = attribute.CountStat
            };
        }

        // A weapon slot accepts weapons, and its items gate their effects on being the active weapon.
        private V2.SlotDefinition Slot(SlotDefinition slot)
        {
            Icon("slots", slot.ID, slot.Icon, $"slots.{slot.ID}.icon");
            var weapon = slot.Accepts.Contains(WeaponTag) && source.Items.Any(item => item.Occupies is [var occupied] && occupied == slot.ID && IsOwnWeaponGate(item.EffectsRequireContext, slot.ID));
            return new() { ID = slot.ID, Name = slot.Name, Accepts = slot.Accepts, Weapon = weapon };
        }

        private static bool IsOwnWeaponGate(ImmutableDictionary<string, string>? gate, string slot) =>
            gate is { Count: 1 } && gate.TryGetValue(WeaponSlotKey, out var value) && value == slot;

        private V2.ItemDefinition Item(ItemDefinition item, ImmutableHashSet<string> weaponSlots)
        {
            var path = $"items.{item.ID}";
            if (item.Occupies is not [var slot])
            {
                Problem(path + ".occupies", $"v2 items occupy exactly one slot, found {item.Occupies.Length}");
                slot = item.Occupies.FirstOrDefault("");
            }
            if (!item.Requirements.IsEmpty) { Problem(path + ".requirements", "item requirements are removed in v2"); }
            if (item.Tier is not null) { Problem(path + ".tier", "item tiers are removed in v2"); }
            var weapon = weaponSlots.Contains(slot);
            if (item.EffectsRequireContext is { } gate && !(weapon && IsOwnWeaponGate(gate, slot)))
            { Problem(path + ".effectsRequireContext", $"the only item gate v2 can express is {{{WeaponSlotKey}: {Quoted(slot)}}} in a weapon slot"); }
            if (weapon && item.EffectsRequireContext is null)
            { Problem(path + ".effectsRequireContext", $"items in weapon slot {Quoted(slot)} must be gated on being the active weapon"); }
            if (item.Artwork is { } artwork)
            {
                Asset(artwork.Image, path + ".artwork.image");
                Asset(artwork.Icon, path + ".artwork.icon");
            }

            // The weapon block is the leading run of base-stat overrides, so the engine's
            // WeaponBase batch reproduces v1's contribution order exactly.
            var ownSlot = weapon ? slot : null;
            var modifiers = item.Modifiers;
            var baseStats = ImmutableArray.CreateBuilder<V2.WeaponBaseStat>();
            var lead = 0;
            if (weapon)
            {
                for (; lead < modifiers.Length && IsWeaponBase(modifiers[lead], slot); lead++)
                {
                    var stat = modifiers[lead].Stat;
                    if (baseStats.Any(value => value.Stat == stat)) { Problem($"{path}.modifiers[{lead}]", $"weapon base stat {Quoted(stat)} is overridden twice"); }
                    baseStats.Add(new(stat, modifiers[lead].Value.Number));
                }
                for (var index = lead; index < modifiers.Length; index++)
                {
                    if (IsWeaponBase(modifiers[index], slot))
                    { Problem($"{path}.modifiers[{index}]", "weapon base-stat overrides must precede the item's other modifiers"); }
                }
            }
            return new()
            {
                ID = item.ID,
                Name = item.Name,
                Description = item.Description,
                Slot = slot,
                Tags = item.Tags,
                Weapon = baseStats.ToImmutable(),
                Modifiers = Modifiers(modifiers, path + ".modifiers", ownSlot, lead),
                AttributeSlots = item.AttributeSlots.Select(attributeSlot => new V2.ItemAttributeSlotDefinition
                {
                    ID = attributeSlot.ID,
                    Name = attributeSlot.Name,
                    Choices = attributeSlot.Choices.Select(choice => new V2.ItemAttributeChoice { AttributeID = choice.AttributeID, Min = choice.Min, Max = choice.Max, Step = choice.Step }).ToImmutableArray()
                }).ToImmutableArray(),
                ModSlots = item.ModSlots.Select(modSlot => new V2.ItemModSlotDefinition { ID = modSlot.ID, Name = modSlot.Name, Choices = modSlot.Choices }).ToImmutableArray(),
                TalentSlots = item.TalentSlots.Select(talentSlot => new V2.ItemTalentSlotDefinition { ID = talentSlot.ID, Name = talentSlot.Name, Choices = talentSlot.Choices }).ToImmutableArray(),
                Upgrade = item.Upgrade is { } upgrade ? new V2.ItemUpgradeDefinition
                {
                    Name = upgrade.Name,
                    Description = upgrade.Description,
                    MaxRank = upgrade.MaxRank,
                    ModifiersPerRank = Modifiers(upgrade.ModifiersPerRank, path + ".upgrade.modifiersPerRank", ownSlot)
                } : null
            };
        }

        private static bool IsWeaponBase(StatModifier modifier, string slot) =>
            modifier.Op == V2.ModifierOps.Override && !modifier.Value.IsExpression && modifier.When.Length == 0 && !modifier.WhileHolstered &&
            modifier.Bucket.Length == 0 && IsOwnWeaponGate(modifier.RequiresContext, slot);

        private V2.TalentDefinition Talent(TalentDefinition talent)
        {
            var path = $"talents.{talent.ID}";
            Icon("talents", talent.ID, talent.Icon, path + ".icon");
            return new()
            {
                ID = talent.ID,
                Name = talent.Name,
                Description = talent.Description,
                Modifiers = Modifiers(talent.Modifiers, path + ".modifiers"),
                ModeledBySets = talent.ModeledBySets,
                SetPieceCredit = talent.SetPieceCredit,
                CopiesTalent = talent.CopiesTalent is { } copy ? new V2.TalentCopyDefinition
                { From = copy.From, To = copy.To, SameTagPrefix = copy.SameTagPrefix, ExcludeTags = copy.ExcludeTags, ExcludeTalents = copy.ExcludeTalents } : null,
                AllyEffects = talent.AllyEffects.Select(effect => new V2.AllyEffect
                {
                    ID = effect.ID,
                    Stack = effect.Stack,
                    Name = effect.Name,
                    Description = effect.Description,
                    Modifiers = Modifiers(effect.Modifiers, $"{path}.allyEffects.{effect.ID}.modifiers")
                }).ToImmutableArray()
            };
        }

        private V2.ActiveSkillDefinition ActiveSkill(ActiveSkillDefinition skill)
        {
            var path = $"activeSkills.{skill.ID}";
            Icon("skills", skill.ID, skill.Icon, path + ".icon");
            return new()
            {
                ID = skill.ID,
                Name = skill.Name,
                Description = skill.Description,
                Family = skill.Family,
                Modifiers = Modifiers(skill.Modifiers, path + ".modifiers"),
                PreviewOutputs = skill.PreviewOutputs.Select(output => new V2.ActiveSkillPreviewOutputDefinition
                {
                    ID = output.ID,
                    Name = output.Name,
                    Description = output.Description,
                    Format = output.Format,
                    Formula = output.Formula,
                    When = output.When,
                    Kind = output.Kind,
                    Headline = output.Headline
                }).ToImmutableArray(),
                Upgrade = skill.Upgrade is { } upgrade ? new V2.SkillUpgradeDefinition { Name = upgrade.Name, Description = upgrade.Description, MaxRank = upgrade.MaxRank } : null,
                ModSlots = skill.ModSlots.Select(slot => new V2.ActiveSkillModSlotDefinition
                {
                    ID = slot.ID,
                    Name = slot.Name,
                    Choices = slot.Choices.Select(choice => new V2.ActiveSkillModDefinition
                    {
                        ID = choice.ID,
                        Name = choice.Name,
                        Description = choice.Description,
                        Input = choice.Input,
                        Min = choice.Min,
                        Max = choice.Max,
                        Step = choice.Step,
                        Format = choice.Format,
                        StatName = choice.StatName
                    }).ToImmutableArray()
                }).ToImmutableArray()
            };
        }

        private V2.SpecializationDefinition Specialization(SpecializationDefinition specialization)
        {
            var path = $"specializations.{specialization.ID}";
            Icon("specializations", specialization.ID, specialization.Icon, path + ".icon");
            Asset(specialization.Image, path + ".image");
            return new()
            {
                ID = specialization.ID,
                Name = specialization.Name,
                Description = specialization.Description,
                Modifiers = Modifiers(specialization.Modifiers, path + ".modifiers"),
                Budgets = specialization.Budgets.Select(budget => new V2.SpecializationBudgetDefinition
                { ID = budget.ID, Name = budget.Name, Points = budget.Points, ParentID = budget.ParentID }).ToImmutableArray(),
                Nodes = specialization.Nodes.Select(node =>
                {
                    Icon("nodes", node.ID, node.Icon, $"{path}.nodes.{node.ID}.icon");
                    return new V2.SpecializationNodeDefinition
                    {
                        ID = node.ID,
                        Name = node.Name,
                        Description = node.Description,
                        BudgetID = node.BudgetID,
                        Cost = node.Cost,
                        Tier = node.Tier,
                        Prerequisites = node.Prerequisites,
                        Modifiers = Modifiers(node.Modifiers, $"{path}.nodes.{node.ID}.modifiers")
                    };
                }).ToImmutableArray()
            };
        }

        /// <param name="ownWeaponSlot">The owning weapon's slot: gates on it are implied by the active weapon and dropped.</param>
        private ImmutableArray<V2.StatModifier> Modifiers(ImmutableArray<StatModifier> values, string path, string? ownWeaponSlot = null, int start = 0)
        {
            var result = ImmutableArray.CreateBuilder<V2.StatModifier>(Math.Max(0, values.Length - start));
            for (var index = start; index < values.Length; index++)
            {
                var modifier = values[index];
                var modifierPath = $"{path}[{index}]";
                if (modifier.Bucket.Length > 0) { Problem(modifierPath + ".bucket", "stat buckets are removed in v2"); }
                if (modifier.Op == "inc") { Problem(modifierPath + ".op", "the inc operation is removed in v2"); }
                else if (!V2.ModifierOps.IsKnown(modifier.Op)) { Problem(modifierPath + ".op", $"unknown op {Quoted(modifier.Op)}"); }
                result.Add(new()
                {
                    Stat = modifier.Stat,
                    Op = modifier.Op,
                    Value = modifier.Value,
                    When = modifier.When,
                    WhileHolstered = modifier.WhileHolstered,
                    RequiresContext = Gate(modifier.RequiresContext, modifierPath + ".requiresContext", ownWeaponSlot)
                });
            }
            return result.ToImmutable();
        }

        private ImmutableDictionary<string, string>? Gate(ImmutableDictionary<string, string>? gate, string path, string? ownWeaponSlot)
        {
            if (gate is null) { return null; }
            if (gate.TryGetValue(WeaponSlotKey, out var slot))
            {
                if (ownWeaponSlot is null || slot != ownWeaponSlot)
                { Problem(path, $"v2 has no {WeaponSlotKey} gates; only a weapon's own modifiers may require it to be active, found {Quoted(slot)}"); }
                gate = gate.Remove(WeaponSlotKey);
            }
            foreach (var (key, value) in gate)
            {
                if (!selectors.TryGetValue(key, out var values)) { selectors[key] = values = new(StringComparer.Ordinal); }
                values.Add(value);
            }
            // An empty v1 gate always matches, which is the same as no gate.
            return gate.IsEmpty ? null : gate.ToImmutableDictionary(StringComparer.Ordinal);
        }

        // v1 never declared its context keys; until the data project supplies names and
        // sections, each selector is named after its key and offers the values observed.
        private ImmutableArray<V2.SelectorDefinition> Selectors() =>
            selectors.Select(pair => new V2.SelectorDefinition { ID = pair.Key, Name = pair.Key, Values = pair.Value.ToImmutableArray() }).ToImmutableArray();

        private V2.SimulationRules Simulation() => new()
        {
            Triggers = source.SimTriggers.Select(trigger => new V2.SimTriggerDefinition
            {
                Talent = trigger.Talent,
                Key = trigger.Key,
                On = trigger.On,
                Kind = trigger.Kind,
                Duration = trigger.Duration,
                Max = trigger.Max,
                Refresh = trigger.Refresh,
                Every = trigger.Every,
                ResetOn = trigger.ResetOn,
                ConsumeOn = trigger.ConsumeOn,
                Phases = trigger.Phases.Select(phase => new V2.SimPhase { Until = phase.Until, Value = phase.Value }).ToImmutableArray(),
                Percent = trigger.Percent,
                Within = trigger.Within,
                Count = trigger.Count,
                Refill = trigger.Refill,
                Gain = trigger.Gain,
                Also = trigger.Also.Select(gain => new V2.SimGain { On = gain.On, Gain = gain.Gain, Count = gain.Count }).ToImmutableArray(),
                Decay = trigger.Decay,
                AtLeast = trigger.AtLeast,
                Purge = trigger.Purge,
                Status = trigger.Status,
                WhileStatus = trigger.WhileStatus,
                Pool = trigger.Pool,
                PoolMax = trigger.PoolMax
            }).ToImmutableArray(),
            Skills = source.SimSkills.Select(skill => new V2.SimSkillDefinition
            {
                Skill = skill.Skill,
                Rate = skill.Rate,
                Window = skill.Window,
                Cooldown = skill.Cooldown,
                Impact = skill.Impact,
                CooldownFromEnd = skill.CooldownFromEnd,
                Impacts = skill.Impacts,
                Shield = skill.Shield,
                Regeneration = skill.Regeneration,
                Weapons = skill.Weapons,
                ExcludeTags = skill.ExcludeTags,
                Status = skill.Status
            }).ToImmutableArray()
        };

        private void Icon(string group, string id, string asset, string path)
        {
            if (asset.Length == 0) { return; }
            Asset(asset, path);
            if (!icons.TryGetValue(group, out var entries)) { icons[group] = entries = new(StringComparer.Ordinal); }
            if (entries.TryGetValue(id, out var existing) && existing != asset)
            { Problem(path, $"{group} icons are keyed by id, and {Quoted(id)} already uses {Quoted(existing)}"); }
            entries[id] = asset;
        }

        private void Asset(string asset, string path) { if (asset.Length > 0) { assetReferences.Add((path, asset)); } }

        private V2.PackPresentation Presentation()
        {
            var assets = source.Artwork?.Assets ?? [];
            var known = assets.Select(asset => asset.ID).ToHashSet(StringComparer.Ordinal);
            foreach (var (path, asset) in assetReferences.Where(reference => !known.Contains(reference.Asset)))
            { Problem(path, $"unknown artwork asset {Quoted(asset)}"); }
            if (source.Layout is { } layout && layout.WeaponContextKey.Length > 0 && layout.WeaponContextKey != WeaponSlotKey)
            { Problem("layout.weaponContextKey", $"the active weapon is built in as {Quoted(WeaponSlotKey)}, found {Quoted(layout.WeaponContextKey)}"); }
            return new()
            {
                Theme = source.Theme is { } theme ? new V2.Theme { Primary = theme.Primary, Background = theme.Background, Variant = theme.Variant, Colors = theme.Colors } : null,
                Layout = source.Layout is { } value ? new V2.PackLayout
                {
                    SlotGroups = value.SlotGroups.Select(group => new V2.LayoutSlotGroup
                    { Name = group.Name, Slots = group.Slots, CountAttributes = group.CountAttributes, Columns = group.Columns, CompareStats = group.CompareStats }).ToImmutableArray(),
                    Summary = value.Summary.Select(group => new V2.LayoutSummaryGroup { Name = group.Name, Stats = group.Stats, Conditional = group.Conditional }).ToImmutableArray(),
                    Headline = value.Headline.Select(stat => new V2.LayoutHeadlineStat
                    {
                        Stat = stat.Stat,
                        BuildOutput = stat.BuildOutput,
                        SkillRates = stat.SkillRates,
                        Label = stat.Label,
                        Conditional = stat.Conditional,
                        Unavailable = stat.Unavailable,
                        LowerIsBetter = stat.LowerIsBetter
                    }).ToImmutableArray(),
                    AttributeCategories = value.AttributeCategories.Select(category => new V2.LayoutAttributeCategory(category.ID, category.Name, category.Color)).ToImmutableArray(),
                    Rarities = value.Rarities.Select(rarity => new V2.LayoutRarity { ID = rarity.ID, Name = rarity.Name, Color = rarity.Color, Tags = rarity.Tags, TagPrefixes = rarity.TagPrefixes }).ToImmutableArray(),
                    AssumptionSections = value.AssumptionSections.Select(section => new V2.LayoutAssumptionSection(section.Name, section.Default)).ToImmutableArray()
                } : null,
                Icons = icons.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.ToImmutableDictionary(StringComparer.Ordinal), StringComparer.Ordinal),
                SpecializationImages = source.Specializations.Where(specialization => specialization.Image.Length > 0)
                    .ToImmutableDictionary(specialization => specialization.ID, specialization => specialization.Image, StringComparer.Ordinal),
                ItemArt = source.Items.Where(item => item.Artwork is { } art && (art.Image.Length > 0 || art.Icon.Length > 0))
                    .ToImmutableDictionary(item => item.ID, item => new V2.ItemArtwork(item.Artwork!.Image, item.Artwork.Icon), StringComparer.Ordinal),
                ArtworkSources = (source.Artwork?.Sources ?? []).Select(artwork => new V2.ArtworkSource
                { ID = artwork.ID, Name = artwork.Name, URL = artwork.URL, Attribution = artwork.Attribution, License = artwork.License, Notes = artwork.Notes }).ToImmutableArray(),
                ArtworkAssets = assets.Select(asset => new V2.ArtworkAsset { ID = asset.ID, Path = asset.Path, Kind = asset.Kind, SHA256 = asset.SHA256, SourceID = asset.SourceID }).ToImmutableArray()
            };
        }

        private static string Quoted(string value) => CharacterPlanner.Core.Expressions.ExpressionParser.Quote(value);
    }
}
