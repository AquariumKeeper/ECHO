using System.Buffers;
using System.Text;

namespace CharacterPlanner.Application.Artwork;

/// <summary>Portable archive names, checked without filesystem or platform-specific path interpretation.</summary>
public static class ArtworkPaths
{
    private static readonly HashSet<string> Devices = new(["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$"], StringComparer.OrdinalIgnoreCase);

    public static bool IsIdentifier(string value) => value.Length is > 0 and <= 64 && (value[0] is >= 'a' and <= 'z' or >= '0' and <= '9') &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-') &&
        IsSegment(value);

    public static bool IsFile(string value, string folder) => value.Length <= 240 && value.StartsWith(folder + "/", StringComparison.Ordinal) &&
        value.Split('/').All(IsSegment);

    private static bool IsSegment(string value)
    {
        if (value.Length == 0 || value is "." or ".." || value.EndsWith('.') || value.EndsWith(' ') || !WellFormed(value) ||
            value.Any(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character, StringComparison.Ordinal))) { return false; }
        var stem = value.Split('.')[0];
        return !Devices.Contains(stem) &&
            !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                stem[3] is >= '0' and <= '9' or '¹' or '²' or '³');
    }

    /// <summary>Checks every prefix, so folders as well as full paths have one portable spelling.</summary>
    public static bool HasConsistentCasing(IEnumerable<string> paths)
    {
        var prefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            for (var end = path.IndexOf('/'); ; end = path.IndexOf('/', end + 1))
            {
                var prefix = end < 0 ? path : path[..end];
                if (prefixes.TryGetValue(prefix, out var prior) && prior != prefix) { return false; }
                prefixes[prefix] = prefix;
                if (end < 0) { break; }
            }
        }
        return true;
    }

    internal static bool WellFormed(string value)
    {
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out var consumed) != OperationStatus.Done) { return false; }
            remaining = remaining[consumed..];
        }
        return true;
    }
}
