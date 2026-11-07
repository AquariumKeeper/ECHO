using System.Security.Cryptography;

namespace CharacterPlanner.Legacy.Tests;

/// <summary>
/// The captured C0 fixtures are never edited, by the project's rule. Every file under baseline/fixtures must match the
/// SHA-256 that INDEX.txt records for it, and every file must be listed, so "fixtures unchanged" is checked
/// on every run rather than by hand (L14). The test project copies the fixtures, as the other baseline tests do.
/// </summary>
public sealed class BaselineIndexTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "baseline-fixtures");

    [Fact]
    public void EveryFixtureMatchesItsIndexedHash()
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(Path.Combine(Root, "INDEX.txt")).Where(line => line.Length > 0))
        {
            // sha256sum's layout: 64 hex digits, two spaces, then the path.
            Assert.True(line.Length > 66 && line[64..66] == "  ", $"INDEX.txt line isn't \"<sha256>  <path>\": {line}");
            Assert.True(index.TryAdd(line[66..], line[..64]), $"INDEX.txt lists {line[66..]} twice");
        }
        // Hidden and system files (Thumbs.db, desktop.ini) aren't fixtures.
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        var files = Directory.EnumerateFiles(Root, "*", options)
            .Select(path => Path.GetRelativePath(Root, path).Replace('\\', '/'))
            .Where(path => path != "INDEX.txt")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(index.Keys.Order(StringComparer.Ordinal), files);
        Assert.All(files, path => Assert.True(
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Root, path)))) == index[path],
            $"{path} doesn't match INDEX.txt; captured fixtures are never edited"));
    }
}
