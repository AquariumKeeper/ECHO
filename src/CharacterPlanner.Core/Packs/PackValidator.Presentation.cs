using System.Collections.Immutable;
using System.Text.RegularExpressions;
using CharacterPlanner.Core.Diagnostics;

namespace CharacterPlanner.Core.Packs;

public static partial class PackValidator
{
    [GeneratedRegex(@"\A#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex ColorPattern();

    private sealed partial class Checker
    {
        private static readonly HashSet<string> ThemeColors = ["background", "button", "disabledButton", "disabled", "error", "focus",
            "foreground", "foregroundOnError", "foregroundOnPrimary", "foregroundOnSuccess", "foregroundOnWarning", "headerBackground", "hover",
            "hyperlink", "inputBackground", "inputBorder", "menuBackground", "overlayBackground", "placeholder", "pressed", "primary", "scrollBar",
            "scrollBarBackground", "selection", "separator", "shadow", "success", "warning"];

        private void Color(string value, string path, string owner)
        { if (!ColorPattern().IsMatch(value)) { Error("bad-color", path, $"{owner} color {Q(value)} is not #RRGGBB or #RRGGBBAA"); } }

        public void Presentation()
        {
            var presentation = pack.Presentation;
            Layout(presentation.Layout);
            Theme(presentation.Theme);
            Artwork(presentation);
        }

        private void Layout(PackLayout? layout)
        {
            const string root = "presentation.layout";
            var categories = new HashSet<string>(StringComparer.Ordinal);
            var sections = new HashSet<string>(StringComparer.Ordinal);
            if (layout is not null)
            {
                var placed = new HashSet<string>(StringComparer.Ordinal);
                for (var g = 0; g < layout.SlotGroups.Length; g++)
                {
                    var group = layout.SlotGroups[g];
                    var path = $"{root}.slotGroups[{g}]";
                    Name(group.Name, path);
                    Integer(group.Columns, path + ".columns", "Columns");
                    for (var i = 0; i < group.CompareStats.Length; i++) { Stat(group.CompareStats[i], $"{path}.compareStats[{i}]"); }
                    for (var i = 0; i < group.Slots.Length; i++)
                    {
                        var slot = group.Slots[i];
                        if (!slots.Contains(slot)) { Error("unknown-slot", $"{path}.slots[{i}]", $"Unknown slot {Q(slot)}"); }
                        else if (!placed.Add(slot)) { Error("duplicate-layout-slot", $"{path}.slots[{i}]", $"Slot {Q(slot)} appears in more than one slot group"); }
                    }
                }
                var headline = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < layout.Headline.Length; i++)
                {
                    var entry = layout.Headline[i];
                    var path = $"{root}.headline[{i}]";
                    var kinds = (entry.Stat.Length > 0 ? 1 : 0) + (entry.BuildOutput.Length > 0 ? 1 : 0) + (entry.SkillRates ? 1 : 0);
                    if (kinds != 1) { Error("invalid-layout-headline", path, "A headline entry names exactly one of stat, buildOutput or skillRates"); continue; }
                    var key = "stat:" + entry.Stat;
                    if (entry.BuildOutput.Length > 0)
                    {
                        key = "buildOutput:" + entry.BuildOutput;
                        if (!pack.BuildOutputs.Any(output => output.ID == entry.BuildOutput)) { Error("unknown-build-output", path + ".buildOutput", $"Unknown build output {Q(entry.BuildOutput)}"); }
                    }
                    else if (entry.SkillRates) { key = "skillRates"; }
                    else { Stat(entry.Stat, path + ".stat"); }
                    if (!headline.Add(key)) { Error("duplicate-layout-entry", path, $"The headline lists {Q(key)} more than once"); }
                    if (!entry.SkillRates && string.IsNullOrWhiteSpace(entry.Label)) { Error("empty-name", path + ".label", "Headline entries need a label"); }
                }
                for (var g = 0; g < layout.Summary.Length; g++)
                {
                    var group = layout.Summary[g];
                    var path = $"{root}.summary[{g}]";
                    Name(group.Name, path);
                    var listed = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var (name, values) in new[] { ("stats", group.Stats), ("conditional", group.Conditional) })
                    {
                        for (var i = 0; i < values.Length; i++)
                        {
                            if (!stats.Contains(values[i])) { Stat(values[i], $"{path}.{name}[{i}]"); }
                            else if (!listed.Add(values[i])) { Error("duplicate-layout-entry", $"{path}.{name}[{i}]", $"Summary group {Q(group.Name)} lists {Q(values[i])} more than once"); }
                        }
                    }
                }
                for (var i = 0; i < layout.AssumptionSections.Length; i++)
                {
                    var section = layout.AssumptionSections[i];
                    var path = $"{root}.assumptionSections[{i}]";
                    if (string.IsNullOrWhiteSpace(section.Name)) { Error("empty-name", path + ".name", "Names must not be blank"); }
                    else if (!sections.Add(section.Name)) { Error("duplicate-id", path + ".name", $"Assumption section {Q(section.Name)} is declared more than once"); }
                }
                if (layout.AssumptionSections.Length > 0 && layout.AssumptionSections.Count(section => section.Default) != 1)
                { Error("invalid-assumption-sections", root + ".assumptionSections", "Exactly one assumption section must be the default"); }
                for (var i = 0; i < layout.AttributeCategories.Length; i++)
                {
                    var category = layout.AttributeCategories[i];
                    var path = $"{root}.attributeCategories[{i}]";
                    Id(category.ID, path, "Attribute category");
                    Name(category.Name, path);
                    if (!categories.Add(category.ID)) { Error("duplicate-id", path, $"Duplicate attribute category id {Q(category.ID)}"); }
                    Color(category.Color, path + ".color", "Attribute category");
                }
                var rarities = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < layout.Rarities.Length; i++)
                {
                    var rarity = layout.Rarities[i];
                    var path = $"{root}.rarities[{i}]";
                    Id(rarity.ID, path, "Rarity");
                    Name(rarity.Name, path);
                    if (!rarities.Add(rarity.ID)) { Error("duplicate-id", path, $"Duplicate rarity id {Q(rarity.ID)}"); }
                    Color(rarity.Color, path + ".color", "Rarity");
                }
            }
            // Sections are checked even without a layout: a pack with no layout declares none.
            foreach (var selector in pack.Selectors.Where(selector => selector.Section.Length > 0 && !sections.Contains(selector.Section)))
            { Error("unknown-assumption-section", $"selectors.{selector.ID}.section", $"Assumption section {Q(selector.Section)} is not declared in the layout"); }
            foreach (var input in pack.NumericContextInputs.Where(input => input.Section.Length > 0 && !sections.Contains(input.Section)))
            { Error("unknown-assumption-section", $"numericInputs.{input.ID}.section", $"Assumption section {Q(input.Section)} is not declared in the layout"); }
            foreach (var attribute in pack.Attributes.Where(attribute => attribute.Category.Length > 0 && !categories.Contains(attribute.Category)))
            { Error("unknown-attribute-category", $"attributes.{attribute.ID}.category", $"Attribute category {Q(attribute.Category)} is not declared in the layout"); }
        }

