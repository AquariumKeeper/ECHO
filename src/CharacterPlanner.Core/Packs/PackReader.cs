using System.Collections.Immutable;
using CharacterPlanner.Core.Diagnostics;
using CharacterPlanner.Core.Expressions;
using CharacterPlanner.Core.Packs.Wire;

namespace CharacterPlanner.Core.Packs;

public sealed record PackReadResult(ImmutableArray<PackDiagnostic> Diagnostics, PackManifest? Manifest, GamePack? Pack);

/// <summary>
/// Reads a v2 pack from its files' bytes, keyed by file name. Bad data becomes diagnostics,
/// never exceptions; opening files is Infrastructure's job. Semantic checks are <see cref="PackValidator"/>'s.
/// </summary>
public static class PackReader
{
    public static PackReadResult Read(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var diagnostics = new List<PackDiagnostic>();
        PackReadResult Result(PackManifest? manifest, GamePack? pack = null) => new(diagnostics.ToImmutableArray(), manifest, pack);
        if (!files.TryGetValue(PackFiles.Manifest, out var manifestBytes))
        { diagnostics.Add(new("error", "file-missing", PackFiles.Manifest, "The pack has no manifest.json")); return Result(null); }
        if (WireJson.Parse<ManifestWire>(PackFiles.Manifest, manifestBytes, diagnostics) is not { } wire) { return Result(null); }
        var manifest = new PackManifest
        {
            SchemaVersion = wire.SchemaVersion,
            ID = wire.Id,
            Name = wire.Name,
            Version = wire.Version,
            GameVersion = wire.GameVersion,
            Description = wire.Description ?? "",
            Capabilities = [.. wire.Capabilities],
            Files = wire.Files.ToImmutableSortedDictionary(pair => pair.Key, pair => new PackFileHash(pair.Value.Sha256, pair.Value.Bytes), StringComparer.Ordinal),
            AssetsSha256 = wire.AssetsSha256 ?? ""
        };
        CheckManifest(manifest, wire.AssetsSha256 is not null, files, diagnostics);
        if (diagnostics.Count > 0) { return Result(manifest); }

        var rules = WireJson.Parse<RulesWire>(PackFiles.Rules, files[PackFiles.Rules], diagnostics);
        var simulation = WireJson.Parse<SimulationWire>(PackFiles.Simulation, files[PackFiles.Simulation], diagnostics);
        var presentation = WireJson.Parse<PresentationWire>(PackFiles.Presentation, files[PackFiles.Presentation], diagnostics);
        var shareCodes = WireJson.Parse<ShareCodesWire>(PackFiles.ShareCodes, files[PackFiles.ShareCodes], diagnostics);
        if (rules is null || simulation is null || presentation is null || shareCodes is null) { return Result(manifest); }
        var pack = new Mapper(diagnostics).Map(manifest, rules, simulation, presentation, shareCodes);
        return Result(manifest, diagnostics.Any(diagnostic => diagnostic.Severity == "error") ? null : pack);
    }

