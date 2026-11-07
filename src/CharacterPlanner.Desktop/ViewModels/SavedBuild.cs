using System.Globalization;
using CharacterPlanner.Application.Library;

namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>
/// A saved build as the library picker lists it, as Go's library rows: its name, when it was saved, and, for a build
/// saved with an earlier version of the pack, that it was (G391): it still opens, checked against the current pack.
/// </summary>
public sealed record SavedBuild(LibraryEntry Entry, string PackVersion)
{
    public string Name => Entry.Name.Length > 0 ? Entry.Name : "(unnamed build)";

    public string Detail => $"Saved {Entry.SavedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}"
        + (Entry.PackVersion.Length > 0 && Entry.PackVersion != PackVersion ? " · earlier pack version" : "");

    public bool IsEarlierPack => Entry.PackVersion.Length > 0 && Entry.PackVersion != PackVersion;

    public override string ToString() => Name;
}
