using System.Globalization;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Infrastructure.Storage;

namespace CharacterPlanner.Desktop.Tests;

/// <summary>The summary column, as the Go app's: short labels, rows against an empty build, weapon tabs and attribute counts.</summary>
public sealed class SummaryTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "summary-" + Guid.NewGuid().ToString("N"));
    private readonly TestPack.QueueDispatcher dispatcher = new();
    private readonly PlannerSession session;
    private readonly EditorViewModel editor;
    private readonly CultureInfo culture = CultureInfo.CurrentCulture;

    public SummaryTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        session = new(TestPack.Division, PlannerState.Of(BuildSnapshot.Create(TestPack.Division.Pack)), new OperationApplier(TestPack.Division));
        editor = new(session, new FileBuildLibrary(folder), dispatcher);
    }

    public void Dispose()
    {
        editor.Dispose();
        CultureInfo.CurrentCulture = culture;
        if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); }
    }

    private async Task Resolved()
    {
        await editor.Resolution;
        dispatcher.RunPending();
    }

    private StatRowViewModel Row(string id) => editor.Summary.SelectMany(group => group.Rows).First(row => row.Id == id);

    [Fact]
    public void RowLabelsDropQualifiersUnlessTwoWouldReadTheSame()
    {
        Assert.Equal("Reload-Cycle Bodyshot DPS", Row("equipped_weapon_reload_cycle_dps").Label);
        Assert.Equal("Total Armor", Row("character_armor").Label);
        // "Estimated Headshot Damage" would read as Offense's "Headshot Damage", so it keeps its name.
        var headshot = editor.Summary.SelectMany(group => group.Rows).Where(row => row.Name.EndsWith("Headshot Damage", StringComparison.Ordinal)).ToList();
        Assert.Equal(headshot.Count, headshot.Select(row => row.Label).Distinct().Count());
    }

    [Fact]
    public void LabelsFollowTheGoRules()
    {
        var stats = new Dictionary<string, StatDefinition>
        {
            ["a"] = new() { ID = "a", Name = "Estimated Headshot Damage" },
            ["b"] = new() { ID = "b", Name = "Headshot Damage" },
            ["c"] = new() { ID = "c", Name = "Weapon Reload Time" },
            ["d"] = new() { ID = "d", Name = "Combined Accuracy" }
        };
        var labels = StatRowViewModel.Labels([new() { Name = "Weapon", Stats = ["a", "c"] }, new() { Name = "Offense", Stats = ["b", "d"] }], stats);
        Assert.Equal("Estimated Headshot Damage", labels["a"]);
        Assert.Equal("Headshot Damage", labels["b"]);
        Assert.Equal("Reload Time", labels["c"]);
        Assert.Equal("Accuracy", labels["d"]);

        // A stat listed twice is one row's label, not a collision with itself.
        var twice = StatRowViewModel.Labels([new() { Name = "Offense", Stats = ["d"], Conditional = ["d"] }], stats);
        Assert.Equal("Accuracy", twice["d"]);
    }

    [Fact]
    public async Task WeaponTabsPutAWeaponInHandAndTakeItOut()
    {
        Assert.Equal(["Primary", "Secondary", "Sidearm"], editor.WeaponTabs.Select(tab => tab.Label));
        var primary = editor.WeaponTabs[0];
        primary.ToggleCommand.Execute(null);
        Assert.Equal("primary_weapon", session.Snapshot.State.Assumptions[GamePack.WeaponSlotSelector]);
        Assert.True(primary.IsActive);
        primary.ToggleCommand.Execute(null);
        Assert.False(session.Snapshot.State.Assumptions.ContainsKey(GamePack.WeaponSlotSelector));
        Assert.False(primary.IsActive);
        await Resolved();
    }

    [Fact]
    public async Task ConditionalRowsAndEmptyGroupsHideOnAnEmptyBuild()
    {
        await Resolved();
        // A conditional row shows once the build moves it from the empty build's value.
        var conditional = editor.Summary.SelectMany(group => group.Rows).Where(row => !row.IsVisible).ToList();
        Assert.NotEmpty(conditional);
        Assert.All(editor.Summary.Where(group => group.Rows.All(row => !row.IsVisible)), group => Assert.False(group.HasVisible));
    }

    [Fact]
    public void AttributesAreCountedByCategoryOnGear()
    {
        Assert.Equal(["Offensive", "Defensive", "Skill"], editor.AttributeCounts.Select(count => count.Name));
        var mask = editor.Slot("mask");
        mask.Equip("mask_n_5_11_tactical_mask");
        var core = mask.Attributes.Single(attribute => attribute.Id == "core_1");
        core.Selected = core.Choices.Single(choice => choice.Id == "armor_gear_core");
        var defensive = editor.AttributeCounts.Single(count => count.Name == "Defensive");
        Assert.Equal((1, 0), (defensive.Core, defensive.Other));
    }
}
