using System.Collections.Immutable;
using System.Text.Json;
using CharacterPlanner.Application.Assumptions;
using CharacterPlanner.Infrastructure.Storage;

namespace CharacterPlanner.Infrastructure.Tests;

public sealed class FileAssumptionSetStoreTests : IDisposable
{
    private const string Pack = "division_2";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "assumption-sets-" + Guid.NewGuid().ToString("N"));
    private readonly string file;
    private readonly FileAssumptionSetStore store;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public FileAssumptionSetStoreTests()
    {
        file = Path.Combine(directory, "assumption-sets.json");
        store = new(file, TimeSpan.FromMilliseconds(200));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
    }

    private static ImmutableDictionary<string, string> Values(params (string Id, string Value)[] values) =>
        values.ToImmutableDictionary(pair => pair.Id, pair => pair.Value, StringComparer.Ordinal);

    private static async Task<AssumptionSetException> Rejected(Func<Task> action) => await Assert.ThrowsAsync<AssumptionSetException>(action);

    /// <summary>The folder's files apart from the writer lock.</summary>
    private string[] Files() => [.. Directory.EnumerateFiles(directory).Select(path => Path.GetFileName(path)).Where(name => !name.EndsWith(FileAssumptionSetStore.LockSuffix, StringComparison.Ordinal))];

    [Fact]
    public async Task AMissingFileHasNoSets() => Assert.Empty(await store.ListAsync(Pack, Token));

    [Fact]
    public async Task SavedSetsAreListedPerPackByNameIgnoringCase()
    {
        var boss = await store.SaveAsync(Pack, "  boss, full stacks ", Values(("enemy_target", "2"), ("in_sync_weapon_hit_active", "true")), replace: false, Token);
        Assert.Equal("boss, full stacks", boss.Name);
        await store.SaveAsync(Pack, "Adds", Values(("enemy_target", "1")), replace: false, Token);
        await store.SaveAsync("ashen_vale", "Ashen", Values(), replace: false, Token);

        var listed = await store.ListAsync(Pack, Token);
        Assert.Equal(["Adds", "boss, full stacks"], listed.Select(set => set.Name));
        Assert.Equal(boss.Values, listed[1].Values);
        Assert.Equal(Pack, listed[1].PackId);
        Assert.Equal(["Ashen"], (await store.ListAsync("ashen_vale", Token)).Select(set => set.Name));
        // One file of the app's own format, and nothing else left behind.
        Assert.Equal([Path.GetFileName(file)], Files());
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(file, Token));
        Assert.Equal(FileAssumptionSetStore.Format, document.RootElement.GetProperty("format").GetString());
        Assert.Equal(FileAssumptionSetStore.Version, document.RootElement.GetProperty("version").GetInt32());
        // Another store on the same file reads them.
        Assert.Equal(2, (await new FileAssumptionSetStore(file).ListAsync(Pack, Token)).Length);
    }

    [Fact]
    public async Task ASetNeedsAName()
    {
        var error = await Rejected(() => store.SaveAsync(Pack, "   ", Values(), replace: false, Token));
        Assert.Equal((AssumptionSetErrorCodes.NameRequired, "an assumption set needs a name"), (error.Code, error.Message));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task ASameNamedSetIsReplacedOnlyWhenAsked()
    {
        await store.SaveAsync(Pack, "Boss", Values(("enemy_target", "2")), replace: false, Token);
        var error = await Rejected(() => store.SaveAsync(Pack, "BOSS", Values(("enemy_target", "1")), replace: false, Token));
        Assert.Equal((AssumptionSetErrorCodes.Exists, "an assumption set named \"Boss\" already exists"), (error.Code, error.Message));
        // The same name in another pack is another set.
        await store.SaveAsync("ashen_vale", "Boss", Values(), replace: false, Token);

        await store.SaveAsync(Pack, "BOSS", Values(("enemy_target", "1")), replace: true, Token);
        var set = Assert.Single(await store.ListAsync(Pack, Token));
        Assert.Equal(("BOSS", "1"), (set.Name, set.Values["enemy_target"]));
    }

    [Fact]
    public async Task DeletingRemovesOnlyThatSet()
    {
        await store.SaveAsync(Pack, "Boss", Values(), replace: false, Token);
        await store.SaveAsync(Pack, "Adds", Values(), replace: false, Token);
        await store.DeleteAsync(Pack, "boss", Token);
        Assert.Equal(["Adds"], (await store.ListAsync(Pack, Token)).Select(set => set.Name));
        var error = await Rejected(() => store.DeleteAsync(Pack, "Boss", Token));
        Assert.Equal((AssumptionSetErrorCodes.NotFound, "no assumption set named \"Boss\""), (error.Code, error.Message));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"format":"character-planner-assumption-sets","version":2,"sets":[]}""")]
    [InlineData("""{"version":1,"sets":[]}""")]
    [InlineData("""{"format":"character-planner-assumption-sets","version":1,"sets":[{"packId":"division_2","name":" ","values":{}}]}""")]
    [InlineData("""{"format":"character-planner-assumption-sets","version":1,"sets":[{"packId":"division_2","name":"Boss","values":{"enemy_target":2}}]}""")]
    public async Task ADamagedFileIsReportedAndNeverWrittenOver(string text)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(file, text, Token);
        Assert.Equal(AssumptionSetErrorCodes.Damaged, (await Rejected(() => store.ListAsync(Pack, Token))).Code);
        Assert.Equal(AssumptionSetErrorCodes.Damaged, (await Rejected(() => store.SaveAsync(Pack, "Boss", Values(), replace: true, Token))).Code);
        Assert.Equal(AssumptionSetErrorCodes.Damaged, (await Rejected(() => store.DeleteAsync(Pack, "Boss", Token))).Code);
        Assert.Equal(text, await File.ReadAllTextAsync(file, Token));
    }

    [Fact]
    public async Task AWriterWaitsForAnotherWritersLockThenReportsBusy()
    {
        Directory.CreateDirectory(directory);
        using (new FileStream(file + FileAssumptionSetStore.LockSuffix, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(AssumptionSetErrorCodes.Busy, (await Rejected(() => store.SaveAsync(Pack, "Boss", Values(), replace: false, Token))).Code);
            // Readers take no lock.
            Assert.Empty(await store.ListAsync(Pack, Token));
        }
        await store.SaveAsync(Pack, "Boss", Values(), replace: false, Token);
    }

    [Fact]
    public async Task ConcurrentSavesAreAllKept()
    {
        var other = new FileAssumptionSetStore(file);
        var third = new FileAssumptionSetStore(file);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(index => (index % 2 == 0 ? other : third).SaveAsync(Pack, $"Set {index}", Values(), replace: false, Token)));
        Assert.Equal(10, (await store.ListAsync(Pack, Token)).Length);
    }
}
