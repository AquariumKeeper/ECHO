using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Legacy.Documents;

/// <summary>A share code read for import.</summary>
/// <param name="Legacy">True for a Go-era (v1–v12) code, which a caller may offer to re-share in the app's format.</param>
/// <param name="DroppedFields">The removed v1 fields a legacy code held values for; always empty for an app code.</param>
public sealed record ShareCodeImport(BuildSnapshot Build, bool Legacy, ImmutableArray<string> DroppedFields);

/// <summary>Reads either share code format, so composition roots can accept codes testers already have (ADR 0002, ADR 0003).</summary>
public static class ShareCodeReader
{
    public static ShareCodeImport Read(string code, LegacyShareOrder order)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (ShareCode.IsAppCode(code)) { return new(ShareCode.Decode(code), false, []); }
        var import = LegacyShareCode.Decode(code, order);
        return new(import.Build, true, import.DroppedFields);
    }
}
