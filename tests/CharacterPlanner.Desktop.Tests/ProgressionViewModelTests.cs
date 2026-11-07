using System.Globalization;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Infrastructure.Storage;

namespace CharacterPlanner.Desktop.Tests;

/// <summary>C5's equipment and progression editing: finding items, upgrade grades, Prototype and the SHD Watch.</summary>
public sealed class ProgressionViewModelTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "progression-" + Guid.NewGuid().ToString("N"));
    private readonly PlannerSession session;
    private readonly EditorViewModel editor;
    private readonly CultureInfo culture = CultureInfo.CurrentCulture;

    public ProgressionViewModelTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        session = new(TestPack.Division, PlannerState.Of(BuildSnapshot.Create(TestPack.Division.Pack)), new OperationApplier(TestPack.Division));
        editor = new(session, new FileBuildLibrary(folder), new TestPack.QueueDispatcher());
    }

    public void Dispose()
    {
        editor.Dispose();
        CultureInfo.CurrentCulture = culture;
        if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); }
    }

    private BuildSnapshot Build => session.Snapshot.State.Build;

    private ItemPickerViewModel Browse(string slotId)
    {
        editor.Slot(slotId).BrowseCommand.Execute(null);
        return editor.Picker!;
    }

    [Fact]
    public void ItemsAreFoundByNameAndRarity()
    {
        var picker = Browse("mask");
        var all = picker.Rows.Length;
        // Every word must appear, ignoring case, as Go's search.
        picker.Search = "TACTICAL 5.11";
        Assert.Equal(["mask_n_5_11_tactical_mask"], picker.Rows.Select(row => row.Id));

        picker.Search = "";
        picker.Rarity = Pick(picker.Rarities, "exotic");
        Assert.Contains(picker.Rows, row => row.Id == "mask_vile");
        Assert.DoesNotContain(picker.Rows, row => row.Id == "mask_punch_drunk");
        Assert.All(picker.Rows, row => Assert.Contains("exotic", TestPack.Division.Pack.Items.Single(definition => definition.ID == row.Id).Tags));

        picker.Rarity = ItemPickerViewModel.AnyRarity;
        Assert.Equal(all, picker.Rows.Length);
    }

    [Fact]
    public void ItemsAreFoundByBrandSetOrCategory()
    {
        // A brand lists its named items too, which don't carry its name.
        var backpack = Browse("backpack");
        Assert.Equal("Brand or gear set", backpack.FamilyCaption);
        backpack.Family = Pick(backpack.Families, "brand_ceska_vyroba_s_r_o");
        Assert.Equal(["backpack_ceska_vyroba_s_r_o_backpack", "backpack_devil_s_due"], backpack.Rows.Select(row => row.Id));
        backpack.Family = backpack.Families[0];

        // Searching matches the brand's name as well as the item's.
        backpack.Search = "ceska";
        Assert.Contains(backpack.Rows, row => row.Id == "backpack_devil_s_due");

        // Weapons are found by category.
        var primary = Browse("primary_weapon");
        Assert.Equal("Weapon category", primary.FamilyCaption);
        Assert.Contains(primary.Families, family => family.Name == "SMG");
        primary.Family = Pick(primary.Families, "weapon_type_assault_rifle");
        Assert.Equal("Assault rifle", primary.Family.Name);
        Assert.Contains(primary.Rows, row => row.Id == "primary_weapon_acr");
        Assert.All(primary.Rows, row => Assert.Contains("weapon_type_assault_rifle", TestPack.Division.Pack.Items.Single(definition => definition.ID == row.Id).Tags));
    }

    [Fact]
    public void UpgradeGradesAreTypedWithinTheirRange()
    {
        var mask = editor.Slot("mask");
        Assert.Null(mask.Upgrade);
        mask.Equip("mask_n_5_11_tactical_mask");
        var grade = Assert.IsType<RankViewModel>(mask.Upgrade);
        Assert.Equal("0", grade.ValueText);

        grade.ValueText = "31";
        grade.CommitValue();
        Assert.Contains("whole number", editor.Message, StringComparison.Ordinal);
        Assert.False(Build.EquipmentUpgrades.ContainsKey("mask"));

        grade.ValueText = "12";
        grade.CommitValue();
        Assert.Null(editor.Message);
        Assert.Equal(new EquipmentUpgrade("mask_n_5_11_tactical_mask", 12), Build.EquipmentUpgrades["mask"]);

        // Another item has its own grade, starting from none.
        mask.Equip("mask_punch_drunk");
        Assert.Equal("0", mask.Upgrade!.ValueText);
    }

    [Fact]
    public void PrototypeQualityAndAugmentLevels()
    {
        var mask = editor.Slot("mask");
        mask.Equip("mask_n_5_11_tactical_mask");
        Assert.True(mask.PrototypeEligible);
        Assert.False(mask.IsPrototype);
        Assert.False(mask.HasAugment);

        // As Go's section: the check makes it Prototype without an augment.
        mask.IsPrototype = true;
        Assert.Equal(new PrototypeSelection("mask_n_5_11_tactical_mask", "", 0), Build.Prototypes["mask"]);
        Assert.Equal(SlotViewModel.NoAugment, mask.Augment);
        Assert.Empty(mask.Levels);

        // An augment starts at its highest level; its level is then chosen, and its effect reads its value there.
        mask.Augment = Pick(mask.Augments, "augment_echo");
        Assert.True(mask.HasAugment);
        Assert.Equal("10", mask.Level.Id);
        Assert.Equal(10, mask.Levels.Length);
        mask.Level = Pick(mask.Levels, "4");
        Assert.Equal(new PrototypeSelection("mask_n_5_11_tactical_mask", "augment_echo", 4), Build.Prototypes["mask"]);
        Assert.True(mask.HasAugmentEffect);
        Assert.DoesNotContain("{}", mask.AugmentEffect, StringComparison.Ordinal);

        mask.IsPrototype = false;
        Assert.False(Build.Prototypes.ContainsKey("mask"));
        Assert.False(mask.HasAugment);

        // Exotics can't be Prototype.
        mask.Equip("mask_vile");
        Assert.False(mask.PrototypeEligible);
    }

    [Fact]
    public void RollsSlideOnTheirStepsAsOneUndoStep()
    {
        var mask = editor.Slot("mask");
        mask.Equip("mask_n_5_11_tactical_mask");
        var core = mask.Attributes.Single(attribute => attribute.Id == "core_1");
        core.Selected = Pick(core.Choices, "armor_gear_core");
        Assert.True(core.CanSlide);
        Assert.Equal(core.SliderMaximum, core.SliderValue);
        var before = session.Snapshot.Revision;

        // A drag's values snap to the steps and land as one undo step.
        core.SliderValue = core.SliderMinimum + 1234.5;
        core.SliderValue = core.SliderMinimum + 2000.4;
        var rolled = Build.AttributeRolls["mask"]["core_1"].Value;
        Assert.InRange(rolled, core.SliderMinimum, core.SliderMaximum);
        Assert.True(core.SliderStep > 0);
        var steps = (rolled - core.SliderMinimum) / core.SliderStep;
        Assert.Equal(Math.Round(steps), steps, 6);
        session.Undo(session.Snapshot.Revision);
        Assert.Equal(core.SliderMaximum, Build.AttributeRolls["mask"]["core_1"].Value);
        Assert.True(session.Snapshot.Revision > before);
    }

    [Fact]
    public void ShdWatchUpgradesAreRanks()
    {
        var damage = editor.AccountUpgrades.Single(upgrade => upgrade.Name == TestPack.Division.Pack.AccountUpgrades.Single(definition => definition.ID == "watch_weapon_damage").Name);
        Assert.Equal("0", damage.ValueText);
        damage.ValueText = "50";
        damage.CommitValue();
        Assert.Equal(50, Build.AccountUpgrades["watch_weapon_damage"]);
        editor.UndoCommand.Execute(null);
        Assert.Equal("0", damage.ValueText);
    }

    /// <summary>A draft that can't be committed stops the save and says why, rather than being silently left out.</summary>
    [Fact]
    public async Task AnInvalidDraftStopsTheSave()
    {
        var mask = editor.Slot("mask");
        mask.Equip("mask_n_5_11_tactical_mask");
        mask.Upgrade!.ValueText = "31";
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Null(editor.RecordId);
        Assert.StartsWith("Not saved: Expertise must be a whole number", editor.Message, StringComparison.Ordinal);
        Assert.Empty(editor.LibraryEntries);
    }

    /// <summary>Ranks accept thousands separators, as their range shows them.</summary>
    [Fact]
    public void RanksReadTheirRangesFormat()
    {
        var level = editor.AccountUpgrades.Single(upgrade => upgrade.Key == "shd_level");
        level.ValueText = "1,000";
        Assert.True(level.CommitValue());
        Assert.Equal(1000, Build.AccountUpgrades["shd_level"]);
    }

    /// <summary>Saving commits every draft, ranks included.</summary>
    [Fact]
    public async Task SavingCommitsRankDrafts()
    {
        var mask = editor.Slot("mask");
        mask.Equip("mask_n_5_11_tactical_mask");
        mask.Upgrade!.ValueText = "20";
        editor.AccountUpgrades[0].ValueText = "7";
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal(20, Build.EquipmentUpgrades["mask"].Rank);
        Assert.Equal(7, Build.AccountUpgrades[TestPack.Division.Pack.AccountUpgrades[0].ID]);
        Assert.False(editor.IsDirty);
    }
}
