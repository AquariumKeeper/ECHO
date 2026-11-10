using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>
/// How a Prototype item looks on its tile and in the inspector: the pack's Prototype colour (<c>layout.prototype</c>, the
/// game's loot quality colour, R74) in place of its rarity's, with circuit traces across the card. A pack without one
/// gets the app's own purple.
/// </summary>
public static class PrototypeStyle
{
    /// <summary>The app's Prototype purple, for a pack that names no Prototype colour.</summary>
    public const string Fallback = "#A35CFF";

    /// <summary>A pack's Prototype colour, or the app's own when it has none.</summary>
    public static string Color(GamePack pack) => pack.Presentation.Layout?.Prototype?.Color is { Length: > 0 } color ? color : Fallback;

    /// <summary>The accent for an item: the Prototype colour when it's Prototype, otherwise its rarity's colour.</summary>
    public static string Accent(bool prototype, string rarityColor, string prototypeColor) => prototype ? prototypeColor : rarityColor;
}