        private void Theme(Theme? theme)
        {
            if (theme is null) { return; }
            const string root = "presentation.theme";
            if (theme.Variant is not ("" or "light" or "dark")) { Error("bad-theme-variant", root + ".variant", $"Theme variant {Q(theme.Variant)} is not light or dark"); }
            if (theme.Primary.Length > 0) { Color(theme.Primary, root + ".primary", "Theme"); }
            if (theme.Background.Length > 0) { Color(theme.Background, root + ".background", "Theme"); }
            foreach (var (name, color) in theme.Colors.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!ThemeColors.Contains(name)) { Error("unknown-theme-color", $"{root}.colors.{name}", $"Unknown theme color name {Q(name)}"); }
                Color(color, $"{root}.colors.{name}", "Theme");
            }
        }

        private void Artwork(PackPresentation presentation)
        {
            const string root = "presentation.artwork";
            var sources = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < presentation.ArtworkSources.Length; i++)
            {
                var source = presentation.ArtworkSources[i];
                var path = $"{root}.sources[{i}]";
                Id(source.ID, path, "Artwork source");
                Name(source.Name, path);
                if (!sources.Add(source.ID)) { Error("duplicate-id", path, $"Duplicate artwork source id {Q(source.ID)}"); }
            }
            var assets = new Dictionary<string, ArtworkAsset>(StringComparer.Ordinal);
            for (var i = 0; i < presentation.ArtworkAssets.Length; i++)
            {
                var asset = presentation.ArtworkAssets[i];
                var path = $"{root}.assets[{i}]";
                Id(asset.ID, path, "Artwork asset");
                if (!assets.TryAdd(asset.ID, asset)) { Error("duplicate-id", path, $"Duplicate artwork asset id {Q(asset.ID)}"); }
                if (!IsAssetPath(asset.Path)) { Error("bad-artwork-path", path + ".path", $"Artwork path {Q(asset.Path)} must be a relative path under assets/"); }
                if (asset.Kind is not ("image" or "icon")) { Error("bad-artwork-kind", path + ".kind", $"Artwork kind {Q(asset.Kind)} is not image or icon"); }
                if (!PackFiles.IsSha256(asset.SHA256)) { Error("bad-artwork-checksum", path + ".sha256", "Artwork hashes must be 64 lowercase hex digits"); }
                Reference(asset.SourceID, sources, "unknown-artwork-source", path + ".sourceId", "artwork source");
            }
            void Asset(string id, string path)
            { if (id.Length > 0 && !assets.ContainsKey(id)) { Error("unknown-artwork-asset", path, $"Unknown artwork asset {Q(id)}"); } }
            var owners = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
            {
                [PresentationIcons.Slots] = slots,
                [PresentationIcons.Talents] = pack.Talents.Select(talent => talent.ID).ToHashSet(StringComparer.Ordinal),
                [PresentationIcons.Skills] = pack.ActiveSkills.Select(skill => skill.ID).ToHashSet(StringComparer.Ordinal),
                [PresentationIcons.Specializations] = pack.Specializations.Select(specialization => specialization.ID).ToHashSet(StringComparer.Ordinal),
                [PresentationIcons.Nodes] = pack.Specializations.SelectMany(specialization => specialization.Nodes).Select(node => node.ID).ToHashSet(StringComparer.Ordinal)
            };
            foreach (var (group, icons) in presentation.Icons.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var path = "presentation.icons." + group;
                if (!owners.TryGetValue(group, out var known)) { Error("unknown-icon-group", path, $"Unknown icon group {Q(group)}"); continue; }
                foreach (var (id, asset) in icons.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    Reference(id, known, "unknown-icon-owner", $"{path}.{id}", group.TrimEnd('s'));
                    Asset(asset, $"{path}.{id}");
                }
            }
            foreach (var (id, asset) in presentation.SpecializationImages.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                Reference(id, owners[PresentationIcons.Specializations], "unknown-icon-owner", $"presentation.specializationImages.{id}", "specialization");
                Asset(asset, $"presentation.specializationImages.{id}");
            }
            var items = pack.Items.Select(item => item.ID).ToHashSet(StringComparer.Ordinal);
            foreach (var (id, art) in presentation.ItemArt.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                Reference(id, items, "unknown-icon-owner", $"presentation.itemArt.{id}", "item");
                Asset(art.Image, $"presentation.itemArt.{id}.image");
                Asset(art.Icon, $"presentation.itemArt.{id}.icon");
            }
        }

        // A slash-separated pack path, independent of the host OS.
        private static bool IsAssetPath(string path) =>
            path.StartsWith(PackFiles.AssetsDirectory + "/", StringComparison.Ordinal) && !path.Contains('\\') && !path.Contains(':') &&
            path.Split('/').All(part => part.Length > 0 && part is not ("." or ".."));

        public void ShareCodes()
        {
            var order = pack.LegacyShareOrder;
            const string root = "shareCodes.legacyOrder";
            var renamedSlots = pack.RenamedIds.GetValueOrDefault("slot") ?? ImmutableDictionary<string, string>.Empty;
            for (var i = 0; i < order.Slots.Length; i++)
            {
                var slot = order.Slots[i];
                // A legacy slot may have been renamed since; it must still resolve to a current slot.
                if (!slots.Contains(renamedSlots.GetValueOrDefault(slot, slot))) { Error("unknown-slot", $"{root}.slots[{i}]", $"Unknown slot {Q(slot)}"); }
            }
            foreach (var (name, values) in new[] { ("slots", order.Slots), ("stats", order.Stats), ("scenarios", order.Scenarios), ("passiveSkills", order.PassiveSkills) })
            {
                Unique(values, "legacy " + name, $"{root}.{name}");
                for (var i = 0; i < values.Length; i++) { Id(values[i], $"{root}.{name}[{i}]", "Legacy"); }
            }
        }
    }
}
