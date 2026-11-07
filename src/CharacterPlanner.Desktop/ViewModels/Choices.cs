using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>
/// Where view-model updates run. Background results are posted here, so view models change on one thread only:
/// the UI thread in the app, the test's own thread in tests.
/// </summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>An option in a picker: an id the build stores and a name people read. The empty id clears.</summary>
public sealed record Choice(string Id, string Name)
{
    public static readonly Choice None = new("", "None");

    /// <summary>A stored id the pack no longer has, shown as itself so it isn't silently replaced, and never applied.</summary>
    public static Choice Unknown(string id) => new(id, $"Unknown ({id})") { IsUnknown = true };

    /// <summary>An id the pack doesn't know: shown so the build isn't misread, never chosen.</summary>
    public bool IsUnknown { get; init; }

    /// <summary>The choice a stored id stands for: None for "", the listed choice, or an Unknown one.</summary>
    public static Choice Of(ImmutableArray<Choice> choices, string id) => id.Length == 0 ? None : choices.FirstOrDefault(choice => choice.Id == id) ?? Unknown(id);

    public override string ToString() => Name;
}

/// <summary>
/// Numbers as people read and type them, in the current culture. Percent values are stored as fractions and shown
/// as percentages, so a stored 0.15 reads and edits as 15.
/// </summary>
public static class Formatting
{
    /// <summary>
    /// A value as the Go app showed it: a percentage to up to three decimals; otherwise a whole number without decimals,
    /// and any other number with exactly two.
    /// </summary>
    public static string Value(string format, double value, IFormatProvider? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (format == StatFormats.Percent) { return (Math.Round(value * 100 * 1000) / 1000).ToString("#,0.###", culture) + "%"; }
        if (value == 0) { value = 0; } // -0 reads 0
        return value == Math.Truncate(value) && Math.Abs(value) < 1e15 ? value.ToString("#,0", culture) : value.ToString("#,0.00", culture);
    }

    /// <summary>A headline figure: 1,000 and over as a whole number, smaller ones as <see cref="Value"/>.</summary>
    public static string Headline(string format, double value, IFormatProvider? culture = null) =>
        Math.Abs(value) >= 1000 ? Math.Round(value).ToString("#,0", culture ?? CultureInfo.CurrentCulture) : Value(format, value, culture);

    /// <summary>A change with its sign, rounded to two decimals: "+34", "-2.50"; a percentage's in points ("+1.20 pp").</summary>
    public static string Delta(string format, double delta, IFormatProvider? culture = null)
    {
        var percent = format == StatFormats.Percent;
        var rounded = Math.Round((percent ? delta * 100 : delta) * 100) / 100;
        var text = Value(StatFormats.Decimal, rounded, culture);
        return (rounded > 0 ? "+" + text : text) + (percent ? " pp" : "");
    }

    /// <summary>1 when the new value is higher, -1 when lower, 0 when equal, ignoring float noise.</summary>
    public static int Direction(double value, double current) => Math.Sign(Math.Round((value - current) * 1e6) / 1e6);

    /// <summary>The arrow a previewed value carries: " ▲" up, " ▼" down, nothing when unchanged.</summary>
    public static string Mark(int direction) => direction > 0 ? " ▲" : direction < 0 ? " ▼" : "";

    /// <summary>The value as typed in an input box: no grouping or percent sign, every significant digit kept.</summary>
    public static string Input(string format, double value, IFormatProvider? culture = null) =>
        (format == StatFormats.Percent ? value * 100 : value).ToString("0.##########", culture ?? CultureInfo.CurrentCulture);

    /// <summary>Parses typed text back into a stored value, or null when it isn't a number.</summary>
    public static double? Parse(string format, string? text, IFormatProvider? culture = null)
    {
        var trimmed = (text ?? "").Trim().TrimEnd('%').Trim();
        // No thousands separators: a mistyped decimal point ("1,5" for 1.5) must fail rather than read as 15.
        if (!double.TryParse(trimmed, NumberStyles.Float, culture ?? CultureInfo.CurrentCulture, out var value) || !double.IsFinite(value)) { return null; }
        return format == StatFormats.Percent ? value / 100 : value;
    }
}