    private static void CheckManifest(PackManifest manifest, bool hasAssetsDigest, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files, List<PackDiagnostic> diagnostics)
    {
        const string file = PackFiles.Manifest;
        void Error(string code, string path, string message) => diagnostics.Add(new("error", code, $"{file}:{path}", message));
        if (manifest.SchemaVersion != PackManifest.CurrentSchemaVersion)
        { Error("schema-version", "$.schemaVersion", $"Schema version {manifest.SchemaVersion} is not supported; this app reads version {PackManifest.CurrentSchemaVersion}"); return; }
        var declared = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < manifest.Capabilities.Length; i++)
        {
            var capability = manifest.Capabilities[i];
            if (!declared.Add(capability)) { Error("duplicate-capability", $"$.capabilities[{i}]", $"Capability {ReferenceText.Quote(capability)} is declared more than once"); }
            else if (!PackCapabilities.Implemented.Contains(capability))
            { Error("unknown-capability", $"$.capabilities[{i}]", $"The pack needs capability {ReferenceText.Quote(capability)}, which this app does not implement"); }
        }
        foreach (var (name, hash) in manifest.Files)
        {
            var path = $"$.files.{name}";
            if (!PackFiles.Listable.Contains(name)) { Error("manifest-file-unknown", path, $"{ReferenceText.Quote(name)} is not a pack file"); continue; }
            if (!PackFiles.IsSha256(hash.Sha256)) { Error("manifest-hash-invalid", path + ".sha256", "File hashes must be 64 lowercase hex digits"); continue; }
            if (!files.TryGetValue(name, out var bytes)) { Error("file-missing", path, $"{name} is listed in the manifest but missing"); continue; }
            if (bytes.Length != hash.Bytes) { Error("file-size-mismatch", path + ".bytes", $"{name} has {bytes.Length} bytes; the manifest says {hash.Bytes}"); continue; }
            if (PackFiles.Sha256(bytes.Span) != hash.Sha256) { Error("file-hash-mismatch", path + ".sha256", $"{name} does not match its manifest hash"); }
        }
        foreach (var name in PackFiles.Required.Where(name => !manifest.Files.ContainsKey(name)))
        { Error("manifest-file-missing", "$.files", $"The manifest must list {name}"); }
        if (hasAssetsDigest && !PackFiles.IsSha256(manifest.AssetsSha256))
        { Error("manifest-hash-invalid", "$.assetsSha256", "The assets digest must be 64 lowercase hex digits"); }
    }

    // Wire to domain, applying the documented default for every omitted member and
    // expanding templates. Structural problems the DTOs cannot express are reported here.
    private sealed class Mapper(List<PackDiagnostic> diagnostics)
    {
        private void Error(string code, string path, string message) => diagnostics.Add(new("error", code, $"{PackFiles.Rules}:{path}", message));
        private void Warning(string code, string path, string message) => diagnostics.Add(new("warning", code, $"{PackFiles.Rules}:{path}", message));

        public GamePack Map(PackManifest manifest, RulesWire rules, SimulationWire simulation, PresentationWire presentation, ShareCodesWire shareCodes) => new()
        {
            ID = manifest.ID,
            Name = manifest.Name,
            Version = manifest.Version,
            GameVersion = manifest.GameVersion,
            Description = manifest.Description,
            Capabilities = manifest.Capabilities,
            Level = rules.Level,
            Stats = [.. rules.Stats.Select(stat => new StatDefinition
            {
                ID = stat.Id, Name = stat.Name, ShortName = stat.ShortName ?? "", Description = stat.Description ?? "",
                Format = stat.Format, Formula = stat.Formula, Min = stat.Min, Max = stat.Max
            })],
            Curves = Curves(rules.Curves),
            Selectors = [.. List(rules.Selectors).Select(selector => new SelectorDefinition
            {
                ID = selector.Id, Name = selector.Name, Description = selector.Description ?? "", Section = selector.Section ?? "", Values = [.. selector.Values]
            })],
            NumericContextInputs = [.. List(rules.NumericInputs).Select(input => new NumericContextInputDefinition
            {
                ID = input.Id, Name = input.Name, Description = input.Description ?? "", Min = input.Min, Max = input.Max, Step = input.Step ?? 1,
                Default = input.Default, Global = input.Global ?? false, Options = [.. List(input.Options).Select(option => new NumericContextOption(option.Value, option.Label))],
                Group = input.Group ?? "", GroupName = input.GroupName ?? "", Section = input.Section ?? ""
            })],
            BuildOutputs = [.. List(rules.BuildOutputs).Select(output => new BuildOutputDefinition
            { ID = output.Id, Name = output.Name, Description = output.Description ?? "", Format = output.Format, Formula = output.Formula })],
            Attributes = [.. List(rules.Attributes).Select(attribute => new AttributeDefinition
            {
                ID = attribute.Id, Name = attribute.Name, Stat = attribute.Stat, Min = attribute.Min, Max = attribute.Max, Step = attribute.Step,
                Format = attribute.Format ?? StatFormats.Decimal, Category = attribute.Category ?? "", Core = attribute.Core ?? false,
                PrototypeMax = attribute.Prototype?.Max, PrototypeMin = attribute.Prototype?.Min, CountStat = attribute.CountStat ?? ""
            })],
            Slots = [.. rules.Slots.Select(slot => new SlotDefinition { ID = slot.Id, Name = slot.Name, Accepts = Strings(slot.Accepts), Weapon = slot.Weapon ?? false })],
            EquipmentLimits = [.. List(rules.EquipmentLimits).Select(limit => new EquipmentLimitDefinition
            { ID = limit.Id, Name = limit.Name, Tag = limit.Tag, Slots = [.. limit.Slots], Maximum = limit.Maximum })],
            Items = Items(rules),
            Mods = [.. List(rules.Mods).Select(mod => new ModDefinition
            { ID = mod.Id, Name = mod.Name, Description = mod.Description ?? "", Modifiers = Modifiers(mod.Modifiers), RollAttributeID = mod.RollAttribute ?? "" })],
            Talents = [.. List(rules.Talents).Select(Talent)],
            Sets = [.. List(rules.Sets).Select(set => new SetDefinition
            {
                ID = set.Id, Name = set.Name, Description = set.Description ?? "", Items = [.. set.Items],
                Bonuses = [.. set.Bonuses.Select(bonus => new SetBonus { Pieces = bonus.Pieces, Modifiers = Modifiers(bonus.Modifiers) })]
            })],
            Prototype = rules.Prototype is { } prototype ? new PrototypeDefinition
            {
                Slots = [.. prototype.Slots],
                ExcludeTags = Strings(prototype.ExcludeTags),
                CountStat = prototype.CountStat ?? "",
                NormalizeInput = prototype.NormalizeInput ?? "",
                Augments = [.. List(prototype.Augments).Select(augment => new AugmentDefinition
                {
                    ID = augment.Id, Name = augment.Name, Description = augment.Description ?? "", Stat = augment.Stat,
                    Base = augment.Base, PerLevel = augment.PerLevel, Levels = augment.Levels
                })]
            } : null,
            AccountUpgrades = [.. List(rules.AccountUpgrades).Select(upgrade => new AccountUpgradeDefinition
            {
                ID = upgrade.Id, Name = upgrade.Name, Description = upgrade.Description ?? "", MaxRank = upgrade.MaxRank,
                EffectCap = upgrade.EffectCap, ModifiersPerRank = Modifiers(upgrade.ModifiersPerRank)
            })],
            ActiveSkillSlots = [.. List(rules.SkillSlots).Select(slot => new ActiveSkillSlotDefinition { ID = slot.Id, Name = slot.Name, Choices = [.. slot.Choices] })],
            ActiveSkills = [.. List(rules.Skills).Select(Skill)],
            Specializations = [.. List(rules.Specializations).Select(Specialization)],
            RenamedIds = (rules.RenamedIds ?? new()).ToImmutableDictionary(pair => pair.Key, pair => pair.Value.ToImmutableDictionary(StringComparer.Ordinal), StringComparer.Ordinal),
            Simulation = Simulation(simulation),
            Presentation = Presentation(presentation),
            LegacyShareOrder = new()
            {
                Slots = [.. shareCodes.LegacyOrder.Slots],
                Stats = Strings(shareCodes.LegacyOrder.Stats),
                Scenarios = Strings(shareCodes.LegacyOrder.Scenarios),
                PassiveSkills = Strings(shareCodes.LegacyOrder.PassiveSkills)
            }
        };

        private static List<T> List<T>(List<T>? values) => values ?? [];
        private static ImmutableArray<string> Strings(List<string>? values) => values is null ? [] : [.. values];

        private static ImmutableArray<StatModifier> Modifiers(List<ModifierWire>? values) => values is null ? [] : [.. values.Select(modifier => new StatModifier
        {
            Stat = modifier.Stat,
            Op = modifier.Op,
            Value = modifier.Value,
            When = modifier.When ?? "",
            RequiresContext = modifier.Requires?.ToImmutableDictionary(StringComparer.Ordinal),
            WhileHolstered = modifier.WhileHolstered ?? false
        })];

        private ImmutableDictionary<string, ImmutableArray<CurvePoint>> Curves(Dictionary<string, List<double[]>>? curves)
        {
            var result = ImmutableDictionary.CreateBuilder<string, ImmutableArray<CurvePoint>>(StringComparer.Ordinal);
            foreach (var (name, points) in curves ?? new())
            {
                for (var i = 0; i < points.Count; i++)
                {
                    if (points[i].Length != 2) { Error("curve-point-invalid", $"$.curves.{name}[{i}]", "A curve point is an [x, y] pair"); }
                }
                result[name] = [.. points.Where(point => point.Length == 2).Select(point => new CurvePoint(point[0], point[1]))];
            }
            return result.ToImmutable();
        }

        private ImmutableArray<ItemDefinition> Items(RulesWire rules)
        {
            var templates = rules.Templates;
            var attributeSlots = new Templates<List<AttributeSlotWire>, ImmutableArray<ItemAttributeSlotDefinition>>(this, "attributeSlots", templates?.AttributeSlots,
                slots => [.. slots.Select(slot => new ItemAttributeSlotDefinition
                {
                    ID = slot.Id, Name = slot.Name,
                    Choices = [.. slot.Choices.Select(choice => new ItemAttributeChoice { AttributeID = choice.Attribute, Min = choice.Min, Max = choice.Max, Step = choice.Step, CountStat = choice.CountStat ?? "" })]
                })]);
            var modSlots = new Templates<List<ChoiceSlotWire>, ImmutableArray<ItemModSlotDefinition>>(this, "modSlots", templates?.ModSlots,
                slots => [.. slots.Select(slot => new ItemModSlotDefinition { ID = slot.Id, Name = slot.Name, Choices = [.. slot.Choices] })]);
            var talentSlots = new Templates<List<ChoiceSlotWire>, ImmutableArray<ItemTalentSlotDefinition>>(this, "talentSlots", templates?.TalentSlots,
                slots => [.. slots.Select(slot => new ItemTalentSlotDefinition { ID = slot.Id, Name = slot.Name, Choices = [.. slot.Choices] })]);
            var upgrades = new Templates<UpgradeWire, ItemUpgradeDefinition?>(this, "upgrades", templates?.Upgrades, upgrade => new ItemUpgradeDefinition
            { Name = upgrade.Name, Description = upgrade.Description ?? "", MaxRank = upgrade.MaxRank, ModifiersPerRank = Modifiers(upgrade.ModifiersPerRank) });
            var items = ImmutableArray.CreateBuilder<ItemDefinition>(rules.Items.Count);
            for (var i = 0; i < rules.Items.Count; i++)
            {
                var item = rules.Items[i];
                var path = $"$.items[{i}]";
                items.Add(new()
                {
                    ID = item.Id,
                    Name = item.Name,
                    Description = item.Description ?? "",
                    Slot = item.Slot,
                    Tags = Strings(item.Tags),
                    Weapon = item.Weapon is null ? [] : [.. item.Weapon.Select(pair => new WeaponBaseStat(pair.Key, pair.Value))],
                    Modifiers = Modifiers(item.Modifiers),
                    AttributeSlots = attributeSlots.Expand(item.AttributeSlots, path + ".attributeSlots", []),
                    ModSlots = modSlots.Expand(item.ModSlots, path + ".modSlots", []),
                    TalentSlots = talentSlots.Expand(item.TalentSlots, path + ".talentSlots", []),
                    Upgrade = upgrades.Expand(item.Upgrade, path + ".upgrade", null)
                });
            }
            attributeSlots.ReportUnused();
            modSlots.ReportUnused();
            talentSlots.ReportUnused();
            upgrades.ReportUnused();
            return items.MoveToImmutable();
        }

        /// <summary>Expands each named template once; items that share it share the immutable result.</summary>
        private sealed class Templates<TWire, TDomain>(Mapper mapper, string kind, Dictionary<string, TWire>? definitions, Func<TWire, TDomain> map)
        {
            private readonly Dictionary<string, TDomain> expanded = new(StringComparer.Ordinal);
            private readonly HashSet<string> used = new(StringComparer.Ordinal);

            public TDomain Expand(string? name, string path, TDomain none)
            {
                if (name is null) { return none; }
                used.Add(name);
                if (expanded.TryGetValue(name, out var value)) { return value; }
                if (definitions is null || !definitions.TryGetValue(name, out var definition))
                { mapper.Error("unknown-template", path, $"Unknown {kind} template {ReferenceText.Quote(name)}"); return none; }
                return expanded[name] = map(definition);
            }

            public void ReportUnused()
            {
                foreach (var name in (definitions?.Keys ?? Enumerable.Empty<string>()))
                {
                    if (!PackIds.IsSlug(name)) { mapper.Error("invalid-id", $"$.templates.{kind}", $"Template name {ReferenceText.Quote(name)} must be lower_snake_case starting with a letter"); }
                    else if (!used.Contains(name))
                    { mapper.Warning("unused-template", $"$.templates.{kind}.{name}", $"Template {ReferenceText.Quote(name)} is not used by any item"); }
                }
            }
        }

        private static TalentDefinition Talent(TalentWire talent) => new()
        {
            ID = talent.Id,
            Name = talent.Name,
            Description = talent.Description ?? "",
            Modifiers = Modifiers(talent.Modifiers),
            ModeledBySets = Strings(talent.ModeledBySets),
            SetPieceCredit = talent.SetPieceCredit ?? 0,
            CopiesTalent = talent.CopiesTalent is { } copy ? new TalentCopyDefinition
            {
                From = copy.From,
                To = copy.To,
                SameTagPrefix = copy.SameTagPrefix ?? "",
                ExcludeTags = Strings(copy.ExcludeTags),
                ExcludeTalents = Strings(copy.ExcludeTalents)
            } : null,
            AllyEffects = [.. List(talent.AllyEffects).Select(effect => new AllyEffect
            { ID = effect.Id, Stack = effect.Stack, Name = effect.Name, Description = effect.Description ?? "", Modifiers = Modifiers(effect.Modifiers) })],
            Coverage = talent.Coverage is { } coverage ? new TalentCoverage
            { Status = coverage.Status, Omits = Strings(coverage.Omits), Evidence = coverage.Evidence, SettleBy = coverage.SettleBy ?? "" } : null
        };

        /// <summary>A preview output's condition; an empty one would read as no condition, so it must be left out instead.</summary>
        private string When(string? when, string path)
        {
            if (when is { Length: 0 }) { Error("empty-when", path, "An empty when reads as no condition; leave when out instead"); }
            return when ?? "";
        }

        private ActiveSkillDefinition Skill(SkillWire skill, int index) => new()
        {
            ID = skill.Id,
            Name = skill.Name,
            Description = skill.Description ?? "",
            Family = skill.Family ?? "",
            Modifiers = Modifiers(skill.Modifiers),
            Upgrade = skill.Upgrade is { } upgrade ? new SkillUpgradeDefinition { Name = upgrade.Name, Description = upgrade.Description ?? "", MaxRank = upgrade.MaxRank } : null,
            ModSlots = [.. List(skill.ModSlots).Select(slot => new ActiveSkillModSlotDefinition
            {
                ID = slot.Id, Name = slot.Name,
                Choices = [.. slot.Choices.Select(choice => new ActiveSkillModDefinition
                {
                    ID = choice.Id, Name = choice.Name, Description = choice.Description ?? "", Input = choice.Input, Min = choice.Min, Max = choice.Max,
                    Step = choice.Step, Format = choice.Format ?? StatFormats.Decimal, StatName = choice.StatName ?? ""
                })]
            })],
            PreviewOutputs = [.. List(skill.PreviewOutputs).Select((output, outputIndex) => new ActiveSkillPreviewOutputDefinition
            {
                ID = output.Id, Name = output.Name, Description = output.Description ?? "", Format = output.Format ?? StatFormats.Decimal,
                Formula = output.Formula, When = When(output.When, $"$.skills[{index}].previewOutputs[{outputIndex}].when"), Kind = output.Kind ?? "", Headline = output.Headline ?? false
            })]
        };

        private static SpecializationDefinition Specialization(SpecializationWire specialization) => new()
        {
            ID = specialization.Id,
            Name = specialization.Name,
            Description = specialization.Description ?? "",
            Modifiers = Modifiers(specialization.Modifiers),
            Budgets = [.. List(specialization.Budgets).Select(budget => new SpecializationBudgetDefinition
            { ID = budget.Id, Name = budget.Name, Points = budget.Points, ParentID = budget.ParentId ?? "" })],
            Nodes = [.. List(specialization.Nodes).Select(node => new SpecializationNodeDefinition
            {
                ID = node.Id, Name = node.Name, Description = node.Description ?? "", BudgetID = node.BudgetId, Cost = node.Cost ?? 0, Tier = node.Tier ?? 0,
                Prerequisites = Strings(node.Prerequisites), Modifiers = Modifiers(node.Modifiers)
            })]
        };

        private static SimulationRules Simulation(SimulationWire simulation) => new()
        {
            Triggers = [.. List(simulation.Triggers).Select(trigger => new SimTriggerDefinition
            {
                Talent = trigger.Talent, Key = trigger.Key, On = trigger.On ?? "", Kind = trigger.Kind, Duration = trigger.Duration ?? 0, Max = trigger.Max ?? 0,
                Refresh = trigger.Refresh ?? false, Every = trigger.Every ?? 0, EveryGroup = trigger.EveryGroup ?? "", Chance = trigger.Chance ?? 1, RequiresContext = trigger.Requires?.ToImmutableDictionary(StringComparer.Ordinal), CooldownFromEnd = trigger.CooldownFromEnd ?? false, ResetOn = trigger.ResetOn ?? "", AlsoResetOn = trigger.AlsoResetOn ?? "", ConsumeOn = trigger.ConsumeOn ?? "",
                Phases = [.. List(trigger.Phases).Select(phase => new SimPhase { Until = phase.Until, Value = phase.Value })],
                Percent = trigger.Percent ?? false, Within = trigger.Within ?? 0, Count = trigger.Count ?? 0, Refill = trigger.Refill ?? false, Gain = trigger.Gain ?? 0,
                Also = [.. List(trigger.Also).Select(gain => new SimGain { On = gain.On, Gain = gain.Gain ?? 0, Count = gain.Count ?? 0 })],
                Decay = trigger.Decay ?? 0, AtLeast = trigger.AtLeast ?? 0, Purge = trigger.Purge ?? false, Status = trigger.Status ?? "", ApplyOnHit = trigger.ApplyOnHit ?? false, GainWhileHolstered = trigger.GainWhileHolstered ?? 0,
                WhileStatus = trigger.WhileStatus ?? "", Pool = trigger.Pool ?? "", PoolMax = trigger.PoolMax ?? 0,
                CancelOn = trigger.CancelOn ?? "", CancelFor = trigger.CancelFor ?? 0, Bank = trigger.Bank ?? "",
                ClearGrant = trigger.ClearGrant ?? "", ClearAmount = trigger.ClearAmount ?? 0, ClearDuration = trigger.ClearDuration ?? 0,
                WeaponTags = Strings(trigger.WeaponTags), ForceHeadshot = trigger.ForceHeadshot ?? false,
                Grant = trigger.Grant ?? "", GrantAmount = trigger.GrantAmount ?? 0, GrantDuration = trigger.GrantDuration ?? 0, GrantInstances = trigger.GrantInstances ?? "", GrantHoldDelay = trigger.GrantHoldDelay ?? 0, GrantAmountStat = trigger.GrantAmountStat ?? "", ActiveAfter = trigger.ActiveAfter ?? 0, CountWindow = trigger.CountWindow ?? 0,
                MaxMagazine = trigger.MaxMagazine ?? 0, WhileKey = trigger.WhileKey ?? "", WhileFull = trigger.WhileFull ?? false,
                WhileArmorBelow = trigger.WhileArmorBelow ?? 0, DurationStat = trigger.DurationStat ?? "",
                WhileStatAtLeast = trigger.WhileStatAtLeast is { } gate ? new SimStatGate { Stat = gate.Stat, Value = gate.Value } : null
            })],
            Skills = [.. List(simulation.Skills).Select(skill => new SimSkillDefinition
            {
                Skill = skill.Skill, Rate = skill.Rate ?? "", Window = skill.Window ?? "", Cooldown = skill.Cooldown ?? "", Impact = skill.Impact ?? "",
                CooldownFromEnd = skill.CooldownFromEnd ?? false, Impacts = skill.Impacts ?? "", ImpactInterval = skill.ImpactInterval ?? "", Shield = skill.Shield ?? "", Regeneration = skill.Regeneration ?? "",
                Weapons = Strings(skill.Weapons), ExcludeTags = Strings(skill.ExcludeTags), Status = skill.Status ?? "", RateArmorBonus = skill.RateArmorBonus ?? "", RateRead = skill.RateRead ?? "", Stock = skill.Stock ?? "", StockInterval = skill.StockInterval ?? 0, StatusRate = skill.StatusRate ?? "", StatusWindow = skill.StatusWindow ?? "", StatusArmorBonus = skill.StatusArmorBonus ?? ""
            })],
            StatusDamage = [.. List(simulation.StatusDamage).Select(damage => new SimStatusDamageDefinition
            {
                Status = damage.Status, Tick = damage.Tick, Period = damage.Period, TickOnApply = damage.TickOnApply ?? false, ArmorBonus = damage.ArmorBonus ?? ""
            })],
            TalentDamage = [.. List(simulation.TalentDamage).Select(damage => new SimTalentDamageDefinition
            {
                Talent = damage.Talent, On = damage.On, Total = damage.Total, Duration = damage.Duration, Period = damage.Period, MaxStacks = damage.MaxStacks ?? 1
            })],
            PassOns = [.. List(simulation.PassOns).Select(passOn => new SimPassOnDefinition { Talent = passOn.Talent, Within = passOn.Within ?? 0, WithinStat = passOn.WithinStat ?? "", Status = passOn.Status ?? "", Share = passOn.Share ?? 0, ShareStat = passOn.ShareStat ?? "", To = passOn.To ?? "" })],
            Spreads = [.. List(simulation.Spreads).Select(spread => new SimSpreadDefinition { Talent = spread.Talent, Within = spread.Within, Every = spread.Every ?? 0 })],
            Redirects = [.. List(simulation.Redirects).Select(redirect => new SimRedirectDefinition { Talent = redirect.Talent, Share = redirect.Share, Within = redirect.Within })],
            Traps = [.. List(simulation.Traps).Select(trap => new SimTrapDefinition
            {
                Talent = trap.Talent, TagAfter = trap.TagAfter, MaxTags = trap.MaxTags, Status = trap.Status, StatusDuration = trap.StatusDuration,
                Every = trap.Every ?? 0, Cut = trap.Cut ?? 0
            })],
            Blasts = [.. List(simulation.Blasts).Select(blast => new SimBlastDefinition { Talent = blast.Talent, On = blast.On, Amount = blast.Amount ?? "", Mark = blast.Mark ?? "", Within = blast.Within, Status = blast.Status ?? "", StatusDuration = blast.StatusDuration ?? 0 })],
            DistanceDefaults = [.. List(simulation.DistanceDefaults).Select(entry => new SimDistanceDefault
            {
                Input = entry.Input, Bands = [.. entry.Bands.Select(band => new SimDistanceBand { UpTo = band.UpTo, Value = band.Value })]
            })],
            ArmorKit = simulation.ArmorKit is { } kit ? new SimArmorKit
            {
                Repair = kit.Repair,
                UseSeconds = kit.UseSeconds,
                ExtendSeconds = kit.ExtendSeconds,
                MaxUseSeconds = kit.MaxUseSeconds,
                ReplacedBySpecializations = Strings(kit.ReplacedBySpecializations),
                ReplacedByTalents = Strings(kit.ReplacedByTalents)
            } : null
        };

        private static PackPresentation Presentation(PresentationWire presentation)
        {
            var icons = ImmutableDictionary.CreateBuilder<string, ImmutableDictionary<string, string>>(StringComparer.Ordinal);
            void Group(string name, Dictionary<string, string>? values)
            // An empty group carries nothing, and the writer omits it, so the reader does too.
            { if (values is { Count: > 0 }) { icons[name] = values.ToImmutableDictionary(StringComparer.Ordinal); } }
            if (presentation.Icons is { } source)
            {
                Group(PresentationIcons.Slots, source.Slots);
                Group(PresentationIcons.Talents, source.Talents);
                Group(PresentationIcons.Skills, source.Skills);
                Group(PresentationIcons.Specializations, source.Specializations);
                Group(PresentationIcons.Nodes, source.Nodes);
            }
            return new()
            {
                Theme = presentation.Theme is { } theme ? new Theme
                {
                    Primary = theme.Primary ?? "",
                    Background = theme.Background ?? "",
                    Variant = theme.Variant ?? "",
                    Colors = (theme.Colors ?? new()).ToImmutableDictionary(StringComparer.Ordinal)
                } : null,
                Layout = presentation.Layout is { } layout ? new PackLayout
                {
                    SlotGroups = [.. List(layout.SlotGroups).Select(group => new LayoutSlotGroup
                    {
                        Name = group.Name, Slots = [.. group.Slots], CountAttributes = group.CountAttributes ?? false,
                        Columns = group.Columns ?? 0, CompareStats = Strings(group.CompareStats)
                    })],
                    Headline = [.. List(layout.Headline).Select(entry => new LayoutHeadlineStat
                    {
                        Stat = entry.Stat ?? "", BuildOutput = entry.BuildOutput ?? "", SkillRates = entry.SkillRates ?? false, Label = entry.Label ?? "",
                        Conditional = entry.Conditional ?? false, Unavailable = entry.Unavailable ?? "", LowerIsBetter = entry.LowerIsBetter ?? false
                    })],
                    Summary = [.. List(layout.Summary).Select(group => new LayoutSummaryGroup
                    { Name = group.Name, Stats = Strings(group.Stats), Conditional = Strings(group.Conditional) })],
                    AssumptionSections = [.. List(layout.AssumptionSections).Select(section => new LayoutAssumptionSection(section.Name, section.Default ?? false))],
                    AttributeCategories = [.. List(layout.AttributeCategories).Select(category => new LayoutAttributeCategory(category.Id, category.Name, category.Color))],
                    Rarities = [.. List(layout.Rarities).Select(rarity => new LayoutRarity
                    { ID = rarity.Id, Name = rarity.Name, Color = rarity.Color, Tags = Strings(rarity.Tags), TagPrefixes = Strings(rarity.TagPrefixes) })],
                    Prototype = layout.Prototype is { } prototype ? new LayoutPrototype { Name = prototype.Name, Color = prototype.Color } : null
                } : null,
                Icons = icons.ToImmutable(),
                SpecializationImages = (presentation.SpecializationImages ?? new()).ToImmutableDictionary(StringComparer.Ordinal),
                ItemArt = (presentation.ItemArt ?? new()).ToImmutableDictionary(pair => pair.Key, pair => new ItemArtwork(pair.Value.Image ?? "", pair.Value.Icon ?? ""), StringComparer.Ordinal),
                ArtworkSources = [.. List(presentation.Artwork?.Sources).Select(source => new ArtworkSource
                {
                    ID = source.Id, Name = source.Name, URL = source.Url ?? "", Attribution = source.Attribution ?? "",
                    License = source.License ?? "", Notes = source.Notes ?? ""
                })],
                ArtworkAssets = [.. List(presentation.Artwork?.Assets).Select(asset => new ArtworkAsset
                { ID = asset.Id, Path = asset.Path, Kind = asset.Kind, SHA256 = asset.Sha256, SourceID = asset.SourceId })]
            };
        }
    }
}

/// <summary>The icon groups of presentation.json, keyed by the object kind whose ids they map.</summary>
public static class PresentationIcons
{
    public const string Slots = "slots";
    public const string Talents = "talents";
    public const string Skills = "skills";
    public const string Specializations = "specializations";
    public const string Nodes = "nodes";

    public static ImmutableArray<string> Groups { get; } = [Slots, Talents, Skills, Specializations, Nodes];
}
