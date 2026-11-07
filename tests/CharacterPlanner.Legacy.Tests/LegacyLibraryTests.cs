using System.Text;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Legacy.Documents;

namespace CharacterPlanner.Legacy.Tests;

/// <summary>The Go app's build library, read for the one-time import (ADR 0002, ledger F52).</summary>
public sealed class LegacyLibraryTests
{
    private static readonly string[] Builds = ["foundry-shield", "hard-wired-skills", "party-buffs", "prototype-gear", "shotgun-close", "striker-rifle"];

    /// <summary>
    /// The captured Go library holds the six C0 builds; each record reads as the same build as that build's frozen v1
    /// document, so the library imports by the document's rules.
    /// </summary>
    [Fact]
    public void TheCapturedGoLibraryReadsAsTheFrozenDocuments()
    {
        var records = LegacyLibrary.Read(LegacyPacks.Bytes(Path.Combine("baseline-fixtures", "library", "library.json")));
        Assert.Equal(Builds, records.Select(record => record.Id));
        foreach (var record in records)
        {
            Assert.Null(record.Problem);
            Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero), record.SavedAt);
            var frozen = BuildDocumentJson.Decode(LegacyPacks.Bytes(Path.Combine("c0-builds", record.Id, "document.json")));
            Assert.Equal(Encoding.UTF8.GetString(BuildDocument.Encode(frozen.Build)), Encoding.UTF8.GetString(BuildDocument.Encode(record.Import!.Build)));
            Assert.Equal(frozen.DroppedFields, record.Import.DroppedFields);
            Assert.Equal(record.Id, record.Import.Build.Meta.Name);
        }
    }

    /// <summary>A record that can't be read is reported with why, and the others still read.</summary>
    [Fact]
    public void ABadRecordIsReportedAndTheRestRead()
    {
        var good = """{"id":"good","savedAt":"2026-10-25T00:00:00Z","build":{"PackID":"division_2","PackVersion":"1","Level":40,"Meta":{"Name":"Good","Notes":""}}}""";
        var odd = """{"id":"odd","build":{"PackID":"division_2","Level":40,"Surprise":1}}""";
        var records = LegacyLibrary.Read(Encoding.UTF8.GetBytes($"[{good}, {odd}, {{\"id\":\"empty\"}}, 7]"));
        Assert.Equal(["good", "odd", "empty", "#4"], records.Select(record => record.Id));
        Assert.Equal("Good", records[0].Import!.Build.Meta.Name);
        Assert.All(records.Skip(1), record => Assert.NotNull(record.Problem));
        Assert.Null(records[1].SavedAt);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("null")]
    public void AFileThatIsntALibraryIsRefused(string json) =>
        Assert.Throws<LegacyDocumentException>(() => LegacyLibrary.Read(Encoding.UTF8.GetBytes(json)));
}
