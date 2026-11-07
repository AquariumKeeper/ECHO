using System.Collections.Immutable;
using System.Text;
using CharacterPlanner.Core.Packs.Wire;

namespace CharacterPlanner.Core.Packs;

public sealed record PackWriteOptions
{
    /// <summary>provenance.md content; when set it is written and hashed into the manifest.</summary>
    public ReadOnlyMemory<byte>? Provenance { get; init; }
    /// <summary>The assets directory digest (<see cref="PackFiles.DirectoryDigest"/>); omitted when empty.</summary>
    public string AssetsSha256 { get; init; } = "";
}

/// <summary>
/// Writes a domain pack as v2 files: UTF-8, LF, two-space indentation and stable ordering, so equal
/// packs give identical bytes. Repeated item slot lists and upgrades become shared templates.
/// </summary>
public static class PackWriter
{
    public static ImmutableSortedDictionary<string, ReadOnlyMemory<byte>> Write(GamePack pack, PackWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pack);
        options ??= new();
        var files = ImmutableSortedDictionary.CreateBuilder<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        files[PackFiles.Rules] = WireJson.Serialize(Rules(pack));
        files[PackFiles.Simulation] = WireJson.Serialize(Simulation(pack.Simulation));
        files[PackFiles.Presentation] = WireJson.Serialize(Presentation(pack.Presentation));
        files[PackFiles.ShareCodes] = WireJson.Serialize(new ShareCodesWire
        {
            LegacyOrder = new()
            {
                Slots = [.. pack.LegacyShareOrder.Slots],
                Stats = Optional(pack.LegacyShareOrder.Stats),
                Scenarios = Optional(pack.LegacyShareOrder.Scenarios),
                PassiveSkills = Optional(pack.LegacyShareOrder.PassiveSkills)
            }
        });
        if (options.Provenance is { } provenance) { files[PackFiles.Provenance] = provenance.ToArray(); }
        var hashes = new Dictionary<string, FileHashWire>(StringComparer.Ordinal);
        foreach (var (name, bytes) in files) { hashes[name] = new() { Sha256 = PackFiles.Sha256(bytes.Span), Bytes = bytes.Length }; }
        files[PackFiles.Manifest] = WireJson.Serialize(new ManifestWire
        {
            SchemaVersion = PackManifest.CurrentSchemaVersion,
            Id = pack.ID,
            Name = pack.Name,
            Version = pack.Version,
            GameVersion = pack.GameVersion,
            Description = Optional(pack.Description),
            Capabilities = [.. pack.Capabilities],
            Files = hashes,
            AssetsSha256 = Optional(options.AssetsSha256)
        });
        return files.ToImmutable();
    }

    private static string? Optional(string value) => value.Length == 0 ? null : value;
    private static bool? Optional(bool value) => value ? true : null;
    private static long? Optional(long value) => value == 0 ? null : value;
    private static double? Optional(double value) => value == 0 ? null : value;
    private static List<string>? Optional(ImmutableArray<string> values) => values.IsDefaultOrEmpty ? null : [.. values];

    // Maps are written in ordinal key order so output never depends on hash order.
    private static Dictionary<string, TValue>? Sorted<TSource, TValue>(IEnumerable<KeyValuePair<string, TSource>>? values, Func<TSource, TValue> map)
    {
        if (values is null || !values.Any()) { return null; }
        var result = new Dictionary<string, TValue>(StringComparer.Ordinal);
        foreach (var (key, value) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal)) { result[key] = map(value); }
        return result;
    }

    private static List<ModifierWire> Modifiers(ImmutableArray<StatModifier> values) => [.. values.Select(modifier => new ModifierWire
    {
        Stat = modifier.Stat,
        Op = modifier.Op,
        Value = modifier.Value,
        When = Optional(modifier.When),
        // An empty map is kept (and rejected by validation) rather than silently dropped.
        Requires = modifier.RequiresContext is null ? null : Sorted(modifier.RequiresContext, value => value) ?? new Dictionary<string, string>(StringComparer.Ordinal),
        WhileHolstered = Optional(modifier.WhileHolstered)
    })];

    private static List<ModifierWire>? OptionalModifiers(ImmutableArray<StatModifier> values) => values.IsDefaultOrEmpty ? null : Modifiers(values);

    private static RulesWire Rules(GamePack pack)
    {
        var templates = new TemplateNamer();
        var items = pack.Items.Select(item => new ItemWire
        {
            Id = item.ID,
            Name = item.Name,
            Description = Optional(item.Description),
            Slot = item.Slot,
            Tags = Optional(item.Tags),
            Weapon = item.Weapon.IsDefaultOrEmpty ? null : [.. item.Weapon.Select(stat => new KeyValuePair<string, double>(stat.Stat, stat.Value))],
            Modifiers = OptionalModifiers(item.Modifiers),
            AttributeSlots = item.AttributeSlots.IsDefaultOrEmpty ? null : templates.AttributeSlots.Name(item.Slot, [.. item.AttributeSlots.Select(slot => new AttributeSlotWire
            {
                Id = slot.ID,
                Name = slot.Name,
                Choices = [.. slot.Choices.Select(choice => new AttributeChoiceWire { Attribute = choice.AttributeID, Min = choice.Min, Max = choice.Max, Step = choice.Step, CountStat = Optional(choice.CountStat) })]
            })]),
            ModSlots = item.ModSlots.IsDefaultOrEmpty ? null : templates.ModSlots.Name(item.Slot, [.. item.ModSlots.Select(slot => new ChoiceSlotWire
            { Id = slot.ID, Name = slot.Name, Choices = [.. slot.Choices] })]),
            TalentSlots = item.TalentSlots.IsDefaultOrEmpty ? null : templates.TalentSlots.Name(item.Slot, [.. item.TalentSlots.Select(slot => new ChoiceSlotWire
            { Id = slot.ID, Name = slot.Name, Choices = [.. slot.Choices] })]),
            Upgrade = item.Upgrade is not { } upgrade ? null : templates.Upgrades.Name(item.Slot, new UpgradeWire
            { Name = upgrade.Name, Description = Optional(upgrade.Description), MaxRank = upgrade.MaxRank, ModifiersPerRank = Modifiers(upgrade.ModifiersPerRank) })
        }).ToList();
        return new()
        {
            Level = pack.Level,
            Stats = [.. pack.Stats.Select(stat => new StatWire
            {
                Id = stat.ID, Name = stat.Name, ShortName = Optional(stat.ShortName), Description = Optional(stat.Description),
                Format = stat.Format, Formula = stat.Formula, Min = stat.Min, Max = stat.Max
            })],
            Curves = Sorted(pack.Curves, points => points.Select(point => new[] { point.X, point.Y }).ToList()),
            Selectors = NullIfEmpty(pack.Selectors.Select(selector => new SelectorWire
            {
                Id = selector.ID,
                Name = selector.Name,
                Values = [.. selector.Values],
                Section = Optional(selector.Section),
                Description = Optional(selector.Description)
            })),
            NumericInputs = NullIfEmpty(pack.NumericContextInputs.Select(input => new NumericInputWire
            {
                Id = input.ID,
                Name = input.Name,
                Description = Optional(input.Description),
                Min = input.Min,
                Max = input.Max,
                Step = input.Step == 1 ? null : input.Step,
                Default = input.Default,
                Global = Optional(input.Global),
                Options = input.Options.IsDefaultOrEmpty ? null : [.. input.Options.Select(option => new NumericOptionWire { Value = option.Value, Label = option.Label })],
                Group = Optional(input.Group),
                GroupName = Optional(input.GroupName),
                Section = Optional(input.Section)
            })),
            BuildOutputs = NullIfEmpty(pack.BuildOutputs.Select(output => new BuildOutputWire
            { Id = output.ID, Name = output.Name, Description = Optional(output.Description), Format = output.Format, Formula = output.Formula })),
            Attributes = NullIfEmpty(pack.Attributes.Select(attribute => new AttributeWire
            {
                Id = attribute.ID,
                Name = attribute.Name,
                Stat = attribute.Stat,
                Min = attribute.Min,
                Max = attribute.Max,
                Step = attribute.Step,
                Format = attribute.Format,
                Category = Optional(attribute.Category),
                Core = Optional(attribute.Core),
                Prototype = attribute.PrototypeMax is { } max ? new AttributePrototypeWire { Max = max, Min = attribute.PrototypeMin } : null,
                CountStat = Optional(attribute.CountStat)
            })),
            Templates = templates.Wire(),
            Slots = [.. pack.Slots.Select(slot => new SlotWire { Id = slot.ID, Name = slot.Name, Accepts = Optional(slot.Accepts), Weapon = Optional(slot.Weapon) })],
            EquipmentLimits = NullIfEmpty(pack.EquipmentLimits.Select(limit => new EquipmentLimitWire
            { Id = limit.ID, Name = limit.Name, Tag = limit.Tag, Slots = [.. limit.Slots], Maximum = limit.Maximum })),
            Items = items,
            Mods = NullIfEmpty(pack.Mods.Select(mod => new ModWire
            {
                Id = mod.ID,
                Name = mod.Name,
                Description = Optional(mod.Description),
                Modifiers = OptionalModifiers(mod.Modifiers),
                RollAttribute = Optional(mod.RollAttributeID)
            })),
            Talents = NullIfEmpty(pack.Talents.Select(talent => new TalentWire
            {
                Id = talent.ID,
                Name = talent.Name,
                Description = Optional(talent.Description),
                Modifiers = OptionalModifiers(talent.Modifiers),
                ModeledBySets = Optional(talent.ModeledBySets),
                SetPieceCredit = Optional(talent.SetPieceCredit),
                CopiesTalent = talent.CopiesTalent is { } copy ? new TalentCopyWire
                {
                    From = copy.From,
                    To = copy.To,
                    SameTagPrefix = Optional(copy.SameTagPrefix),
                    ExcludeTags = Optional(copy.ExcludeTags),
                    ExcludeTalents = Optional(copy.ExcludeTalents)
                } : null,
                AllyEffects = NullIfEmpty(talent.AllyEffects.Select(effect => new AllyEffectWire
                { Id = effect.ID, Stack = effect.Stack, Name = effect.Name, Description = Optional(effect.Description), Modifiers = Modifiers(effect.Modifiers) })),
                Coverage = talent.Coverage is { } coverage ? new TalentCoverageWire
                { Status = coverage.Status, Omits = Optional(coverage.Omits), Evidence = coverage.Evidence, SettleBy = Optional(coverage.SettleBy) } : null
            })),
            Sets = NullIfEmpty(pack.Sets.Select(set => new SetWire
            {
                Id = set.ID,
                Name = set.Name,
                Description = Optional(set.Description),
                Items = [.. set.Items],
                Bonuses = [.. set.Bonuses.Select(bonus => new SetBonusWire { Pieces = bonus.Pieces, Modifiers = Modifiers(bonus.Modifiers) })]
            })),
            Prototype = pack.Prototype is { } prototype ? new PrototypeWire
            {
                Slots = [.. prototype.Slots],
                ExcludeTags = Optional(prototype.ExcludeTags),
                CountStat = Optional(prototype.CountStat),
                NormalizeInput = Optional(prototype.NormalizeInput),
                Augments = NullIfEmpty(prototype.Augments.Select(augment => new AugmentWire
                {
                    Id = augment.ID,
                    Name = augment.Name,
                    Description = Optional(augment.Description),
                    Stat = augment.Stat,
                    Base = augment.Base,
                    PerLevel = augment.PerLevel,
                    Levels = augment.Levels
                }))
            } : null,
            AccountUpgrades = NullIfEmpty(pack.AccountUpgrades.Select(upgrade => new AccountUpgradeWire
            {
                Id = upgrade.ID,
                Name = upgrade.Name,
                Description = Optional(upgrade.Description),
                MaxRank = upgrade.MaxRank,
                EffectCap = upgrade.EffectCap,
                ModifiersPerRank = Modifiers(upgrade.ModifiersPerRank)
            })),
            SkillSlots = NullIfEmpty(pack.ActiveSkillSlots.Select(slot => new SkillSlotWire { Id = slot.ID, Name = slot.Name, Choices = [.. slot.Choices] })),
            Skills = NullIfEmpty(pack.ActiveSkills.Select(Skill)),
            Specializations = NullIfEmpty(pack.Specializations.Select(Specialization)),
            RenamedIds = Sorted(pack.RenamedIds, ids => Sorted(ids, id => id) ?? new Dictionary<string, string>(StringComparer.Ordinal))
        };
    }

    private static List<T>? NullIfEmpty<T>(IEnumerable<T> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? null : list;
    }

    private static SkillWire Skill(ActiveSkillDefinition skill) => new()
    {
        Id = skill.ID,
        Name = skill.Name,
        Description = Optional(skill.Description),
        Family = Optional(skill.Family),
        Modifiers = OptionalModifiers(skill.Modifiers),
        Upgrade = skill.Upgrade is { } upgrade ? new SkillUpgradeWire { Name = upgrade.Name, Description = Optional(upgrade.Description), MaxRank = upgrade.MaxRank } : null,
        ModSlots = NullIfEmpty(skill.ModSlots.Select(slot => new SkillModSlotWire
        {
            Id = slot.ID,
            Name = slot.Name,
            Choices = [.. slot.Choices.Select(choice => new SkillModWire
            {
                Id = choice.ID, Name = choice.Name, Description = Optional(choice.Description), Input = choice.Input, Min = choice.Min, Max = choice.Max,
                Step = choice.Step, Format = choice.Format, StatName = Optional(choice.StatName)
            })]
        })),
        PreviewOutputs = NullIfEmpty(skill.PreviewOutputs.Select(output => new PreviewOutputWire
        {
            Id = output.ID,
            Name = output.Name,
            Description = Optional(output.Description),
            Format = output.Format,
            Formula = output.Formula,
            When = Optional(output.When),
            Kind = Optional(output.Kind),
            Headline = Optional(output.Headline)
        }))
    };

    private static SpecializationWire Specialization(SpecializationDefinition specialization) => new()
    {
        Id = specialization.ID,
        Name = specialization.Name,
        Description = Optional(specialization.Description),
        Modifiers = OptionalModifiers(specialization.Modifiers),
        Budgets = NullIfEmpty(specialization.Budgets.Select(budget => new BudgetWire
        { Id = budget.ID, Name = budget.Name, Points = budget.Points, ParentId = Optional(budget.ParentID) })),
        Nodes = NullIfEmpty(specialization.Nodes.Select(node => new NodeWire
        {
            Id = node.ID,
            Name = node.Name,
            Description = Optional(node.Description),
            BudgetId = node.BudgetID,
            Cost = Optional(node.Cost),
            Tier = Optional(node.Tier),
            Prerequisites = Optional(node.Prerequisites),
            Modifiers = OptionalModifiers(node.Modifiers)
        }))
    };

    private static SimulationWire Simulation(SimulationRules simulation) => new()
    {
        Triggers = NullIfEmpty(simulation.Triggers.Select(trigger => new TriggerWire
        {
            Talent = trigger.Talent,
            Key = trigger.Key,
            On = Optional(trigger.On),
            Kind = trigger.Kind,
            Duration = Optional(trigger.Duration),
            Max = Optional(trigger.Max),
            Refresh = Optional(trigger.Refresh),
            Every = Optional(trigger.Every),
            EveryGroup = Optional(trigger.EveryGroup),
            CooldownFromEnd = Optional(trigger.CooldownFromEnd),
            ResetOn = Optional(trigger.ResetOn),
            AlsoResetOn = Optional(trigger.AlsoResetOn),
            ConsumeOn = Optional(trigger.ConsumeOn),
            Phases = NullIfEmpty(trigger.Phases.Select(phase => new PhaseWire { Until = phase.Until, Value = phase.Value })),
            Percent = Optional(trigger.Percent),
            Within = Optional(trigger.Within),
            Count = Optional(trigger.Count),
            Refill = Optional(trigger.Refill),
            Gain = Optional(trigger.Gain),
            Also = NullIfEmpty(trigger.Also.Select(gain => new GainWire { On = gain.On, Gain = gain.Gain, Count = Optional(gain.Count) })),
            Decay = Optional(trigger.Decay),
            AtLeast = Optional(trigger.AtLeast),
            Purge = Optional(trigger.Purge),
            Status = Optional(trigger.Status),
            ApplyOnHit = Optional(trigger.ApplyOnHit),
            GainWhileHolstered = Optional(trigger.GainWhileHolstered),
            WhileStatus = Optional(trigger.WhileStatus),
            Pool = Optional(trigger.Pool),
            PoolMax = Optional(trigger.PoolMax),
            CancelOn = Optional(trigger.CancelOn),
            CancelFor = Optional(trigger.CancelFor),
            Bank = Optional(trigger.Bank),
            ClearGrant = Optional(trigger.ClearGrant),
            ClearAmount = Optional(trigger.ClearAmount),
            ClearDuration = Optional(trigger.ClearDuration),
            WeaponTags = Optional(trigger.WeaponTags),
            ForceHeadshot = Optional(trigger.ForceHeadshot),
            Grant = Optional(trigger.Grant),
            GrantAmount = Optional(trigger.GrantAmount),
            GrantDuration = Optional(trigger.GrantDuration),
            GrantInstances = Optional(trigger.GrantInstances),
            CountWindow = Optional(trigger.CountWindow),
            MaxMagazine = Optional(trigger.MaxMagazine),
            WhileKey = Optional(trigger.WhileKey),
            WhileFull = Optional(trigger.WhileFull),
            WhileArmorBelow = Optional(trigger.WhileArmorBelow),
            WhileStatAtLeast = trigger.WhileStatAtLeast is { } gate ? new StatGateWire { Stat = gate.Stat, Value = gate.Value } : null,
            DurationStat = Optional(trigger.DurationStat)
        })),
        Skills = NullIfEmpty(simulation.Skills.Select(skill => new SimSkillWire
        {
            Skill = skill.Skill,
            Rate = Optional(skill.Rate),
            Window = Optional(skill.Window),
            Cooldown = Optional(skill.Cooldown),
            Impact = Optional(skill.Impact),
            CooldownFromEnd = Optional(skill.CooldownFromEnd),
            Impacts = Optional(skill.Impacts),
            ImpactInterval = Optional(skill.ImpactInterval),
            Shield = Optional(skill.Shield),
            Regeneration = Optional(skill.Regeneration),
            Weapons = Optional(skill.Weapons),
            ExcludeTags = Optional(skill.ExcludeTags),
            Status = Optional(skill.Status),
            RateArmorBonus = Optional(skill.RateArmorBonus),
            RateRead = Optional(skill.RateRead),
            Stock = Optional(skill.Stock),
            StockInterval = Optional(skill.StockInterval),
            StatusRate = Optional(skill.StatusRate),
            StatusWindow = Optional(skill.StatusWindow),
            StatusArmorBonus = Optional(skill.StatusArmorBonus)
        })),
        StatusDamage = NullIfEmpty(simulation.StatusDamage.Select(damage => new StatusDamageWire
        {
            Status = damage.Status,
            Tick = damage.Tick,
            Period = damage.Period,
            TickOnApply = Optional(damage.TickOnApply),
            ArmorBonus = Optional(damage.ArmorBonus)
        })),
        TalentDamage = NullIfEmpty(simulation.TalentDamage.Select(damage => new TalentDamageWire
        {
            Talent = damage.Talent,
            On = damage.On,
            Total = damage.Total,
            Duration = damage.Duration,
            Period = damage.Period
        })),
        DistanceDefaults = NullIfEmpty(simulation.DistanceDefaults.Select(entry => new DistanceDefaultWire
        {
            Input = entry.Input,
            Bands = [.. entry.Bands.Select(band => new DistanceBandWire { UpTo = band.UpTo, Value = band.Value })]
        })),
        ArmorKit = simulation.ArmorKit is { } kit ? new ArmorKitWire
        {
            Repair = kit.Repair,
            UseSeconds = kit.UseSeconds,
            ExtendSeconds = kit.ExtendSeconds,
            MaxUseSeconds = kit.MaxUseSeconds,
            ReplacedBySpecializations = Optional(kit.ReplacedBySpecializations),
            ReplacedByTalents = Optional(kit.ReplacedByTalents)
        } : null
    };

    // Icon groups outside PresentationIcons.Groups have no place in the file; validation rejects them.
    private static PresentationWire Presentation(PackPresentation presentation)
    {
        Dictionary<string, string>? Icons(string group) => Sorted(presentation.Icons.GetValueOrDefault(group), id => id);
        var icons = new IconsWire
        {
            Slots = Icons(PresentationIcons.Slots),
            Talents = Icons(PresentationIcons.Talents),
            Skills = Icons(PresentationIcons.Skills),
            Specializations = Icons(PresentationIcons.Specializations),
            Nodes = Icons(PresentationIcons.Nodes)
        };
        var hasIcons = icons.Slots is not null || icons.Talents is not null || icons.Skills is not null || icons.Specializations is not null || icons.Nodes is not null;
        var artwork = new ArtworkWire
        {
            Sources = NullIfEmpty(presentation.ArtworkSources.Select(source => new ArtworkSourceWire
            {
                Id = source.ID,
                Name = source.Name,
                Url = Optional(source.URL),
                Attribution = Optional(source.Attribution),
                License = Optional(source.License),
                Notes = Optional(source.Notes)
            })),
            Assets = NullIfEmpty(presentation.ArtworkAssets.Select(asset => new ArtworkAssetWire
            { Id = asset.ID, Path = asset.Path, Kind = asset.Kind, Sha256 = asset.SHA256, SourceId = asset.SourceID }))
        };
        return new()
        {
            Theme = presentation.Theme is { } theme ? new ThemeWire
            {
                Primary = Optional(theme.Primary),
                Background = Optional(theme.Background),
                Variant = Optional(theme.Variant),
                Colors = Sorted(theme.Colors, color => color)
            } : null,
            Layout = presentation.Layout is { } layout ? new LayoutWire
            {
                SlotGroups = NullIfEmpty(layout.SlotGroups.Select(group => new SlotGroupWire
                {
                    Name = group.Name,
                    Slots = [.. group.Slots],
                    CountAttributes = Optional(group.CountAttributes),
                    Columns = Optional(group.Columns),
                    CompareStats = Optional(group.CompareStats)
                })),
                Headline = NullIfEmpty(layout.Headline.Select(entry => new HeadlineWire
                {
                    Stat = Optional(entry.Stat),
                    BuildOutput = Optional(entry.BuildOutput),
                    SkillRates = Optional(entry.SkillRates),
                    Label = Optional(entry.Label),
                    Conditional = Optional(entry.Conditional),
                    Unavailable = Optional(entry.Unavailable),
                    LowerIsBetter = Optional(entry.LowerIsBetter)
                })),
                Summary = NullIfEmpty(layout.Summary.Select(group => new SummaryGroupWire
                { Name = group.Name, Stats = Optional(group.Stats), Conditional = Optional(group.Conditional) })),
                AssumptionSections = NullIfEmpty(layout.AssumptionSections.Select(section => new AssumptionSectionWire { Name = section.Name, Default = Optional(section.Default) })),
                AttributeCategories = NullIfEmpty(layout.AttributeCategories.Select(category => new AttributeCategoryWire { Id = category.ID, Name = category.Name, Color = category.Color })),
                Rarities = NullIfEmpty(layout.Rarities.Select(rarity => new RarityWire
                { Id = rarity.ID, Name = rarity.Name, Color = rarity.Color, Tags = Optional(rarity.Tags), TagPrefixes = Optional(rarity.TagPrefixes) }))
            } : null,
            Icons = hasIcons ? icons : null,
            SpecializationImages = Sorted(presentation.SpecializationImages, image => image),
            ItemArt = Sorted(presentation.ItemArt, art => new ItemArtWire { Image = Optional(art.Image), Icon = Optional(art.Icon) }),
            Artwork = artwork.Sources is null && artwork.Assets is null ? null : artwork
        };
    }

    /// <summary>
    /// Deduplicates by structural equality (the template's own JSON). A new template is named
    /// "&lt;slot of the first item using it&gt;_&lt;n&gt;", numbered per slot and kind in item order.
    /// </summary>
    private sealed class TemplateNamer
    {
        public Kind<List<AttributeSlotWire>> AttributeSlots { get; } = new();
        public Kind<List<ChoiceSlotWire>> ModSlots { get; } = new();
        public Kind<List<ChoiceSlotWire>> TalentSlots { get; } = new();
        public Kind<UpgradeWire> Upgrades { get; } = new();

        public TemplatesWire? Wire()
        {
            var templates = new TemplatesWire
            {
                AttributeSlots = AttributeSlots.Definitions(),
                ModSlots = ModSlots.Definitions(),
                TalentSlots = TalentSlots.Definitions(),
                Upgrades = Upgrades.Definitions()
            };
            return templates.AttributeSlots is null && templates.ModSlots is null && templates.TalentSlots is null && templates.Upgrades is null ? null : templates;
        }

        public sealed class Kind<T>
        {
            private readonly Dictionary<string, string> byContent = new(StringComparer.Ordinal);
            private readonly Dictionary<string, int> counts = new(StringComparer.Ordinal);
            private readonly List<KeyValuePair<string, T>> definitions = [];

            public string Name(string slot, T definition)
            {
                var content = Encoding.UTF8.GetString(WireJson.Serialize(definition));
                if (byContent.TryGetValue(content, out var name)) { return name; }
                var prefix = PackIds.IsSlug(slot) ? slot : "template";
                var count = counts[prefix] = counts.GetValueOrDefault(prefix) + 1;
                name = $"{prefix}_{count}";
                byContent[content] = name;
                definitions.Add(new(name, definition));
                return name;
            }

            // First-use order, which is deterministic and keeps related templates together.
            public Dictionary<string, T>? Definitions() => definitions.Count == 0 ? null : definitions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }
    }
}
