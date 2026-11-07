using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using CharacterPlanner.Core.Packs;
using CharacterPlanner.Desktop.Themes;

namespace CharacterPlanner.Desktop.Tests;

/// <summary>Fluent's accent is the pack's primary colour, with hover and press shades Fluent derives from it.</summary>
public sealed class ThemeTests
{
    private static Color Resource(string key) =>
        Avalonia.Application.Current!.TryGetResource(key, ThemeVariant.Dark, out var value) ? Assert.IsType<Color>(value) : throw new KeyNotFoundException(key);

    [AvaloniaFact]
    public void TheAccentIsThePacksPrimaryShadedByFluent()
    {
        var orange = Color.Parse("#FF7F00");
        Assert.Equal(orange, Resource("SystemAccentColor"));
        // The shades are the orange's, lighter and darker, not Fluent's default blue.
        var dark = Resource("SystemAccentColorDark1");
        var light = Resource("SystemAccentColorLight1");
        Assert.NotEqual(orange, dark);
        Assert.True(dark.R > dark.B && light.R > light.B);

        try
        {
            PackTheme.Apply(Avalonia.Application.Current!, new Theme { Primary = "#3366FFFF" });
            Assert.Equal(Color.Parse("#3366FF"), Resource("SystemAccentColor"));
        }
        finally { PackTheme.Apply(Avalonia.Application.Current!, new Theme { Primary = "#FF7F00FF" }); }
    }
}
