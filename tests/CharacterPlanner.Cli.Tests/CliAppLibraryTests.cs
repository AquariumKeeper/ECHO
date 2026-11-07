using System.Text;
using System.Text.Json;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Infrastructure.Storage;

namespace CharacterPlanner.Cli.Tests;

public sealed partial class CliAppTests
{
    private string Library => Path.Combine(root, "library");

    private string WriteNamedBuild(string name, string file)
    {
        var path = Path.Combine(root, file);
        File.WriteAllBytes(path, BuildDocument.Encode(BuildSnapshot.Create(Mini).WithEquipment("mask", "mask_a") with { Meta = new(name) }));
        return path;
    }

    private static JsonElement Entries(Run run) => run.Json.GetProperty("result").GetProperty("entries");

    [Fact]
    public async Task LibrarySaveListLoadReplaceAndDeleteNameTheVersionTheyExpect()
    {
        var first = WriteNamedBuild("Raid", "raid.json");
        var saved = await RunAsync("library", "save", first, "--library", Library);
        Assert.Equal(CliApp.Success, saved.ExitCode);
        Assert.Empty(Diagnostics(saved, true));
        var id = saved.Json.GetProperty("result").GetProperty("id").GetString()!;
        var version = saved.Json.GetProperty("result").GetProperty("version").GetString()!;
        Assert.False(saved.Json.GetProperty("result").GetProperty("replaced").GetBoolean());

        // A second save of the same name is a new record, never a silent replacement.
        var again = await RunAsync("library", "save", first, "--library", Library);
        Assert.NotEqual(id, again.Json.GetProperty("result").GetProperty("id").GetString());
        var listed = await RunAsync("library", "list", "--library", Library);
        Assert.Equal(CliApp.Success, listed.ExitCode);
        Assert.Equal(Path.GetFullPath(Library), listed.Json.GetProperty("result").GetProperty("directory").GetString());
        Assert.Equal(2, Entries(listed).GetArrayLength());
        var entry = Entries(listed).EnumerateArray().Single(item => item.GetProperty("id").GetString() == id);
        Assert.Equal("Raid", entry.GetProperty("name").GetString());
        Assert.Equal("mini", entry.GetProperty("packId").GetString());
        Assert.Equal(version, entry.GetProperty("version").GetString());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("problem").ValueKind);
        Assert.True(DateTimeOffset.TryParse(entry.GetProperty("savedAt").GetString(), out _));

        var loaded = await RunAsync("library", "load", id, "--library", Library);
        Assert.Equal(CliApp.Success, loaded.ExitCode);
        Assert.Equal(version, loaded.Json.GetProperty("result").GetProperty("version").GetString());
        var document = BuildDocument.Decode(Encoding.UTF8.GetBytes(loaded.Json.GetProperty("result").GetProperty("document").GetRawText()));
        Assert.Equal(BuildDocument.Encode(BuildDocument.Decode(File.ReadAllBytes(first))), BuildDocument.Encode(document));

        var renamed = WriteNamedBuild("Raid v2", "raid-v2.json");
        var replaced = await RunAsync("library", "save", renamed, "--replace", id, "--expected-version", version, "--library", Library);
        Assert.Equal(CliApp.Success, replaced.ExitCode);
        Assert.True(replaced.Json.GetProperty("result").GetProperty("replaced").GetBoolean());
        Assert.Equal(id, replaced.Json.GetProperty("result").GetProperty("id").GetString());
        var current = replaced.Json.GetProperty("result").GetProperty("version").GetString()!;

        // The version read before the replacement is now stale, so neither replace nor delete may use it.
        var stale = await RunAsync("library", "save", first, "--replace", id, "--expected-version", version, "--library", Library);
        Assert.Equal(CliApp.InvalidInput, stale.ExitCode);
        Assert.Contains(Diagnostics(stale, false), diagnostic => diagnostic.Code == "library-conflict");
        var staleDelete = await RunAsync("library", "delete", id, "--expected-version", version, "--library", Library);
        Assert.Contains(Diagnostics(staleDelete, false), diagnostic => diagnostic.Code == "library-conflict");

