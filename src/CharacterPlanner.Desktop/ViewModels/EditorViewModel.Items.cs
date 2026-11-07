using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>How an item reads: its picker entry, its card, its brand bonuses, its attribute dots and how much of it is configured.</summary>
public sealed partial class EditorViewModel
{
    /// <summary>Each item's entry, worked out once.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ItemEntry> entries = new(StringComparer.Ordinal);

    /// <summary>An item as the pickers find it: its rarity and its brand or gear set, or else its weapon category.</summary>
    internal ItemEntry EntryOf(ItemDefinition item) => entries.GetOrAdd(item.ID, _ => Entry(item));

    private ItemEntry Entry(ItemDefinition item)
    {
        // The rarity is the first of the pack's rarities whose tags or tag prefixes the item carries.
        var rarity = rarities.FirstOrDefault(entry => item.Tags.Any(tag => entry.Tags.Contains(tag) || entry.TagPrefixes.Any(prefix => tag.StartsWith(prefix, StringComparison.Ordinal))));
        var set = Sets.GetValueOrDefault(item.ID);
        var category = item.Tags.FirstOrDefault(tag => tag.StartsWith(ItemEntry.WeaponCategoryPrefix, StringComparison.Ordinal) && tag.Length > ItemEntry.WeaponCategoryPrefix.Length);
        var family = set is not null ? new Choice(set.ID, set.Name) : category is not null ? new Choice(category, ItemEntry.CategoryName(category)) : null;
        return new(item, rarity, family, set);
    }

    /// <summary>
    /// The item's brand or gear set, as Go's brandBonusLines: "Set · N equipped (+K set credit)", then each tier with
    /// its fixed, unconditional effects, "Active · applied automatically" or "Inactive". Other sets aren't brand or gear sets.
    /// </summary>
    private ImmutableArray<string> BrandBonusLines(ResolvedBuild resolved, string itemId)
    {
        if (Sets.GetValueOrDefault(itemId) is not { } set || !(set.ID.StartsWith("brand_", StringComparison.Ordinal) || set.ID.StartsWith("gear_set_", StringComparison.Ordinal)))
        { return []; }
        var active = resolved.ActiveSets.FirstOrDefault(entry => entry.SetId == set.ID);
        var (pieces, bonus) = (active?.Pieces ?? 0, active?.BonusPieces ?? 0);
        var lines = ImmutableArray.CreateBuilder<string>();
        lines.Add($"{set.Name} · {pieces - bonus} equipped{(bonus > 0 ? $" (+{bonus} set credit)" : "")}");
        foreach (var tier in set.Bonuses)
        {
            // A multiplier reads as a percentage, whatever its stat's format.
            var effects = tier.Modifiers.Where(modifier => !modifier.Value.IsExpression && modifier.RequiresContext is null && modifier.When.Length == 0)
                .Select(modifier => Statistics.GetValueOrDefault(modifier.Stat) is { } stat
                    ? $"+{Formatting.Value(modifier.Op == ModifierOps.Mult ? StatFormats.Percent : stat.Format, modifier.Value.Number)} {stat.Name}" : null)
                .OfType<string>().ToList();
            if (effects.Count > 0) { lines.Add($"{tier.Pieces}-piece · {(pieces >= tier.Pieces ? "Active · applied automatically" : "Inactive")}: {string.Join(", ", effects)}"); }
        }
        return lines.ToImmutable();
    }

