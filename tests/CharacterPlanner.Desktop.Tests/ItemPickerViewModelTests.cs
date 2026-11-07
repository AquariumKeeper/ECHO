using System.Globalization;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Infrastructure.Storage;

namespace CharacterPlanner.Desktop.Tests;

/// <summary>The item picker (F40): filters, comparing each item with the build as it is, sorting by a stat's change, and equipping.</summary>
public sealed class ItemPickerViewModelTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "picker-" + Guid.NewGuid().ToString("N"));
    private readonly TestPack.QueueDispatcher dispatcher = new();
    private readonly PlannerSession session;
    private readonly EditorViewModel editor;
    private readonly CultureInfo culture = CultureInfo.CurrentCulture;

    public ItemPickerViewModelTests()
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

    private async Task<ItemPickerViewModel> Browse(string slotId)
    {
        editor.Slot(slotId).BrowseCommand.Execute(null);
        var picker = editor.Picker!;
        await Compared(picker);
        return picker;
    }

    private async Task Previewed()
    {
        await editor.PreviewResolution;
        dispatcher.RunPending();
    }

    [Fact]
    public async Task OpeningAnotherPickerEndsThePreview()
    {
        await editor.Resolution;
        dispatcher.RunPending();
        var picker = await Browse("mask");
        picker.Highlighted = picker.Rows.Single(row => row.Id == "mask_n_5_11_tactical_mask");
        await Previewed();
        Assert.Contains(editor.Headline, figure => figure.Change != 0);

        var gloves = await Browse("gloves");
        Assert.Null(gloves.Highlighted);
        Assert.All(editor.Headline, figure => Assert.Equal(0, figure.Change));
        Assert.IsNotType<CandidateViewModel>(editor.Inspected);
    }

    [Fact]
    public async Task ARebuiltRowForTheSameItemResolvesNothingNew()
    {
        await editor.Resolution;
        dispatcher.RunPending();
        var picker = await Browse("mask");
        picker.Highlighted = picker.Rows.Single(row => row.Id == "mask_n_5_11_tactical_mask");
        await Previewed();
        var resolving = editor.PreviewResolution;
        // Searching rebuilds the rows; the highlighted item is the same, so the preview stands.
        picker.Search = "tactical";
        Assert.Same(resolving, editor.PreviewResolution);
        Assert.IsType<CandidateViewModel>(editor.Inspected);
    }

    private async Task Compared(ItemPickerViewModel picker)
    {
        await picker.Comparison;
        dispatcher.RunPending();
    }

    [Fact]
    public async Task RowsCompareEachItemWithTheBuildAsItIs()
    {
        var picker = await Browse("chest");
        Assert.Equal("Select Chest", picker.Title);
        Assert.False(picker.IsComparing);
        Assert.Equal(TestPack.Division.Pack.Items.Count(item => item.Slot == "chest"), picker.Rows.Length);

        // Gear compares the layout's three gear stats; with nothing equipped, an armored chest raises armor.
        var tactical = picker.Rows.Single(row => row.Id == "chest_n_5_11_tactical_chest");
        Assert.Equal(3, tactical.Stats.Length);
        Assert.True(tactical.Stats[0].IsUp);
        Assert.Equal("#FFAF10", tactical.Accent);
        Assert.StartsWith("High-End", tactical.Caption, StringComparison.Ordinal);

        // A named item's caption names its fixed talent.
        var named = picker.Rows.First(row => row.Caption.StartsWith("Named", StringComparison.Ordinal));
        Assert.Contains(" · ", named.Caption, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ItemsAreFilteredAndSortedByAStatsChange()
    {
        var picker = await Browse("primary_weapon");
        Assert.Equal("Weapon category", picker.FamilyCaption);
        picker.Family = Pick(picker.Families, "weapon_type_smg");
        Assert.All(picker.Rows, row => Assert.Contains("weapon_type_smg", TestPack.Division.Pack.Items.Single(item => item.ID == row.Id).Tags));

        // Sorting by damage puts the biggest gain first, and each row shows its change.
        picker.SortBy = Pick(picker.SortOptions, "equipped_weapon_damage");
        var damage = picker.Rows.Select(row => double.Parse(row.Change, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(damage.OrderByDescending(value => value), damage);
        Assert.StartsWith("+", picker.Rows[0].Change, StringComparison.Ordinal);

        picker.Search = "vector";
        Assert.All(picker.Rows, row => Assert.Contains("vector", row.Name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HighlightingPreviewsTheItemAndEquipApplies()
    {
        await editor.Resolution;
        dispatcher.RunPending();
        var picker = await Browse("mask");
        Assert.False(picker.EquipCommand.CanExecute(null));
        picker.Highlighted = picker.Rows.Single(row => row.Id == "mask_n_5_11_tactical_mask");
        await Previewed();
        Assert.Equal("", session.Snapshot.State.Build.Equipment.GetValueOrDefault("mask", ""));

        // As Go's picker: the summary and headline show the build with it, marked, and the inspector shows the item.
        var armor = editor.Summary.SelectMany(group => group.Rows).First(row => row.Id == "character_armor");
        Assert.True(armor.IsUp);
        Assert.EndsWith(" ▲", armor.Value, StringComparison.Ordinal);
        Assert.True(editor.Headline.Single(figure => figure.Label == "Effective HP").IsUp);
        var candidate = Assert.IsType<CandidateViewModel>(editor.Inspected);
        Assert.Equal("5.11 Tactical Mask", candidate.Title);
        Assert.True(candidate.EquipCommand.CanExecute(null));

        candidate.EquipCommand.Execute(null);
        Assert.Equal("mask_n_5_11_tactical_mask", session.Snapshot.State.Build.Equipment["mask"]);
        Assert.Null(editor.Picker);
        // The preview ends with the picker: no marks, and the inspector is back on the slot.
        Assert.False(armor.IsUp);
        Assert.IsType<SlotViewModel>(editor.Inspected);
    }

    [Fact]
    public async Task BackEndsThePreview()
    {
        await editor.Resolution;
        dispatcher.RunPending();
        var picker = await Browse("mask");
        picker.Highlighted = picker.Rows.Single(row => row.Id == "mask_n_5_11_tactical_mask");
        await Previewed();
        picker.CloseCommand.Execute(null);
        Assert.Equal("", session.Snapshot.State.Build.Equipment.GetValueOrDefault("mask", ""));
        Assert.All(editor.Headline, figure => Assert.Equal(0, figure.Change));
        Assert.Same(editor.Slot("mask"), editor.Inspected);
    }

    [Fact]
    public async Task TheEquippedItemComesFirstAndIsMarked()
    {
        var mask = editor.Slot("mask");
        mask.Equip("mask_punch_drunk");
        var picker = await Browse("mask");
        Assert.Equal("mask_punch_drunk", picker.Rows[0].Id);
        Assert.Equal("Equipped", picker.Rows[0].Marker);

        // The same weapon in the other weapon slot is marked there.
        var primary = editor.Slot("primary_weapon");
        primary.Equip("primary_weapon_acr");
        var secondary = await Browse("secondary_weapon");
        Assert.Equal("In Primary Weapon", secondary.Rows.Single(row => row.Name == TestPack.Division.Pack.Items.Single(item => item.ID == "primary_weapon_acr").Name).Marker);
    }

    [Fact]
    public async Task AnEditComparesAgainAndBackCloses()
    {
        var picker = await Browse("mask");
        var before = picker.Rows.Single(row => row.Id == "mask_n_5_11_tactical_mask").Stats[0];
        Assert.True(before.IsUp);

        // With the same mask equipped, it no longer raises anything.
        editor.Slot("mask").Equip("mask_n_5_11_tactical_mask");
        await Compared(picker);
        var after = picker.Rows.Single(row => row.Id == "mask_n_5_11_tactical_mask").Stats[0];
        Assert.Equal(0, after.Direction);
        Assert.Equal(before.Value, after.Value);

        picker.CloseCommand.Execute(null);
        Assert.Null(editor.Picker);
    }
}
