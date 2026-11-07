using System.Globalization;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Desktop.ViewModels;
using CharacterPlanner.Infrastructure.Storage;

namespace CharacterPlanner.Desktop.Tests;

/// <summary>The loadout tiles and the inspector, as the Go app's: groups, what each tile shows, and selection.</summary>
public sealed class LoadoutTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "loadout-" + Guid.NewGuid().ToString("N"));
    private readonly TestPack.QueueDispatcher dispatcher = new();
    private readonly PlannerSession session;
    private readonly EditorViewModel editor;
    private readonly CultureInfo culture = CultureInfo.CurrentCulture;

    public LoadoutTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        session = new(TestPack.Division, PlannerState.Of(BuildSnapshot.Create(TestPack.Division.Pack)), new OperationApplier(TestPack.Division));
        editor = new(session, new FileBuildLibrary(folder), dispatcher,
            new PackArtwork(TestPack.Division.Pack.Presentation, Path.Combine(AppContext.BaseDirectory, "packs", "division-2")));
    }

    public void Dispose()
    {
        editor.Dispose();
        CultureInfo.CurrentCulture = culture;
        if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); }
    }

    private TileViewModel Tile(string caption) => editor.TileGroups.SelectMany(group => group.Tiles).Single(tile => tile.Caption == caption);

    [Fact]
    public void TheLoadoutHasTheGoAppsGroups()
    {
        Assert.Equal(["Agent", "Weapons", "Gear", "Skills"], editor.TileGroups.Select(group => group.Name));
        Assert.Equal(["Specialization", "Account upgrades"], editor.TileGroups[0].Tiles.Select(tile => tile.Caption));
        Assert.Equal(2, editor.TileGroups.Single(group => group.Name == "Gear").Columns);

        // The inspector starts on the first slot.
        Assert.Same(editor.Slot("primary_weapon"), editor.Inspected);
        Assert.True(Tile("Primary Weapon").IsSelected);
    }

    [Fact]
    public void TilesShowWhatTheyHold()
    {
        // Empty: muted, with the slot's icon.
        var mask = Tile("Mask");
        Assert.Equal("Empty", mask.Title);
        Assert.True(mask.IsMuted);
        Assert.True(mask.Art.HasImage);
        Assert.Equal("None", Tile("Specialization").Title);
        Assert.Equal(("SHD Level 0", "0 of 17 ranked"), (Tile("Account upgrades").Title, Tile("Account upgrades").Subtitle));

        // Equipped: the item, its rarity and configuration, its accent, and a dot per configured attribute.
        editor.Slot("mask").Equip("mask_n_5_11_tactical_mask");
        Assert.Equal("5.11 Tactical Mask", mask.Title);
        Assert.False(mask.IsMuted);
        Assert.Equal("High-End · 0/4 set · Expertise 0", mask.Subtitle);
        Assert.Equal("#FFAF10", mask.Accent);
        Assert.Empty(mask.Dots);
        var core = editor.Slot("mask").Attributes.Single(attribute => attribute.Id == "core_1");
        core.Selected = core.Choices.Single(choice => choice.Id == "armor_gear_core");
        Assert.Equal("High-End · 1/4 set · Expertise 0", mask.Subtitle);
        Assert.Single(mask.Dots);

        // A named item shows its fixed talent.
        editor.Slot("backpack").Equip("backpack_devil_s_due");
        Assert.True(Tile("Backpack").HasTalent);

        // The specialization shows its points.
        editor.Specialization.Selected = editor.Specialization.Choices.Single(choice => choice.Id == "gunner");
        Assert.Equal(("Gunner", "0 / 165 points"), (Tile("Specialization").Title, Tile("Specialization").Subtitle));
    }

    [Fact]
    public async Task SelectingATileInspectsIt()
    {
        var skill = Tile("Skill 1");
        skill.SelectCommand.Execute(null);
        Assert.Same(editor.SkillSlots[0], editor.Inspected);
        Assert.True(skill.IsSelected);
        Assert.False(Tile("Primary Weapon").IsSelected);

        // A skill's tile shows its first preview figure once resolved.
        editor.SkillSlots[0].Selected = editor.SkillSlots[0].Choices.Single(choice => choice.Id == "skill_assault_turret");
        await editor.Resolution;
        dispatcher.RunPending();
        Assert.Equal("Assault Turret", skill.Title);
        Assert.StartsWith("Effective Skill Tier: ", skill.Subtitle, StringComparison.Ordinal);

        Tile("Account upgrades").SelectCommand.Execute(null);
        Assert.IsType<AccountUpgradesViewModel>(editor.Inspected);
    }

    [Fact]
    public async Task TheInspectorShowsTheSlotAsGosDid()
    {
        var mask = editor.Slot("mask");
        Assert.Equal(("Empty", "Mask", "Choose item", "Choose an item to equip in this slot."), (mask.Title, mask.HeaderCaption, mask.ChooseText, mask.Hint));
        Assert.False(mask.UnequipCommand.CanExecute(null));

        Tile("Mask").SelectCommand.Execute(null);
        mask.Equip("mask_n_5_11_tactical_mask");
        await editor.Resolution;
        dispatcher.RunPending();
        Assert.Equal(("5.11 Tactical Mask", "Mask · High-End", "Change item", ""), (mask.Title, mask.HeaderCaption, mask.ChooseText, mask.Hint));
        Assert.True(mask.HasArt);
        Assert.True(mask.UnequipCommand.CanExecute(null));

        // Its core stats, its core slot apart from the others, and its brand's tiers.
        Assert.Equal(["Armor", "Health", "Skill Tier"], mask.CoreStats.Select(stat => stat.Label));
        Assert.Equal(["core_1"], mask.CoreAttributes.Select(attribute => attribute.Id));
        Assert.Equal("5.11 Tactical · 1 equipped", mask.BrandBonuses[0]);
        Assert.StartsWith("1-piece · Active · applied automatically: +", mask.BrandBonuses[1], StringComparison.Ordinal);
        Assert.StartsWith("2-piece · Inactive: ", mask.BrandBonuses[2], StringComparison.Ordinal);

        // Mods read by what they do; a talent shows its description.
        Assert.All(mask.Mods.SelectMany(mod => mod.Choices.Skip(1)), choice => Assert.Contains(" · ", choice.Name, StringComparison.Ordinal));
        var backpack = editor.Slot("backpack");
        backpack.Equip("backpack_devil_s_due");
        Assert.True(backpack.Talents.Single().HasDescription);

        mask.UnequipCommand.Execute(null);
        Assert.Equal("Empty", mask.Title);
    }

    [Fact]
    public async Task AWeaponNotInHandShowsItsFiguresAsTheWeaponInHand()
    {
        editor.Slot("primary_weapon").Equip("primary_weapon_acr");
        editor.Slot("secondary_weapon").Equip("secondary_weapon_acr");
        editor.WeaponTabs[0].ToggleCommand.Execute(null);
        await editor.Resolution;
        dispatcher.RunPending();

        // The secondary isn't in hand; its figures are worked out as if it were, in the background.
        Tile("Secondary Weapon").SelectCommand.Execute(null);
        await editor.InspectedResolution;
        dispatcher.RunPending();
        var secondary = editor.Slot("secondary_weapon");
        Assert.NotEqual("0", secondary.CoreStats[0].Value);
    }
}
