using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Desktop.Themes;

/// <summary>
/// Applies a pack's theme (presentation.json) to the app's colour tokens, as the Go app's pack theme did. Its colours
/// are named as Fyne's: primary, background, and keys such as inputBackground, headerBackground and warning (the gold
/// of values). A colour the pack doesn't give, or gives unreadably, keeps the default.
/// </summary>
public static class PackTheme
{
    /// <summary>Each token and the theme colour it takes.</summary>
    private static readonly (string Token, string Color)[] Tokens =
    [
        ("DivisionBackground", "background"),
        ("DivisionSurface", "inputBackground"),
        ("DivisionRaised", "button"),
        ("DivisionBorder", "inputBorder"),
        ("DivisionHeader", "headerBackground"),
        ("DivisionAccent", "primary"),
        ("DivisionText", "foreground"),
        ("DivisionMuted", "disabled"),
        ("DivisionPlaceholder", "placeholder"),
        ("DivisionValue", "warning"),
        ("DivisionError", "error"),
        ("DivisionGain", "success"),
    ];

    public static void Apply(Avalonia.Application application, Theme? theme)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (theme is null) { return; }
        foreach (var (token, name) in Tokens)
        {
            if (Color(theme, name) is { } color) { application.Resources[token] = new SolidColorBrush(color); }
        }
        // Fluent's own controls (focus, selection, checked buttons) follow the pack's primary colour, shaded by Fluent.
        if (Color(theme, "primary") is { } accent)
        {
            foreach (var fluent in application.Styles.OfType<FluentTheme>())
            {
                foreach (var palette in fluent.Palettes.Values) { palette.Accent = accent; }
            }
        }
    }

    private static Color? Color(Theme theme, string name)
    {
        var text = name switch
        {
            "primary" when theme.Primary.Length > 0 => theme.Primary,
            "background" when theme.Background.Length > 0 => theme.Background,
            _ => theme.Colors.GetValueOrDefault(name, "")
        };
        return text.Length > 0 && Avalonia.Media.Color.TryParse(Fyne(text), out var color) ? color : null;
    }

    /// <summary>Fyne's #RRGGBBAA as Avalonia's #AARRGGBB.</summary>
    private static string Fyne(string hex) => hex.Length == 9 && hex[0] == '#' ? "#" + hex[7..] + hex[1..7] : hex;
}