    /// <summary>
    /// The item's read-only card as the build would hold it, as Go's buildItemDetails: its upgrade's rank, its core and
    /// other attributes with roll bars, its talents with their descriptions, and its mods; an unconfigured slot reads
    /// how many choices it offers.
    /// </summary>
    private ImmutableArray<CardLine> ItemCard(BuildSnapshot build, string slotId, ItemDefinition item)
    {
        var lines = ImmutableArray.CreateBuilder<CardLine>();
        var coverage = EffectCoverage.Describe(Pack, build, session.Snapshot.State.Assumptions);
        if (item.Upgrade is { } upgrade)
        {
            var installed = build.EquipmentUpgrades.GetValueOrDefault(slotId);
            lines.Add(new(CardLineKind.Header, upgrade.Name));
            lines.Add(new(CardLineKind.Text, $"Rank {(installed is not null && installed.ItemId == item.ID ? installed.Rank : 0)} / {upgrade.MaxRank}"));
        }
        var prototype = PrototypeRules.IsPrototype(Pack, build, slotId, item);
        var core = new List<CardLine>();
        var other = new List<CardLine>();
        foreach (var slot in item.AttributeSlots)
        {
            // Grouped by the slot, as the inspector groups it.
            var isCore = !slot.Choices.IsEmpty && slot.Choices.All(choice => Attributes.GetValueOrDefault(choice.AttributeID)?.Core == true);
            var roll = build.AttributeRolls.GetValueOrDefault(slotId)?.GetValueOrDefault(slot.ID);
            var attribute = roll is null ? null : Attributes.GetValueOrDefault(roll.AttributeId);
            var choice = attribute is null ? null : slot.Choices.FirstOrDefault(entry => entry.AttributeID == attribute.ID);
            if (roll is null || attribute is null || choice is null)
            {
                (isCore ? core : other).Add(new(CardLineKind.Text, $"{slot.Name} — choose from {slot.Choices.Length}"));
                continue;
            }
            var bounds = PrototypeRules.RollBounds(attribute, choice, prototype);
            var fraction = bounds.Max > bounds.Min ? (roll.Value - bounds.Min) / (bounds.Max - bounds.Min) : 1;
            (isCore ? core : other).Add(new(CardLineKind.Roll, $"+{Formatting.Value(attribute.Format, roll.Value)} {attribute.Name}",
                Math.Clamp(fraction, 0, 1), categoryColors.GetValueOrDefault(attribute.Category, "")));
        }
        if (core.Count > 0) { lines.Add(new(CardLineKind.Header, "Core attributes")); lines.AddRange(core); }
        if (other.Count > 0) { lines.Add(new(CardLineKind.Header, "Attributes")); lines.AddRange(other); }
        if (!item.TalentSlots.IsEmpty)
        {
            lines.Add(new(CardLineKind.Header, "Talents"));
            foreach (var slot in item.TalentSlots)
            {
                if (Talents.GetValueOrDefault(build.TalentSelections.GetValueOrDefault(slotId)?.GetValueOrDefault(slot.ID) ?? "") is not { } talent)
                {
                    lines.Add(new(CardLineKind.Text, $"{slot.Name} — choose from {slot.Choices.Length}"));
                    continue;
                }
                lines.Add(new(CardLineKind.Talent, talent.Name, Icon: Artwork.Icon("talents", talent.ID)));
                if (talent.Description.Length > 0) { lines.Add(new(CardLineKind.Text, talent.Description)); }
                if (coverage.Talents.FirstOrDefault(row => row.Slot == slotId && row.Talent == talent.ID) is { } modeled)
                { lines.Add(new(CardLineKind.Caption, modeled.Description)); }
                // A selection the loadout doesn't apply (not this slot's choice) has no coverage row; say only what the pack holds.
                else if (talent.Modifiers.IsEmpty && talent.ModeledBySets.IsEmpty) { lines.Add(new(CardLineKind.Caption, "Description only; no calculated stat effect.")); }
            }
        }
        if (Sets.GetValueOrDefault(item.ID) is { } set)
        {
            foreach (var talent in coverage.Talents.Where(row => row.Slot.Length == 0 && row.Channels.Any(channel => channel.Kind == "set-bonuses" && channel.Sources.Contains(set.ID))))
            {
                lines.Add(new(CardLineKind.Header, talent.Name));
                lines.Add(new(CardLineKind.Caption, talent.Description));
            }
        }
        if (!item.ModSlots.IsEmpty)
        {
            lines.Add(new(CardLineKind.Header, "Mods"));
            foreach (var slot in item.ModSlots)
            {
                if (Mods.GetValueOrDefault(build.ModSelections.GetValueOrDefault(slotId)?.GetValueOrDefault(slot.ID) ?? "") is not { } mod)
                {
                    lines.Add(new(CardLineKind.Text, $"{slot.Name} — choose from {slot.Choices.Length}"));
                    continue;
                }
                var text = $"{slot.Name}: {mod.Name}";
                if (build.ModRolls.GetValueOrDefault(slotId)?.TryGetValue(slot.ID, out var value) == true && Attributes.GetValueOrDefault(mod.RollAttributeID) is { } rollAttribute)
                { text += " +" + Formatting.Value(rollAttribute.Format, value); }
                lines.Add(new(CardLineKind.Text, text));
            }
        }
        return lines.ToImmutable();
    }

    /// <summary>
    /// The colours of an item's attributes as it would be equipped, as a tile's or picker row's dots: the build's rolls
    /// for the item in the slot now, otherwise the attributes it always has (its single-choice slots).
    /// </summary>
    internal ImmutableArray<string> AttributeDots(BuildSnapshot build, string slotId, ItemDefinition item)
    {
        var equipped = build.Equipment.GetValueOrDefault(slotId, "") == item.ID;
        return [.. item.AttributeSlots
            .Select(slot => equipped ? build.AttributeRolls.GetValueOrDefault(slotId)?.GetValueOrDefault(slot.ID)?.AttributeId
                : slot.Choices.Length == 1 ? slot.Choices[0].AttributeID : null)
            .Select(id => Attributes.GetValueOrDefault(id ?? "")?.Category)
            .Select(category => categoryColors.GetValueOrDefault(category ?? ""))
            .OfType<string>()];
    }

    private static bool Configurable(ItemDefinition item) =>
        item.AttributeSlots.Length + item.ModSlots.Length + item.TalentSlots.Length > 0 || item.Upgrade is not null;

    /// <summary>How much of the item is configured, as Go's configSummary: "3/5 set", its upgrade rank, and Prototype.</summary>
    private string ConfigSummary(BuildSnapshot build, string slotId, ItemDefinition item)
    {
        var total = item.AttributeSlots.Length + item.ModSlots.Length + item.TalentSlots.Length;
        var set = item.AttributeSlots.Count(slot => build.AttributeRolls.GetValueOrDefault(slotId)?.ContainsKey(slot.ID) == true)
            + item.ModSlots.Count(slot =>
            {
                var selected = build.ModSelections.GetValueOrDefault(slotId)?.GetValueOrDefault(slot.ID) ?? "";
                var needsRoll = Mods.GetValueOrDefault(selected)?.RollAttributeID.Length > 0;
                return selected.Length > 0 && (!needsRoll || build.ModRolls.GetValueOrDefault(slotId)?.ContainsKey(slot.ID) == true);
            })
            + item.TalentSlots.Count(slot => (build.TalentSelections.GetValueOrDefault(slotId)?.GetValueOrDefault(slot.ID) ?? "").Length > 0);
        var summary = $"{set}/{total} set";
        if (item.Upgrade is { } upgrade)
        {
            var installed = build.EquipmentUpgrades.GetValueOrDefault(slotId);
            summary += $" · {upgrade.Name} {(installed is not null && installed.ItemId == item.ID ? installed.Rank : 0)}";
        }
        return PrototypeRules.IsPrototype(Pack, build, slotId, item) ? summary + " · Prototype" : summary;
    }
}
