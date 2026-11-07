using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Library;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Desktop.ViewModels;

namespace CharacterPlanner.Desktop.Tests;

/// <summary>Compare Builds (F43): the selectors, the stat table, and Go's status lines.</summary>
public sealed class CompareViewModelTests : IDisposable
{
    private readonly TestPack.QueueDispatcher dispatcher = new();
    private readonly FakeLibrary library = new();
    private readonly PlannerSession session;
    private readonly EditorViewModel editor;
    private readonly CultureInfo culture = CultureInfo.CurrentCulture;

    public CompareViewModelTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        session = new(TestPack.Division, PlannerState.Of(BuildSnapshot.Create(TestPack.Division.Pack)), new OperationApplier(TestPack.Division));
        editor = new(session, library, dispatcher);
    }

    public void Dispose()
    {
        editor.Dispose();
        CultureInfo.CurrentCulture = culture;
    }

    /// <summary>The current build with operations applied, without changing the session.</summary>
    internal static BuildSnapshot Edited(string name, params BuildOperation[] operations)
    {
        var outcome = new OperationApplier(TestPack.Division).Apply(PlannerState.Of(BuildSnapshot.Create(TestPack.Division.Pack)), [new NameOperation(name), .. operations]);
        Assert.True(outcome.Succeeded);
        return outcome.State.Build;
    }

    private async Task Compared(CompareViewModel compare)
    {
        await compare.Comparison;
        dispatcher.RunPending();
    }

    [Fact]
    public async Task ASavedBuildIsComparedWithTheCurrentOne()
    {
        library.Add("a", Edited("Tank", new EquipmentOperation("chest", "chest_n_5_11_tactical_chest")));
        using var compare = editor.CreateCompare();
        Assert.Equal(CompareViewModel.CurrentBuild, Assert.Single(compare.Options).Label);
        await compare.LoadAsync();
        await Compared(compare);

        Assert.Equal([CompareViewModel.CurrentBuild, "Saved: Tank"], compare.Options.Select(option => option.Label));
        Assert.Same(compare.Options[0], compare.BuildA);
        Assert.Same(compare.Options[0], compare.BuildB);
        // The same build twice: every stat, no change anywhere.
        Assert.Equal(TestPack.Division.Pack.Stats.Length, compare.Rows.Length);
        Assert.All(compare.Rows, row => Assert.Matches("^0( pp)?$", row.Delta));
        Assert.False(compare.HasStatus);

        compare.BuildB = compare.Options[1];
        Assert.True(compare.IsComparing);
        await Compared(compare);
        Assert.False(compare.IsComparing);
        // The largest change comes first, with its sign; the current build is untouched.
        var first = compare.Rows[0];
        Assert.StartsWith("+", first.Delta, StringComparison.Ordinal);
        var armor = compare.Rows.Single(row => row.Stat == TestPack.Division.Pack.Stats.Single(stat => stat.ID == "character_armor").Name);
        Assert.StartsWith("+", armor.Delta, StringComparison.Ordinal);
        Assert.Equal("", session.Snapshot.State.Build.Equipment.GetValueOrDefault("chest", ""));
    }

    [Fact]
    public async Task DamagedRecordsAndOtherPacksAreLeftOutWithAReason()
    {
        library.Add("a", Edited("Unnamed") with { Meta = new("") });
        library.Add("b", Edited("Same"));
        library.Add("c", Edited("Same"));
        library.Entries.Add(new("d", "Broken", TestPack.Division.Pack.ID, "1", DateTimeOffset.UnixEpoch, "v", "the file isn't a build"));
        library.Entries.Add(new("e", "Elsewhere", "other_pack", "1", DateTimeOffset.UnixEpoch, "v"));
        using var compare = editor.CreateCompare();
        await compare.LoadAsync();
        await Compared(compare);

        Assert.Equal([CompareViewModel.CurrentBuild, "Saved: (unnamed build)", "Saved: Same", "Saved: Same (2)"], compare.Options.Select(option => option.Label));
        Assert.Equal("Saved build \"Broken\" is invalid: the file isn't a build\n"
            + $"Saved build \"Elsewhere\" is invalid: the build is for pack other_pack, not {TestPack.Division.Pack.ID}", compare.Status);
    }

    [Fact]
    public async Task ASavedBuildThatDoesNotResolveIsReportedAndNotCompared()
    {
        var other = Edited("Foreign") with { PackId = "other_pack" };
        library.Builds["x"] = other;
        library.Entries.Add(new("x", "Foreign", "", "1", DateTimeOffset.UnixEpoch, "v"));
        using var compare = editor.CreateCompare();
        await compare.LoadAsync();
        await Compared(compare);
        compare.BuildA = compare.Options[1];
        await Compared(compare);

        Assert.Empty(compare.Rows);
        Assert.Equal($"Saved build \"Foreign\" is invalid: the build is for pack other_pack, not {TestPack.Division.Pack.ID}", compare.Status);
        compare.BuildA = compare.Options[0];
        await Compared(compare);
        Assert.False(compare.HasStatus);
        Assert.NotEmpty(compare.Rows);
    }

    [Fact]
    public async Task AnUnreadableLibraryLeavesTheCurrentBuild()
    {
        library.Failure = new IOException("disk gone");
        using var compare = editor.CreateCompare();
        await compare.LoadAsync();
        await Compared(compare);
        Assert.Single(compare.Options);
        Assert.Equal("Could not read build library: disk gone", compare.Status);
        Assert.NotEmpty(compare.Rows);
    }

    /// <summary>A library whose records and failures a test sets.</summary>
    internal sealed class FakeLibrary : IBuildLibrary
    {
        public List<LibraryEntry> Entries { get; } = [];
        public Dictionary<string, BuildSnapshot> Builds { get; } = new(StringComparer.Ordinal);
        public Exception? Failure { get; set; }

        public void Add(string id, BuildSnapshot build)
        {
            Builds[id] = build;
            Entries.Add(new(id, build.Meta.Name, build.PackId, "1", DateTimeOffset.UnixEpoch, "v"));
        }

        public Task<ImmutableArray<LibraryEntry>> ListAsync(CancellationToken cancellationToken = default) =>
            Failure is { } failure ? Task.FromException<ImmutableArray<LibraryEntry>>(failure) : Task.FromResult(Entries.ToImmutableArray());

        public Task<LibraryBuild> LoadAsync(string id, CancellationToken cancellationToken = default) =>
            Builds.TryGetValue(id, out var build)
                ? Task.FromResult(new LibraryBuild(id, build, "v", DateTimeOffset.UnixEpoch))
                : Task.FromException<LibraryBuild>(new LibraryException(LibraryErrorCodes.NotFound, $"no build {id}"));

        public Task<LibraryBuild> CreateAsync(BuildSnapshot build, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LibraryBuild> ReplaceAsync(string id, BuildSnapshot build, string expectedVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string id, string expectedVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