        var deleted = await RunAsync("library", "delete", id, "--expected-version", current, "--library", Library);
        Assert.Equal(CliApp.Success, deleted.ExitCode);
        Assert.True(deleted.Json.GetProperty("result").GetProperty("deleted").GetBoolean());
        Assert.Equal(1, Entries(await RunAsync("library", "list", "--library", Library)).GetArrayLength());
    }

    [Fact]
    public async Task LibraryRecordsAreTheDesktopsOwnFiles()
    {
        var saved = await RunAsync("library", "save", WriteNamedBuild("Shared", "shared.json"), "--library", Library);
        var id = saved.Json.GetProperty("result").GetProperty("id").GetString()!;
        var desktop = new FileBuildLibrary(Library);
        var loaded = await desktop.LoadAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal("Shared", loaded.Build.Meta.Name);
        Assert.Equal(saved.Json.GetProperty("result").GetProperty("version").GetString(), loaded.Version);
        Assert.EndsWith(Path.Combine("CharacterPlanner", "alpha", "library"), AppDataPaths.Library, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LibraryFailuresExitThreeWithTheirLibraryCodes()
    {
        Assert.Equal(0, Entries(await RunAsync("library", "list", "--library", Library)).GetArrayLength());
        var missing = await RunAsync("library", "load", new string('a', 32), "--library", Library);
        Assert.Equal(CliApp.InvalidInput, missing.ExitCode);
        Assert.Contains(Diagnostics(missing, false), diagnostic => diagnostic.Code == "library-not-found");
        var invalid = await RunAsync("library", "load", "../escape", "--library", Library);
        Assert.Contains(Diagnostics(invalid, false), diagnostic => diagnostic.Code == "library-invalid-id");

        var notABuild = Path.Combine(root, "not-a-build.json");
        File.WriteAllText(notABuild, "{}");
        Assert.Contains(Diagnostics(await RunAsync("library", "save", notABuild, "--library", Library), false), diagnostic => diagnostic.Code == "build-invalid");
        var half = await RunAsync("library", "save", WriteNamedBuild("Half", "half.json"), "--replace", new string('a', 32), "--library", Library);
        Assert.Contains(Diagnostics(half, false), diagnostic => diagnostic.Code == "library-replace-invalid");
        var empty = await RunAsync("library", "list", "--library", "");
        Assert.Contains(Diagnostics(empty, false), diagnostic => diagnostic.Code == "library-path-invalid");

        // A damaged record is listed with why, not hidden, and can't be loaded.
        Directory.CreateDirectory(Library);
        var damaged = new string('b', 32);
        File.WriteAllText(Path.Combine(Library, damaged + FileBuildLibrary.Extension), "not json");
        var entry = Entries(await RunAsync("library", "list", "--library", Library)).EnumerateArray().Single();
        Assert.Equal(damaged, entry.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.String, entry.GetProperty("problem").ValueKind);
        Assert.Contains(Diagnostics(await RunAsync("library", "load", damaged, "--library", Library), false), diagnostic => diagnostic.Code == "library-damaged");
    }

    [Theory]
    [InlineData("library", "delete", "id")]
    [InlineData("library", "delete", "id", "--library", "x")]
    [InlineData("library", "list", "--library")]
    [InlineData("library", "list", "--library", "a", "--library", "b")]
    [InlineData("library", "load")]
    [InlineData("library", "save", "build.json", "--expected")]
    [InlineData("library", "list", "--replace", "x")]
    public async Task MalformedLibraryArgumentsAreUsageErrors(params string[] args)
    {
        var run = await RunAsync(args);
        Assert.Equal(CliApp.UsageError, run.ExitCode);
        AssertInvalidCommand(run);
    }
}
