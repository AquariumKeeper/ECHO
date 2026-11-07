namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>
/// The app's one search rule, as the Go app's matchesAllWords: every word typed must appear in the text, ignoring case
/// (and accents); an empty query matches everything.
/// </summary>
public static class TextSearch
{
    public static bool MatchesAllWords(string text, string query)
    {
        var compare = System.Globalization.CultureInfo.CurrentCulture.CompareInfo;
        return query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .All(word => compare.IndexOf(text, word, System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace) >= 0);
    }
}
