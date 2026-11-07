using System.Collections.Frozen;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Media;
using CharacterPlanner.Desktop.ViewModels;

namespace CharacterPlanner.Desktop.Views;

/// <summary>
/// The app's own symbols, drawn on a 24-unit grid: one per equipment slot and one per kind of choice (talent, skill,
/// specialization, node, account upgrades). They stand in wherever a pack's image is missing, which is always so in a release: the
/// release ships no game art. They are original to this project and share its licence.
/// </summary>
public static class Glyphs
{
    // F0 fills even-odd, so inner shapes cut holes; F1 fills non-zero, so overlapping shapes join.
    private static readonly FrozenDictionary<string, string> Paths = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["mask"] = "F0 M12 2.5 C16.5 2.5 19.5 5 19.5 9.5 L19 14 C18.5 18 15.5 21.5 12 21.5 C8.5 21.5 5.5 18 5 14 L4.5 9.5 C4.5 5 7.5 2.5 12 2.5 Z "
            + "M7.5 8.5 L10.8 8.5 L10.8 11.5 L7.5 11.5 Z M13.2 8.5 L16.5 8.5 L16.5 11.5 L13.2 11.5 Z M10 15 L14 15 L14 18.5 L10 18.5 Z",
        ["backpack"] = "F0 M9 2.5 L15 2.5 L15 5 L17 5 C18.7 5 20 6.3 20 8 L20 19.5 C20 20.6 19.1 21.5 18 21.5 L6 21.5 C4.9 21.5 4 20.6 4 19.5 L4 8 C4 6.3 5.3 5 7 5 L9 5 Z "
            + "M10.5 3.8 L13.5 3.8 L13.5 5 L10.5 5 Z M7 9 L17 9 L17 10.5 L7 10.5 Z M8 13.5 L16 13.5 L16 19 L8 19 Z",
        ["chest"] = "F0 M7 2.5 L10 2.5 C10.5 4.5 13.5 4.5 14 2.5 L17 2.5 L17 6 L20 8 L20 21.5 L4 21.5 L4 8 L7 6 Z "
            + "M8 10 L16 10 L16 12 L8 12 Z M8 14 L11.2 14 L11.2 18.5 L8 18.5 Z M12.8 14 L16 14 L16 18.5 L12.8 18.5 Z",
        ["gloves"] = "F1 M6 10 L6 5.5 C6 4.7 6.6 4 7.4 4 C8.2 4 8.8 4.7 8.8 5.5 L8.8 10 Z M9.4 10 L9.4 3.5 C9.4 2.7 10 2 10.8 2 C11.6 2 12.2 2.7 12.2 3.5 L12.2 10 Z "
            + "M12.8 10 L12.8 4 C12.8 3.2 13.4 2.5 14.2 2.5 C15 2.5 15.6 3.2 15.6 4 L15.6 10 Z M16.2 10 L16.2 6 C16.2 5.2 16.8 4.5 17.6 4.5 C18.4 4.5 19 5.2 19 6 L19 10 Z "
            + "M6 9.5 L19 9.5 L19 13.5 L21.2 11.2 C21.8 10.6 22.7 10.7 23 11.4 C23.3 12 23.1 12.5 22.7 12.9 L18.5 17.6 L18.5 21.5 L7.5 21.5 L6 16.5 Z",
        ["holster"] = "F0 M6 2.5 L18 2.5 L18 6.5 L16.5 6.5 L16.5 17 L12 21.5 L7.5 17 L7.5 6.5 L6 6.5 Z M8 4 L16 4 L16 5 L8 5 Z M10 9 L14 9 L14 10.5 L10 10.5 Z",
        ["knees"] = "F0 M6.5 2.5 L17.5 2.5 L17.5 9.5 C17.5 12.5 15 15 12 15 C9 15 6.5 12.5 6.5 9.5 Z "
            + "M9.5 5.5 L14.5 5.5 L14.5 9.5 C14.5 10.9 13.4 12 12 12 C10.6 12 9.5 10.9 9.5 9.5 Z M5 17 L19 17 L19 18.5 L5 18.5 Z M5 20 L19 20 L19 21.5 L5 21.5 Z",
        ["primary_weapon"] = "F1 M1.5 9 L4 8.5 L6 9.5 L15 9.5 L15 8.5 L16.5 8.5 L16.5 9.5 L22.5 9.5 L22.5 11 L16.5 11 L16 12.5 L13 12.5 L12.5 16.5 L10 16.5 "
            + "L10.8 12.5 L9 12.5 L8.5 14.5 L6.8 14.5 L7 12.5 L4 12.5 L1.5 14 Z",
        ["secondary_weapon"] = "F1 M3 9.5 L13 9.5 L13 8.5 L14.5 8.5 L14.5 9.5 L21.5 9.5 L21.5 11 L19.5 11 L19.5 12.5 L15 12.5 L15 11 L12 11 L11.5 12.5 L9.2 12.5 "
            + "L8.5 16 L6.5 16 L7 12.5 L5 12.5 L3 15 L2 15 L2.5 11 Z",
        ["sidearm"] = "F1 M4.5 6.5 L19.5 6.5 L19.5 10.5 L12.5 10.5 L12 12 L10.5 12 L9.8 18 L5.5 18 L7 10.5 L4.5 10.5 Z",
        [ArtGlyphs.Talent] = "F0 M12 2 L14.2 9.8 L22 12 L14.2 14.2 L12 22 L9.8 14.2 L2 12 L9.8 9.8 Z M12 10 L14 12 L12 14 L10 12 Z",
        [ArtGlyphs.Skill] = "F0 M12 2 L20.7 7 L20.7 17 L12 22 L3.3 17 L3.3 7 Z M12 7 L16.5 12 L12 17 L7.5 12 Z",
        [ArtGlyphs.Specialization] = "F0 M12 2 L20 5 L20 11 C20 16 16.5 19.8 12 22 C7.5 19.8 4 16 4 11 L4 5 Z M8 9 L12 12 L16 9 L16 11.5 L12 14.5 L8 11.5 Z",
        [ArtGlyphs.Upgrades] = "F1 M12 2.5 L20 9.5 L16.5 9.5 L12 5.6 L7.5 9.5 L4 9.5 Z M12 9 L20 16 L16.5 16 L12 12.1 L7.5 16 L4 16 Z M7 18.5 L17 18.5 L17 21.5 L7 21.5 Z",
        [ArtGlyphs.Node] = "F0 M4 12 A8 8 0 1 0 20 12 A8 8 0 1 0 4 12 Z M7 12 A5 5 0 1 0 17 12 A5 5 0 1 0 7 12 Z M10 12 A2 2 0 1 0 14 12 A2 2 0 1 0 10 12 Z",
        [ArtGlyphs.Item] = "F0 M4 6.5 L12 2.5 L20 6.5 L20 17.5 L12 21.5 L4 17.5 Z M12 4.7 L7 7.2 L12 9.7 L17 7.2 Z",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, Geometry> Geometries =
        Paths.ToFrozenDictionary(pair => pair.Key, pair => (Geometry)StreamGeometry.Parse(pair.Value), StringComparer.Ordinal);

    /// <summary>The symbols' keys: slot ids, then the kinds of choice.</summary>
    public static IEnumerable<string> Keys => Paths.Keys;

    /// <summary>A key's symbol; an unknown slot gets the generic item symbol, and no key gets none.</summary>
    public static Geometry? Get(string? key) => key is { Length: > 0 } ? Geometries.GetValueOrDefault(key) ?? Geometries[ArtGlyphs.Item] : null;

    /// <summary>A shape that draws a symbol in the theme's muted colour, scaled to its box.</summary>
    internal static Path NewShape()
    {
        var shape = new Path { Stretch = Stretch.Uniform, IsVisible = false };
        shape.Bind(Shape.FillProperty, shape.GetResourceObservable("DivisionMuted"));
        return shape;
    }
}
