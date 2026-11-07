using System.Globalization;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Infrastructure.Storage;

namespace CharacterPlanner.Desktop.Tests;

/// <summary>Rank Upgrades (F42), as the Go app's G397: the ranked rows and labels, the filters, and Apply in one undo step.</summary>
public sealed class RankUpgradesViewModelTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "rank-" + Guid.NewGuid().ToString("N"));
    private readonly TestPack.QueueDispatcher dispatcher = new();
    private readonly PlannerSession session;
    private readonly EditorViewModel editor;
    private readonly CultureInfo culture = CultureInfo.CurrentCulture;

    public RankUpgradesViewModelTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        session = new(TestPack.Division, PlannerState.Of(BuildSnapshot.Create(TestPack.Division.Pack)), new OperationApplier(TestPack.Division));
        editor = new(session, new FileBuildLibrary(folder), dispatcher);
    }

    private void Apply(params BuildOperation[] operations)
    {
        Assert.True(session.Apply([.. operations], session.Snapshot.Revision, EditOrigin.Desktop).Succeeded);
        dispatcher.RunPending();
    }

    public void Dispose()
    {
        editor.Dispose();
        CultureInfo.CurrentCulture = culture;
        if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); }
    }

    /// <summary>Go's G397 build: a 5.11 Tactical chest with its armor core rolled at 100,000.</summary>
    private void Chest()
    {
        Apply(new EquipmentOperation("chest", "chest_n_5_11_tactical_chest"));
        Apply(new AttributeOperation("chest", "core_1", "armor_gear_core", 100000));
    }

    private async Task<RankUpgradesViewModel> Ranked(RankUpgradesViewModel? ranking = null)
    {
        ranking ??= editor.RankUpgrades!;
        await ranking.Ranking;
        dispatcher.RunPending();
        return ranking;
    }

    [Fact]
    public async Task ChangesAreRankedByTheirGainAndAppliedInOneStep()
    {
        Chest();
        editor.LastRankedStat = "character_armor";
        Assert.True(editor.RankUpgradesCommand.CanExecute("chest"));
        editor.RankUpgradesCommand.Execute("chest");
        var ranking = await Ranked();

        Assert.Equal("Rank upgrades · 5.11 Tactical Chest", ranking.Title, ignoreCase: true);
        Assert.Equal("character_armor", ranking.RankBy.Id);
        Assert.False(ranking.IsRanking);
        Assert.Null(ranking.Problem);
        var first = ranking.Rows[0];
        Assert.Equal("+70,000 Estimated Total Armor", first.Change);
        Assert.Equal("Core 1 · Armor (Defensive) at 170,000 (reroll)", first.Label);
        Assert.True(first.IsGain);
        Assert.Matches(@"^\d+ of \d+ changes$", ranking.Shown);

        var revision = session.Snapshot.Revision;
        first.ApplyCommand.Execute(null);
        Assert.Equal(revision + 1, session.Snapshot.Revision);
        Assert.Equal(170000, session.Snapshot.State.Build.AttributeRolls["chest"]["core_1"].Value);
        Assert.Null(editor.RankUpgrades);
        // One undo step takes it back.
        editor.UndoCommand.Execute(null);
        Assert.Equal(100000, session.Snapshot.State.Build.AttributeRolls["chest"]["core_1"].Value);
    }

    [Fact]
    public async Task TheFiltersNarrowTheRankingAndTheStatIsRemembered()
    {
        Chest();
        editor.RankUpgradesCommand.Execute("chest");
        var ranking = await Ranked();
        // The summary's stats, in its order, once each; the first by default.
        var summary = editor.Summary.SelectMany(group => group.Rows).Select(row => row.Id).Distinct().ToList();
        Assert.Equal(summary, ranking.Stats.Select(stat => stat.Id));
        Assert.Equal(summary[0], ranking.RankBy.Id);

        ranking.RankBy = ranking.Stats.Single(stat => stat.Id == "character_armor");
        Assert.True(ranking.IsRanking);
        await Ranked(ranking);
        var gains = ranking.Rows.Length;
        Assert.All(ranking.Rows, row => Assert.True(row.IsGain));
        var total = int.Parse(ranking.Shown.Split(' ')[2], CultureInfo.InvariantCulture);
        Assert.Equal($"{gains} of {total} changes", ranking.Shown);

        ranking.OnlyGains = false;
        Assert.Equal(total, ranking.Rows.Length);
        Assert.Contains(ranking.Rows, row => row.IsLoss);

        // Every typed word must appear, in any case.
        ranking.Search = "ARMOR reroll";
        Assert.All(ranking.Rows, row => Assert.Contains("(reroll)", row.Label, StringComparison.Ordinal));
        Assert.NotEmpty(ranking.Rows);
        ranking.Search = "no such change";
        Assert.Empty(ranking.Rows);
        Assert.True(ranking.IsEmpty);
        Assert.Equal($"0 of {total} changes", ranking.Shown);

        // The next dialog opens on the stat last ranked by.
        editor.CloseRankUpgrades();
        Assert.Null(editor.RankUpgrades);
        editor.RankUpgradesCommand.Execute("chest");
        Assert.Equal("character_armor", (await Ranked()).RankBy.Id);
    }

    [Fact]
    public void AnEmptySlotHasNothingToRank()
    {
        Assert.False(editor.RankUpgradesCommand.CanExecute("chest"));
        Assert.False(editor.RankUpgradesCommand.CanExecute(null));
        editor.RankUpgradesCommand.Execute("chest");
        Assert.Null(editor.RankUpgrades);
        Chest();
        Assert.True(editor.RankUpgradesCommand.CanExecute("chest"));
    }

    [Fact]
    public void LabelsAndSearchAreGos()
    {
        var talent = new RankedUpgrade(UpgradeKind.Talent, "talent", "Talent", "vanguard", "Vanguard", null, "", false, 0, 1, []);
        Assert.Equal("Talent · Vanguard", RankUpgradesViewModel.Label(talent));
        // A mod's value isn't part of its label; an attribute's is.
        var mod = new RankedUpgrade(UpgradeKind.Mod, "mod_1", "Mod 1", "chc", "Critical Hit Chance", 0.06, "percent", true, 0, 1, []);
        Assert.Equal("Mod 1 · Critical Hit Chance (reroll)", RankUpgradesViewModel.Label(mod));
        var attribute = mod with { Kind = UpgradeKind.Attribute, Reroll = false };
        Assert.Equal("Mod 1 · Critical Hit Chance at 6%", RankUpgradesViewModel.Label(attribute));
        Assert.True(TextSearch.MatchesAllWords("Core 1 · Armor", "  "));
        Assert.True(TextSearch.MatchesAllWords("Core 1 · Armor", "armor CORE"));
        Assert.False(TextSearch.MatchesAllWords("Core 1 · Armor", "armor talent"));
    }
}
