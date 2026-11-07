using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>
/// An item as the picker finds it: the item, its rarity, and its family, which is its brand or gear set, or for a
/// weapon its category. Filters are by ids; an empty id means any.
/// </summary>
public sealed record ItemEntry(ItemDefinition Item, LayoutRarity? Rarity, Choice? Family, SetDefinition? Set)
{
    /// <summary>The tag prefix of a weapon's category (assault rifle, SMG and so on), as the Go app read it.</summary>
    internal const string WeaponCategoryPrefix = "weapon_type_";

    /// <summary>Whether the item has the rarity and family, and every word of the text is in its name or its brand or set's name.</summary>
    public bool Matches(string text, string rarity, string family)
    {
        if (rarity.Length > 0 && Rarity?.ID != rarity) { return false; }
        if (family.Length > 0 && Family?.Id != family) { return false; }
        return TextSearch.MatchesAllWords(Set is { } set ? $"{Item.Name} {set.Name}" : Item.Name, text);
    }

    /// <summary>A weapon category's name: "SMG" and "LMG" stay capitals, others read as words.</summary>
    internal static string CategoryName(string tag)
    {
        var category = tag[WeaponCategoryPrefix.Length..];
        return category is "smg" or "lmg" ? category.ToUpperInvariant() : AssumptionViewModel.Humanize(category);
    }
}
