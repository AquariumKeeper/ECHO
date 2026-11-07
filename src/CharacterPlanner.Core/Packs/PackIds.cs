using System.Text.RegularExpressions;

namespace CharacterPlanner.Core.Packs;

public static partial class PackIds
{
    // \A and \z rather than ^ and $: in .NET, $ also matches before a trailing newline.
    [GeneratedRegex(@"\A[a-z][a-z0-9_]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();

    /// <summary>lower_snake_case starting with a letter: the form of every pack id.</summary>
    public static bool IsSlug(string value) => SlugPattern().IsMatch(value);
}
